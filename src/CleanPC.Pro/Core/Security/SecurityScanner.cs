using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;

namespace CleanPC.Pro
{
    public class SecItem : ObservableObject
    {
        public string Severity { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public int Pid { get; set; }
        public string PidText { get { return Pid > 0 ? Pid.ToString() : ""; } }
        public string Path { get; set; }
        public string Signature { get; set; }
        public string Signer { get; set; }
        public string SigText { get { return string.IsNullOrEmpty(Signer) ? Signature : Signature + " (" + Signer + ")"; } }
        public string Reasons { get; set; }
        public string Sha256 { get; set; }
        public string Connections { get; set; }
        public string VtUrl { get { return string.IsNullOrEmpty(Sha256) ? null : "https://www.virustotal.com/gui/file/" + Sha256; } }
        public int Order { get { return Severity == "High" ? 0 : Severity == "Medium" ? 1 : 2; } }
    }

    public class DefenderInfo
    {
        public bool Available; public bool? Realtime, AMService; public int? SigAgeDays; public DateTime? LastFullScan, LastQuickScan;
        public List<string> Advice = new List<string>();
        public string Summary
        {
            get
            {
                if (!Available) return "";
                return string.Format("Defender: real-time protection {0}, service {1}, definitions {2} day(s) old, last full scan {3}.",
                    Realtime, AMService, SigAgeDays, LastFullScan.HasValue ? LastFullScan.Value.ToString("yyyy-MM-dd") : "never/unknown");
            }
        }
    }

    public class BrowserExt { public string Browser { get; set; } public string Profile { get; set; } public string Name { get; set; } public string Id { get; set; } public string Version { get; set; } }

    public class SecurityReport
    {
        public List<SecItem> Items = new List<SecItem>();
        public DefenderInfo Defender = new DefenderInfo();
        public List<BrowserExt> Extensions = new List<BrowserExt>();
        public int Processes, StartupEntries, SignedAppDataStartup;
    }

    /// <summary>
    /// Port of Get-SecurityReport. READ-ONLY heuristics: results mean "suspicious, review", never confirmed malware.
    /// Never kills, deletes or modifies anything. Hashes are computed locally; nothing is uploaded.
    /// </summary>
    public static class SecurityScanner
    {
        public static int EditDistance(string a, string b)
        {
            int n = a.Length, m = b.Length;
            if (n == 0) return m; if (m == 0) return n;
            var d = new int[n + 1, m + 1];
            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;
            for (int i = 1; i <= n; i++)
                for (int j = 1; j <= m; j++)
                {
                    int c = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + c);
                }
            return d[n, m];
        }

        public static string RiskyLocation(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (Regex.IsMatch(s, @"\\AppData\\Local\\Temp\\|\\Windows\\Temp\\|\\Temp\\", RegexOptions.IgnoreCase)) return "runs from a Temp folder";
            if (Regex.IsMatch(s, @"\\Downloads\\", RegexOptions.IgnoreCase)) return "runs from Downloads";
            if (Regex.IsMatch(s, @"\\Users\\Public\\", RegexOptions.IgnoreCase)) return "runs from Users\\Public";
            if (Regex.IsMatch(s, @"\$Recycle\.Bin", RegexOptions.IgnoreCase)) return "runs from the Recycle Bin";
            return null;
        }

        static bool StartsWithPath(string p, string root) { return !string.IsNullOrEmpty(root) && p.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }

