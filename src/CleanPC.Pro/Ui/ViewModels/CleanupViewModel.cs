using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace CleanPC.Pro.Ui
{
    public class CleanupViewModel : TabViewModel
    {
        public ObservableCollection<Target> Targets { get; private set; }
        public RelayCommand SelectNoneCommand { get; private set; }
        public RelayCommand SaveCustomCommand { get; private set; }
        public RelayCommand CleanCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }
        public RelayCommand SetPresetCommand { get; private set; }
        public RelayCommand ExportCommand { get; private set; }

        string _preset = "Safe";
        public string PresetName { get { return _preset; } }
        public bool IsPresetSafe { get { return _preset == "Safe"; } set { if (value) SetPreset("Safe"); } }
        public bool IsPresetDev { get { return _preset == "Developer"; } set { if (value) SetPreset("Developer"); } }
        public bool IsPresetDeep { get { return _preset == "Deep"; } set { if (value) SetPreset("Deep"); } }
        public bool IsPresetCustom { get { return _preset == "Custom"; } set { if (value) SetPreset("Custom"); } }

        string _total = "Nothing scanned yet."; public string TotalText { get { return _total; } private set { Set(ref _total, value); } }
        bool _cleaning; public bool IsCleaning { get { return _cleaning; } private set { if (Set(ref _cleaning, value)) { Raise("CanClean"); } } }
        public bool CanClean { get { return !IsBusy && !_cleaning; } }

        public CleanupViewModel(MainViewModel main) : base(main)
        {
            Targets = new ObservableCollection<Target>();
            var view = CollectionViewSource.GetDefaultView(Targets);
            view.SortDescriptions.Add(new SortDescription("Category", ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription("Size", ListSortDirection.Descending));
            _preset = Presets.Names.Contains(CleanPC.Pro.Settings.Current.Preset) ? CleanPC.Pro.Settings.Current.Preset : "Safe";
            SetPresetCommand = new RelayCommand(p => SetPreset((string)p));
            SelectNoneCommand = new RelayCommand(() => { foreach (var t in Targets) t.IsChecked = false; });
            SaveCustomCommand = new RelayCommand(SaveCustom);
            CleanCommand = new RelayCommand(() => { var t = CleanAsync(); }, () => CanClean && Targets.Any(x => x.IsChecked));
            CancelCommand = new RelayCommand(() => Cancel());
            ExportCommand = new RelayCommand(Export);
            PropertyChanged += (s, e) => { if (e.PropertyName == "IsBusy") Raise("CanClean"); };
        }

        void SetPreset(string name)
        {
            _preset = name;
            Raise("PresetName"); Raise("IsPresetSafe"); Raise("IsPresetDev"); Raise("IsPresetDeep"); Raise("IsPresetCustom");
            var s = CleanPC.Pro.Settings.Current; s.Preset = name; s.Save();
            Presets.Apply(name, Targets, s);
            UpdateTotals();
        }

        void SaveCustom()
        {
            var s = CleanPC.Pro.Settings.Current;
            s.CustomChecked = Targets.Where(t => t.IsChecked).Select(t => t.Name).ToList();
            s.Preset = "Custom"; s.Save();
            _preset = "Custom";
            Raise("PresetName"); Raise("IsPresetSafe"); Raise("IsPresetDev"); Raise("IsPresetDeep"); Raise("IsPresetCustom");
            Dialogs.Info("Custom preset saved", s.CustomChecked.Count + " item(s) remembered in " + CleanPC.Pro.Settings.FilePath + ".\nRisky items stay unticked unless you tick them yourself and confirm.");
        }

        public void UpdateTotals()
        {
            long found = Targets.Where(t => t.Size > 0).Sum(t => t.Size);
            long sel = Targets.Where(t => t.IsChecked && t.Size > 0).Sum(t => t.Size);
            int n = Targets.Count(t => t.IsChecked);
            TotalText = Targets.Count == 0 ? "Nothing found yet." : "Selected: " + Fmt.Size(sel) + "  (" + n + " of " + Targets.Count + " items)   -   total found: " + Fmt.Size(found);
        }

        void Hook(Target t) { t.PropertyChanged += (s, e) => { if (e.PropertyName == "IsChecked" || e.PropertyName == "Size") UpdateTotals(); }; }

        public Task ScanAsync()
        {
            return RunAsync("Scanning...", async (ct, prog) =>
            {
                var drives = Main.SelectedDrives;
                if (drives.Count == 0) { Dialogs.Info("PC Cache Cleaner Pro", "Tick at least one drive to scan."); return; }
                Log.Write("SCAN start drives=" + string.Join(",", drives));
                Targets.Clear(); Main.Report.Items.Clear();
                var res = await Task.Run(() => TargetBuilder.Build(drives, prog, ct), ct);
                Presets.Apply(_preset, res.Targets, CleanPC.Pro.Settings.Current);
                foreach (var t in res.Targets)
                {
                    Hook(t); Targets.Add(t);
                    Log.Write("FOUND " + Fmt.Size(t.Size).PadLeft(12) + "  " + t.Name + "  [" + t.Category + "]" + (t.IsChecked ? "" : "  (unticked)"));
                }
                foreach (var r in res.Report.OrderByDescending(r => r.Size)) { Main.Report.Items.Add(r); Log.Write("REPORT " + Fmt.Size(r.Size).PadLeft(12) + "  " + r.Name); }
                Main.Report.UpdateCount();
                UpdateTotals();
                string msg = "Scan finished: " + Targets.Count + " cleanup items, " + res.Info.DirsVisited + " folders searched.";
                if (res.Info.WalkTimedOut) msg += " Drive scan hit its time limit - some folders were not searched.";
                if (ct.IsCancellationRequested) msg += " Scan was stopped early.";
                Status = msg; Main.Status = msg;
                Main.RefreshDrives();
            });
        }

        public void ShowDetails(Target t)
        {
            if (t == null || t.Paths == null || t.Paths.Count == 0) return;
            string what = t.Kind == TargetKind.Files ? "files" : t.Kind == TargetKind.RemoveDirs ? "folders (removed completely)" : t.Kind == TargetKind.RecycleBin ? "Recycle Bin (emptied)" : "folders (only their CONTENTS are deleted)";
            Dialogs.ShowPaths("Everything in: " + t.Name, t.Paths.Count + " " + what + " - total " + Fmt.Size(t.Size) + ". Nothing is deleted from this window.\n" + (string.IsNullOrEmpty(t.Note) ? "" : t.Note), t.Paths);
        }

        Task CleanAsync()
        {
            return RunAsync("Cleaning...", async (ct, prog) =>
            {
                var chosen = Targets.Where(t => t.IsChecked && t.Size >= 0).ToList();
                if (chosen.Count == 0) { Dialogs.Info("PC Cache Cleaner Pro", "Nothing selected."); return; }
                var risky = chosen.Where(t => t.Risk == Risk.Risky || t.Risk == Risk.Optional).ToList();
                if (risky.Any(t => t.Risk == Risk.Risky))
                {
                    string names = string.Join("\n", risky.Where(t => t.Risk == Risk.Risky).Select(t => " - " + t.Name + "  (" + Fmt.Size(t.Size) + ")"));
                    if (!Dialogs.Confirm("Confirm risky items", "You ticked item(s) that are NOT selected by default because they carry some risk:\n\n" + names + "\n\nThey will be deleted permanently (not sent to the Recycle Bin). Continue?", "Yes, delete them", "Cancel")) return;
                }
                var optional = risky.Where(t => t.Risk == Risk.Optional).ToList();
                if (optional.Count > 0)
                {
                    string names = string.Join("\n", optional.Select(t => " - " + t.Name + "  (" + Fmt.Size(t.Size) + ")  " + t.Note));
                    if (!Dialogs.Confirm("Confirm optional items", "These optional items are not part of the safe default and will be deleted permanently:\n\n" + names + "\n\nContinue?", "Yes, delete them", "Cancel")) return;
                }
                if (chosen.Any(t => t.Name.StartsWith("Claude", StringComparison.OrdinalIgnoreCase)) && Process.GetProcessesByName("claude").Length > 0)
                    if (!Dialogs.Confirm("Claude Desktop is open", "Claude Desktop is running, so its VM cache can't be fully freed. Close Claude Desktop first for that item.\n\nContinue with the rest now?", "Continue", "Cancel")) return;
                IsCleaning = true;
                try
                {
                    var s = CleanPC.Pro.Settings.Current;
                    if (s.RestorePointBeforeClean)
                    {
                        prog.Report("Creating a System Restore point...");
                        string rp = await Task.Run(() => AppInfo.IsAdmin ? RestorePoint.Create("PC Cache Cleaner Pro - before cleaning") : "needs Administrator");
                        if (rp != null && !Dialogs.Confirm("Restore point not created", "A System Restore point could not be created:\n" + rp + "\n\nClean anyway?", "Clean anyway", "Cancel")) return;
                    }
                    var before = Main.Drives.Where(d => d.IsSelected).ToDictionary(d => d.Letter, d => CleanPC.Pro.Drives.FreeOf(d.Letter));
                    var res = await Task.Run(() => CleanRunner.Run(chosen, prog, ct, t => { Application.Current.Dispatcher.BeginInvoke(new Action(() => t.IsChecked = false)); }));
                    CleanHistory.Add(new CleanRecord { Time = DateTime.Now.ToString("s"), Mode = "gui", Drives = string.Join(",", before.Keys), Items = res.Done, Freed = res.Freed, Detail = _preset });
                    SpaceHistory.Record("clean");
                    Main.Space.Reload(); Main.Settings.ReloadHistory();
                    Main.RefreshDrives();
                    UpdateTotals();
                    string free = string.Join(",  ", Main.Drives.Where(d => d.IsSelected).Select(d => d.Letter + " " + Fmt.Size(CleanPC.Pro.Drives.FreeOf(d.Letter))));
                    string m = (res.Cancelled ? "Cancelled. Freed about " : "Freed about ") + Fmt.Size(res.Freed) + "." + (res.SkippedItems > 0 ? "  " + res.SkippedItems + " item(s) had files that were skipped" + (res.Locked.Count > 0 ? " (" + res.Locked.Count + " in use by running programs - see Locked files below)." : " (see the row and the log).") : "");
                    Status = m; Main.Status = m;
                    Main.Locks.Load(res.Locked);
                    StatusPopup.Show(m, free, res.Locked.Count, () => { var w = Application.Current.MainWindow as MainWindow; if (w != null) w.ShowCleanupTab(); });
                }
                finally { IsCleaning = false; }
            });
        }

        /// <summary>Hidden test hook (--demo-locks): cleans the CONTENTS of one throwaway folder through the same guard + post-clean flow.</summary>
        public void DemoClean(string folder)
        {
            var t = new Target { Name = "Demo folder", Kind = TargetKind.Contents, Paths = new List<string> { folder }, Size = CleanOps.DirSize(folder, CancellationToken.None, null), IsChecked = true };
            var res = CleanRunner.Run(new List<Target> { t }, null, CancellationToken.None, null);
            string m = "Freed about " + Fmt.Size(res.Freed) + "." + (res.Locked.Count > 0 ? "  " + res.Locked.Count + " in use." : "");
            Main.Locks.Load(res.Locked);
            StatusPopup.Show(m, "", res.Locked.Count, () => { var w = Application.Current.MainWindow as MainWindow; if (w != null) w.ShowCleanupTab(); });
        }

        void Export()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export scan report", Filter = "HTML report (*.html)|*.html|CSV (*.csv)|*.csv", FileName = "CleanPC-Pro-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm") };
            if (dlg.ShowDialog() != true) return;
            if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) ReportExporter.ExportCsv(dlg.FileName, Targets, Main.Report.Items);
            else ReportExporter.ExportHtml(dlg.FileName, Targets, Main.Report.Items);
            Status = "Report saved: " + dlg.FileName;
        }
    }

    public class ReportViewModel : TabViewModel
    {
        public ObservableCollection<Target> Items { get; private set; }
        public string Title { get { return Items.Count == 0 ? "Report-only" : "Report-only (" + Items.Count + ")"; } }
        string _out = ""; public string Output { get { return _out; } private set { Set(ref _out, value); } }
        public string ShadowCapText { get; private set; }
        public RelayCommand DismCommand { get; private set; }
        public RelayCommand StorageSenseCommand { get; private set; }
        public RelayCommand CleanMgrCommand { get; private set; }
        public RelayCommand CopyCommand { get; private set; }

        public ReportViewModel(MainViewModel main) : base(main)
        {
            Items = new ObservableCollection<Target>();
            ShadowCapText = Maintenance.ShadowCapCommand(AppInfo.SystemDrive);
            DismCommand = new RelayCommand(() => { var t = Dism(); }, () => IsIdle);
            StorageSenseCommand = new RelayCommand(() =>
            {
                if (Dialogs.Confirm("Open Storage Sense", "This opens Windows Settings > Storage, where YOU choose what Storage Sense does (temporary files, Recycle Bin rules). This tool changes nothing by itself.", "Open Settings", "Cancel")) Maintenance.OpenStorageSense();
            });
            CleanMgrCommand = new RelayCommand(() =>
            {
                if (Dialogs.Confirm("Open Disk Cleanup", "This opens the classic Windows Disk Cleanup window for drive " + AppInfo.SystemDrive + ". Nothing is deleted until you tick items and press OK in that window.", "Open Disk Cleanup", "Cancel")) Maintenance.OpenCleanMgr(AppInfo.SystemDrive);
            });
            CopyCommand = new RelayCommand(p => { try { Clipboard.SetText(Convert.ToString(p)); Status = "Copied."; } catch { } });
        }

        public void UpdateCount() { Raise("Title"); }

        Task Dism()
        {
            if (!AppInfo.IsAdmin) { Dialogs.Info("Administrator needed", "DISM component cleanup needs an elevated (Administrator) run of this program."); return Task.FromResult(0); }
            if (!Dialogs.Confirm("DISM component store cleanup",
                "This runs:\n    " + Maintenance.DismCommand + "\n\nIt removes superseded Windows component versions from WinSxS. It is a standard Microsoft maintenance step, can take 5-30 minutes, and cannot be undone (you can no longer uninstall already-installed updates). It does not touch your files.\n\nRun it now?", "Run DISM", "Cancel")) return Task.FromResult(0);
            return RunAsync("Running DISM (this can take a while)...", async (ct, prog) =>
            {
                Output = "Running " + Maintenance.DismCommand + " ...";
                string o = await Task.Run(() => Maintenance.RunDism());
                Output = o;
                Status = "DISM finished.";
            });
        }
    }
}
