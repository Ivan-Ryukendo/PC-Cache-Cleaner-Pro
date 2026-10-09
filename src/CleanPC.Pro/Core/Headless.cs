using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CleanPC.Pro
{
    public class CliArgs
    {
        public bool Auto, DryRun, Safe, AllDrives, Help, SelfTest, Gui = true, AutoScan;
        public List<string> Drives = new List<string>();
        public string Preset = "Safe", SelfTestDir, Tab, DemoLockDir;

        public static CliArgs Parse(string[] args)
        {
            var a = new CliArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string s = args[i].ToLowerInvariant();
                switch (s)
                {
                    case "--auto": a.Auto = true; break;
                    case "--dryrun": case "--dry-run": a.DryRun = true; break;
                    case "--safe": a.Safe = true; a.Preset = "Safe"; break;
                    case "--all-drives": a.AllDrives = true; break;
                    case "--help": case "-h": case "/?": a.Help = true; break;
                    case "--selftest": a.SelfTest = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) a.SelfTestDir = args[++i]; break;
                    case "--preset": if (i + 1 < args.Length) a.Preset = args[++i]; break;
                    case "--autoscan": a.AutoScan = true; break;
                    case "--demo-locks": if (i + 1 < args.Length) a.DemoLockDir = args[++i]; break;   // hidden test hook: cleans ONLY that throwaway folder through the normal post-clean flow
                    case "--tab": if (i + 1 < args.Length) a.Tab = args[++i]; break;
                    case "--drives":
                        if (i + 1 < args.Length) a.Drives.AddRange(args[++i].Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                        break;
                }
            }
            a.Gui = !(a.Auto || a.DryRun || a.Help || a.SelfTest);
            return a;
        }
    }

    /// <summary>No-UI mode used by the scheduled task and for testing: --auto / --dryrun / --safe / --drives C,D / --all-drives.</summary>
    public static class Headless
    {
        public const string HelpText =
            "PC Cache Cleaner Pro " + AppInfo.Version + " - command line\r\n" +
            "  CleanPC-Pro.exe                      start the window\r\n" +
            "  --dryrun [--drives C,D | --all-drives]   scan and list, delete nothing\r\n" +
            "  --auto   [--safe] [--drives C,D | --all-drives]   delete the pre-ticked SAFE items, no UI\r\n" +
            "  --preset Safe|Developer|Deep         which items --auto/--dryrun select (RISKY items are never selected)\r\n" +
            "  --selftest [folder]                  run the built-in tests in a throwaway folder\r\n" +
            "Default drive: the system drive. Log: CleanPC-Pro-log.txt next to the exe.";

        public static int Run(CliArgs a)
        {
            Native.AttachConsole(-1);
            if (a.Help) { Console.WriteLine(HelpText); return 0; }
            if (a.SelfTest) return SelfTest.Run(a.SelfTestDir);

            Log.Write("=== Run start (headless v" + AppInfo.InformationalVersion + ") === admin=" + AppInfo.IsAdmin + " dryrun=" + a.DryRun + " preset=" + a.Preset);
            var settings = Settings.Current;
            var drives = Drives.Resolve(a.Drives, a.AllDrives);
            if (drives.Count == 0) drives = Drives.Resolve(new[] { AppInfo.SystemDrive }, false);
            Console.WriteLine("PC Cache Cleaner Pro " + AppInfo.InformationalVersion + "   mode: " + (a.DryRun ? "DRY RUN (nothing deleted)" : "auto") + "   admin: " + AppInfo.IsAdmin);
            Console.WriteLine("Drives: " + string.Join(", ", drives));
            var freeStart = drives.ToDictionary(d => d, d => Drives.FreeOf(d));
            if (!AppInfo.IsAdmin) Console.WriteLine("(not elevated: Windows\\Temp, Windows Update cache and shadow copies are not scanned)");

            var prog = new Progress<string>(m => { });
            var res = TargetBuilder.Build(drives, prog, CancellationToken.None);
            Presets.Apply(a.Preset, res.Targets, settings);
            foreach (var t in res.Targets.OrderBy(t => t.Category).ThenByDescending(t => t.Size))
            {
                Console.WriteLine("  [{0}] {1,10}  {2,-9} {3}{4}", t.IsChecked ? "x" : " ", Fmt.Size(t.Size), t.RiskText, t.Name, string.IsNullOrEmpty(t.Note) ? "" : "   - " + t.Note);
                Log.Write("FOUND " + Fmt.Size(t.Size).PadLeft(12) + "  " + t.Name + "  [" + t.Category + "] selected=" + t.IsChecked);
            }
            Console.WriteLine("Report only (never deleted): " + res.Report.Count);
            foreach (var r in res.Report.OrderByDescending(r => r.Size)) { Console.WriteLine("  {0,10}  {1}", Fmt.Size(r.Size), r.Name); Log.Write("REPORT " + Fmt.Size(r.Size) + "  " + r.Name); }
            var sel = res.Targets.Where(t => t.IsChecked).ToList();
            long totalSel = sel.Sum(t => Math.Max(0, t.Size));
            Console.WriteLine("Scan: {0} cleanup rows ({1} total found), {2} report rows, {3} folders visited{4}",
                res.Targets.Count, Fmt.Size(res.Targets.Sum(t => Math.Max(0, t.Size))), res.Report.Count, res.Info.DirsVisited, res.Info.WalkTimedOut ? ", drive walk hit its time limit" : "");
            long freed = 0;
            if (a.DryRun)
            {
                Console.WriteLine("DRY RUN - would free about {0} ({1} items selected)", Fmt.Size(totalSel), sel.Count);
                Log.Write("=== TOTAL WOULD FREE " + Fmt.Size(totalSel) + " ===");
            }
            else
            {
                if (settings.RestorePointBeforeClean && AppInfo.IsAdmin) { var rp = RestorePoint.Create("PC Cache Cleaner Pro (scheduled clean)"); if (rp != null) Console.WriteLine("Restore point not created: " + rp); }
                var r = CleanRunner.Run(sel, null, CancellationToken.None, t => Console.WriteLine("  {0,10}  {1}", t.Status, t.Name));
                freed = r.Freed;
                Console.WriteLine("TOTAL FREED: " + Fmt.Size(freed));
                CleanHistory.Add(new CleanRecord { Time = DateTime.Now.ToString("s"), Mode = "auto", Drives = string.Join(",", drives), Items = r.Done, Freed = freed, Detail = a.Preset });
                SpaceHistory.Record("auto");
            }
            foreach (var d in drives) Console.WriteLine("{0} free  {1}  ->  {2}", d, Fmt.Size(freeStart[d]), Fmt.Size(Drives.FreeOf(d)));
            return 0;
        }
    }
}
