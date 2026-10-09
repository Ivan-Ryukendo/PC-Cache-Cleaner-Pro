using System;
using System.Collections.Generic;
using System.IO;

namespace CleanPC.Pro
{
    /// <summary>User settings, stored in %APPDATA%\CleanPC-Pro\settings.json.</summary>
    public class Settings
    {
        public string Theme = "System";                 // System | Light | Dark
        public string Preset = "Safe";                  // Safe | Developer | Deep | Custom
        public List<string> CustomChecked = new List<string>();
        public List<string> Exclusions = new List<string>();
        public bool RestorePointBeforeClean = false;
        public int StaleDays = 90;
        public int LargeTopN = 100;
        public int DuplicateMinMB = 1;
        public double WarnDropGB = 10;

        static readonly object Gate = new object();
        static Settings _cur;
        public static string FilePath { get { return Path.Combine(AppInfo.DataDir, "settings.json"); } }

        public static Settings Current
        {
            get
            {
                lock (Gate)
                {
                    if (_cur == null)
                    {
                        _cur = Json.Load<Settings>(FilePath);
                        if (_cur.Exclusions == null) _cur.Exclusions = new List<string>();
                        if (_cur.CustomChecked == null) _cur.CustomChecked = new List<string>();
                        CleanGuard.SetExclusions(_cur.Exclusions);
                    }
                    return _cur;
                }
            }
        }

        public void Save()
        {
            CleanGuard.SetExclusions(Exclusions);
            Json.Save(FilePath, this);
        }
    }
}
