using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CleanPC.Pro
{
    /// <summary>
    /// Decides which processes may EVER be offered for closing, and does the (user-confirmed, one-process-at-a-time)
    /// close. Nothing here runs by itself: the UI calls Graceful/Force only after the user confirmed that exact process.
    /// </summary>
    public static class ProcessGuard
    {
        // lower-case image names (no .exe) that are never offered
        static readonly HashSet<string> Never = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "system", "system idle process", "registry", "memory compression", "secure system", "smss", "csrss", "wininit", "winlogon",
            "services", "lsass", "lsm", "svchost", "explorer", "dwm", "msmpeng", "nissrv", "securityhealthservice", "fontdrvhost",
            "sihost", "taskhostw", "runtimebroker", "audiodg", "spoolsv", "wudfhost", "dllhost", "conhost", "ctfmon", "searchindexer",
            "shellexperiencehost", "startmenuexperiencehost", "textinputhost", "lockapp", "wmiprvse", "trustedinstaller", "tiworker" };

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        /// <summary>Full image path of a process (works for most processes even when MainModule is denied). Null if unreadable.</summary>
        public static string ImagePath(int pid)
        {
            if (pid <= 4) return null;
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>null = this process may be offered for closing; otherwise the reason it is protected.</summary>
        public static string ProtectedReason(int pid, string name, string exePath, int appType, string service)
        {
            if (pid <= 4) return "system process (PID " + pid + ")";
            int me = Process.GetCurrentProcess().Id;
            if (pid == me) return "this program itself";
            string n = (name ?? "").Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            string file = "";
            try { if (!string.IsNullOrEmpty(exePath)) file = Path.GetFileNameWithoutExtension(exePath); } catch { }
            if (Never.Contains(n) || (file.Length > 0 && Never.Contains(file))) return "critical Windows / security process";
            if (n.StartsWith("CleanPC", StringComparison.OrdinalIgnoreCase) || file.StartsWith("CleanPC", StringComparison.OrdinalIgnoreCase)) return "this program (another window of it)";
            if (appType == 1000) return "critical system process";
            if (appType == 4) return "Windows Explorer";
            if (appType == 3 || !string.IsNullOrEmpty(service))
            {
                string win = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
                if (string.IsNullOrEmpty(exePath) || exePath.StartsWith(win + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "Windows service";
                return "runs as a service (" + (string.IsNullOrEmpty(service) ? "service" : service) + ")";
            }
            if (string.IsNullOrEmpty(exePath)) return "cannot identify the program (path not readable)";
            return null;
        }
    }

    public enum CloseOutcome { Closed, StillRunning, NoWindow, Denied, Refused, Gone, Failed }

    public class CloseResult
    {
        public CloseOutcome Outcome; public string Message;
        public CloseResult(CloseOutcome o, string m) { Outcome = o; Message = m; }
    }

    /// <summary>Closing a running program. The caller MUST have asked the user first; each call acts on exactly one process.</summary>
    public static class ProcessCloser
    {
        /// <summary>Returns the live process only if PID and start time still match the one that was diagnosed (PID reuse safe).</summary>
        static Process Resolve(LockHolder h, out CloseResult fail)
        {
            fail = null;
            string why = ProcessGuard.ProtectedReason(h.Pid, h.Name, h.ExePath, h.AppType, h.Service);
            if (why != null) { fail = new CloseResult(CloseOutcome.Refused, "Not allowed: " + why); return null; }
            Process p;
            try { p = Process.GetProcessById(h.Pid); }
            catch (ArgumentException) { fail = new CloseResult(CloseOutcome.Gone, "The program is no longer running."); return null; }
            try
            {
                if (h.StartFileTime != 0 && Math.Abs(p.StartTime.ToFileTime() - h.StartFileTime) > 20000000L)   // > 2 s apart = a different process
                { p.Dispose(); fail = new CloseResult(CloseOutcome.Gone, "That PID now belongs to a different program; nothing was touched."); return null; }
            }
            catch (Exception) { /* start time unreadable: fall through, the close itself will be denied if we have no access */ }
            return p;
        }

        /// <summary>Politely ask the main window to close and wait. No kill.</summary>
        public static CloseResult Graceful(LockHolder h, int waitMs = 5000)
        {
            CloseResult f; var p = Resolve(h, out f);
            if (p == null) return Log2(h, "close", f);
            using (p)
            {
                try
                {
                    Log.Write("CLOSE-REQUEST (user confirmed) " + h.Name + " pid=" + h.Pid);
                    p.Refresh();
                    if (p.HasExited) return Log2(h, "close", new CloseResult(CloseOutcome.Closed, "Already closed."));
                    if (p.MainWindowHandle == IntPtr.Zero) return Log2(h, "close", new CloseResult(CloseOutcome.NoWindow, "It has no window that can be asked to close."));
                    if (!p.CloseMainWindow()) return Log2(h, "close", new CloseResult(CloseOutcome.NoWindow, "Windows could not deliver the close request to its window."));
                    if (p.WaitForExit(waitMs)) return Log2(h, "close", new CloseResult(CloseOutcome.Closed, "Closed normally."));
                    return Log2(h, "close", new CloseResult(CloseOutcome.StillRunning, "It did not close within " + (waitMs / 1000) + " seconds (it may be asking to save work, or be busy)."));
                }
                catch (Win32Exception e) when (e.NativeErrorCode == 5) { return Log2(h, "close", new CloseResult(CloseOutcome.Denied, "Access denied: Windows does not allow this program to close it. Nothing further was tried.")); }
                catch (UnauthorizedAccessException) { return Log2(h, "close", new CloseResult(CloseOutcome.Denied, "Access denied: Windows does not allow this program to close it. Nothing further was tried.")); }
                catch (Exception e) { return Log2(h, "close", new CloseResult(CloseOutcome.Failed, e.Message)); }
            }
        }

        /// <summary>Forced end (Process.Kill). Only after a SECOND explicit user confirmation. Access denied is reported, never escalated.</summary>
        public static CloseResult Force(LockHolder h, int waitMs = 5000)
        {
            CloseResult f; var p = Resolve(h, out f);
            if (p == null) return Log2(h, "force-end", f);
            using (p)
            {
                try
                {
                    Log.Write("FORCE-END (user confirmed twice) " + h.Name + " pid=" + h.Pid);
                    p.Kill();
                    if (p.WaitForExit(waitMs)) return Log2(h, "force-end", new CloseResult(CloseOutcome.Closed, "Ended."));
                    return Log2(h, "force-end", new CloseResult(CloseOutcome.StillRunning, "It is still shutting down."));
                }
                catch (Win32Exception e) when (e.NativeErrorCode == 5) { return Log2(h, "force-end", new CloseResult(CloseOutcome.Denied, "Access denied: Windows does not allow this program to end it. Nothing further was tried.")); }
                catch (UnauthorizedAccessException) { return Log2(h, "force-end", new CloseResult(CloseOutcome.Denied, "Access denied: Windows does not allow this program to end it. Nothing further was tried.")); }
                catch (InvalidOperationException) { return Log2(h, "force-end", new CloseResult(CloseOutcome.Closed, "It had already ended.")); }
                catch (Exception e) { return Log2(h, "force-end", new CloseResult(CloseOutcome.Failed, e.Message)); }
            }
        }

        static CloseResult Log2(LockHolder h, string what, CloseResult r)
        {
            Log.Write("  " + what + " " + h.Name + " pid=" + h.Pid + " -> " + r.Outcome + ": " + r.Message);
            return r;
        }
    }
}
