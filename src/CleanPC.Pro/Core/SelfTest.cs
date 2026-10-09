using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace CleanPC.Pro
{
    /// <summary>
    /// `CleanPC-Pro.exe --selftest [folder]`: unit-style checks run inside the exe. Everything destructive happens only in a
    /// throwaway folder (default %TEMP%\CleanPCProSelfTest). Never touches real caches.
    /// </summary>
    public static class SelfTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { _pass++; Console.WriteLine("PASS  " + name); }
            else { _fail++; Console.WriteLine("FAIL  " + name + (detail.Length > 0 ? "  -> " + detail : "")); }
        }

        public static int Run(string dir)
        {
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetTempPath(), "CleanPCProSelfTest");
            dir = Path.GetFullPath(dir);
            Console.WriteLine("PC Cache Cleaner Pro " + AppInfo.InformationalVersion + " self-test, work folder: " + dir);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);
            Section("CleanGuard table", () => GuardTable());
            Section("Reparse point not followed + locked file skipped", () => ReparseAndLocked(dir));
            Section("Clean runner on throwaway rows", () => RunnerRows(dir));
            Section("Duplicate finder", () => Duplicates(dir));
            Section("Large file scan", () => LargeFiles(dir));
            Section("Stale dev projects", () => Stale(dir));
            Section("Recycle Bin only (user files)", () => RecycleOnly(dir));
            Section("Startup toggle is reversible", () => StartupToggle());
            Section("Security heuristics helpers", () => SecurityHelpers());
            Section("Updater (version compare, 404, real API)", () => Updater());
            Console.WriteLine();
            Console.WriteLine("RESULT: {0} passed, {1} failed", _pass, _fail);
            try { Directory.Delete(dir, true); } catch { }
            return _fail == 0 ? 0 : 1;
        }

        static void Section(string name, Action a)
        {
            Console.WriteLine("-- " + name);
            try { a(); } catch (Exception e) { _fail++; Console.WriteLine("FAIL  " + name + " threw " + e.GetType().Name + ": " + e.Message); }
        }

        // ---------------------------------------------------------------- guard
        static void GuardTable()
        {
            string u = Environment.GetEnvironmentVariable("USERPROFILE");
            var rows = new List<Tuple<string, bool, bool>>   // path, isRoot, expected allowed
            {
                Tuple.Create(@"C:\", true, false),
                Tuple.Create(@"C:\Windows", true, false),
                Tuple.Create(@"C:\Windows\System32", true, false),
                Tuple.Create(@"C:\Windows\WinSxS", true, false),
                Tuple.Create(@"C:\Windows\Installer", true, false),
                Tuple.Create(@"C:\Program Files\Some App\Cache", true, false),
                Tuple.Create(@"C:\Program Files (x86)\Some App", true, false),
                Tuple.Create(@"C:\System Volume Information", true, false),
                Tuple.Create(u, true, false),
                Tuple.Create(u + @"\Documents", true, false),
                Tuple.Create(u + @"\Desktop", true, false),
                Tuple.Create(u + @"\Downloads", true, false),
                Tuple.Create(u + @"\Pictures", true, false),
                Tuple.Create(u + @"\AppData\Local", true, false),
                Tuple.Create(@"D:\Games\Saved Games", true, false),
                Tuple.Create(@"C:\Stuff", true, false),
                Tuple.Create(@"\\server\share\Temp", true, false),
                Tuple.Create(@"relative\path", true, false),
                Tuple.Create(@"C:\Windows\Temp", true, true),
                Tuple.Create(@"C:\Windows\SoftwareDistribution\Download", true, true),
                Tuple.Create(u + @"\AppData\Local\Temp", true, true),
                Tuple.Create(u + @"\AppData\Local\Google\Chrome\User Data\Default\Cache", true, true),
                Tuple.Create(@"C:\ProgramData\NVIDIA Corporation\NV_Cache", true, true),
                Tuple.Create(@"C:\Temp", true, true),
                Tuple.Create(@"D:\tmp", true, true),
            };
            foreach (var r in rows)
            {
                string why; bool got = CleanGuard.Check(r.Item1, r.Item2, out why);
                Check("guard " + (r.Item3 ? "ALLOW " : "DENY  ") + r.Item1, got == r.Item3, "got " + got + " (" + why + ")");
            }
            string tmp = Path.Combine(u, @"AppData\Local\Temp");
            CleanGuard.SetExclusions(new[] { tmp });
            string w; Check("guard DENY user exclusion " + tmp, !CleanGuard.Check(tmp + @"\x", true, out w) && w.Contains("excluded"), w);
            CleanGuard.SetExclusions(new string[0]);
            Check("guard ALLOW again after exclusions cleared", CleanGuard.Check(tmp, true, out w), w);
            Check("user file DENY C:\\Windows\\x.dll", !CleanGuard.CheckUserFile(@"C:\Windows\x.dll", out w));
            Check("user file DENY pagefile.sys", !CleanGuard.CheckUserFile(@"C:\pagefile.sys", out w));
            Check("user file DENY Program Files", !CleanGuard.CheckUserFile(@"C:\Program Files\a\b.dll", out w));
            Check("user file ALLOW D:\\Movies\\a.mkv", CleanGuard.CheckUserFile(@"D:\Movies\a.mkv", out w), w);
        }

        // ---------------------------------------------------------------- reparse + locked
        static void ReparseAndLocked(string dir)
        {
            string root = Path.Combine(dir, @"guardtest\cache"), keep = Path.Combine(dir, @"guardtest\keep");
            Directory.CreateDirectory(root); Directory.CreateDirectory(keep); Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(keep, "canary.txt"), "must survive");
            File.WriteAllText(Path.Combine(root, "a.txt"), "aaaa");
            File.WriteAllText(Path.Combine(root, @"sub\b.txt"), "bbbbbb");
            string locked = Path.Combine(root, "locked.bin"); File.WriteAllText(locked, "lockedlocked");
            string link = Path.Combine(root, "link");
            int code; Proc.Run("cmd.exe", "/c mklink /J \"" + link + "\" \"" + keep + "\"", 15000, out code);
            Check("junction created", code == 0 && Directory.Exists(link), "exit " + code);
            long size = CleanOps.DirSize(root, CancellationToken.None, null);
            Check("DirSize does not count the junction target", size == 4 + 6 + 12, "size " + size);
            var skips = new List<string>();
            long freed;
            using (var fs = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                freed = CleanOps.DeleteContents(root, skips);
            Check("junction target survived", File.Exists(Path.Combine(keep, "canary.txt")));
            Check("junction link itself removed", !Directory.Exists(link));
            Check("normal files deleted, folder kept", !File.Exists(Path.Combine(root, "a.txt")) && !Directory.Exists(Path.Combine(root, "sub")) && Directory.Exists(root));
            Check("locked file skipped (still there)", File.Exists(locked));
            Check("locked file logged as 'SKIP path: reason'", skips.Any(s => s.StartsWith("SKIP ") && s.Contains("locked.bin: ")), string.Join(" | ", skips));
            Check("freed bytes = the deletable files only", freed == 10, "freed " + freed);
            var s2 = new List<string>();
            Check("DeleteContents refuses a blocked root", CleanOps.DeleteContents(@"C:\Windows\System32", s2) == 0 && s2.Any(x => x.StartsWith("BLOCKED ")));
        }

        // ---------------------------------------------------------------- runner on throwaway rows
        static void RunnerRows(string dir)
        {
            string c1 = Path.Combine(dir, @"runner\cacheA"), c2 = Path.Combine(dir, @"runner\dirB\node_modules"), f1 = Path.Combine(dir, @"runner\files");
            Directory.CreateDirectory(Path.Combine(c1, "deep")); Directory.CreateDirectory(c2); Directory.CreateDirectory(f1);
            File.WriteAllBytes(Path.Combine(c1, @"deep\x.bin"), new byte[1000]); File.WriteAllBytes(Path.Combine(c2, "y.js"), new byte[500]);
            File.WriteAllBytes(Path.Combine(f1, "a.tmp"), new byte[300]); File.WriteAllBytes(Path.Combine(f1, "keep.txt"), new byte[10]);
            var rows = new List<Target>
            {
                new Target { Name = "t-contents", Kind = TargetKind.Contents, Paths = new List<string> { c1 }, Size = 1000, IsChecked = true },
                new Target { Name = "t-removedirs", Kind = TargetKind.RemoveDirs, Paths = new List<string> { c2 }, Size = 500, IsChecked = true },
                new Target { Name = "t-files", Kind = TargetKind.Files, Paths = new List<string> { Path.Combine(f1, "a.tmp") }, Size = 300, IsChecked = true },
                new Target { Name = "t-report", Kind = TargetKind.Report, Paths = new List<string> { f1 }, Size = 5, IsChecked = true },
                new Target { Name = "t-blocked", Kind = TargetKind.Contents, Paths = new List<string> { @"C:\Windows\System32" }, Size = 1, IsChecked = true },
            };
            int cancelAfter = 99; var cts = new CancellationTokenSource();
            var res = CleanRunner.Run(rows, null, cts.Token, t => { if (--cancelAfter < 0) cts.Cancel(); });
            Check("contents row freed 1000 B and kept the folder", Directory.Exists(c1) && !File.Exists(Path.Combine(c1, @"deep\x.bin")) && rows[0].Status.StartsWith("Freed"));
            Check("remove-dirs row removed the folder", !Directory.Exists(c2));
            Check("files row deleted only the listed file", !File.Exists(Path.Combine(f1, "a.tmp")) && File.Exists(Path.Combine(f1, "keep.txt")));
            Check("report-only row refused (nothing freed)", rows[3].Status.StartsWith("Freed 0"), rows[3].Status);
            Check("blocked system path refused at delete time", rows[4].Status.StartsWith("Freed 0") && Directory.Exists(@"C:\Windows\System32"), rows[4].Status);
            Check("total freed = 1000 + 500 + 300", res.Freed == 1800, "freed " + res.Freed);
            // cancel stops BETWEEN items
            var more = new List<Target>(); for (int i = 0; i < 3; i++) { string p = Path.Combine(dir, @"runner\c" + i); Directory.CreateDirectory(p); File.WriteAllText(Path.Combine(p, "f"), "x"); more.Add(new Target { Name = "m" + i, Kind = TargetKind.Contents, Paths = new List<string> { p }, Size = 1 }); }
            var cts2 = new CancellationTokenSource(); int done = 0;
            var r2 = CleanRunner.Run(more, null, cts2.Token, t => { if (++done == 1) cts2.Cancel(); });
            Check("cancel stops between items (1 of 3 done, flagged cancelled)", r2.Cancelled && r2.Done == 1 && File.Exists(Path.Combine(dir, @"runner\c2\f")), "done " + r2.Done);
        }

        // ---------------------------------------------------------------- duplicates
        static byte[] Bytes(int seed, int n) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static void Duplicates(string dir)
        {
            string d = Path.Combine(dir, "dup"); Directory.CreateDirectory(Path.Combine(d, "sub"));
            var a = Bytes(1, 2 * 1024 * 1024);
            File.WriteAllBytes(Path.Combine(d, "a1.bin"), a); File.WriteAllBytes(Path.Combine(d, "a2.bin"), a); File.WriteAllBytes(Path.Combine(d, @"sub\a3.bin"), a);
            File.WriteAllBytes(Path.Combine(d, "b1.bin"), Bytes(2, 2 * 1024 * 1024));                       // same size, different content
            var c1 = Bytes(3, 1024 * 1024); var c2 = (byte[])c1.Clone(); c2[512 * 1024] ^= 0xFF;            // identical first/last 64 KB, differ in the middle
            File.WriteAllBytes(Path.Combine(d, "c1.bin"), c1); File.WriteAllBytes(Path.Combine(d, "c2.bin"), c2);
            File.WriteAllBytes(Path.Combine(d, "tiny1.bin"), Bytes(4, 10)); File.WriteAllBytes(Path.Combine(d, "tiny2.bin"), Bytes(4, 10));   // below min size
            File.WriteAllBytes(Path.Combine(d, "unique.bin"), Bytes(5, 3 * 1024 * 1024));
            var groups = DuplicateFinder.FindIn(new[] { d }, 100 * 1024, null, CancellationToken.None);
            Check("exactly one duplicate group found", groups.Count == 1, "groups " + groups.Count);
            if (groups.Count == 1)
            {
                var names = groups[0].Select(Path.GetFileName).OrderBy(x => x).ToArray();
                Check("group = a1,a2,a3", string.Join(",", names) == "a1.bin,a2.bin,a3.bin", string.Join(",", names));
            }
            Check("partial-hash twin (c1/c2) NOT reported (full hash differs)", !groups.Any(g => g.Any(p => p.EndsWith("c1.bin"))));
            Check("below-min-size files ignored", !groups.Any(g => g.Any(p => p.Contains("tiny"))));
            Check("full SHA-256 is stable", DuplicateFinder.HashFull(Path.Combine(d, "a1.bin"), CancellationToken.None) == DuplicateFinder.HashFull(Path.Combine(d, "a2.bin"), CancellationToken.None));
        }

        // ---------------------------------------------------------------- large files
        static void LargeFiles(string dir)
        {
            string d = Path.Combine(dir, "large"); Directory.CreateDirectory(Path.Combine(d, "x"));
            int[] kb = { 50, 900, 300, 700, 10, 500 };
            for (int i = 0; i < kb.Length; i++) File.WriteAllBytes(Path.Combine(d, (i % 2 == 0 ? "" : @"x\") + "f" + i + ".dat"), new byte[kb[i] * 1024]);
            var top = LargeFileScanner.Scan(new[] { d }, 3, null, CancellationToken.None);
            Check("top 3 returned", top.Count == 3, "count " + top.Count);
            Check("sorted biggest first: 900,700,500 KB", string.Join(",", top.Select(t => t.Size / 1024)) == "900,700,500", string.Join(",", top.Select(t => t.Size / 1024)));
        }

        // ---------------------------------------------------------------- stale projects
        static void Stale(string dir)
        {
            string sp = Path.Combine(dir, "projects");
            Action<string, string, string, int> mk = (proj, marker, art, ageDays) =>
            {
                string p = Path.Combine(sp, proj); string a = Path.Combine(p, art);
                Directory.CreateDirectory(a); File.WriteAllText(Path.Combine(a, "x.js"), "x");
                if (marker != null) File.WriteAllText(Path.Combine(p, marker), "m");
                var t = DateTime.Now.AddDays(-ageDays);
                foreach (var f in Directory.GetFiles(p)) File.SetLastWriteTime(f, t);
                File.SetLastWriteTime(Path.Combine(a, "x.js"), t); Directory.SetLastWriteTime(a, t); Directory.SetLastWriteTime(p, t);
            };
            mk("oldnode", "package.json", "node_modules", 200);
            mk("newnode", "package.json", "node_modules", 3);
            mk("oldrust", "Cargo.toml", "target", 150);
            mk("notaproject", null, "node_modules", 400);        // no package.json: not recognised, never listed
            var r = StaleProjectScanner.Scan(new[] { sp }, 90, null, CancellationToken.None);
            var names = r.Select(x => Path.GetFileName(x.Project)).OrderBy(x => x).ToArray();
            Check("stale projects = oldnode, oldrust", string.Join(",", names) == "oldnode,oldrust", string.Join(",", names));
            Check("every hit is unticked by default", r.All(x => !x.IsChecked));
            Check("sizes measured", r.All(x => x.Size > 0));
            Check("recent project not listed", !names.Contains("newnode"));
            Check("Classify rejects unknown folders", StaleProjectScanner.Classify(Path.Combine(sp, "notaproject"), "node_modules") == null);
        }

        // ---------------------------------------------------------------- recycle bin only
        static void RecycleOnly(string dir)
        {
            string f = Path.Combine(dir, "cleanpc-selftest-recycle-me.txt"); File.WriteAllText(f, "throwaway");
            string res = CleanOps.RecycleUserFile(f);
            Check("throwaway file sent to the Recycle Bin", res == null && !File.Exists(f), res ?? "");
            Check("system file refused (never touched)", CleanOps.RecycleUserFile(@"C:\Windows\notepad.exe") != null);
        }

        // ---------------------------------------------------------------- startup toggle
        static void StartupToggle()
        {
            const string nm = "CleanPCProSelfTestEntry";
            const string rk = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string ak = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(rk)) k.SetValue(nm, "\"C:\\Windows\\System32\\notepad.exe\"");
                Func<StartupEntry> find = () => StartupManager.Collect(false, false).FirstOrDefault(e => e.Name == nm);
                var e1 = find();
                Check("test entry listed and enabled", e1 != null && e1.Enabled && e1.CanToggle);
                if (e1 == null) return;
                Check("disable ok", StartupManager.SetEnabled(e1, false) == null);
                var e2 = find();
                Check("re-read: disabled (StartupApproved)", e2 != null && !e2.Enabled);
                Check("entry still exists in Run key (nothing deleted)", Registry.CurrentUser.OpenSubKey(rk).GetValue(nm) != null);
                Check("enable ok", StartupManager.SetEnabled(e2, true) == null);
                var e3 = find(); Check("re-read: enabled again", e3 != null && e3.Enabled);
            }
            finally
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(rk, true)) k.DeleteValue(nm, false); } catch { }
                try { using (var k = Registry.CurrentUser.OpenSubKey(ak, true)) k.DeleteValue(nm, false); } catch { }
            }
        }

        // ---------------------------------------------------------------- security helpers
        static void SecurityHelpers()
        {
            Check("edit distance svchost/svch0st = 1", SecurityScanner.EditDistance("svchost", "svch0st") == 1);
            Check("risky location Temp", SecurityScanner.RiskyLocation(@"C:\Users\a\AppData\Local\Temp\x.exe") != null);
            Check("risky location Downloads", SecurityScanner.RiskyLocation(@"C:\Users\a\Downloads\x.exe") != null);
            Check("normal location not flagged", SecurityScanner.RiskyLocation(@"C:\Program Files\App\x.exe") == null);
            Check("CmdExePath quoted", StartupManager.CmdExePath("\"C:\\a b\\x.exe\" /s") == "C:\\a b\\x.exe");
            Check("CmdExePath unquoted with args", StartupManager.CmdExePath("C:\\ab\\x.exe -min") == "C:\\ab\\x.exe");
            var sig = SigInfo.Get(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot"), @"System32\kernel32.dll"));
            Check("signature API runs (kernel32 status read)", !string.IsNullOrEmpty(sig.Status), sig.Status);
            var ext = SecurityScanner.ReadExtensions();
            Check("extension audit runs, names only (" + ext.Count + " found)", ext != null);
            var def = SecurityScanner.ReadDefender(); Check("Defender status read does not throw (available=" + def.Available + ")", def != null);
        }

        // ---------------------------------------------------------------- updater
        static void Updater()
        {
            Func<string, string, int> cmp = (a, b) => SemVer.Parse(a).CompareTo(SemVer.Parse(b));
            Check("1.3.0-beta.1 < 1.3.0", cmp("v1.3.0-beta.1", "1.3.0") < 0);
            Check("1.3.0 < 1.3.1", cmp("1.3.0", "v1.3.1") < 0);
            Check("1.2.1 < 1.3.0-beta.1", cmp("1.2.1", "1.3.0-beta.1") < 0);
            Check("beta.10 > beta.2", cmp("1.3.0-beta.10", "1.3.0-beta.2") > 0);
            Check("equal versions compare 0", cmp("v1.3.0", "1.3.0") == 0);
            var bad = UpdateChecker.Check("Ivan-Ryukendo/this-repo-does-not-exist-xyz", "x.exe", AppInfo.InformationalVersion);
            if (bad.Status == CheckStatus.Error && bad.Message.Contains("reach")) { Console.WriteLine("SKIP  network tests (offline): " + bad.Message); return; }
            Check("404 handled as 'no release' (not a crash)", bad.Status == CheckStatus.NoRelease, bad.Status + " " + bad.Message);
            var pro = UpdateChecker.Check(AppInfo.ProRepo, AppInfo.ProAsset, AppInfo.InformationalVersion);
            Console.WriteLine("INFO  Pro channel: " + pro.Status + " - " + pro.Message);
            Check("Pro channel answer is a handled status", pro.Status != CheckStatus.Error || pro.Message.Length > 0);
            var std = UpdateChecker.Check(AppInfo.StandardRepo, AppInfo.StandardAsset, "0.0.1");
            Console.WriteLine("INFO  Standard channel: " + std.Status + " - " + std.Message + (std.Release != null ? " (" + std.Release.Tag + ", " + Fmt.Size(std.Release.AssetSize) + ")" : ""));
            Check("Standard channel real API returns a release with the CleanPC.exe asset", std.Status == CheckStatus.UpdateAvailable && std.Release != null && std.Release.AssetSize > 0, std.Status + " " + std.Message);
        }
    }
}
