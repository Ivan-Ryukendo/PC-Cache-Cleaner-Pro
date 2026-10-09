using System;
using System.Windows;
using System.Windows.Controls;

namespace CleanPC.Pro.Ui
{
    /// <summary>
    /// Small NON-modal summary shown after a clean (freed space, locked-item count with a link to the Locked files panel).
    /// It is an ordinary owned window: no tray icon, no background service, closes with the main window.
    /// </summary>
    public static class StatusPopup
    {
        static Window _current;

        public static void Show(string freedText, string freeSpaceText, int lockedCount, Action showLocked)
        {
            try
            {
                if (_current != null) { try { _current.Close(); } catch { } _current = null; }
                var owner = Application.Current == null ? null : Application.Current.MainWindow;
                var w = new Window
                {
                    Title = "Cleanup finished", Width = 360, SizeToContent = SizeToContent.Height, MinWidth = 300, ShowInTaskbar = false, ShowActivated = false,
                    ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, Topmost = false
                };
                if (owner != null && owner.IsVisible) w.Owner = owner;
                w.SetResourceReference(Control.BackgroundProperty, "BgBrush");
                w.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                var sp = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };
                var head = new TextBlock { Text = freedText, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
                sp.Children.Add(head);
                if (!string.IsNullOrEmpty(freeSpaceText)) sp.Children.Add(Dialogs.Text("Free space now: " + freeSpaceText, true));
                var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
                if (lockedCount > 0)
                {
                    sp.Children.Add(Dialogs.Text(lockedCount + " file(s) were skipped because programs are using them."));
                    var b = new Button { Content = "Show locked files", Margin = new Thickness(0, 0, 8, 0) };
                    b.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
                    b.Click += (s, e) => { w.Close(); if (showLocked != null) showLocked(); };
                    row.Children.Add(b);
                }
                var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(0) };
                close.Click += (s, e) => w.Close();
                row.Children.Add(close);
                sp.Children.Add(row);
                w.Content = sp;
                if (owner != null)
                {
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = Math.Max(0, owner.Left + owner.ActualWidth - 360 - 40);
                    w.Top = Math.Max(0, owner.Top + owner.ActualHeight - 230);
                }
                w.Closed += (s, e) => { if (_current == w) _current = null; };
                _current = w;
                w.Show();
            }
            catch (Exception e) { Log.Error("status popup", e); }
        }
    }
}
