using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace CleanPC.Pro.Ui
{
    /// <summary>
    /// "Locked files" panel on the Cleanup tab: which running program holds each file the clean had to skip (Windows Restart
    /// Manager, read-only), an optional per-program "Close this program" (always confirmed by the user, graceful first), and a
    /// retry of ONLY the skipped files through the normal guarded delete.
    /// </summary>
    public class LockedViewModel : TabViewModel
    {
        public ObservableCollection<LockHolder> Holders { get; private set; }
        public RelayCommand CloseCommand { get; private set; }
        public RelayCommand OpenCommand { get; private set; }
        public RelayCommand FilesCommand { get; private set; }
        public RelayCommand RetryCommand { get; private set; }
        public RelayCommand DismissCommand { get; private set; }
        public RelayCommand RefreshCommand { get; private set; }

        List<SkipInfo> _locked = new List<SkipInfo>();
        public bool HasLocks { get { return _locked.Count > 0; } }
        public int LockedCount { get { return _locked.Count; } }
        string _summary = ""; public string Summary { get { return _summary; } private set { Set(ref _summary, value); } }
        string _note = ""; public string Note { get { return _note; } private set { Set(ref _note, value); } }

        public LockedViewModel(MainViewModel main) : base(main)
        {
            Holders = new ObservableCollection<LockHolder>();
            CloseCommand = new RelayCommand(p => { var h = p as LockHolder; if (h != null) { var t = CloseAsync(h); } }, p => IsIdle);
            OpenCommand = new RelayCommand(p => { var h = p as LockHolder; if (h != null && !string.IsNullOrEmpty(h.ExePath)) Dialogs.Reveal(h.ExePath); });
            FilesCommand = new RelayCommand(p =>
            {
                var h = p as LockHolder; if (h == null) return;
                Dialogs.ShowPaths("Files held by " + h.Name, h.FileCount + " skipped file(s), about " + Fmt.Size(h.Bytes) + ", held by " + h.Name + " (PID " + h.Pid + "). Nothing is changed from this window.", h.Files.Select(f => f.Path).ToList());
            });
            RetryCommand = new RelayCommand(() => { var t = RetryAsync(); }, () => IsIdle && HasLocks);
            RefreshCommand = new RelayCommand(() => { var t = DiagnoseAsync(); }, () => IsIdle && HasLocks);
            DismissCommand = new RelayCommand(() => { _locked = new List<SkipInfo>(); Holders.Clear(); Note = ""; Changed(); }, () => IsIdle);
        }

        void Changed() { Raise("HasLocks"); Raise("LockedCount"); }

        /// <summary>Called after a clean with the files that were in use. Starts the (read-only) diagnosis.</summary>
        public void Load(List<SkipInfo> locked)
        {
            _locked = locked ?? new List<SkipInfo>();
            Holders.Clear(); Note = "";
            Changed();
            UpdateSummary();
            if (_locked.Count > 0) { var t = DiagnoseAsync(); }
        }

        void UpdateSummary()
        {
            Summary = _locked.Count == 0 ? "" : _locked.Count + " file(s), about " + Fmt.Size(_locked.Sum(x => x.Bytes)) + ", skipped: in use by running programs.";
        }

        Task DiagnoseAsync()
        {
            return RunAsync("Asking Windows which programs hold the locked files...", async (ct, prog) =>
            {
                var snapshot = _locked.ToList();
                var res = await Task.Run(() => RestartManager.Diagnose(snapshot, ct, prog), ct);
                Holders.Clear();
                foreach (var h in res.Holders) { Holders.Add(h); Log.Write("LOCKED-BY " + h.Name + " pid=" + h.Pid + " files=" + h.FileCount + " bytes=" + h.Bytes + (h.ProtectedReason != null ? "  (protected: " + h.ProtectedReason + ")" : "")); }
                var sb = new System.Text.StringBuilder();
                if (res.Holders.Count == 0) sb.Append("No running program holds them any more (or Windows could not tell). Try 'Retry skipped items'. ");
                if (res.NoHolderFound > 0 && res.Holders.Count > 0) sb.Append(res.NoHolderFound + " file(s) are not held by a program Windows can name right now. ");
                if (res.NotAnalysed > 0) sb.Append(res.NotAnalysed + " more file(s) were not analysed (only the first 500 are checked); the totals above cover those checked. ");
                if (res.Error != null) sb.Append("Windows reported: " + res.Error + ". ");
                Note = sb.ToString().Trim();
                Status = _locked.Count + " locked file(s); " + res.Holders.Count + " program(s) hold them.";
            });
        }

        async Task CloseAsync(LockHolder h)
        {
            string why = ProcessGuard.ProtectedReason(h.Pid, h.Name, h.ExePath, h.AppType, h.Service);
            if (why != null) { Dialogs.Info("Not offered", h.Name + " cannot be closed from here: " + why + "."); return; }
            if (!Dialogs.Confirm("Close " + h.Name + "?",
                "Close '" + h.Name + "' (PID " + h.Pid + ")?\n\nIt is holding " + h.FileCount + " skipped file(s), about " + Fmt.Size(h.Bytes) + ".\n\n" +
                "WARNING: any unsaved work in that program can be lost. Windows will be asked to close it politely and this program waits about 5 seconds. Nothing is forced yet.",
                "Ask it to close", "Cancel")) return;
            Log.Write("USER confirmed graceful close of " + h.Name + " pid=" + h.Pid);
            CloseResult r = null;
            await RunAsync("Asking " + h.Name + " to close...", async (ct, prog) => { r = await Task.Run(() => ProcessCloser.Graceful(h)); });
            if (r == null) return;
            if (r.Outcome == CloseOutcome.StillRunning || r.Outcome == CloseOutcome.NoWindow)
            {
                bool force = Dialogs.Confirm("Force end " + h.Name + "?",
                    h.Name + " did not close: " + r.Message + "\n\nYou can end it by force. This is immediate: unsaved data in '" + h.Name + "' is lost and it cannot be undone. Only do this if you are sure.\n\nForce end '" + h.Name + "' (PID " + h.Pid + ")?",
                    "Force end it", "Leave it running");
                if (!force) { Log.Write("USER declined force end of " + h.Name + " pid=" + h.Pid); Note = h.Name + " was left running."; return; }
                Log.Write("USER confirmed FORCE end of " + h.Name + " pid=" + h.Pid);
                await RunAsync("Ending " + h.Name + "...", async (ct, prog) => { r = await Task.Run(() => ProcessCloser.Force(h)); });
            }
            if (r == null) return;
            if (r.Outcome == CloseOutcome.Closed || r.Outcome == CloseOutcome.Gone)
            {
                h.State = "Closed";
                Note = h.Name + " closed. Press 'Retry skipped items' to delete the files it was holding.";
                Status = Note;
            }
            else
            {
                h.State = "";
                Dialogs.Info("Could not close " + h.Name, r.Message);
                Note = "Could not close " + h.Name + ": " + r.Message;
            }
        }

        async Task RetryAsync()
        {
            var items = _locked.ToList();
            SkipList again = null; long freed = 0;
            await RunAsync("Retrying " + items.Count + " skipped file(s)...", async (ct, prog) =>
            {
                again = new SkipList();
                freed = await Task.Run(() => CleanRunner.RetrySkipped(items, again));
            });
            if (again == null) return;
            _locked = again.Items.Where(x => x.Locked).ToList();
            int other = again.Items.Count(x => !x.Locked);
            Log.Write("RETRY done: freed " + Fmt.Size(freed) + ", still locked " + _locked.Count + ", other skips " + other);
            if (freed > 0) { CleanHistory.Add(new CleanRecord { Time = DateTime.Now.ToString("s"), Mode = "retry", Drives = "", Items = items.Count - _locked.Count - other, Freed = freed, Detail = "retry of locked files" }); Main.Space.Reload(); Main.Settings.ReloadHistory(); SpaceHistory.Record("clean"); }
            Main.RefreshDrives();
            Changed(); UpdateSummary();
            string m = "Retry freed " + Fmt.Size(freed) + "." + (_locked.Count > 0 ? " " + _locked.Count + " file(s) are still in use." : " Nothing is locked any more.") + (other > 0 ? " " + other + " other file(s) were skipped (see the log)." : "");
            Status = m; Main.Status = m; Note = m;
            if (_locked.Count > 0) await DiagnoseAsync(); else Holders.Clear();
        }
    }

    /// <summary>"Who is using space" tab: running programs mapped to their install folder and likely data folders. Read-only.</summary>
    public class OwnersViewModel : TabViewModel
    {
        public ObservableCollection<OwnerRow> Rows { get; private set; }
        public RelayCommand ScanCommand { get; private set; }
        public RelayCommand OpenCommand { get; private set; }
        public RelayCommand FoldersCommand { get; private set; }
        string _sum = ""; public string Summary { get { return _sum; } private set { Set(ref _sum, value); } }

        public OwnersViewModel(MainViewModel main) : base(main)
        {
            Rows = new ObservableCollection<OwnerRow>();
            ScanCommand = new RelayCommand(() => { var t = ScanAsync(); }, () => IsIdle);
            OpenCommand = new RelayCommand(p => { var r = p as OwnerRow; if (r != null) Dialogs.Reveal(r.InstallDir); });
            FoldersCommand = new RelayCommand(p =>
            {
                var r = p as OwnerRow; if (r == null) return;
                Dialogs.ShowPaths("Folders for " + r.Name,
                    "Install folder plus per-user/per-machine folders that LIKELY belong to " + r.Name + " (matched by folder/product name, not certain). Read-only.",
                    r.Folders.Select(f => (f.IsInstall ? "[install]  " : "[likely]   ") + f.SizeText.PadLeft(10) + "   " + f.Path).ToList());
            });
        }

        Task ScanAsync()
        {
            return RunAsync("Looking at running programs...", async (ct, prog) =>
            {
                Rows.Clear();
                var rows = await Task.Run(() =>
                {
                    var procs = StorageOwners.RunningProcesses(ct);
                    return StorageOwners.Build(procs, StorageOwners.DefaultContainers(), StorageOwners.DefaultDataRoots(), ct);
                }, ct);
                foreach (var r in rows) Rows.Add(r);
                Summary = rows.Count + " program(s) found; measuring their folders...";
                await Task.Run(() => StorageOwners.SizeAll(rows, ct, prog, null), ct);
                var sorted = rows.OrderByDescending(r => r.Total).ToList();
                Rows.Clear(); foreach (var r in sorted) Rows.Add(r);
                Summary = sorted.Count + " running program(s), " + Fmt.Size(sorted.Sum(r => r.Total)) + " in their install and likely data folders." + (ct.IsCancellationRequested ? "  Stopped early: sizes may be partial." : "");
                Status = Summary;
            });
        }
    }
}
