using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CleanPC.Pro.Ui
{
    public partial class MainWindow : Window
    {
        readonly MainViewModel _vm;

        public MainWindow(CliArgs args)
        {
            InitializeComponent();
            Title = AppInfo.Name + " " + AppInfo.InformationalVersion;
            try
            {
                using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(AppInfo.ExePath))
                    Icon = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch (Exception e) { Log.Error("window icon", e); }
            _vm = new MainViewModel();
            DataContext = _vm;
            int tab;
            if (args != null && int.TryParse(args.Tab, out tab) && tab >= 0 && tab < Tabs.Items.Count) Tabs.SelectedIndex = tab;
            Loaded += (s, e) => { _vm.Start(); if (args != null && args.AutoScan) AutoScan(); if (args != null && !string.IsNullOrEmpty(args.DemoLockDir)) _vm.Cleanup.DemoClean(args.DemoLockDir); };
            Closing += OnClosing;
        }

        // hidden test hook (--autoscan): runs the other tabs' scans so screenshots show real rows
        void AutoScan()
        {
            _vm.Large.ScanCommand.Execute(null); _vm.Startup.RefreshCommand.Execute(null); _vm.Security.RunCommand.Execute(null);
            _vm.Profiles.ScanCommand.Execute(null); _vm.Dev.ScanCommand.Execute(null);
        }

        /// <summary>Used by the post-clean popup: bring the window forward on the Cleanup tab (where the Locked files panel is).</summary>
        public void ShowCleanupTab()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Tabs.SelectedIndex = 0; Activate();
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (_vm.Cleanup.IsCleaning)
            {
                e.Cancel = true;
                _vm.Cleanup.Cancel();
                _vm.Status = "Cleaning is running - it will stop after the current item; close the window again afterwards.";
                return;
            }
            _vm.StopCommand.Execute(null);
        }

        static bool IsInside<T>(object src) where T : DependencyObject
        {
            var d = src as DependencyObject;
            while (d != null)
            {
                if (d is T) return true;
                if (d is DataGridRow || d is DataGrid) return false;
                d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var g = sender as DataGrid;
            if (g == null || IsInside<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource) || IsInside<System.Windows.Controls.Primitives.DataGridColumnHeader>(e.OriginalSource)) return;
            var t = g.SelectedItem as Target;
            if (t != null) _vm.Cleanup.ShowDetails(t);
        }

        void Grid_KeyDown(object sender, KeyEventArgs e)
        {
            var g = sender as DataGrid;
            if (g == null || e.OriginalSource is TextBox) return;
            if (e.Key == Key.Space && !(e.OriginalSource is CheckBox))
            {
                var sel = g.SelectedItems.OfType<ITickable>().ToList();
                if (sel.Count == 0) return;
                bool v = !sel[0].IsChecked;
                foreach (var s in sel) s.IsChecked = v;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && g.SelectedItem is Target)
            {
                _vm.Cleanup.ShowDetails((Target)g.SelectedItem); e.Handled = true;
            }
        }

        void StartupGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.Startup.SelectedItems = StartupGrid.SelectedItems.Cast<object>().ToList();
            _vm.Startup.Selected = StartupGrid.SelectedItem as StartupEntry;
        }
    }
}
