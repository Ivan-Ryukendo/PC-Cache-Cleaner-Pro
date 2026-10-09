using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CleanPC.Pro
{
    public class StartupEntry : ObservableObject
    {
        public string Source { get; set; }       // "HKCU Run", "Startup folder (user)", "Scheduled task" ...
        public string Name { get; set; }         // value name / file name / task path+name
        public string Command { get; set; }
        public string Exe { get; set; }
        public string Publisher { get; set; } = "";
        public string Signature { get; set; } = "";
        public bool CanToggle { get; set; }
        // toggling info
        public RegistryHive Hive { get; set; }
        public string ApprovedKey { get; set; }  // e.g. Run, Run32, StartupFolder (under ...\Explorer\StartupApproved)
        public bool IsTask { get; set; }
        bool _en = true;
        public bool Enabled { get { return _en; } set { if (Set(ref _en, value)) { Raise("StateText"); } } }
        public string StateText { get { return Enabled ? "Enabled" : "Disabled"; } }
        string _st = ""; public string Status { get { return _st; } set { Set(ref _st, value); } }
        public string Trust { get { return string.IsNullOrEmpty(Publisher) ? Signature : Signature + " - " + Publisher; } }
    }

    /// <summary>
    /// Lists startup entries (Run keys, startup folders, non-Microsoft scheduled tasks) and enables/disables them REVERSIBLY:
    /// registry/folder entries through the same StartupApproved values Task Manager uses, tasks through schtasks /change.
    /// Nothing is ever deleted.
    /// </summary>
    public static class StartupManager
    {
        const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static string CmdExePath(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            string c = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (c.StartsWith("\"")) { int e = c.IndexOf('"', 1); if (e > 1) return c.Substring(1, e - 1); }
            var m = Regex.Match(c, @"^(.*?\.(exe|bat|cmd|com|ps1|vbs|vbe|js|jse|wsf|scr|lnk|dll|msi|cpl))(\s|$)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            return c.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        static bool IsDisabled(RegistryHive hive, string approvedKey, string valueName)
        {
            try
            {
                using (var b = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                using (var k = b.OpenSubKey(ApprovedBase + approvedKey))
                {
                    if (k == null) return false;
                    var v = k.GetValue(valueName) as byte[];
                    return v != null && v.Length > 0 && (v[0] & 1) == 1;
                }
            }
            catch { return false; }
        }

        public static List<StartupEntry> Collect(bool withSignatures, bool includeDisabledTasks)
        {
            var list = new List<StartupEntry>();
            var runs = new[]
            {
                new { H = RegistryHive.CurrentUser, K = @"Software\Microsoft\Windows\CurrentVersion\Run", S = "HKCU Run", A = "Run", Toggle = true },
                new { H = RegistryHive.CurrentUser, K = @"Software\Microsoft\Windows\CurrentVersion\RunOnce", S = "HKCU RunOnce", A = (string)null, Toggle = false },
                new { H = RegistryHive.LocalMachine, K = @"Software\Microsoft\Windows\CurrentVersion\Run", S = "HKLM Run", A = "Run", Toggle = true },
                new { H = RegistryHive.LocalMachine, K = @"Software\Microsoft\Windows\CurrentVersion\RunOnce", S = "HKLM RunOnce", A = (string)null, Toggle = false },
                new { H = RegistryHive.LocalMachine, K = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", S = "HKLM Run (32-bit)", A = "Run32", Toggle = true },
            };
            foreach (var r in runs)
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(r.H, RegistryView.Registry64))
                    using (var k = b.OpenSubKey(r.K))
                    {
                        if (k == null) continue;
                        foreach (var vn in k.GetValueNames())
                        {
                            if (string.IsNullOrEmpty(vn)) continue;
                            string cmd = Convert.ToString(k.GetValue(vn));
                            list.Add(new StartupEntry
                            {
                                Source = r.S, Name = vn, Command = cmd, Exe = CmdExePath(cmd), CanToggle = r.Toggle,
                                Hive = r.H, ApprovedKey = r.A, Enabled = !(r.Toggle && IsDisabled(r.H, r.A, vn))
                            });
                        }
                    }
                }
                catch (Exception e) { Log.Error("startup " + r.K, e); }
            }
            var folders = new[]
            {
                new { P = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup"), H = RegistryHive.CurrentUser, S = "Startup folder (user)" },
                new { P = Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", @"Microsoft\Windows\Start Menu\Programs\Startup"), H = RegistryHive.LocalMachine, S = "Startup folder (all users)" },
            };
            foreach (var f in folders)
            {
                try
                {
                    if (!Directory.Exists(f.P)) continue;
                    foreach (var fi in new DirectoryInfo(f.P).EnumerateFiles())
                    {
                        if (fi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        string exe = fi.FullName;
                        if (fi.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) exe = ShortcutTarget(fi.FullName) ?? fi.FullName;
                        list.Add(new StartupEntry { Source = f.S, Name = fi.Name, Command = fi.FullName, Exe = exe, CanToggle = true, Hive = f.H, ApprovedKey = "StartupFolder", Enabled = !IsDisabled(f.H, "StartupFolder", fi.Name) });
                    }
                }
                catch (Exception e) { Log.Error("startup folder", e); }
            }
            try { list.AddRange(Tasks(includeDisabledTasks)); } catch (Exception e) { Log.Error("startup tasks", e); }
            if (withSignatures)
                foreach (var s in list)
                {
                    if (string.IsNullOrEmpty(s.Exe)) continue;
                    var sig = SigInfo.Get(s.Exe); s.Signature = sig.Status; s.Publisher = sig.Signer;
                }
            return list;
        }

        static string ShortcutTarget(string lnk)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic sh = Activator.CreateInstance(t);
                dynamic sc = sh.CreateShortcut(lnk);
                string target = sc.TargetPath;
                System.Runtime.InteropServices.Marshal.ReleaseComObject(sc); System.Runtime.InteropServices.Marshal.ReleaseComObject(sh);
                return target;
            }
            catch { return null; }
        }

        static IEnumerable<StartupEntry> Tasks(bool includeDisabled)
        {
            string script = "Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' } | ForEach-Object { $a=@($_.Actions)[0]; [pscustomobject]@{ P=$_.TaskPath; N=$_.TaskName; S=[int]$_.State; X=[string]$a.Execute; A=[string]$a.Arguments } } | ConvertTo-Json -Compress";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string psExe = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", @"System32\WindowsPowerShell\v1.0\powershell.exe");
            string outp = Proc.Run(psExe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, 60000);
            int i = outp.IndexOfAny(new[] { '[', '{' });
            if (i < 0) yield break;
            object parsed;
            try { parsed = Json.Parse(outp.Substring(i)); } catch { yield break; }
            var items = new List<Dictionary<string, object>>();
            var arr = parsed as object[];
            if (arr != null) foreach (var o in arr) { var d = o as Dictionary<string, object>; if (d != null) items.Add(d); }
            else { var d = parsed as Dictionary<string, object>; if (d != null) items.Add(d); }
            foreach (var d in items)
            {
                string x = Convert.ToString(d.ContainsKey("X") ? d["X"] : "");
                if (string.IsNullOrWhiteSpace(x)) continue;
                int state = 0; try { state = Convert.ToInt32(d["S"]); } catch { }
                bool disabled = state == 1;
                if (disabled && !includeDisabled) continue;
                string a = Convert.ToString(d.ContainsKey("A") ? d["A"] : "");
                yield return new StartupEntry
                {
                    Source = "Scheduled task", Name = Convert.ToString(d["P"]) + Convert.ToString(d["N"]), Command = (x + " " + a).Trim(),
                    Exe = CmdExePath(x), CanToggle = true, IsTask = true, Enabled = !disabled
                };
            }
        }

        /// <summary>Reversibly enables or disables an entry. Returns null on success, else the reason.</summary>
        public static string SetEnabled(StartupEntry e, bool enable)
        {
            try
            {
                if (!e.CanToggle) return "this kind of entry cannot be toggled";
                if (e.IsTask)
                {
                    int code;
                    string o = Proc.Run("schtasks.exe", "/change /tn \"" + e.Name.Replace("\"", "") + "\" " + (enable ? "/enable" : "/disable"), 30000, out code);
                    if (code != 0) return "schtasks failed: " + o.Trim();
                    Log.Write("STARTUP " + (enable ? "ENABLED" : "DISABLED") + " task " + e.Name);
                    e.Enabled = enable; return null;
                }
                using (var b = RegistryKey.OpenBaseKey(e.Hive, RegistryView.Default))
                using (var k = b.CreateSubKey(ApprovedBase + e.ApprovedKey))
                {
                    var data = new byte[12];
                    if (enable) data[0] = 2;
                    else { data[0] = 3; BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); }
                    k.SetValue(e.Name, data, RegistryValueKind.Binary);
                }
                Log.Write("STARTUP " + (enable ? "ENABLED" : "DISABLED") + " " + e.Source + " " + e.Name);
                e.Enabled = enable; return null;
            }
            catch (Exception ex) { Log.Error("startup toggle " + e.Name, ex); return ex.Message; }
        }
    }
}
