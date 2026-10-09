using System;
using System.IO;
using System.Net;
using System.Threading;

namespace CleanPC.Pro
{
    /// <summary>
    /// Downloads the update to a .part file, verifies the size, then renames. NEVER replaces the running exe and never runs
    /// anything. Also handles the "delete the old installer?" marker (consumed on the NEXT start, because a running exe cannot
    /// delete itself).
    /// </summary>
    public static class Downloader
    {
        public static long FreeBytes(string folder)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace; } catch { return -1; }
        }

        /// <summary>Returns the final path. Throws on failure/cancel (the .part file is removed).</summary>
        public static string Download(string url, string folder, string fileName, long expectedSize, Action<long, long> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            string dest = Path.Combine(folder, fileName);
            if (File.Exists(dest))
            {   // never overwrite anything (especially not the running exe)
                string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
                for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(folder, stem + " (" + i + ")" + ext);
            }
            string part = dest + ".part";
            long free = FreeBytes(folder);
            if (expectedSize > 0 && free >= 0 && free < expectedSize + (16L << 20)) throw new IOException("Not enough free space in " + folder + " (need " + Fmt.Size(expectedSize) + ", have " + Fmt.Size(free) + ").");
            try
            {
                var rq = UpdateChecker.NewRequest(url, 30000);
                rq.Accept = "application/octet-stream";
                using (var rp = (HttpWebResponse)rq.GetResponse())
                using (var net = rp.GetResponseStream())
                using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    long total = rp.ContentLength > 0 ? rp.ContentLength : expectedSize, got = 0;
                    var buf = new byte[1 << 16]; int r;
                    while ((r = net.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        fs.Write(buf, 0, r); got += r;
                        if (progress != null) progress(got, total);
                    }
                    fs.Flush();
                    if (total > 0 && got != total) throw new IOException("Download incomplete (" + got + " of " + total + " bytes).");
                    if (expectedSize > 0 && got != expectedSize) throw new IOException("Downloaded size (" + got + ") does not match the release asset size (" + expectedSize + ").");
                }
                File.Move(part, dest);
                Log.Write("UPDATE downloaded " + dest);
                return dest;
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                throw;
            }
        }

        // ---------- old installer deletion (marker consumed on the next start) ----------
        static string MarkerPath { get { return Path.Combine(AppInfo.DataDir, "delete-old-installer.txt"); } }

        /// <summary>Only a single .exe FILE named CleanPC*, not a link, not under Windows / Program Files, never the running exe.</summary>
        public static bool IsDeletableInstaller(string path, out string why)
        {
            why = "";
            try
            {
                string full = Path.GetFullPath(path);
                if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { why = "not an .exe file"; return false; }
                if (!Path.GetFileName(full).StartsWith("CleanPC", StringComparison.OrdinalIgnoreCase)) { why = "not a CleanPC installer"; return false; }
                if (Directory.Exists(full)) { why = "is a folder"; return false; }
                if (!File.Exists(full)) { why = "file not found"; return false; }
                if (CleanOps.IsReparse(new FileInfo(full))) { why = "is a link"; return false; }
                foreach (var env in new[] { "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
                {
                    string root = Environment.GetEnvironmentVariable(env);
                    if (!string.IsNullOrEmpty(root) && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { why = "inside a system location"; return false; }
                }
                if (string.Equals(full, AppInfo.ExePath, StringComparison.OrdinalIgnoreCase)) { why = "this is the running program"; return false; }
                return true;
            }
            catch (Exception e) { why = e.Message; return false; }
        }

        public static bool MarkForDeletion(string oldExe)
        {
            // validated against everything except "is the running exe" (it IS the running exe now; it gets deleted on the next start)
            try
            {
                string full = Path.GetFullPath(oldExe);
                if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("CleanPC", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return false;
                foreach (var env in new[] { "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
                {
                    string root = Environment.GetEnvironmentVariable(env);
                    if (!string.IsNullOrEmpty(root) && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                }
                File.WriteAllText(MarkerPath, full);
                Log.Write("UPDATE marked old installer for deletion on next start: " + full);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Called at start-up: deletes the single marked old .exe if it is not the program that is running now.</summary>
        public static string ConsumeMarker()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return null;
                string target = File.ReadAllText(MarkerPath).Trim();
                string why;
                if (string.Equals(Path.GetFullPath(target), AppInfo.ExePath, StringComparison.OrdinalIgnoreCase)) return null;   // still the old one: keep marker
                if (!File.Exists(target)) { File.Delete(MarkerPath); return null; }
                if (!IsDeletableInstaller(target, out why)) { Log.Write("UPDATE old installer NOT deleted (" + why + "): " + target); File.Delete(MarkerPath); return null; }
                File.Delete(target);
                File.Delete(MarkerPath);
                Log.Write("UPDATE deleted old installer: " + target);
                return target;
            }
            catch (Exception e) { Log.Error("consume marker", e); return null; }
        }
    }
}
