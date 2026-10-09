using System;
using System.Diagnostics;
using System.IO;

namespace CleanPC.Pro
{
    /// <summary>Windows maintenance helpers. Each one is started only from a button, after an explanation and a confirmation.</summary>
    public static class Maintenance
    {
        public const string DismCommand = "dism.exe /Online /Cleanup-Image /StartComponentCleanup";

        public static string RunDism()
        {
            Log.Write("MAINTENANCE DISM StartComponentCleanup (user confirmed)");
            int code;
            string o = Proc.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 45 * 60 * 1000, out code);
            Log.Write("MAINTENANCE DISM exit=" + code);
            return "Exit code " + code + Environment.NewLine + o;
        }

        public static void OpenStorageSense()
        {
            Log.Write("MAINTENANCE open Storage Sense settings");
            Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
        }

        public static void OpenCleanMgr(string drive)
        {
            Log.Write("MAINTENANCE open cleanmgr " + drive);
            string exe = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", @"System32\cleanmgr.exe");
            Process.Start(new ProcessStartInfo(exe, "/d " + drive.Substring(0, 1)) { UseShellExecute = true });
        }

        public static string ShadowCapCommand(string drive)
        {
            return "vssadmin resize shadowstorage /for=" + drive + " /on=" + drive + " /maxsize=10GB     (display only - this tool never runs it)";
        }
    }

    /// <summary>Weekly scheduled safe clean via schtasks.exe.</summary>
    public static class Scheduler
    {
        public const string TaskName = "CleanPC-Pro Weekly Safe Clean";

        public static bool Exists()
        {
            int c; Proc.Run("schtasks.exe", "/query /tn \"" + TaskName + "\"", 20000, out c); return c == 0;
        }

        public static string Create()
        {
            string tr = "\\\"" + AppInfo.ExePath + "\\\" --auto --safe";
            int c;
            string o = Proc.Run("schtasks.exe", "/create /tn \"" + TaskName + "\" /tr \"" + tr + "\" /sc weekly /d SUN /st 12:00 /rl highest /f", 30000, out c);
            Log.Write("SCHEDULE create exit=" + c + " " + o.Trim());
            return c == 0 ? null : o.Trim();
        }

        public static string Remove()
        {
            int c;
            string o = Proc.Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f", 30000, out c);
            Log.Write("SCHEDULE delete exit=" + c + " " + o.Trim());
            return c == 0 ? null : o.Trim();
        }
    }
}
