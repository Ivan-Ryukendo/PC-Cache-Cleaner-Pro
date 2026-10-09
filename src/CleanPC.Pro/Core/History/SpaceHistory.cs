using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CleanPC.Pro
{
    public class SpaceSample
    {
        public string Time = "";      // ISO
        public string Drive = "";
        public long Free;
        public long Size;
        public string Event = "";     // start | clean | auto
    }

    public class SpaceStore { public List<SpaceSample> Samples = new List<SpaceSample>(); }

    /// <summary>Free space per drive at each run, in %APPDATA%\CleanPC-Pro\history.json.</summary>
    public static class SpaceHistory
    {
        static readonly object Gate = new object();
        public static string FilePath { get { return Path.Combine(AppInfo.DataDir, "history.json"); } }

        public static List<SpaceSample> All()
        {
            lock (Gate) return Json.Load<SpaceStore>(FilePath).Samples ?? new List<SpaceSample>();
        }

        public static void Record(string evt)
        {
            try
            {
                lock (Gate)
                {
                    var st = Json.Load<SpaceStore>(FilePath);
                    if (st.Samples == null) st.Samples = new List<SpaceSample>();
                    string now = DateTime.Now.ToString("s");
                    foreach (var d in Drives.Fixed())
                        st.Samples.Add(new SpaceSample { Time = now, Drive = d.Letter, Free = d.Free, Size = d.Size, Event = evt });
                    if (st.Samples.Count > 5000) st.Samples = st.Samples.Skip(st.Samples.Count - 5000).ToList();
                    Json.Save(FilePath, st);
                }
            }
            catch (Exception e) { Log.Error("space history", e); }
        }

        /// <summary>Warns when free space on a drive dropped by more than warnGB since the previous run's sample.</summary>
        public static List<string> Warnings(List<SpaceSample> before, double warnGB)
        {
            var w = new List<string>();
            try
            {
                foreach (var d in Drives.Fixed())
                {
                    var last = before.Where(s => s.Drive == d.Letter).OrderBy(s => s.Time).LastOrDefault();
                    if (last == null) continue;
                    double dropGB = (last.Free - d.Free) / 1073741824.0;
                    if (dropGB > warnGB)
                        w.Add(string.Format("{0}: free space dropped by {1:N1} GB since {2} ({3} -> {4}).", d.Letter, dropGB, last.Time.Replace('T', ' '), Fmt.Size(last.Free), Fmt.Size(d.Free)));
                }
            }
            catch { }
            return w;
        }
    }
}
