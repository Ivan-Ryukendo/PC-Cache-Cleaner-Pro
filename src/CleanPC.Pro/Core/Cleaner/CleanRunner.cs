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

        public class Result { public long Freed; public int Done; public bool Cancelled; public int SkippedItems; }

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
                var skips = new List<string>();
                long df = 0;
                try { df = CleanOne(t, skips); }
                catch (Exception e) { skips.Add("SKIP " + t.Name + ": " + e.Message); Log.Error("clean " + t.Name, e); }
                if (df > before) df = before;
                res.Freed += df; res.Done++;
                foreach (var s in skips) Log.Write(s);
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