        public static SecurityReport Run(IProgress<string> prog, CancellationToken ct)
        {
            var rep = new SecurityReport();
            string win = Environment.GetEnvironmentVariable("SystemRoot");
            string pf = Environment.GetEnvironmentVariable("ProgramFiles"), pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            Func<string, bool> trusted = p => StartsWithPath(p, win) || StartsWithPath(p, pf) || StartsWithPath(p, pf86) || p.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
            var sysNames = new Dictionary<string, string>
            {
                { "svchost.exe", win + "\\System32" }, { "lsass.exe", win + "\\System32" }, { "csrss.exe", win + "\\System32" }, { "smss.exe", win + "\\System32" },
                { "services.exe", win + "\\System32" }, { "winlogon.exe", win + "\\System32" }, { "wininit.exe", win + "\\System32" }, { "taskhostw.exe", win + "\\System32" },
                { "spoolsv.exe", win + "\\System32" }, { "dwm.exe", win + "\\System32" }, { "conhost.exe", win + "\\System32" }, { "sihost.exe", win + "\\System32" }, { "explorer.exe", win }
            };

            prog.Report("Reading startup entries and scheduled tasks...");
            var startup = StartupManager.Collect(false, false).Where(s => s.Enabled).ToList();
            rep.StartupEntries = startup.Count;
            int i = 0;
            foreach (var s in startup)
            {
                if (ct.IsCancellationRequested) return rep;
                i++; if (i % 5 == 0) prog.Report("Checking startup entry " + i + "/" + startup.Count + "...");
                var reasons = new List<string>(); string sev = "Low"; SigResult sig = null;
                string loc = RiskyLocation(s.Command) ?? RiskyLocation(s.Exe);
                if (loc != null) { reasons.Add("startup entry that " + loc); sev = "High"; }
                else if (Regex.IsMatch(s.Command ?? "", @"\\AppData\\", RegexOptions.IgnoreCase) || Regex.IsMatch(s.Exe ?? "", @"\\AppData\\", RegexOptions.IgnoreCase))
                {
                    sig = SigInfo.Get(s.Exe);
                    if (sig.Status != "Valid") { reasons.Add("startup entry in AppData with signature '" + sig.Status + "'"); sev = "Medium"; }
                    else rep.SignedAppDataStartup++;
                }
                if (reasons.Count > 0)
                {
                    if (sig == null) sig = SigInfo.Get(s.Exe);
                    rep.Items.Add(new SecItem { Type = "Startup", Severity = sev, Name = s.Name + "  [" + s.Source + "]", Path = s.Exe ?? "", Signature = sig.Status, Signer = sig.Signer, Reasons = string.Join("; ", reasons) });
                }
            }
            var startupPaths = new HashSet<string>(startup.Where(s => !string.IsNullOrEmpty(s.Exe)).Select(s => s.Exe.ToLowerInvariant()));

            // running processes
            prog.Report("Listing processes...");
            var procs = new List<Tuple<int, string, string>>();
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT ProcessId,Name,ExecutablePath FROM Win32_Process"))
                    foreach (ManagementObject o in q.Get())
                        procs.Add(Tuple.Create(Convert.ToInt32(o["ProcessId"]), Convert.ToString(o["Name"]), Convert.ToString(o["ExecutablePath"])));
            }
            catch (Exception e) { Log.Error("security process list", e); }
            rep.Processes = procs.Count;
            var winH = new Dictionary<int, long>();
            Func<Dictionary<int, double>> sample = () =>
            {
                var d = new Dictionary<int, double>();
                foreach (var gp in Process.GetProcesses())
                {
                    try { d[gp.Id] = gp.TotalProcessorTime.TotalSeconds; if (!winH.ContainsKey(gp.Id)) winH[gp.Id] = gp.MainWindowHandle.ToInt64(); } catch { }
                    finally { gp.Dispose(); }
                }
                return d;
            };
            prog.Report("Sampling CPU (5 s)...");
            int cores = Environment.ProcessorCount;
            var c0 = sample(); var t0 = DateTime.UtcNow;
            if (ct.WaitHandle.WaitOne(2500)) return rep;
            var c1 = sample(); var t1 = DateTime.UtcNow;
            if (ct.WaitHandle.WaitOne(2500)) return rep;
            var c2 = sample(); var t2 = DateTime.UtcNow;
            double e1 = (t1 - t0).TotalSeconds, e2 = (t2 - t1).TotalSeconds;
            int n = 0;
            foreach (var p in procs)
            {
                if (ct.IsCancellationRequested) return rep;
                n++; if (n % 15 == 0) prog.Report("Checking process " + n + "/" + procs.Count + "...");
                int pid = p.Item1; if (pid <= 4) continue;
                string path = p.Item3; if (string.IsNullOrEmpty(path)) continue;
                string name = p.Item2, lname = name.ToLowerInvariant();
                var reasons = new List<string>(); string sev = "Low";
                string loc = RiskyLocation(path);
                if (loc != null) { reasons.Add(loc); sev = "High"; }
                string expected;
                if (sysNames.TryGetValue(lname, out expected))
                {
                    string dir = Path.GetDirectoryName(path);
                    bool ok = dir.Equals(expected, StringComparison.OrdinalIgnoreCase) || (lname != "explorer.exe" && dir.Equals(win + "\\SysWOW64", StringComparison.OrdinalIgnoreCase));
                    if (!ok) { reasons.Add("has the system name '" + name + "' but runs from '" + dir + "' (expected " + expected + ")"); sev = "High"; }
                }
                else
                {
                    string bs = Path.GetFileNameWithoutExtension(lname);
                    foreach (var sn in sysNames.Keys)
                    {
                        string sb = Path.GetFileNameWithoutExtension(sn);
                        if (sb.Length >= 5 && bs.Length >= 4 && Math.Abs(bs.Length - sb.Length) <= 1 && EditDistance(bs, sb) <= (sb.Length >= 7 ? 2 : 1))
                        { reasons.Add("name looks like the system process '" + sn + "'"); if (sev != "High") sev = "Medium"; break; }
                    }
                }
                bool isTrusted = trusted(path); SigResult sig = null;
                if (!isTrusted)
                {
                    sig = SigInfo.Get(path);
                    if (sig.Status != "Valid") { reasons.Add("signature: " + sig.Status + " (outside Windows/Program Files)"); if (sev == "Low") sev = "Medium"; }
                }
                long wh;
                if (!isTrusted && startupPaths.Contains(path.ToLowerInvariant()) && winH.TryGetValue(pid, out wh) && wh == 0)
                {
                    reasons.Add("no visible window and registered to start automatically (possible hidden persistence)");
                    if (sev == "Low" && sig != null && sig.Status != "Valid") sev = "Medium";
                }
                double a0, a1, a2;
                if (c0.TryGetValue(pid, out a0) && c1.TryGetValue(pid, out a1) && c2.TryGetValue(pid, out a2) && e1 > 0 && e2 > 0)
                {
                    double u1 = (a1 - a0) / e1 / cores * 100, u2 = (a2 - a1) / e2 / cores * 100;
                    if (u1 >= 50 && u2 >= 50) reasons.Add(string.Format("very high CPU: ~{0:N0}% of the whole CPU over 5 s", (u1 + u2) / 2));
                }
                if (reasons.Count > 0)
                {
                    if (sig == null) sig = SigInfo.Get(path);
                    rep.Items.Add(new SecItem { Type = "Process", Severity = sev, Name = name, Pid = pid, Path = path, Signature = sig.Status, Signer = sig.Signer, Reasons = string.Join("; ", reasons) });
                }
            }
            // hashes (local only) + network connections for flagged items
            prog.Report("Hashing flagged files (SHA-256) and reading connections...");
            var conns = NetConnections.Read();
            foreach (var it in rep.Items)
            {
                if (ct.IsCancellationRequested) break;
                if (!string.IsNullOrEmpty(it.Path)) it.Sha256 = SigInfo.Sha256(it.Path, ct);
                if (it.Type == "Process" && it.Pid > 0)
                {
                    var mine = conns.Where(c => c.Pid == it.Pid).Select(c => c.Text).Take(12).ToList();
                    it.Connections = mine.Count == 0 ? "no network connections seen" : string.Join("\n", mine);
                }
            }
            rep.Items = rep.Items.OrderBy(x => x.Order).ThenBy(x => x.Type).ThenBy(x => x.Name).ToList();
            prog.Report("Reading Defender status...");
            rep.Defender = ReadDefender();
            prog.Report("Auditing browser extension names...");
            rep.Extensions = ReadExtensions();
            return rep;
        }

