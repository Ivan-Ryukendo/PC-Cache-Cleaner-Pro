using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using CleanPC.Pro.Ui;

namespace CleanPC.Pro
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Log.Init();
            var a = CliArgs.Parse(args);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                if (a.Gui) ErrorPanel.Show("AppDomain unhandled exception", ex, true);
                else { Log.Error("AppDomain unhandled exception", ex); try { Console.WriteLine("ERROR: " + (ex == null ? "?" : ex.Message)); } catch { } }
            };
            TaskScheduler.UnobservedTaskException += (s, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };
            if (!a.Gui)
            {
                try { return Headless.Run(a); }
                catch (Exception e) { Log.Error("headless", e); try { Console.WriteLine("ERROR: " + e.Message); } catch { } return 2; }
            }
            return RunGui(a);
        }

        static int RunGui(CliArgs a)
        {
            Log.Write("=== Run start (GUI v" + AppInfo.InformationalVersion + ") === admin=" + AppInfo.IsAdmin + " os=" + Environment.OSVersion.Version + " host=" + Environment.MachineName);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            ThemeManager.Apply(Settings.Current.Theme);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/CleanPC-Pro;component/Ui/Themes/Controls.xaml", UriKind.Absolute) });
            app.DispatcherUnhandledException += (s, e) => { e.Handled = true; ErrorPanel.Show("UI thread exception", e.Exception, false); };
            UserPreferenceChangedEventHandler pref = (s, e) =>
            {
                if (Settings.Current.Theme == "System" && app.Dispatcher != null)
                    app.Dispatcher.BeginInvoke(new Action(() => ThemeManager.Apply("System")));
            };
            SystemEvents.UserPreferenceChanged += pref;
            try
            {
                var win = new MainWindow(a);
                return app.Run(win);
            }
            finally { SystemEvents.UserPreferenceChanged -= pref; }
        }
    }
}
