using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Threading;

namespace CleanPC.Pro
{
    public class ProfileRow : ObservableObject, ITickable
    {
        public string Sid { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Drive { get; set; }
        public long Size { get; set; }
        public string SizeText { get { return Fmt.Size(Size); } }
        public string LastUse { get; set; }
        bool _c; public bool IsChecked { get { return _c; } set { Set(ref _c, value); } }
        string _st = ""; public string Status { get { return _st; } set { Set(ref _st, value); } }
    }

    /// <summary>Other Windows user profiles (opt-in). Same rules as the Standard edition; deletion via Win32_UserProfile.</summary>
    public static class ProfileManager
    {
        public static HashSet<string> RegisteredPaths()
        {
            var h = new HashSet<string>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT LocalPath FROM Win32_UserProfile"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string lp = o["LocalPath"] as string;
                        if (!string.IsNullOrEmpty(lp)) h.Add(lp.TrimEnd('\\').ToLowerInvariant());
                    }
            }
            catch (Exception e) { Log.Error("profiles", e); }
            return h;
        }

        static bool Removable(string sid, string localPath, bool special, bool loaded)
        {
            try
            {
                if (string.IsNullOrEmpty(localPath)) return false;
                if (special || loaded) return false;
                string me = WindowsIdentity.GetCurrent().User.Value;
                if (sid == me) return false;
                if (!sid.StartsWith("S-1-5-21-")) return false;     // only real local/domain accounts
                if (sid.EndsWith("-500")) return false;               // built-in Administrator
                string lp = localPath.TrimEnd('\\');
                if (lp.Equals(Environment.GetEnvironmentVariable("USERPROFILE").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                if (lp.StartsWith(Environment.GetEnvironmentVariable("SystemRoot") + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                string leaf = System.IO.Path.GetFileName(lp);
                foreach (var b in new[] { "Default", "Default User", "Public", "All Users", "systemprofile", "LocalService", "NetworkService" })
                    if (leaf.Equals(b, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch { return false; }
        }

        public static List<ProfileRow> List(IList<string> drives, CancellationToken ct)
        {
            var res = new List<ProfileRow>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT SID,LocalPath,Special,Loaded,LastUseTime FROM Win32_UserProfile"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string sid = o["SID"] as string, lp = o["LocalPath"] as string;
                        bool special = o["Special"] is bool && (bool)o["Special"], loaded = o["Loaded"] is bool && (bool)o["Loaded"];
                        if (!Removable(sid, lp, special, loaded)) continue;
                        string drv = lp.Substring(0, 2).ToUpperInvariant();
                        if (!drives.Contains(drv)) continue;
                        string last = "";
                        try { var lu = o["LastUseTime"] as string; if (!string.IsNullOrEmpty(lu)) last = ManagementDateTimeConverter.ToDateTime(lu).ToString("yyyy-MM-dd"); } catch { }
                        res.Add(new ProfileRow { Sid = sid, Name = System.IO.Path.GetFileName(lp.TrimEnd('\\')), Path = lp, Drive = drv, LastUse = last, Size = -1 });
                    }
            }
            catch (Exception e) { Log.Error("profile list", e); }
            foreach (var r in res) { if (ct.IsCancellationRequested) break; r.Size = CleanOps.DirSize(r.Path, ct, null); }
            return res;
        }

        /// <summary>Deletes one profile with the proper Windows API (folder AND registry entry). Re-validates first.</summary>
        public static string Remove(ProfileRow prof)
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_UserProfile WHERE SID='" + prof.Sid.Replace("'", "") + "'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string sid = o["SID"] as string, lp = o["LocalPath"] as string;
                        bool special = o["Special"] is bool && (bool)o["Special"], loaded = o["Loaded"] is bool && (bool)o["Loaded"];
                        if (!Removable(sid, lp, special, loaded)) { Log.Write("REFUSED profile (not removable now): " + prof.Path); return "not removable now (in use or protected)"; }
                        Log.Write("DELETE PROFILE (user confirmed by typing name): " + lp + " sid=" + sid);
                        o.Delete();
                        Log.Write("PROFILE DELETED: " + prof.Path);
                        return null;
                    }
                return "profile no longer registered";
            }
            catch (Exception e) { Log.Write("PROFILE DELETE FAILED " + prof.Path + ": " + e.Message); return e.Message; }
        }
    }
}
