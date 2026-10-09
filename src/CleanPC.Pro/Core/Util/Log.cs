using System;
using System.IO;
using System.Text;

namespace CleanPC.Pro
{
    /// <summary>Append-only log next to the exe (CleanPC-Pro-log.txt, previous run rolled to .old). Never throws.</summary>
    public static class Log
    {
        static readonly object Gate = new object();
        static string _path;
        public static string Path { get { return _path; } }

        public static void Init()
        {
            lock (Gate)
            {
                try
                {
                    string dir = AppInfo.ExeDir;
                    if (!IsWritable(dir)) dir = AppInfo.DataDir;
                    _path = System.IO.Path.Combine(dir, "CleanPC-Pro-log.txt");
                    if (File.Exists(_path))
                    {
                        string old = System.IO.Path.Combine(dir, "CleanPC-Pro-log.old");
                        try { File.Copy(_path, old, true); File.Delete(_path); } catch { }
                    }
                }
                catch { }
            }
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string t = System.IO.Path.Combine(dir, ".w" + Guid.NewGuid().ToString("N").Substring(0, 6));
                File.WriteAllText(t, "x"); File.Delete(t); return true;
            }
            catch { return false; }
        }

        public static void Write(string msg)
        {
            try
            {
                if (_path == null) return;
                lock (Gate)
                    File.AppendAllText(_path, DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public static void Error(string where, Exception e)
        {
            try { Write("ERROR " + where + ": " + (e == null ? "?" : e.GetType().Name + ": " + e.Message) + (e == null ? "" : Environment.NewLine + e.StackTrace)); }
            catch { }
        }
    }
}
