using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CleanPC.Pro
{
    /// <summary>
    /// Last line of defence, checked at DELETE time (not only when targets are built): refuses to delete anything
    /// that is not an obvious cache/temp location. Rules are identical to the Standard (PowerShell) edition's
    /// CleanGuard so the two projects can merge later; Pro adds the user's exclusion list on top.
    /// </summary>
    public static class CleanGuard
    {
        static readonly HashSet<string> BlockedSegs = new HashSet<string>(new[] {
            "program files", "program files (x86)", "system volume information", "winsxs", "$recycle.bin",
            "windowsapps", "$winreagent", "recovery", "config.msi", "msocache", "boot", "efi",
            "system32", "syswow64", "installer", "saved games", "my games", ".ssh", ".gnupg", ".aws" });
        static readonly HashSet<string> PersonalLeaf = new HashSet<string>(new[] {
            "documents", "desktop", "pictures", "downloads", "videos", "music", "onedrive", "appdata", "local",
            "roaming", "locallow", "users", "default", "public", "favorites", "contacts", "links", "searches",
            "3d objects", "windows", "programdata", "user data" });
        static readonly string[] WindowsAllowed = {
            "windows\\temp", "windows\\softwaredistribution\\download", "windows\\logs\\cbs", "windows\\minidump",
            "windows\\livekernelreports", "windows\\memory.dmp",
            "windows\\serviceprofiles\\networkservice\\appdata\\local\\microsoft\\windows\\deliveryoptimization\\cache" };

        static string[] _exclusions = new string[0];

        public static void SetExclusions(IEnumerable<string> paths)
        {
            var l = new List<string>();
            if (paths != null)
                foreach (var p in paths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    try { l.Add(Path.GetFullPath(p.Trim()).TrimEnd('\\')); } catch { }
                }
            _exclusions = l.ToArray();
        }

        public static bool IsExcluded(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string n;
            try { n = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }
            foreach (var e in _exclusions)
                if (n.Equals(e, StringComparison.OrdinalIgnoreCase) || n.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Cache/temp deletion check. isRoot = the folder whose contents will be removed (or the item itself).</summary>
        public static bool Check(string path, bool isRoot, out string why)
        {
            why = "";
            string n;
            if (path == null || path.Length < 3 || path[1] != ':' || path[2] != '\\') { why = "not an absolute local drive path"; return false; }
            try { n = Path.GetFullPath(path).TrimEnd('\\'); } catch (Exception) { why = "bad path"; return false; }
            if (n.Length < 4 || n[1] != ':') { why = "not a local drive path"; return false; }
            if (IsExcluded(n)) { why = "excluded by the user (Settings > Exclusions)"; return false; }
            string rest = n.Substring(3);
            string lower = rest.ToLowerInvariant();
            string[] segs = lower.Split('\\');
            foreach (string s in segs) { if (BlockedSegs.Contains(s)) { why = "protected folder '" + s + "'"; return false; } }
            if (segs[0] == "windows")
            {
                bool ok = false;
                foreach (string a in WindowsAllowed) { if (lower == a || lower.StartsWith(a + "\\")) ok = true; }
                if (!ok) { why = "inside Windows folder and not an allow-listed cache"; return false; }
            }
            if (isRoot)
            {
                string leaf = segs[segs.Length - 1];
                if (PersonalLeaf.Contains(leaf) && !(segs[0] == "windows")) { why = "personal/profile folder '" + leaf + "'"; return false; }
                if (segs.Length == 1 && !(leaf == "temp" || leaf == "tmp" || leaf.StartsWith("found."))) { why = "top-level folder not recognised as temp"; return false; }
                if (segs.Length == 2 && segs[0] == "users") { why = "user profile root"; return false; }
            }
            return true;
        }

        /// <summary>
        /// Check for files the USER explicitly picked in Large files / Duplicates (sent to the Recycle Bin only).
        /// Never anything under Windows, Program Files, ProgramData, system folders or the exclusion list.
        /// </summary>
        public static bool CheckUserFile(string path, out string why)
        {
            why = "";
            string n;
            if (path == null || path.Length < 4 || path[1] != ':' || path[2] != '\\') { why = "not an absolute local drive path"; return false; }
            try { n = Path.GetFullPath(path).TrimEnd('\\'); } catch (Exception) { why = "bad path"; return false; }
            if (n.Length < 4 || n[1] != ':') { why = "not a local drive path"; return false; }
            if (IsExcluded(n)) { why = "excluded by the user"; return false; }
            string[] segs = n.Substring(3).ToLowerInvariant().Split('\\');
            foreach (string s in segs) { if (BlockedSegs.Contains(s)) { why = "protected folder '" + s + "'"; return false; } }
            if (segs[0] == "windows" || segs[0] == "programdata" || segs[0] == "$windows.~bt" || segs[0] == "$windows.~ws") { why = "system location '" + segs[0] + "'"; return false; }
            string name = segs[segs.Length - 1];
            if (name == "pagefile.sys" || name == "hiberfil.sys" || name == "swapfile.sys") { why = "system file"; return false; }
            return true;
        }

        public static bool IsReparse(FileSystemInfo fi) { return (fi.Attributes & FileAttributes.ReparsePoint) != 0; }
    }
}
