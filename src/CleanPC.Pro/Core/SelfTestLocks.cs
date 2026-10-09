using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CleanPC.Pro
{
    /// <summary>Self-tests for locked-file diagnosis, the guarded close/retry flow and the storage-owners mapping. Throwaway folders only.</summary>
    public static partial class SelfTest
    {
        static Process StartHidden(string psCommand)
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"" + psCommand + "\"")
            { UseShellExecute = false, CreateNoWindow = true };
            return Process.Start(psi);
        }

        static bool WaitLocked(string file, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                try { using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException) { return true; }
                Thread.Sleep(150);
            }
            return false;
        }

        // ---------------------------------------------------------------- process safety rules
        static void ProcessRules()
        {
            string win = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
            string sys32 = Path.Combine(win, "System32");
            int me = Process.GetCurrentProcess().Id;
            Check("never: PID 0", ProcessGuard.ProtectedReason(0, "Idle", null, 1, null) != null);
            Check("never: PID 4 System", ProcessGuard.ProtectedReason(4, "System", null, 1, null) != null);
            Check("never: this program (own PID)", ProcessGuard.ProtectedReason(me, "notepad", Path.Combine(sys32, "notepad.exe"), 1, null) != null);
            Check("never: another CleanPC-Pro window", ProcessGuard.ProtectedReason(99999, "CleanPC-Pro", @"D:\x\CleanPC-Pro.exe", 1, null) != null);
            foreach (var n in new[] { "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost", "explorer", "dwm", "MsMpEng", "System" })
                Check("never: " + n, ProcessGuard.ProtectedReason(5000, n, Path.Combine(sys32, n + ".exe"), 1, null) != null);
            Check("never: service host under WINDIR", ProcessGuard.ProtectedReason(5000, "somesvc", Path.Combine(sys32, "somesvc.exe"), 3, "SomeSvc") != null);
            Check("never: RM critical type", ProcessGuard.ProtectedReason(5000, "x", @"D:\x\x.exe", 1000, null) != null);
            Check("never: Explorer type", ProcessGuard.ProtectedReason(5000, "x", @"D:\x\x.exe", 4, null) != null);
            Check("never: path unreadable (cannot identify)", ProcessGuard.ProtectedReason(5000, "mystery", null, 1, null) != null);
            Check("offered: ordinary window app (notepad)", ProcessGuard.ProtectedReason(5000, "notepad", Path.Combine(sys32, "notepad.exe"), 1, null) == null);
            Check("offered: ordinary app in Program Files", ProcessGuard.ProtectedReason(5000, "winword", @"C:\Program Files\Office\winword.exe", 1, null) == null);
            var self = new LockHolder { Pid = me, Name = "x", ExePath = @"D:\x\x.exe" };
            Check("Graceful() refuses own PID without touching it", ProcessCloser.Graceful(self).Outcome == CloseOutcome.Refused);
            Check("Force() refuses own PID without touching it", ProcessCloser.Force(self).Outcome == CloseOutcome.Refused);
            var sv = new LockHolder { Pid = 5000, Name = "svchost", ExePath = Path.Combine(sys32, "svchost.exe") };
            Check("Force() refuses svchost", ProcessCloser.Force(sv).Outcome == CloseOutcome.Refused);
        }

        // ---------------------------------------------------------------- structured skips, Restart Manager, close, retry
        static void LockFlow(string dir)
        {
            string root = Path.Combine(dir, @"lockflow\cache");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            string held = Path.Combine(root, @"sub\held.bin");
            File.WriteAllBytes(held, new byte[4321]);
            File.WriteAllBytes(Path.Combine(root, "free.bin"), new byte[100]);

            // child process that holds the file open exclusively AND owns a window (so a graceful close is possible)
            string ps = "$f=[IO.File]::Open('" + held + "','Open','ReadWrite','None'); Add-Type -AssemblyName System.Windows.Forms; " +
                        "$form=New-Object Windows.Forms.Form; $form.Text='CleanPCProSelfTestHolder'; $form.WindowState='Minimized'; $form.ShowDialog() | Out-Null";
            Process child = StartHidden(ps);
            try
            {
                Check("child process holds the file", WaitLocked(held, 25000));
                for (int i = 0; i < 100 && child.MainWindowHandle == IntPtr.Zero; i++) { Thread.Sleep(150); child.Refresh(); }
                Check("child process has a window", child.MainWindowHandle != IntPtr.Zero);

                var target = new Target { Name = "lockflow", Kind = TargetKind.Contents, Paths = new List<string> { root }, Size = 4421, IsChecked = true };
                var res = CleanRunner.Run(new List<Target> { target }, null, CancellationToken.None, null);
                Check("clean freed the unlocked file only", res.Freed == 100 && !File.Exists(Path.Combine(root, "free.bin")) && File.Exists(held), "freed " + res.Freed);
                Check("skip kept structurally (1 locked item)", res.Locked.Count == 1, "n=" + res.Locked.Count);
                var sk = res.Locked.FirstOrDefault();
                Check("skip has path, reason, size, root", sk != null && sk.Path == held && !string.IsNullOrEmpty(sk.Reason) && sk.Bytes == 4321 && sk.Root == root && sk.Locked,
                    sk == null ? "none" : sk.Path + " | " + sk.Reason + " | " + sk.Bytes + " | " + sk.Root);

                var diag = RestartManager.Diagnose(res.Locked, CancellationToken.None, null);
                var h = diag.Holders.FirstOrDefault(x => x.Pid == child.Id);
                Check("Restart Manager names the holding process (PID " + child.Id + ")", h != null, "holders: " + string.Join(", ", diag.Holders.Select(x => x.Name + "#" + x.Pid)) + " err=" + diag.Error);
                if (h == null) return;
                Check("holder name is powershell", string.Equals(h.Name, "powershell", StringComparison.OrdinalIgnoreCase), h.Name);
                Check("holder path resolved", !string.IsNullOrEmpty(h.ExePath) && h.ExePath.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase), h.ExePath);
                Check("holder holds 1 file / 4321 bytes", h.FileCount == 1 && h.Bytes == 4321, h.FileCount + " / " + h.Bytes);
                Check("holder has an app type text", !string.IsNullOrEmpty(h.TypeText));
                Check("holder is not protected (may be offered)", h.ProtectedReason == null, h.ProtectedReason);

                // retry while STILL locked: nothing forced, file stays
                var again0 = new SkipList();
                long f0 = CleanRunner.RetrySkipped(res.Locked, again0);
                Check("retry while locked frees nothing and keeps the file", f0 == 0 && File.Exists(held) && again0.Items.Count(x => x.Locked) == 1);

                // tampered retry items are refused by the guard
                string other = Path.Combine(dir, @"lockflow\other.txt"); File.WriteAllText(other, "keep");
                var tamper = new List<SkipInfo>
                {
                    new SkipInfo { Path = other, Root = root, Locked = true },                                         // outside its root
                    new SkipInfo { Path = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", @"System32\notepad.exe"), Root = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "System32"), Locked = true },   // guarded root
                    new SkipInfo { Path = other, Root = null, Locked = true }                                          // no root
                };
                var sk3 = new SkipList();
                CleanRunner.RetrySkipped(tamper, sk3);
                Check("retry refuses outside-root / blocked-root / rootless items", File.Exists(other) && sk3.Count(x => x.StartsWith("BLOCKED")) == 3, string.Join(" | ", sk3));

                // graceful close of the harmless test process
                var cr = ProcessCloser.Graceful(h, 15000);
                Check("graceful close of the test process worked", cr.Outcome == CloseOutcome.Closed && child.HasExited, cr.Outcome + ": " + cr.Message);
                Thread.Sleep(300);

                // retry now
                var again = new SkipList();
                long f1 = CleanRunner.RetrySkipped(res.Locked, again);
                Check("retry after the close freed the held file", f1 == 4321 && !File.Exists(held) && again.Items.Count == 0, "freed " + f1 + " " + string.Join(" | ", again));
                Check("retry kept the cache folder itself", Directory.Exists(root));
            }
            finally { try { if (!child.HasExited) child.Kill(); } catch { } child.Dispose(); }
        }

        static void ForceAndGone()
        {
            Process p = StartHidden("Start-Sleep -Seconds 120");
            try
            {
                Thread.Sleep(1500);
                string path = ProcessGuard.ImagePath(p.Id);
                var h = new LockHolder { Pid = p.Id, Name = "powershell", ExePath = path, AppType = 5, StartFileTime = p.StartTime.ToFileTime() };
                var g = ProcessCloser.Graceful(h, 1000);
                Check("graceful on a window-less process does not kill it", (g.Outcome == CloseOutcome.NoWindow || g.Outcome == CloseOutcome.StillRunning) && !p.HasExited, g.Outcome + ": " + g.Message);
                var wrong = new LockHolder { Pid = p.Id, Name = "powershell", ExePath = path, AppType = 5, StartFileTime = p.StartTime.ToFileTime() + 600000000L };
                var wr = ProcessCloser.Force(wrong);
                Check("force refuses a PID whose start time does not match (PID reuse)", wr.Outcome == CloseOutcome.Gone && !p.HasExited, wr.Outcome + ": " + wr.Message);
                var f = ProcessCloser.Force(h);
                Check("explicit force end of the test process", f.Outcome == CloseOutcome.Closed && p.HasExited, f.Outcome + ": " + f.Message);
                var again = ProcessCloser.Force(h);
                Check("force on an already-gone process is harmless", again.Outcome == CloseOutcome.Gone, again.Outcome + "");
            }
            finally { try { if (!p.HasExited) p.Kill(); } catch { } p.Dispose(); }
        }

        // ---------------------------------------------------------------- storage owners
        static void Owners(string dir)
        {
            string s = Path.Combine(dir, "owners");
            string pf = Path.Combine(s, "PF"), local = Path.Combine(s, "Local"), roam = Path.Combine(s, "Roaming");
            string inst = Path.Combine(pf, @"Acme Tools");
            Directory.CreateDirectory(Path.Combine(inst, "bin")); Directory.CreateDirectory(Path.Combine(local, "AcmeTools")); Directory.CreateDirectory(Path.Combine(roam, "Acme Studio"));
            Directory.CreateDirectory(Path.Combine(local, @"Acme\Studio")); Directory.CreateDirectory(Path.Combine(local, "Random")); Directory.CreateDirectory(Path.Combine(local, "Microsoft"));
            Directory.CreateDirectory(Path.Combine(s, "Big"));
            File.WriteAllBytes(Path.Combine(inst, @"bin\a.dll"), new byte[3000]); File.WriteAllBytes(Path.Combine(inst, "readme.txt"), new byte[200]);
            File.WriteAllBytes(Path.Combine(local, @"AcmeTools\c.bin"), new byte[1000]); File.WriteAllBytes(Path.Combine(roam, @"Acme Studio\s.bin"), new byte[500]);
            File.WriteAllBytes(Path.Combine(local, @"Acme\Studio\v.bin"), new byte[200]); File.WriteAllBytes(Path.Combine(local, @"Random\r.bin"), new byte[9999]);
            File.WriteAllBytes(Path.Combine(local, @"Microsoft\m.bin"), new byte[7777]); File.WriteAllBytes(Path.Combine(s, @"Big\big.bin"), new byte[50000]);
            int code; Proc.Run("cmd.exe", "/c mklink /J \"" + Path.Combine(inst, "link") + "\" \"" + Path.Combine(s, "Big") + "\"", 15000, out code);
            Check("owners: junction created", code == 0);

            var procs = new List<ProcInfo>
            {
                new ProcInfo { Pid = 1, Name = "acmeapp", ExePath = Path.Combine(inst, @"bin\acmeapp.exe"), Product = "Acme Studio", Company = "Acme Corp" },
                new ProcInfo { Pid = 2, Name = "acmehelper", ExePath = Path.Combine(inst, @"bin\acmehelper.exe"), Product = "Acme Studio", Company = "Acme Corp" },
                new ProcInfo { Pid = 3, Name = "rootexe", ExePath = @"C:\rootexe.exe" },
                new ProcInfo { Pid = 4, Name = "winthing", ExePath = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", @"System32\winthing.exe") },
            };
            var rows = StorageOwners.Build(procs, new[] { pf }, new[] { local, roam }, CancellationToken.None);
            Check("owners: processes in one install folder become one row; drive-root and Windows programs are dropped", rows.Count == 1 && rows[0].ProcessCount == 2, "rows " + rows.Count);
            var r = rows[0];
            Check("owners: install folder is the folder below Program Files", r.InstallDir == inst, r.InstallDir);
            Check("owners: name taken from the product", r.Name == "Acme Studio", r.Name);
            var data = r.Folders.Where(f => !f.IsInstall).Select(f => f.Path).OrderBy(x => x).ToList();
            Check("owners: 3 likely data folders (folder name, product name, vendor\\product)", data.Count == 3, string.Join(" | ", data));
            Check("owners: unrelated and generic folders are not matched", !data.Any(d => d.Contains("Random") || d.EndsWith("Microsoft")) && !data.Contains(Path.Combine(local, "Acme")), string.Join(" | ", data));
            Check("owners: matches are labelled likely", r.Folders.Where(f => !f.IsInstall).All(f => f.Kind == "Likely data") && r.MatchText.Contains("likely"));
            StorageOwners.SizeAll(rows, CancellationToken.None, null, null);
            Check("owners: install size excludes the junction target", r.InstallSize == 3200, "install " + r.InstallSize);
            Check("owners: data size = 1000 + 500 + 200", r.DataSize == 1700, "data " + r.DataSize);
            Check("owners: total = install + data", r.Total == 4900, "total " + r.Total);
            Check("owners: nothing is deleted by measuring", File.Exists(Path.Combine(s, @"Big\big.bin")) && File.Exists(Path.Combine(local, @"Random\r.bin")));

            var cts = new CancellationTokenSource(); cts.Cancel();
            var rows2 = StorageOwners.Build(procs, new[] { pf }, new[] { local, roam }, CancellationToken.None);
            StorageOwners.SizeAll(rows2, cts.Token, null, null);
            Check("owners: cancelled sizing returns without throwing", true);
            Check("owners: InstallRoot for a drive root / Windows program is null", StorageOwners.InstallRoot(@"C:\x.exe", new string[0]) == null);
        }
    }
}
