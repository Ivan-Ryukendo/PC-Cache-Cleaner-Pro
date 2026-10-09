using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CleanPC.Pro.Ui
{
    /// <summary>Global error panel: shown by the DispatcherUnhandledException / AppDomain handlers. The error is already in the log.</summary>
    public static class ErrorPanel
    {
        static int _open;

        public static void Show(string where, Exception e, bool fatal)
        {
            Log.Error(where, e);
            try
            {
                if (System.Threading.Interlocked.Exchange(ref _open, 1) == 1) return;     // never stack error windows
                string details = where + "\n" + (e == null ? "" : e.GetType().Name + ": " + e.Message + "\n\n" + e.StackTrace);
                var w = new Window
                {
                    Title = "Something went wrong", Width = 640, Height = 460, MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowInTaskbar = true
                };
                try { var o = Application.Current == null ? null : Application.Current.MainWindow; if (o != null && o.IsVisible) { w.Owner = o; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; } } catch { }
                try { w.SetResourceReference(Control.BackgroundProperty, "BgBrush"); w.SetResourceReference(Control.ForegroundProperty, "TextBrush"); } catch { }
                var g = new Grid { Margin = new Thickness(16) };
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.Children.Add(Dialogs.Text((fatal ? "The program hit an unexpected error and has to close." : "An unexpected error happened. Nothing was deleted by the failing step; you can continue.") +
                    "\nThe details were written to the log:\n" + (Log.Path ?? "(log unavailable)")));
                var tb = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
                Grid.SetRow(tb, 1); g.Children.Add(tb);
                var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
                var copy = new Button { Content = "Copy details" }; copy.Click += (s, a) => { try { Clipboard.SetText(details); } catch { } };
                var open = new Button { Content = "Open log folder" }; open.Click += (s, a) => { try { if (Log.Path != null) Process.Start("explorer.exe", "/select,\"" + Log.Path + "\""); } catch { } };
                var cont = new Button { Content = fatal ? "Close program" : "Continue", IsDefault = true, Margin = new Thickness(0) }; cont.Click += (s, a) => w.Close();
                row.Children.Add(copy); row.Children.Add(open); row.Children.Add(cont);
                Grid.SetRow(row, 2); g.Children.Add(row);
                w.Content = g;
                w.ShowDialog();
            }
            catch (Exception ex) { Log.Error("error panel", ex); }
            finally { System.Threading.Interlocked.Exchange(ref _open, 0); }
        }
    }
}
