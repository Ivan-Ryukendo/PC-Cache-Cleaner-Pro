using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CleanPC.Pro
{
    public class CleanRecord
    {
        public string Time { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Drives { get; set; } = "";
        public int Items { get; set; }
        public long Freed { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore] public string FreedText { get { return Fmt.Size(Freed); } }
        public string Detail { get; set; } = "";
    }

    public class CleanStore { public List<CleanRecord> Records = new List<CleanRecord>(); }

    /// <summary>Cleaning history (what was freed, when) in %APPDATA%\CleanPC-Pro\cleanings.json.</summary>
    public static class CleanHistory
    {
        static readonly object Gate = new object();
        public static string FilePath { get { return Path.Combine(AppInfo.DataDir, "cleanings.json"); } }

        public static List<CleanRecord> All()
        {
            lock (Gate) return (Json.Load<CleanStore>(FilePath).Records ?? new List<CleanRecord>()).OrderByDescending(r => r.Time).ToList();
        }

        public static void Add(CleanRecord r)
        {
            try
            {
                lock (Gate)
                {
                    var st = Json.Load<CleanStore>(FilePath);
                    if (st.Records == null) st.Records = new List<CleanRecord>();
                    st.Records.Add(r);
                    if (st.Records.Count > 2000) st.Records = st.Records.Skip(st.Records.Count - 2000).ToList();
                    Json.Save(FilePath, st);
                }
            }
            catch (Exception e) { Log.Error("clean history", e); }
        }
    }
}
