using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanPC.Pro
{
    public enum TargetKind { Contents, Files, RemoveDirs, RecycleBin, Report }
    public enum Risk { Safe, Optional, Risky, Report }

    /// <summary>One row of the Cleanup (or Report-only) list. Same fields as the Standard edition's row object.</summary>
    public class Target : ObservableObject, ITickable
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public List<string> Paths { get; set; }
        public TargetKind Kind { get; set; }
        public bool DefaultChecked { get; set; }
        public string Note { get; set; }
        public string Drive { get; set; }
        public string Service { get; set; }

        long _size = -1;
        public long Size { get { return _size; } set { if (Set(ref _size, value)) { Raise("SizeText"); } } }
        public string SizeText { get { return Fmt.Size(_size); } }

        bool _checked;
        public bool IsChecked { get { return _checked; } set { Set(ref _checked, value); } }

        string _status = "";
        public string Status { get { return _status; } set { Set(ref _status, value ?? ""); } }

        public Risk Risk
        {
            get
            {
                if (Kind == TargetKind.Report) return Risk.Report;
                if (DefaultChecked) return Risk.Safe;
                return (Note ?? "").StartsWith("RISKY", StringComparison.Ordinal) ? Risk.Risky : Risk.Optional;
            }
        }
        public string RiskText { get { return Risk == Risk.Safe ? "Safe" : Risk == Risk.Optional ? "Optional" : Risk == Risk.Risky ? "RISKY" : "Info"; } }
        public int PathCount { get { return Paths == null ? 0 : Paths.Count; } }
        public string PathsText { get { return Paths == null ? "" : string.Join("; ", Paths); } }
        public string NoteOrStatus { get { return string.IsNullOrEmpty(Status) ? Note : Status; } }
    }

    public class ScanInfo
    {
        public bool WalkTimedOut, WalkCancelled;
        public int DirsVisited;
    }

    public class ScanResult
    {
        public List<Target> Targets = new List<Target>();
        public List<Target> Report = new List<Target>();
        public ScanInfo Info = new ScanInfo();
    }

    public static class Presets
    {
        public static readonly string[] Names = { "Safe", "Developer", "Deep", "Custom" };

        /// <summary>Safe = default-ticked items. Developer = safe + all non-risky Dev items. Deep = every non-RISKY item. Custom = saved list.
        /// RISKY items are never ticked by a preset (they need the user's own tick plus an extra confirmation).</summary>
        public static void Apply(string preset, IEnumerable<Target> targets, Settings s)
        {
            foreach (var t in targets)
            {
                if (t.Kind == TargetKind.Report) { t.IsChecked = false; continue; }
                switch (preset)
                {
                    case "Developer": t.IsChecked = t.Risk == Risk.Safe || (t.Category == "Dev" && t.Risk != Risk.Risky); break;
                    case "Deep": t.IsChecked = t.Risk != Risk.Risky; break;
                    case "Custom": t.IsChecked = s.CustomChecked.Contains(t.Name, StringComparer.OrdinalIgnoreCase); break;
                    default: t.IsChecked = t.Risk == Risk.Safe; break;
                }
            }
        }
    }
}
