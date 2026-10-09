using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Threading;

namespace CleanPC.Pro
{
    /// <summary>Runs the cleanup of ticked rows. Cancel is honoured BETWEEN items only (never mid-delete).</summary>
    public static class CleanRunner
    {
        /// <summary>Deletes one row. Returns bytes freed; per-item problems go to skips (and the row's Status).</summary>
        public static long CleanOne(Target t, List<string> skips)
        {
            long freed = 0; bool stopped = false; ServiceController svc = null;
            try
            {
                if (t.Kind == TargetKind.Report) { Log.Write("REFUSED report-only item: " + t.Name); return 0; }
                if (!string.IsNullOrEmpty(t.Service))
                {
                    try
                    {
                        svc = new ServiceController(t.Service);
                        if (svc.Status == ServiceControllerStatus.Running) { svc.Stop(); svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); stopped = true; }
                    }
                    catch (Exception e) { skips.Add("SKIP service " + t.Service + ": " + e.Message); }
                }
                switch (t.Kind)
                {
                    case TargetKind.RecycleBin:
                        {
                            int rc = Native.EmptyRecycleBin(t.Drive + "\\");
                            if (rc == 0 || unchecked((uint)rc) == 0x8000FFFF || rc == 1) freed = t.Size;   // S_OK (also returned when already empty)
                            else skips.Add("SKIP Recycle Bin " + t.Drive + ": error 0x" + rc.ToString("X"));
                            break;
                        }
                    case TargetKind.Files: freed = CleanOps.DeleteFiles(t.Paths, skips); break;
                    case TargetKind.RemoveDirs: foreach (var p in t.Paths) freed += CleanOps.DeleteTree(p, skips); break;
                    default: foreach (var p in t.Paths) freed += CleanOps.DeleteContents(p, skips); break;
                }
            }
            finally
            {
                if (stopped) { try { svc.Start(); } catch { } }
                if (svc != null) svc.Dispose();
            }
            return freed;
        }

        public class Result
        {
            public long Freed; public int Done; public bool Cancelled; public int SkippedItems;
            /// <summary>Every file skipped because it was in use (path + reason + size), for the Locked files panel.</summary>
            public List<SkipInfo> Locked = new List<SkipInfo>();
            /// <summary>Files skipped for other reasons (access denied etc.); not retried.</summary>
            public int OtherSkippedFiles;
        }

        /// <summary>Tags each structured skip with the cleanup path it was found under, so a retry can re-check the guard against that root.</summary>
        public static void TagRoots(Target t, SkipList skips)
        {
            foreach (var it in skips.Items)
            {
                it.TargetName = t.Name;
                string best = null;
                foreach (var p in t.Paths ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (string.Equals(p, it.Path, StringComparison.OrdinalIgnoreCase)) { best = p; it.RootIsFile = true; break; }
                    if (it.Path.StartsWith(p.TrimEnd((char)92) + "\\", StringComparison.OrdinalIgnoreCase) && (best == null || p.Length > best.Length)) best = p;
                }
                it.Root = best;
                if (t.Kind == TargetKind.Files && best != null) it.RootIsFile = true;
            }
        }

        /// <summary>
        /// Retry ONLY the previously skipped files after the user closed the holding program. Each file goes through the
        /// same guarded CleanOps.DeleteFiles, and the root it came from is re-checked by CleanGuard first: a file with no
        /// root, outside its root, or whose root is no longer allowed is refused.
        /// </summary>
        public static long RetrySkipped(IEnumerable<SkipInfo> items, SkipList newSkips)
        {
            long freed = 0;
            foreach (var it in items)
            {
                string why;
                if (string.IsNullOrEmpty(it.Root) || string.IsNullOrEmpty(it.Path)) { newSkips.Add("BLOCKED " + it.Path + ": not tied to a cleanup path"); continue; }
                bool inRoot = string.Equals(it.Path, it.Root, StringComparison.OrdinalIgnoreCase) ||
                              it.Path.StartsWith(it.Root.TrimEnd((char)92) + "\\", StringComparison.OrdinalIgnoreCase);
                if (!inRoot) { newSkips.Add("BLOCKED " + it.Path + ": outside its cleanup path"); continue; }
                if (!CleanGuard.Check(it.Root, !it.RootIsFile, out why)) { newSkips.Add("BLOCKED " + it.Root + ": " + why); continue; }
                if (!System.IO.File.Exists(it.Path)) continue;   // already gone: nothing to do
                Log.Write("RETRY " + it.Path + "  [" + it.TargetName + "]");
                var one = new SkipList();
                long f = CleanOps.DeleteFiles(new[] { it.Path }, one);
                freed += f;
                foreach (var s in one) { newSkips.Add(s); Log.Write(s); }
                foreach (var x in one.Items) { x.Root = it.Root; x.RootIsFile = it.RootIsFile; x.TargetName = it.TargetName; newSkips.Items.Add(x); }
                if (f > 0) Log.Write("FREED " + Fmt.Size(f) + "  (retry) " + it.Path);
            }
            return freed;
        }

        public static Result Run(IList<Target> chosen, IProgress<string> prog, CancellationToken ct, Action<Target> onItemDone)
        {
            var res = new Result();
            int n = 0;
            foreach (var t in chosen)
            {
                if (ct.IsCancellationRequested) { res.Cancelled = true; Log.Write("=== CANCELLED by user ==="); break; }
                n++;
                if (prog != null) prog.Report("Cleaning (" + n + "/" + chosen.Count + "): " + t.Name);
                long before = Math.Max(0, t.Size);
                Log.Write("CLEAN " + t.Name + "  [" + string.Join(" | ", t.Paths) + "]");
                var skips = new SkipList();
                long df = 0;
                try { df = CleanOne(t, skips); }
                catch (Exception e) { skips.Add("SKIP " + t.Name + ": " + e.Message); Log.Error("clean " + t.Name, e); }
                if (df > before) df = before;
                res.Freed += df; res.Done++;
                foreach (var s in skips) Log.Write(s);
                TagRoots(t, skips);
                foreach (var it in skips.Items) { if (it.Locked) res.Locked.Add(it); else res.OtherSkippedFiles++; }
                Log.Write("FREED " + Fmt.Size(df) + "  " + t.Name);
                if (skips.Count > 0)
                {
                    res.SkippedItems++;
                    t.Status = "Freed " + Fmt.Size(df) + "; " + skips.Count + " item(s) skipped (in use / denied) - first: " + Fmt.Mid(skips[0], 140);
                }
                else t.Status = "Freed " + Fmt.Size(df);
                t.Size = before - df;
                if (onItemDone != null) onItemDone(t);
            }
            Log.Write("=== TOTAL FREED " + Fmt.Size(res.Freed) + " ===");
            return res;
        }
    }
}
