using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CleanPC.Pro.Ui
{
    /// <summary>User-clicked update check for the Pro channel and the Standard (lighter) edition. No automatic network calls anywhere.</summary>
    public class UpdateWindow : Window
    {
        class Row
        {
            public string Repo, Asset, Current; public bool IsSelf;
            public TextBlock Result; public Button Check, Download, Page; public ReleaseInfo Release;
        }

        public UpdateWindow()
        {
            Title = "Check for updates"; Width = 640; SizeToContent = SizeToContent.Height; MinWidth = 460; MaxHeight = SystemParameters.WorkArea.Height * 0.92;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "BgBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(Dialogs.Text("Nothing is contacted until you press a Check button. Updates are never installed automatically: the new file is only downloaded, and you decide when (and whether) to run it."));
            root.Children.Add(Section("PC Cache Cleaner Pro  (this edition, " + AppInfo.InformationalVersion + ")", "Full-featured edition: " + AppInfo.ProRepo, AppInfo.ProRepo, AppInfo.ProAsset, AppInfo.InformationalVersion, true));
            root.Children.Add(Section("PC Cache Cleaner - Standard edition  (the lighter edition)", "Small PowerShell-based tool, a separate product: " + AppInfo.StandardRepo, AppInfo.StandardRepo, AppInfo.StandardAsset, "0.0.0", false));
            var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            close.Click += (s, e) => Close();
            root.Children.Add(close);
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        UIElement Section(string title, string sub, string repo, string asset, string current, bool self)
        {
            var r = new Row { Repo = repo, Asset = asset, Current = current, IsSelf = self };
            var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 12) };
            box.SetResourceReference(Border.BorderBrushProperty, "BorderBrushX");
            var sp = new StackPanel();
            var h = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(h);
            var s2 = Dialogs.Text(sub, true); sp.Children.Add(s2);
            r.Result = new TextBlock { Text = "Not checked yet.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(r.Result);
            var row = new WrapPanel();
            r.Check = new Button { Content = "Check now", MinWidth = 100 };
            r.Download = new Button { Content = "Download...", Visibility = Visibility.Collapsed };
            r.Page = new Button { Content = "Open release page", Visibility = Visibility.Collapsed };
            r.Check.Click += async (s, e) => await DoCheck(r);
            r.Download.Click += (s, e) => { var w = new DownloadWindow(r.Release, r.IsSelf) { Owner = this }; w.ShowDialog(); };
            r.Page.Click += (s, e) => Dialogs.OpenUrl(r.Release.HtmlUrl);
            row.Children.Add(r.Check); row.Children.Add(r.Download); row.Children.Add(r.Page);
            sp.Children.Add(row);
            box.Child = sp;
            return box;
        }

        async Task DoCheck(Row r)
        {
            r.Check.IsEnabled = false; r.Result.Text = "Contacting GitHub..."; r.Download.Visibility = Visibility.Collapsed; r.Page.Visibility = Visibility.Collapsed;
            try
            {
                var res = await Task.Run(() => UpdateChecker.Check(r.Repo, r.Asset, r.Current));
                r.Release = res.Release;
                string extra = res.Release != null ? "\nLatest: " + res.Release.Tag + (res.Release.Published.Length >= 10 ? " (" + res.Release.Published.Substring(0, 10) + ")" : "") + ", " + r.Asset + " " + Fmt.Size(res.Release.AssetSize) : "";
                if (!r.IsSelf && res.Release != null) res.Message = "Latest Standard edition release: " + res.Release.Tag + ". (This is a different, lighter product - not an update of Pro.)";
                r.Result.Text = res.Message + extra;
                if (res.Release != null && !string.IsNullOrEmpty(res.Release.AssetUrl))
                {
                    r.Page.Visibility = Visibility.Visible;
                    if (res.Status == CheckStatus.UpdateAvailable) r.Download.Visibility = Visibility.Visible;
                }
            }
            catch (Exception e) { Log.Error("update check", e); r.Result.Text = "Update check failed: " + e.Message; }
            finally { r.Check.IsEnabled = true; }
        }
    }

    /// <summary>Download dialog: same drive as the program (default) or another drive/folder; progress, cancel, .part then rename, size check.</summary>
    public class DownloadWindow : Window
    {
        readonly ReleaseInfo _rel; readonly bool _self;
        readonly RadioButton _same, _other; readonly TextBox _path; readonly Button _browse, _go, _cancel;
        readonly TextBlock _free, _status; readonly ProgressBar _bar;
        CancellationTokenSource _cts;

        public DownloadWindow(ReleaseInfo rel, bool self)
        {
            _rel = rel; _self = self;
            Title = "Download " + rel.AssetName; Width = 600; SizeToContent = SizeToContent.Height; MinWidth = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            MaxHeight = SystemParameters.WorkArea.Height * 0.92;
            SetResourceReference(BackgroundProperty, "BgBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
            var sp = new StackPanel { Margin = new Thickness(18) };
            sp.Children.Add(Dialogs.Text("Download " + rel.AssetName + " " + rel.Tag + " (" + Fmt.Size(rel.AssetSize) + ")\nThe file is saved as a .part first and renamed only when the size matches. It is never run and never replaces the program you are using."));
            _same = new RadioButton { IsChecked = true, GroupName = "dest", Content = new TextBlock { Text = "Same drive as this program (default): " + AppInfo.ExeDir, TextWrapping = TextWrapping.Wrap } };
            _other = new RadioButton { GroupName = "dest", Content = "Another drive / folder" };
            _path = new TextBox { IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
            _browse = new Button { Content = "Browse...", IsEnabled = false, Margin = new Thickness(0) };
            var g = new Grid { Margin = new Thickness(22, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(_path); Grid.SetColumn(_browse, 1); g.Children.Add(_browse);
            _free = new TextBlock { Margin = new Thickness(22, 2, 0, 10), TextWrapping = TextWrapping.Wrap }; _free.SetResourceReference(StyleProperty, "Dim");
            sp.Children.Add(_same); sp.Children.Add(_other); sp.Children.Add(g); sp.Children.Add(_free);
            _bar = new ProgressBar { Height = 14, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 0, 0, 6) };
            _status = new TextBlock { Text = "Ready.", Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(_bar); sp.Children.Add(_status);
            var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            _go = new Button { Content = "Download" }; _go.SetResourceReference(StyleProperty, "Primary");
            _cancel = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(0) };
            row.Children.Add(_go); row.Children.Add(_cancel);
            sp.Children.Add(row);
            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            RoutedEventHandler upd = (s, e) => { _path.IsEnabled = _other.IsChecked == true; _browse.IsEnabled = _path.IsEnabled; UpdateFree(); };
            _same.Checked += upd; _other.Checked += upd; _path.TextChanged += (s, e) => UpdateFree();
            _browse.Click += (s, e) => { string f = Dialogs.PickFolder("Choose where to save " + _rel.AssetName); if (f != null) _path.Text = f; };
            _go.Click += async (s, e) => await Go();
            _cancel.Click += (s, e) => { if (_cts != null) _cts.Cancel(); };
            Closing += (s, e) => { if (_cts != null) _cts.Cancel(); };
            UpdateFree();
        }

        string Folder { get { return _other.IsChecked == true ? _path.Text.Trim() : AppInfo.ExeDir; } }

        void UpdateFree()
        {
            string f = Folder;
            if (string.IsNullOrEmpty(f)) { _free.Text = "Choose a folder."; return; }
            long free = Downloader.FreeBytes(f);
            _free.Text = free < 0 ? "Free space: unknown (check the folder path)." : "Free space there: " + Fmt.Size(free) + (_rel.AssetSize > 0 && free < _rel.AssetSize + (16L << 20) ? "  - NOT ENOUGH for this download." : "");
        }

        async Task Go()
        {
            string folder = Folder;
            if (string.IsNullOrEmpty(folder)) { _status.Text = "Choose a folder first."; return; }
            _go.IsEnabled = false; _same.IsEnabled = false; _other.IsEnabled = false; _browse.IsEnabled = false; _path.IsEnabled = false; _cancel.Content = "Cancel";
            _cts = new CancellationTokenSource();
            string name = _self ? "CleanPC-Pro-" + _rel.Tag.TrimStart('v', 'V') + ".exe" : _rel.AssetName;   // never the running exe's own name
            try
            {
                _status.Text = "Downloading...";
                var ui = new Progress<Tuple<long, long>>(t => { _bar.Value = t.Item2 > 0 ? t.Item1 * 100.0 / t.Item2 : 0; _status.Text = "Downloading... " + Fmt.Size(t.Item1) + " of " + Fmt.Size(t.Item2); });
                var ct = _cts.Token; string url = _rel.AssetUrl; long size = _rel.AssetSize;
                string dest = await Task.Run(() => Downloader.Download(url, folder, name, size, (a, b) => ((IProgress<Tuple<long, long>>)ui).Report(Tuple.Create(a, b)), ct));
                _bar.Value = 100;
                _status.Text = "Saved: " + dest + "\nSize verified. It has NOT been run - start it yourself when you are ready.";
                if (_self)
                {
                    int c = Dialogs.Ask("Old installer", "Do you want to keep the old installer or delete it?\n\nOld installer: " + AppInfo.ExePath + "\n\n'Delete' removes only that single .exe file, and only the next time you start the NEW version (a running program cannot delete itself). Folders are never deleted.", "Keep", "Delete");
                    if (c == 1)
                    {
                        bool ok = Downloader.MarkForDeletion(AppInfo.ExePath);
                        _status.Text += ok ? "\nThe old installer will be deleted the next time you start the new version." : "\nThe old installer could not be marked for deletion (it stays).";
                    }
                    else _status.Text += "\nThe old installer was kept.";
                }
            }
            catch (OperationCanceledException) { _status.Text = "Cancelled. The partial download was removed."; _bar.Value = 0; }
            catch (Exception e) { Log.Error("download", e); _status.Text = "Download failed: " + e.Message; _bar.Value = 0; }
            finally
            {
                _cts.Dispose(); _cts = null; _cancel.Content = "Close"; _go.IsEnabled = true; _same.IsEnabled = true; _other.IsEnabled = true;
                _path.IsEnabled = _other.IsChecked == true; _browse.IsEnabled = _path.IsEnabled; UpdateFree();
            }
        }
    }
}
