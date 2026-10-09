using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace CleanPC.Pro.Ui
{
    /// <summary>Tab 2: biggest files per selected drive. Ticked files go to the Recycle Bin only (never permanent).</summary>
    public class LargeFilesViewModel : TabViewModel
    {
        ObservableCollection<FileRow> _rows = new ObservableCollection<FileRow>();
        public ObservableCollection<FileRow> Rows { get { return _rows; } private set { Set(ref _rows, value); } }
        public RelayCommand ScanCommand { get; private set; }
        public RelayCommand RecycleCommand { get; private set; }
        public RelayCommand OpenCommand { get; private set; }
        int _topN; public int TopN { get { return _topN; } set { if (Set(ref _topN, Math.Max(1, Math.Min(5000, value)))) { var s = CleanPC.Pro.Settings.Current; s.LargeTopN = _topN; s.Save(); } } }
        string _sum = ""; public string Summary { get { return _sum; } private set { Set(ref _sum, value); } }

        public LargeFilesViewModel(MainViewModel main) : base(main)
        {
            _topN = CleanPC.Pro.Settings.Current.LargeTopN;
            ScanCommand = new RelayCommand(() => { var t = ScanAsync(); }, () => IsIdle);
            RecycleCommand = new RelayCommand(() => { var t = RecycleAsync(); }, () => IsIdle && Rows.Any(r => r.IsChecked));
            OpenCommand = new RelayCommand(p => { var r = p as FileRow; if (r != null) Dialogs.Reveal(r.Path); });
        }

        Task ScanAsync()
        {
            return RunAsync("Looking for the biggest files...", async (ct, prog) =>
            {
                var drives = Main.SelectedDrives; if (drives.Count == 0) { Dialogs.Info("Large files", "Tick at least one drive."); return; }
                int n = TopN;
                var list = await Task.Run(() => LargeFileScanner.Scan(drives, n, prog, ct), ct);
                Rows = new ObservableCollection<FileRow>(list);
                foreach (var r in Rows) r.PropertyChanged += (s, e) => { if (e.PropertyName == "IsChecked") UpdateSummary(); };
                UpdateSummary();
                Status = "Found the " + Rows.Count + " biggest files on " + string.Join(", ", drives) + ".";
            });
        }

        void UpdateSummary()
        {
            var t = Rows.Where(r => r.IsChecked).ToList();
            Summary = Rows.Count + " files, " + Fmt.Size(Rows.Sum(r => r.Size)) + " in total.   Ticked: " + t.Count + " (" + Fmt.Size(t.Sum(r => r.Size)) + ")";
        }

        Task RecycleAsync()
        {
            var sel = Rows.Where(r => r.IsChecked).ToList();
            if (sel.Count == 0) return Task.FromResult(0);
            if (!Dialogs.Confirm("Send to Recycle Bin", sel.Count + " file(s), " + Fmt.Size(sel.Sum(r => r.Size)) + ", will be sent to the Recycle Bin (you can restore them from there):\n\n" +
                string.Join("\n", sel.Take(12).Select(r => " - " + r.Path)) + (sel.Count > 12 ? "\n ... and " + (sel.Count - 12) + " more" : ""), "Send to Recycle Bin", "Cancel")) return Task.FromResult(0);
            return RunAsync("Sending to the Recycle Bin...", async (ct, prog) =>
            {
                int ok = 0; long freed = 0;
                foreach (var r in sel)
                {
                    if (ct.IsCancellationRequested) break;
                    prog.Report("Recycling " + r.Name);
                    string err = await Task.Run(() => CleanOps.RecycleUserFile(r.Path));
                    if (err == null) { ok++; freed += r.Size; Rows.Remove(r); Log.Write("RECYCLED " + r.Path); }
                    else { r.Status = "Not recycled: " + err; r.IsChecked = false; Log.Write("SKIP " + r.Path + ": " + err); }
                }
                UpdateSummary(); Main.RefreshDrives();
                Status = "Sent " + ok + " file(s) (" + Fmt.Size(freed) + ") to the Recycle Bin." + (ok < sel.Count ? " " + (sel.Count - ok) + " not moved - see the Status column." : "");
                SpaceHistory.Record("clean");
            });
        }
    }

    /// <summary>Tab 3: duplicate files. NEVER auto-selects; at least one copy per group is always kept; Recycle Bin only.</summary>
    public class DuplicatesViewModel : TabViewModel
    {
        ObservableCollection<FileRow> _rows = new ObservableCollection<FileRow>();
        public ObservableCollection<FileRow> Rows { get { return _rows; } private set { Set(ref _rows, value); } }
        public RelayCommand ScanCommand { get; private set; }
        public RelayCommand RecycleCommand { get; private set; }
        public RelayCommand OpenCommand { get; private set; }
        public RelayCommand ClearCommand { get; private set; }
        public RelayCommand AddFolderCommand { get; private set; }
        public RelayCommand ClearFoldersCommand { get; private set; }
        public RelayCommand KeepFolderCommand { get; private set; }
        public RelayCommand RemoveFolderCommand { get; private set; }
        readonly List<string> _folders = new List<string>();
        string _foldersText = "Scanning: the ticked drives. Add folders to scan only those."; public string FoldersText { get { return _foldersText; } private set { Set(ref _foldersText, value); } }
        int _min; public int MinMB { get { return _min; } set { if (Set(ref _min, Math.Max(0, Math.Min(100000, value)))) { var s = CleanPC.Pro.Settings.Current; s.DuplicateMinMB = _min; s.Save(); } } }
        string _sum = "Duplicates are never selected for you - tick the copies you want to remove. At least one copy in every group is always kept."; public string Summary { get { return _sum; } private set { Set(ref _sum, value); } }

        public DuplicatesViewModel(MainViewModel main) : base(main)
        {
            _min = CleanPC.Pro.Settings.Current.DuplicateMinMB;
            ScanCommand = new RelayCommand(() => { var t = ScanAsync(); }, () => IsIdle);
            RecycleCommand = new RelayCommand(() => { var t = RecycleAsync(); }, () => IsIdle && Rows.Any(r => r.IsChecked));
            OpenCommand = new RelayCommand(p => { var r = p as FileRow; if (r != null) Dialogs.Reveal(r.Path); });
            ClearCommand = new RelayCommand(() => { foreach (var r in Rows) r.IsChecked = false; });
            AddFolderCommand = new RelayCommand(() =>
            {
                string f = Dialogs.PickFolder("Choose a folder to search for duplicates (add several to compare across folders)");
                if (f == null) return;
                f = f.TrimEnd('\\');
                if (!_folders.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) _folders.Add(f);
                ShowFolders();
            }, () => IsIdle);
            ClearFoldersCommand = new RelayCommand(() => { _folders.Clear(); ShowFolders(); }, () => IsIdle);
            KeepFolderCommand = new RelayCommand(() => { PickRule(true); }, () => IsIdle && Rows.Count > 0);
            RemoveFolderCommand = new RelayCommand(() => { PickRule(false); }, () => IsIdle && Rows.Count > 0);
        }

        void ShowFolders()
        {
            FoldersText = _folders.Count == 0 ? "Scanning: the ticked drives. Add folders to scan only those."
                : "Scanning only: " + string.Join("  |  ", _folders);
        }

        static bool InFolder(string path, string folder)
        {
            string f = folder.TrimEnd('\\') + "\\";
            return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
        }

        // keep=true: tick copies OUTSIDE the folder where the group also has a copy inside it (the folder's copy is kept).
        // keep=false: tick copies INSIDE the folder where the group also has a copy outside it.
        // A group is only touched when at least one copy stays, so the last copy is never ticked.
        void PickRule(bool keep)
        {
            string f = Dialogs.PickFolder(keep ? "Choose the folder whose copies you want to KEEP" : "Choose the folder to REMOVE copies from");
            if (f == null) return;
            int groups = 0, ticked = 0;
            foreach (var g in Rows.GroupBy(r => r.Group))
            {
                var inside = g.Where(r => InFolder(r.Path, f)).ToList();
                var outside = g.Where(r => !InFolder(r.Path, f)).ToList();
                if (inside.Count == 0 || outside.Count == 0) continue;
                groups++;
                foreach (var r in (keep ? outside : inside)) { r.IsChecked = true; ticked++; }
            }
            Status = ticked == 0 ? "No group has copies both inside and outside that folder; nothing ticked."
                : "Ticked " + ticked + " copy/copies in " + groups + " group(s). Review the list, then send to the Recycle Bin.";
        }

        Task ScanAsync()
        {
            return RunAsync("Looking for duplicates (size, then partial hash, then SHA-256)...", async (ct, prog) =>
            {
                var drives = Main.SelectedDrives;
                if (_folders.Count == 0 && drives.Count == 0) { Dialogs.Info("Duplicates", "Tick at least one drive, or add a folder."); return; }
                long min = Math.Max(1, (long)MinMB) * 1024 * 1024; if (MinMB == 0) min = 1;
                var roots = _folders.ToList();
                var groups = await Task.Run(() => roots.Count > 0 ? DuplicateFinder.FindIn(roots, min, prog, ct) : DuplicateFinder.Find(drives, min, prog, ct), ct);
                var list = new List<FileRow>(); int gi = 0; long wasted = 0;
                foreach (var g in groups.OrderByDescending(g => g.Count * 1L * new FileInfo(g[0]).Length))
                {
                    gi++;
                    var files = g.Select(p => new FileInfo(p)).OrderBy(f => f.LastWriteTime).ToList();
                    wasted += files[0].Length * (files.Count - 1);
                    foreach (var f in files) list.Add(new FileRow { Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime, Group = gi });
                }
                Rows = new ObservableCollection<FileRow>(list);
                foreach (var r in Rows) r.PropertyChanged += (s, e) => { if (e.PropertyName == "IsChecked") UpdateSummary(gi, wasted); };
                UpdateSummary(gi, wasted);
                Status = gi + " duplicate group(s) found.";
            });
        }

        void UpdateSummary(int groups, long wasted)
        {
            var t = Rows.Where(r => r.IsChecked).ToList();
            Summary = groups + " group(s), " + Fmt.Size(wasted) + " reclaimable if all but one copy is removed.   Ticked: " + t.Count + " file(s), " + Fmt.Size(t.Sum(r => r.Size)) + ".  Nothing is ticked unless you use a keep/remove folder button; one copy per group is always kept.";
        }

        Task RecycleAsync()
        {
            var sel = Rows.Where(r => r.IsChecked).ToList();
            if (sel.Count == 0) return Task.FromResult(0);
            // keep at least one copy of every group
            var blocked = new List<int>();
            foreach (var g in sel.GroupBy(r => r.Group))
                if (g.Count() >= Rows.Count(r => r.Group == g.Key)) blocked.Add(g.Key);
            if (blocked.Count > 0)
            {
                Dialogs.Info("Keep at least one copy", "In group(s) " + string.Join(", ", blocked.Select(b => "#" + b)) + " every copy is ticked. Untick at least one copy per group - this tool will not delete the last remaining copy.");
                return Task.FromResult(0);
            }
            if (!Dialogs.Confirm("Send duplicates to Recycle Bin", sel.Count + " duplicate file(s), " + Fmt.Size(sel.Sum(r => r.Size)) + ", will be sent to the Recycle Bin:\n\n" +
                string.Join("\n", sel.Take(12).Select(r => " - " + r.Path)) + (sel.Count > 12 ? "\n ... and " + (sel.Count - 12) + " more" : ""), "Send to Recycle Bin", "Cancel")) return Task.FromResult(0);
            return RunAsync("Sending duplicates to the Recycle Bin...", async (ct, prog) =>
            {
                int ok = 0; long freed = 0;
                foreach (var r in sel)
                {
                    if (ct.IsCancellationRequested) break;
                    prog.Report("Recycling " + r.Name);
                    string err = await Task.Run(() => CleanOps.RecycleUserFile(r.Path));
                    if (err == null) { ok++; freed += r.Size; Rows.Remove(r); Log.Write("RECYCLED duplicate " + r.Path); }
                    else { r.Status = "Not recycled: " + err; r.IsChecked = false; Log.Write("SKIP " + r.Path + ": " + err); }
                }
                Main.RefreshDrives();
                Status = "Sent " + ok + " duplicate(s) (" + Fmt.Size(freed) + ") to the Recycle Bin.";
                SpaceHistory.Record("clean");
            });
        }
    }

    /// <summary>Tab 4: stale node_modules / target / .venv / __pycache__ / .gradle / build folders. Regenerable only; unticked by default.</summary>
    public class DevProjectsViewModel : TabViewModel
    {
        ObservableCollection<StaleProject> _rows = new ObservableCollection<StaleProject>();
        public ObservableCollection<StaleProject> Rows { get { return _rows; } private set { Set(ref _rows, value); } }
        public RelayCommand ScanCommand { get; private set; }
        public RelayCommand DeleteCommand { get; private set; }
        public RelayCommand OpenCommand { get; private set; }
        int _days; public int Days { get { return _days; } set { if (Set(ref _days, Math.Max(1, Math.Min(3650, value)))) { var s = CleanPC.Pro.Settings.Current; s.StaleDays = _days; s.Save(); } } }
        string _sum = ""; public string Summary { get { return _sum; } private set { Set(ref _sum, value); } }

        public DevProjectsViewModel(MainViewModel main) : base(main)
        {
            _days = CleanPC.Pro.Settings.Current.StaleDays;
            ScanCommand = new RelayCommand(() => { var t = ScanAsync(); }, () => IsIdle);
            DeleteCommand = new RelayCommand(() => { var t = DeleteAsync(); }, () => IsIdle && Rows.Any(r => r.IsChecked));
            OpenCommand = new RelayCommand(p => { var r = p as StaleProject; if (r != null) Dialogs.Reveal(r.Path); });
        }

        Task ScanAsync()
        {
            return RunAsync("Looking for stale dev folders...", async (ct, prog) =>
            {
                var drives = Main.SelectedDrives; if (drives.Count == 0) { Dialogs.Info("Dev projects", "Tick at least one drive."); return; }
                int days = Days;
                var roots = drives.Select(d => d + "\\").ToList();
                var list = await Task.Run(() => StaleProjectScanner.Scan(roots, days, prog, ct), ct);
                Rows = new ObservableCollection<StaleProject>(list);
                foreach (var r in Rows) r.PropertyChanged += (s, e) => { if (e.PropertyName == "IsChecked") Update(); };
                Update();
                Status = Rows.Count + " stale folder(s) found.";
            });
        }

        void Update()
        {
            var t = Rows.Where(r => r.IsChecked).ToList();
            Summary = Rows.Count + " regenerable folder(s), " + Fmt.Size(Rows.Sum(r => r.Size)) + ". Their projects were not touched for " + Days + "+ days. Ticked: " + t.Count + " (" + Fmt.Size(t.Sum(r => r.Size)) + "). All start unticked.";
        }

        Task DeleteAsync()
        {
            var sel = Rows.Where(r => r.IsChecked).ToList();
            if (sel.Count == 0) return Task.FromResult(0);
            if (!Dialogs.Confirm("Delete regenerable folders", sel.Count + " folder(s), " + Fmt.Size(sel.Sum(r => r.Size)) + ", will be deleted PERMANENTLY. They can be regenerated (npm install, cargo build, ...) but that needs time and sometimes network:\n\n" +
                string.Join("\n", sel.Take(12).Select(r => " - " + r.Path)) + (sel.Count > 12 ? "\n ... and " + (sel.Count - 12) + " more" : ""), "Delete", "Cancel")) return Task.FromResult(0);
            return RunAsync("Deleting stale dev folders...", async (ct, prog) =>
            {
                long freed = 0; int ok = 0;
                foreach (var r in sel)
                {
                    if (ct.IsCancellationRequested) break;
                    prog.Report("Deleting " + r.Path);
                    var skips = new List<string>();
                    long f = await Task.Run(() =>
                    {
                        string name = Path.GetFileName(r.Path), parent = Path.GetDirectoryName(r.Path);
                        if (!StaleProjectScanner.ArtifactNames.Contains(name) || StaleProjectScanner.Classify(parent, name) == null) { skips.Add("SKIP " + r.Path + ": no longer recognised as a regenerable folder"); return 0L; }
                        return CleanOps.DeleteTree(r.Path, skips);
                    });
                    foreach (var s in skips) Log.Write(s);
                    freed += f;
                    if (skips.Count == 0) { ok++; Rows.Remove(r); Log.Write("DELETED dev folder " + r.Path + " " + Fmt.Size(f)); }
                    else { r.Status = "Partly skipped: " + Fmt.Mid(skips[0], 120); r.IsChecked = false; }
                }
                CleanHistory.Add(new CleanRecord { Time = DateTime.Now.ToString("s"), Mode = "gui", Drives = string.Join(",", Main.SelectedDrives), Items = ok, Freed = freed, Detail = "dev projects" });
                Main.RefreshDrives(); Main.Settings.ReloadHistory(); SpaceHistory.Record("clean");
                Update();
                Status = "Freed about " + Fmt.Size(freed) + " (" + ok + " folder(s) removed).";
            });
        }
    }
}