        public static DefenderInfo ReadDefender()
        {
            var o = new DefenderInfo();
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Defender");
                scope.Connect();
                using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_MpComputerStatus")))
                    foreach (ManagementObject m in q.Get())
                    {
                        o.Available = true;
                        o.Realtime = m["RealTimeProtectionEnabled"] as bool?; o.AMService = m["AMServiceEnabled"] as bool?;
                        o.SigAgeDays = null;
                        DateTime? sigUp = Dt(m["AntivirusSignatureLastUpdated"]);
                        if (sigUp.HasValue) o.SigAgeDays = (int)(DateTime.Now - sigUp.Value).TotalDays;
                        o.LastFullScan = Dt(m["FullScanEndTime"]); o.LastQuickScan = Dt(m["QuickScanEndTime"]);
                        if (o.Realtime == false) o.Advice.Add("Real-time protection is OFF (unless another antivirus is active) - consider turning it on.");
                        if (o.SigAgeDays.HasValue && o.SigAgeDays.Value > 3) o.Advice.Add("Virus definitions are " + o.SigAgeDays + " days old - update them in Windows Security.");
                        break;
                    }
                if (!o.Available) o.Advice.Add("Windows Defender status is unavailable (a third-party antivirus may be managing protection).");
            }
            catch { o.Advice.Add("Windows Defender status is unavailable (a third-party antivirus may be managing protection)."); }
            o.Advice.Add("Recommended: run a Defender FULL scan (Windows Security > Virus & threat protection > Scan options > Full scan, or admin PowerShell: Start-MpScan -ScanType FullScan). This tool does not run it for you.");
            return o;
        }

        static DateTime? Dt(object v)
        {
            try
            {
                if (v == null) return null;
                if (v is DateTime) return (DateTime)v;
                string s = v as string; if (string.IsNullOrEmpty(s)) return null;
                var d = ManagementDateTimeConverter.ToDateTime(s);
                return d.Year < 1990 ? (DateTime?)null : d;
            }
            catch { return null; }
        }

        // ---- browser extensions: NAMES ONLY, read-only; never touches cookies/logins/history ----
        public static List<BrowserExt> ReadExtensions()
        {
            var res = new List<BrowserExt>();
            string L = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", R = Environment.GetEnvironmentVariable("APPDATA") ?? "";
            var roots = new List<Tuple<string, string>>
            {
                Tuple.Create("Google Chrome", L + @"\Google\Chrome\User Data"), Tuple.Create("Microsoft Edge", L + @"\Microsoft\Edge\User Data"),
                Tuple.Create("Brave", L + @"\BraveSoftware\Brave-Browser\User Data"), Tuple.Create("Vivaldi", L + @"\Vivaldi\User Data"),
                Tuple.Create("Chromium", L + @"\Chromium\User Data"), Tuple.Create("Opera", R + @"\Opera Software\Opera Stable"),
            };
            foreach (var r in roots)
            {
                try
                {
                    if (!Directory.Exists(r.Item2)) continue;
                    var profiles = new List<string>();
                    if (Directory.Exists(Path.Combine(r.Item2, "Extensions"))) profiles.Add(r.Item2);
                    foreach (var d in new DirectoryInfo(r.Item2).EnumerateDirectories())
                        if (d.Name == "Default" || d.Name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) profiles.Add(d.FullName);
                    foreach (var prof in profiles)
                    {
                        string ext = Path.Combine(prof, "Extensions");
                        if (!Directory.Exists(ext)) continue;
                        foreach (var idDir in new DirectoryInfo(ext).EnumerateDirectories())
                        {
                            if (idDir.Name.Equals("Temp", StringComparison.OrdinalIgnoreCase)) continue;
                            var ver = idDir.EnumerateDirectories().OrderByDescending(v => v.Name).FirstOrDefault();
                            if (ver == null) continue;
                            string mf = Path.Combine(ver.FullName, "manifest.json");
                            if (!File.Exists(mf)) continue;
                            try
                            {
                                var m = Json.Parse(File.ReadAllText(mf)) as Dictionary<string, object>;
                                if (m == null) continue;
                                string name = m.ContainsKey("name") ? Convert.ToString(m["name"]) : idDir.Name;
                                if (name.StartsWith("__MSG_")) name = ResolveMsg(ver.FullName, name, m) ?? name;
                                res.Add(new BrowserExt { Browser = r.Item1, Profile = prof == r.Item2 ? "(default)" : Path.GetFileName(prof), Name = name, Id = idDir.Name, Version = m.ContainsKey("version") ? Convert.ToString(m["version"]) : "" });
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception e) { Log.Error("extensions " + r.Item1, e); }
            }
            return res.OrderBy(x => x.Browser).ThenBy(x => x.Profile).ThenBy(x => x.Name).ToList();
        }

        static string ResolveMsg(string verDir, string token, Dictionary<string, object> manifest)
        {
            try
            {
                string key = token.Substring(6).TrimEnd('_');
                var langs = new List<string>();
                if (manifest.ContainsKey("default_locale")) langs.Add(Convert.ToString(manifest["default_locale"]));
                langs.AddRange(new[] { "en", "en_US", "en_GB" });
                foreach (var l in langs)
                {
                    string f = Path.Combine(verDir, "_locales", l, "messages.json");
                    if (!File.Exists(f)) continue;
                    var msgs = Json.Parse(File.ReadAllText(f)) as Dictionary<string, object>;
                    if (msgs == null) continue;
                    foreach (var kv in msgs)
                        if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            var inner = kv.Value as Dictionary<string, object>;
                            if (inner != null && inner.ContainsKey("message")) return Convert.ToString(inner["message"]);
                        }
                }
            }
            catch { }
            return null;
        }
    }
}
