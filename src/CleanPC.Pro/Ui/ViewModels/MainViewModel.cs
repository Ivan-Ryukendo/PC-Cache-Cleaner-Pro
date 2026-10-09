using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CleanPC.Pro.Ui
{
    /// <summary>Base for every tab: one cancellable background job at a time, progress text, error capture (never crashes the app).</summary>
    public abstract class TabViewModel : ObservableObject
    {
        protected readonly MainViewModel Main;
        CancellationTokenSource _cts;
        protected TabViewModel(MainViewModel main) { Main = main; }

        bool _busy; public bool IsBusy { get { return _busy; } private set { if (Set(ref _busy, value)) { Raise("IsIdle"); Main.NotifyBusy(); } } }
        public bool IsIdle { get { return !_busy; } }
        string _status = ""; public string Status { get { return _status; } set { if (Set(ref _status, value) && Main != null) Main.Status = value; } }

        public void Cancel() { var c = _cts; if (c != null) { try { c.Cancel(); } catch { } } }

        protected async Task RunAsync(string startMsg, Func<CancellationToken, IProgress<string>, Task> work)
        {
            if (_busy) return;
            IsBusy = true;
            _cts = new CancellationTokenSource();
            Status = startMsg; Main.Status = startMsg;
            var prog = new Progress<string>(m => { Status = m; Main.Status = m; });
            try { await work(_cts.Token, prog); }
            catch (OperationCanceledException) { Status = "Stopped."; Main.Status = "Stopped."; }
            catch (Exception e)
            {
                Log.Error(GetType().Name, e);
                Status = "Failed: " + e.Message; Main.Status = Status;
                Main.ShowError(GetType().Name.Replace("ViewModel", "") + ": " + e.Message);
            }
            finally { var c = _cts; _cts = null; IsBusy = false; if (c != null) c.Dispose(); }
        }
    }

    public class MainViewModel : ObservableObject
    {
        public ObservableCollection<DriveEntry> Drives { get; private set; }
        public CleanupViewModel Cleanup { get; private set; }
        public LargeFilesViewModel Large { get; private set; }
        public DuplicatesViewModel Dups { get; private set; }
        public DevProjectsViewModel Dev { get; private set; }
        public StartupViewModel Startup { get; private set; }
        public SpaceViewModel Space { get; private set; }
        public SecurityViewModel Security { get; private set; }
        public ReportViewModel Report { get; private set; }
        public ProfilesViewModel Profiles { get; private set; }
        public SettingsViewModel Settings { get; private set; }
        public LockedViewModel Locks { get; private set; }
        public OwnersViewModel Owners { get; private set; }
        readonly List<TabViewModel> _tabs = new List<TabViewModel>();

        public string AppTitle { get { return AppInfo.Name; } }
        public string VersionText { get { return "v" + AppInfo.InformationalVersion; } }
        public bool IsAdmin { get { return AppInfo.IsAdmin; } }
        public string AdminText { get { return AppInfo.IsAdmin ? "Administrator" : "Not elevated - Windows\\Temp, Windows Update cache and shadow copies are skipped"; } }

        string _status = "Ready."; public string Status { get { return _status; } set { Set(ref _status, value); } }
        string _lowSpace = ""; public string LowSpaceBanner { get { return _lowSpace; } private set { if (Set(ref _lowSpace, value)) Raise("HasLowSpace"); } }
        public bool HasLowSpace { get { return _lowSpace.Length > 0; } }
        string _drop = ""; public string SpaceDropBanner { get { return _drop; } private set { if (Set(ref _drop, value)) Raise("HasSpaceDrop"); } }
        public bool HasSpaceDrop { get { return _drop.Length > 0; } }
        string _err = ""; public string ErrorBanner { get { return _err; } private set { if (Set(ref _err, value)) Raise("HasError"); } }
        public bool HasError { get { return _err.Length > 0; } }
        public bool AnyBusy { get { return _tabs.Any(t => t.IsBusy); } }
        public bool AllIdle { get { return !AnyBusy; } }

        public RelayCommand RescanCommand { get; private set; }
        public RelayCommand StopCommand { get; private set; }
        public RelayCommand DismissErrorCommand { get; private set; }
        public RelayCommand DismissDropCommand { get; private set; }
        public RelayCommand UpdatesCommand { get; private set; }

        bool _all;
        public bool AllDrives
        {
            get { return _all; }
            set { if (Set(ref _all, value)) { _suppress = true; foreach (var d in Drives) d.IsSelected = value; _suppress = false; UpdateLowSpace(); } }
        }
        bool _suppress;

        public MainViewModel()
        {
            Drives = new ObservableCollection<DriveEntry>(Drives_Fixed());
            foreach (var d in Drives)
            {
                d.IsSelected = d.Letter == AppInfo.SystemDrive;
                d.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName != "IsSelected" || _suppress) return;
                    _suppress = true; _all = Drives.All(x => x.IsSelected); Raise("AllDrives"); _suppress = false;
                    UpdateLowSpace();
                };
            }
            if (!Drives.Any(d => d.IsSelected) && Drives.Count > 0) Drives[0].IsSelected = true;
            Cleanup = new CleanupViewModel(this); Large = new LargeFilesViewModel(this); Dups = new DuplicatesViewModel(this); Dev = new DevProjectsViewModel(this);
            Startup = new StartupViewModel(this); Space = new SpaceViewModel(this); Security = new SecurityViewModel(this); Report = new ReportViewModel(this);
            Profiles = new ProfilesViewModel(this); Settings = new SettingsViewModel(this);
            Locks = new LockedViewModel(this); Owners = new OwnersViewModel(this);
            _tabs.AddRange(new TabViewModel[] { Cleanup, Large, Dups, Dev, Startup, Space, Security, Report, Profiles, Settings, Locks, Owners });
            RescanCommand = new RelayCommand(() => { var t = Cleanup.ScanAsync(); }, () => AllIdle);
            StopCommand = new RelayCommand(() => { foreach (var t in _tabs) t.Cancel(); Status = "Stopping..."; });
            DismissErrorCommand = new RelayCommand(() => ErrorBanner = "");
            DismissDropCommand = new RelayCommand(() => SpaceDropBanner = "");
            UpdatesCommand = new RelayCommand(() => { var w = new UpdateWindow { Owner = Application.Current.MainWindow }; w.ShowDialog(); });
            RelayCommand.ShowErrorHook = e => ShowError(e.Message);
            UpdateLowSpace();
        }

        static IEnumerable<DriveEntry> Drives_Fixed() { return CleanPC.Pro.Drives.Fixed(); }

        public IList<string> SelectedDrives { get { return Drives.Where(d => d.IsSelected).Select(d => d.Letter).ToList(); } }

        public void NotifyBusy() { Raise("AnyBusy"); Raise("AllIdle"); CommandManagerInvalidate(); }
        static void CommandManagerInvalidate() { System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }

        public void ShowError(string msg) { ErrorBanner = msg; }

        public void RefreshDrives()
        {
            var fresh = CleanPC.Pro.Drives.Fixed();
            foreach (var d in Drives)
            {
                var f = fresh.FirstOrDefault(x => x.Letter == d.Letter);
                if (f == null) continue;
                d.Free = f.Free; d.Size = f.Size;
                d.RaiseText();
            }
            UpdateLowSpace();
        }

        public void UpdateLowSpace()
        {
            var low = Drives.Where(d => d.IsSelected && d.Free >= 0 && d.Free < 5L * 1073741824).ToList();
            LowSpaceBanner = low.Count == 0 ? "" : "Low free space: " + string.Join(", ", low.Select(d => d.Letter + " has only " + Fmt.Size(d.Free) + " free")) + ". Cleaning can help, but consider moving large files off the drive too.";
        }

        /// <summary>Called once from the window: record free space, compare with the previous run, consume the old-installer marker, first scan.</summary>
        public void Start()
        {
            try
            {
                var before = SpaceHistory.All();
                SpaceDropBanner = string.Join("  ", SpaceHistory.Warnings(before, CleanPC.Pro.Settings.Current.WarnDropGB));
                SpaceHistory.Record("start");
                Space.Reload();
            }
            catch (Exception e) { Log.Error("start history", e); }
            try
            {
                string gone = Downloader.ConsumeMarker();
                if (gone != null) Status = "Deleted the old installer: " + gone;
            }
            catch (Exception e) { Log.Error("start marker", e); }
            var t = Cleanup.ScanAsync();
        }
    }
}
