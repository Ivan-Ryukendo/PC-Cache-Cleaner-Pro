using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CleanPC.Pro.Ui
{
    /// <summary>Tab 5: startup entries. Disable/enable is REVERSIBLE (StartupApproved / task disable); nothing is deleted.</summary>
    public class StartupViewModel : TabViewModel
    {
        ObservableCollection<StartupEntry> _rows = new ObservableCollection<StartupEntry>();
        public ObservableCollection<StartupEntry> Rows { get { return _rows; } private set { Set(ref _rows, value); } }
        public RelayCommand RefreshCommand { get; private set; }
        public RelayCommand DisableCommand { get; private set; }
        public RelayCommand EnableCommand { get; private set; }
        public RelayCommand OpenCommand { get; private set; }
        public StartupEntry Selected { get; set; }
        public System.Collections.IList SelectedItems { get; set; }

        public StartupViewModel(MainViewModel main) : base(main)
        {
            RefreshCommand = new RelayCommand(() => { var t = LoadAsync(); }, () => IsIdle);
            DisableCommand = new RelayCommand(() => Toggle(false));
            EnableCommand = new RelayCommand(() => Toggle(true));
            OpenCommand = new RelayCommand(p => { var e = p as StartupEntry; if (e != null && !string.IsNullOrEmpty(e.Exe)) Dialogs.Reveal(e.Exe); });
            Status = "Press Refresh to list startup entries.";
        }

        public Task LoadAsync()
        {
            return RunAsync("Reading startup entries, signatures and scheduled tasks...", async (ct, prog) =>
            {
                var list = await Task.Run(() => StartupManager.Collect(true, true), ct);
                Rows = new ObservableCollection<StartupEntry>(list.OrderBy(e => e.Source).ThenBy(e => e.Name));
                Status = list.Count + " startup entries (" + list.Count(e => !e.Enabled) + " disabled).";
            });
        }

        void Toggle(bool enable)
        {
            var sel = (SelectedItems ?? new List<object>()).Cast<StartupEntry>().ToList();
            if (sel.Count == 0 && Selected != null) sel.Add(Selected);
            if (sel.Count == 0) { Dialogs.Info("Startup", "Select one or more rows first."); return; }
            var act = sel.Where(e => e.CanToggle && e.Enabled != enable).ToList();
            if (act.Count == 0) { Dialogs.Info("Startup", "Nothing to change: the selected entries are already " + (enable ? "enabled" : "disabled") + " or cannot be toggled (RunOnce entries are read-only)."); return; }
            if (!enable && !Dialogs.Confirm("Disable startup entries", "These will stop starting with Windows (you can re-enable them here at any time; nothing is deleted):\n\n" + string.Join("\n", act.Select(e => " - " + e.Name + "  [" + e.Source + "]")), "Disable", "Cancel")) return;
            foreach (var e in act)
            {
                string err = StartupManager.SetEnabled(e, enable);
                e.Status = err == null ? (enable ? "Enabled." : "Disabled (reversible).") : "Failed: " + err;
            }
            Status = "Done. Changes take effect at the next sign-in.";
        }
    }

    public class SpaceRow { public string Time { get; set; } public string Drive { get; set; } public string Free { get; set; } public string Size { get; set; } public string Delta { get; set; } public string Event { get; set; } public long Bytes { get; set; } }
    public class ChartSeries { public string Name { get; set; } public Brush Color { get; set; } public List<KeyValuePair<DateTime, double>> Points { get; set; } }

    /// <summary>Tab 6: free space per drive at each run (history.json) with a chart and a warning on big drops.</summary>
    public class SpaceViewModel : TabViewModel
    {
        public ObservableCollection<SpaceRow> Rows { get; private set; }
        List<ChartSeries> _series = new List<ChartSeries>();
        public List<ChartSeries> Series { get { return _series; } private set { Set(ref _series, value); } }
        string _warn = ""; public string Warning { get { return _warn; } private set { if (Set(ref _warn, value)) Raise("HasWarning"); } }
        public bool HasWarning { get { return _warn.Length > 0; } }
        public RelayCommand RecordNowCommand { get; private set; }
        static readonly Color[] Palette = { Color.FromRgb(0x1F, 0x77, 0xB4), Color.FromRgb(0xFF, 0x7F, 0x0E), Color.FromRgb(0x2C, 0xA0, 0x2C), Color.FromRgb(0xD6, 0x27, 0x28), Color.FromRgb(0x94, 0x67, 0xBD), Color.FromRgb(0x8C, 0x56, 0x4B) };

        public SpaceViewModel(MainViewModel main) : base(main)
        {
            Rows = new ObservableCollection<SpaceRow>();
            RecordNowCommand = new RelayCommand(() => { SpaceHistory.Record("manual"); Reload(); Status = "Recorded the current free space."; });
        }

        public void Reload()
        {
            var all = SpaceHistory.All();
            Rows.Clear();
            var last = new Dictionary<string, long>();
            foreach (var s in all.OrderBy(x => x.Time))
            {
                long prev; string delta = "";
                if (last.TryGetValue(s.Drive, out prev)) { long d = s.Free - prev; delta = (d >= 0 ? "+" : "-") + Fmt.Size(Math.Abs(d)); }
                last[s.Drive] = s.Free;
                Rows.Insert(0, new SpaceRow { Time = s.Time.Replace('T', ' '), Drive = s.Drive, Free = Fmt.Size(s.Free), Size = Fmt.Size(s.Size), Delta = delta, Event = s.Event, Bytes = s.Free });
            }
            var series = new List<ChartSeries>(); int i = 0;
            foreach (var g in all.GroupBy(s => s.Drive).OrderBy(g => g.Key))
            {
                var pts = new List<KeyValuePair<DateTime, double>>();
                foreach (var s in g.OrderBy(x => x.Time)) { DateTime t; if (DateTime.TryParse(s.Time, out t)) pts.Add(new KeyValuePair<DateTime, double>(t, s.Free / 1073741824.0)); }
                var b = new SolidColorBrush(Palette[i++ % Palette.Length]); b.Freeze();
                series.Add(new ChartSeries { Name = g.Key, Color = b, Points = pts });
            }
            Series = series;
            // warn if the latest sample dropped by more than X GB compared with the one before it
            var warns = new List<string>(); double limit = CleanPC.Pro.Settings.Current.WarnDropGB;
            foreach (var g in all.GroupBy(s => s.Drive))
            {
                var l = g.OrderBy(x => x.Time).ToList();
                if (l.Count < 2) continue;
                double drop = (l[l.Count - 2].Free - l[l.Count - 1].Free) / 1073741824.0;
                if (drop > limit) warns.Add(g.Key + " lost " + drop.ToString("N1") + " GB of free space between the last two runs (limit " + limit.ToString("N0") + " GB).");
            }
            Warning = string.Join("  ", warns);
            Status = all.Count + " sample(s) in " + SpaceHistory.FilePath;
        }
    }

    /// <summary>Tab 7: read-only security heuristics. SHA-256 + "Check on VirusTotal" (opens the browser to the hash URL; nothing is uploaded).</summary>
    public class SecurityViewModel : TabViewModel
    {
        ObservableCollection<SecItem> _items = new ObservableCollection<SecItem>();
        public ObservableCollection<SecItem> Items { get { return _items; } private set { Set(ref _items, value); } }
        ObservableCollection<BrowserExt> _ext = new ObservableCollection<BrowserExt>();
        public ObservableCollection<BrowserExt> Extensions { get { return _ext; } private set { Set(ref _ext, value); } }
        SecItem _sel; public SecItem Selected { get { return _sel; } set { if (Set(ref _sel, value)) { Raise("Detail"); Raise("HasSelection"); Raise("CanVt"); } } }
        public bool HasSelection { get { return _sel != null; } }
        public bool CanVt { get { return _sel != null && !string.IsNullOrEmpty(_sel.Sha256); } }
        public string Detail
        {
            get
            {
                if (_sel == null) return "Select a row to see its SHA-256, network connections and why it was flagged.";
                return "Why: " + _sel.Reasons + "\nPath: " + _sel.Path + "\nSignature: " + _sel.SigText + "\nSHA-256: " + (_sel.Sha256 ?? "(not available)") +
                       (string.IsNullOrEmpty(_sel.Connections) ? "" : "\nNetwork connections:\n" + _sel.Connections);
            }
        }
        string _def = "Run the security check to see Windows Defender status and a scan recommendation."; public string DefenderText { get { return _def; } private set { Set(ref _def, value); } }
        string _summary = ""; public string Summary { get { return _summary; } private set { Set(ref _summary, value); } }
        public RelayCommand RunCommand { get; private set; }
        public RelayCommand OpenCommand { get; private set; }
        public RelayCommand VtCommand { get; private set; }
        public RelayCommand CopyHashCommand { get; private set; }

        public SecurityViewModel(MainViewModel main) : base(main)
        {
            RunCommand = new RelayCommand(() => { var t = RunAsync2(); }, () => IsIdle);
            OpenCommand = new RelayCommand(() => { if (_sel != null) Dialogs.Reveal(_sel.Path); }, () => _sel != null);
            VtCommand = new RelayCommand(() => { if (CanVt) Dialogs.OpenUrl(_sel.VtUrl); }, () => CanVt);
            CopyHashCommand = new RelayCommand(() => { if (CanVt) { try { Clipboard.SetText(_sel.Sha256); Status = "SHA-256 copied."; } catch { } } }, () => CanVt);
        }

        Task RunAsync2()
        {
            return RunAsync("Starting security check (read-only)...", async (ct, prog) =>
            {
                Log.Write("SECURITY CHECK start (read-only)");
                var rep = await Task.Run(() => SecurityScanner.Run(prog, ct), ct);
                Items = new ObservableCollection<SecItem>(rep.Items);
                Extensions = new ObservableCollection<BrowserExt>(rep.Extensions);
                foreach (var x in rep.Items) Log.Write(string.Format("SECURITY {0} {1} {2} pid={3} sig={4} path={5} sha256={6} :: {7}", x.Severity, x.Type, x.Name, x.Pid, x.Signature, x.Path, x.Sha256, x.Reasons));
                var d = rep.Defender;
                var lines = new List<string> { "Checked " + rep.Processes + " processes and " + rep.StartupEntries + " startup entries; " + rep.Items.Count + " flagged ('suspicious, review'). " + rep.SignedAppDataStartup + " validly signed AppData startup entries were not listed." };
                if (d.Available) lines.Add(d.Summary);
                lines.AddRange(d.Advice);
                DefenderText = string.Join("\n", lines);
                Summary = "Done - " + rep.Items.Count + " item(s) to review, " + rep.Extensions.Count + " browser extension(s) listed (names only).";
                Status = Summary;
            });
        }
    }

    /// <summary>Tab 9: other Windows user profiles (opt-in, typed-name confirmation, Win32_UserProfile delete).</summary>
    public class ProfilesViewModel : TabViewModel
    {
        ObservableCollection<ProfileRow> _rows = new ObservableCollection<ProfileRow>();
        public ObservableCollection<ProfileRow> Rows { get { return _rows; } private set { Set(ref _rows, value); } }
        public RelayCommand ScanCommand { get; private set; }
        public RelayCommand DeleteCommand { get; private set; }
        public string Title { get { return Rows.Count == 0 ? "Other profiles" : "Other profiles (" + Rows.Count + ")"; } }

        public ProfilesViewModel(MainViewModel main) : base(main)
        {
            ScanCommand = new RelayCommand(() => { var t = ScanAsync(); }, () => IsIdle);
            DeleteCommand = new RelayCommand(() => { var t = DeleteAsync(); }, () => IsIdle && Rows.Any(r => r.IsChecked));
            Status = "Press 'List profiles' - nothing is selected by default.";
        }

        Task ScanAsync()
        {
            return RunAsync("Listing other profiles...", async (ct, prog) =>
            {
                var drives = Main.SelectedDrives;
                var list = await Task.Run(() => ProfileManager.List(drives, ct), ct);
                Rows = new ObservableCollection<ProfileRow>(list);
                foreach (var r in Rows) { r.PropertyChanged += (s, e) => System.Windows.Input.CommandManager.InvalidateRequerySuggested(); Log.Write("PROFILE listed (unticked) " + r.SizeText + "  " + r.Path); }
                Raise("Title");
                Status = list.Count + " removable profile(s). Current, system, loaded, Default/Public and Administrator profiles are never listed.";
            });
        }

        Task DeleteAsync()
        {
            return RunAsync("Deleting profiles...", async (ct, prog) =>
            {
                int deleted = 0;
                foreach (var p in Rows.Where(r => r.IsChecked).ToList())
                {
                    string m = "You are about to PERMANENTLY delete the Windows user profile:\n\n" + p.Path + "   (" + p.SizeText + ")\n\nThis removes that user's documents, desktop, pictures, downloads and settings. It cannot be undone.\n\nType the profile name  " + p.Name + "  below to confirm.";
                    if (!Dialogs.TypedConfirm("Delete user profile '" + p.Name + "'", m, p.Name, "Delete this profile")) { Log.Write("PROFILE delete declined: " + p.Path); continue; }
                    prog.Report("Deleting profile " + p.Name + "...");
                    string err = await Task.Run(() => ProfileManager.Remove(p));
                    if (err == null) { deleted++; Rows.Remove(p); }
                    else { p.Status = "Could not delete: " + err; p.IsChecked = false; }
                }
                Raise("Title");
                Main.RefreshDrives();
                Status = "Profiles deleted: " + deleted;
            });
        }
    }

    /// <summary>Tab 10: history, exclusions, theme, restore point, export, schedule.</summary>
    public class SettingsViewModel : TabViewModel
    {
        public ObservableCollection<CleanRecord> History { get; private set; }
        public ObservableCollection<string> Exclusions { get; private set; }
        public string SelectedExclusion { get; set; }
        public RelayCommand AddExclusionCommand { get; private set; }
        public RelayCommand RemoveExclusionCommand { get; private set; }
        public RelayCommand ScheduleCreateCommand { get; private set; }
        public RelayCommand ScheduleRemoveCommand { get; private set; }
        public RelayCommand ExportCommand { get; private set; }
        public RelayCommand SetThemeCommand { get; private set; }
        string _sched = "Checking..."; public string ScheduleText { get { return _sched; } private set { Set(ref _sched, value); } }
        bool _hasSched; public bool HasSchedule { get { return _hasSched; } private set { Set(ref _hasSched, value); } }
        string _msg = ""; public string Message { get { return _msg; } private set { Set(ref _msg, value); } }

        public string Theme { get { return CleanPC.Pro.Settings.Current.Theme; } }
        public bool IsThemeSystem { get { return Theme == "System"; } set { if (value) SetTheme("System"); } }
        public bool IsThemeLight { get { return Theme == "Light"; } set { if (value) SetTheme("Light"); } }
        public bool IsThemeDark { get { return Theme == "Dark"; } set { if (value) SetTheme("Dark"); } }
        public bool RestorePoint { get { return CleanPC.Pro.Settings.Current.RestorePointBeforeClean; } set { var s = CleanPC.Pro.Settings.Current; s.RestorePointBeforeClean = value; s.Save(); Raise(); } }
        public double WarnDropGB { get { return CleanPC.Pro.Settings.Current.WarnDropGB; } set { var s = CleanPC.Pro.Settings.Current; s.WarnDropGB = Math.Max(0.1, Math.Min(10000, value)); s.Save(); Raise(); } }
        public string DataFolder { get { return AppInfo.DataDir; } }
        public string LogPath { get { return Log.Path ?? "(not available)"; } }

        public SettingsViewModel(MainViewModel main) : base(main)
        {
            History = new ObservableCollection<CleanRecord>();
            Exclusions = new ObservableCollection<string>(CleanPC.Pro.Settings.Current.Exclusions);
            ReloadHistory();
            AddExclusionCommand = new RelayCommand(() =>
            {
                string f = Dialogs.PickFolder("Choose a folder that must NEVER be scanned or cleaned");
                if (string.IsNullOrEmpty(f) || Exclusions.Contains(f, StringComparer.OrdinalIgnoreCase)) return;
                Exclusions.Add(f); SaveExclusions();
            });
            RemoveExclusionCommand = new RelayCommand(() => { if (SelectedExclusion != null) { Exclusions.Remove(SelectedExclusion); SaveExclusions(); } });
            ScheduleCreateCommand = new RelayCommand(() => { var t = Schedule(true); });
            ScheduleRemoveCommand = new RelayCommand(() => { var t = Schedule(false); });
            ExportCommand = new RelayCommand(() => Main.Cleanup.ExportCommand.Execute(null));
            SetThemeCommand = new RelayCommand(p => SetTheme((string)p));
            var t0 = RefreshScheduleAsync();
        }

        void SaveExclusions() { var s = CleanPC.Pro.Settings.Current; s.Exclusions = Exclusions.ToList(); s.Save(); Message = Exclusions.Count + " excluded folder(s) saved. They are skipped by every scan and refused at delete time."; }

        void SetTheme(string t)
        {
            var s = CleanPC.Pro.Settings.Current; s.Theme = t; s.Save();
            ThemeManager.Apply(t);
            Raise("IsThemeSystem"); Raise("IsThemeLight"); Raise("IsThemeDark");
        }

        public void ReloadHistory()
        {
            History.Clear();
            foreach (var r in CleanHistory.All()) { r.Time = r.Time.Replace('T', ' '); History.Add(r); }
        }

        async Task RefreshScheduleAsync()
        {
            bool ex = await Task.Run(() => Scheduler.Exists());
            HasSchedule = ex;
            ScheduleText = ex ? "A weekly task exists: \"" + Scheduler.TaskName + "\" (Sundays 12:00, runs '" + AppInfo.ExeName() + " --auto --safe' with highest rights)." : "No scheduled clean is set up.";
        }

        async Task Schedule(bool create)
        {
            if (create && !Dialogs.Confirm("Weekly safe clean", "This creates a Windows Task Scheduler task that runs\n\n    " + AppInfo.ExePath + " --auto --safe\n\nevery Sunday at 12:00 (highest privileges, no window). It only deletes the pre-ticked SAFE items. You can remove it here at any time.", "Create task", "Cancel")) return;
            string err = await Task.Run(() => create ? Scheduler.Create() : Scheduler.Remove());
            Message = err == null ? (create ? "Scheduled task created." : "Scheduled task removed.") : "Failed: " + err;
            await RefreshScheduleAsync();
        }
    }

    public static class ThemeManager
    {
        public static bool SystemIsDark()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) { var v = k == null ? null : k.GetValue("AppsUseLightTheme"); return v is int && (int)v == 0; } }
            catch { return false; }
        }

        public static void Apply(string choice)
        {
            bool dark = choice == "Dark" || (choice != "Light" && SystemIsDark());
            var app = Application.Current; if (app == null) return;
            var d = new ResourceDictionary { Source = new Uri("pack://application:,,,/CleanPC-Pro;component/Ui/Themes/" + (dark ? "Dark" : "Light") + ".xaml", UriKind.Absolute) };
            var md = app.Resources.MergedDictionaries;
            if (md.Count > 0) md[0] = d; else md.Add(d);
        }
    }
}
