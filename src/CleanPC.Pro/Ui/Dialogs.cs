using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CleanPC.Pro.Ui
{
    /// <summary>Small code-built dialogs (message with custom buttons, typed confirmation, path list). Themed via the app resources.</summary>
    public static class Dialogs
    {
        static Window Owner { get { return Application.Current == null ? null : Application.Current.MainWindow; } }

        static Window NewWindow(string title, double width)
        {
            var w = new Window
            {
                Title = title, Width = width, SizeToContent = SizeToContent.Height, MinWidth = 380, MaxHeight = SystemParameters.WorkArea.Height * 0.9,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResizeWithGrip
            };
            var o = Owner; if (o != null && o.IsVisible) w.Owner = o; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.SetResourceReference(Control.BackgroundProperty, "BgBrush");
            w.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            return w;
        }

        public static TextBlock Text(string s, bool dim = false)
        {
            var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            if (dim) t.SetResourceReference(FrameworkElement.StyleProperty, "Dim");
            return t;
        }

        /// <summary>Message with custom buttons; returns the index of the clicked button (Esc / close = last button).</summary>
        public static int Ask(string title, string message, params string[] buttons)
        {
            var w = NewWindow(title, 520);
            int result = buttons.Length - 1;
            var root = new StackPanel { Margin = new Thickness(18) };
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.6, Content = Text(message) };
            root.Children.Add(sv);
            var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new Button { Content = buttons[i], IsDefault = i == 0, IsCancel = i == buttons.Length - 1, Margin = new Thickness(8, 0, 0, 4) };
                if (i == 0) b.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
                b.Click += (s, e) => { result = idx; w.DialogResult = true; };
                row.Children.Add(b);
            }
            root.Children.Add(row);
            w.Content = root;
            w.ShowDialog();
            return result;
        }

        public static bool Confirm(string title, string message, string yes = "Yes", string no = "Cancel") { return Ask(title, message, yes, no) == 0; }
        public static void Info(string title, string message) { Ask(title, message, "OK"); }

        /// <summary>OK is only enabled when the exact expected text has been typed.</summary>
        public static bool TypedConfirm(string title, string message, string expected, string okText)
        {
            var w = NewWindow(title, 560);
            bool ok = false;
            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(Text(message));
            var tb = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
            root.Children.Add(tb);
            var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var okB = new Button { Content = okText, IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
            var no = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
            tb.TextChanged += (s, e) => okB.IsEnabled = string.Equals(tb.Text.Trim(), expected, StringComparison.OrdinalIgnoreCase);
            okB.Click += (s, e) => { ok = true; w.DialogResult = true; };
            no.Click += (s, e) => { w.DialogResult = false; };
            row.Children.Add(okB); row.Children.Add(no);
            root.Children.Add(row);
            w.Content = root;
            w.Loaded += (s, e) => tb.Focus();
            w.ShowDialog();
            return ok;
        }

        /// <summary>Everything in a row: virtualized, selectable list of paths. Nothing is deleted from this window.</summary>
        public static void ShowPaths(string title, string header, IList<string> paths)
        {
            var w = new Window
            {
                Title = title, Width = 860, Height = 560, MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            var o = Owner; if (o != null && o.IsVisible) w.Owner = o;
            w.SetResourceReference(Control.BackgroundProperty, "BgBrush");
            w.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            var g = new Grid { Margin = new Thickness(14) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.Children.Add(Text(header));
            var lb = new ListBox { ItemsSource = paths, FontFamily = new FontFamily("Consolas"), SelectionMode = SelectionMode.Extended };
            VirtualizingPanel.SetIsVirtualizing(lb, true); VirtualizingPanel.SetVirtualizationMode(lb, VirtualizationMode.Recycling);
            ScrollViewer.SetHorizontalScrollBarVisibility(lb, ScrollBarVisibility.Auto);
            Grid.SetRow(lb, 1); g.Children.Add(lb);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var copy = new Button { Content = "Copy all" };
            copy.Click += (s, e) => { try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch { } };
            var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, Margin = new Thickness(0) };
            close.Click += (s, e) => w.Close();
            row.Children.Add(copy); row.Children.Add(close);
            Grid.SetRow(row, 2); g.Children.Add(row);
            w.Content = g;
            w.ShowDialog();
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Log.Error("open url", e); Info("Could not open the browser", url + "\n\n" + e.Message); }
        }

        public static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                else if (Directory.Exists(Path.GetDirectoryName(path ?? ""))) Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"");
            }
            catch (Exception e) { Log.Error("reveal", e); }
        }

        public static string PickFolder(string description)
        {
            using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = description, ShowNewFolderButton = true })
                return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
        }
    }
}
