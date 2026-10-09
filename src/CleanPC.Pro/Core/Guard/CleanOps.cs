using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CleanPC.Pro
{
    /// <summary>
    /// Reparse-point-safe sizing and deleting. Every delete entry point re-runs CleanGuard. Locked/denied entries are
    /// skipped and reported as "SKIP path: reason"; reparse points (junctions/symlinks) are never followed.
    /// </summary>
    public static class CleanOps
    {
        public static bool IsReparse(FileSystemInfo fi) { return (fi.Attributes & FileAttributes.ReparsePoint) != 0; }

        /// <summary>Size of a file or folder tree; never follows junctions/symlinks; skips user exclusions.</summary>
        public static long DirSize(string path, CancellationToken ct, Action<string> current)
        {
            long sum = 0;
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (!Directory.Exists(path)) return 0;
                if (IsReparse(new DirectoryInfo(path))) return 0;
                var st = new Stack<string>();
                st.Push(path);
                while (st.Count > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    string d = st.Pop();
                    if (current != null) current(d);
                    try
                    {
                        foreach (FileSystemInfo fi in new DirectoryInfo(d).EnumerateFileSystemInfos())
                        {
                            try
                            {
                                if (IsReparse(fi)) continue;
                                if ((fi.Attributes & FileAttributes.Directory) != 0)
                                {
                                    if (!CleanGuard.IsExcluded(fi.FullName)) st.Push(fi.FullName);
                                }
                                else sum += ((FileInfo)fi).Length;
                            }
                            catch (Exception) { }
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return sum;
        }

        /// <summary>Deletes the CONTENTS of a folder and keeps the folder.</summary>
        public static long DeleteContents(string root, List<string> skips)
        {
            string why;
            if (!CleanGuard.Check(root, true, out why)) { skips.Add("BLOCKED " + root + ": " + why); return 0; }
            long freed = 0;
            try
            {
                var di = new DirectoryInfo(root);
                if (!di.Exists) return 0;
                if (IsReparse(di)) { skips.Add("SKIP " + root + ": reparse point (not followed)"); return 0; }
                foreach (FileSystemInfo k in di.GetFileSystemInfos()) freed += DeleteEntry(k, skips);
            }
            catch (Exception e) { skips.Add("SKIP " + root + ": " + e.Message); }
            return freed;
        }

        /// <summary>Deletes one whole folder tree (old release folders, stale dev artifact folders).</summary>
        public static long DeleteTree(string path, List<string> skips)
        {
            string why;
            if (!CleanGuard.Check(path, true, out why)) { skips.Add("BLOCKED " + path + ": " + why); return 0; }
            long freed = DeleteContents(path, skips);
            try
            {
                var di = new DirectoryInfo(path);
                if (di.Exists && !IsReparse(di)) di.Delete(false);
            }
            catch (Exception e) { skips.Add("SKIP " + path + ": " + e.Message); }
            return freed;
        }

        public static long DeleteFiles(IEnumerable<string> paths, List<string> skips)
        {
            long freed = 0;
            foreach (string p in paths)
            {
                string why;
                if (!CleanGuard.Check(p, false, out why)) { skips.Add("BLOCKED " + p + ": " + why); continue; }
                long len = 0;
                try
                {
                    var fi = new FileInfo(p);
                    if (!fi.Exists) continue;
                    if (IsReparse(fi)) { skips.Add("SKIP " + p + ": reparse point"); continue; }
                    len = fi.Length;
                    if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes = FileAttributes.Normal;
                    fi.Delete();
                    freed += len;
                }
                catch (Exception e) { Skip.File(skips, p, e, len); }
            }
            return freed;
        }

        static long DeleteEntry(FileSystemInfo fi, List<string> skips)
        {
            long freed = 0;
            try
            {
                if (CleanGuard.IsExcluded(fi.FullName)) { skips.Add("SKIP " + fi.FullName + ": excluded by the user"); return 0; }
                if (IsReparse(fi))   // remove the link itself, never follow it
                {
                    if ((fi.Attributes & FileAttributes.Directory) != 0) ((DirectoryInfo)fi).Delete(false); else fi.Delete();
                    return 0;
                }
                if ((fi.Attributes & FileAttributes.Directory) != 0)
                {
                    var di = (DirectoryInfo)fi;
                    int before = skips.Count;
                    foreach (FileSystemInfo k in di.GetFileSystemInfos()) freed += DeleteEntry(k, skips);
                    try { di.Delete(false); } catch (Exception e) { if (skips.Count == before) skips.Add("SKIP " + fi.FullName + ": " + e.Message); }
                }
                else
                {
                    var f = (FileInfo)fi;
                    long len = 0;
                    try
                    {
                        len = f.Length;
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes = FileAttributes.Normal;
                        f.Delete();
                        freed += len;
                    }
                    catch (Exception e) { Skip.File(skips, fi.FullName, e, len); }
                }
            }
            catch (Exception e) { skips.Add("SKIP " + fi.FullName + ": " + e.Message); }
            return freed;
        }

        /// <summary>User-picked files (Large files / Duplicates): Recycle Bin ONLY, never permanent.</summary>
        public static string RecycleUserFile(string path)
        {
            string why;
            if (!CleanGuard.CheckUserFile(path, out why)) return "BLOCKED: " + why;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return "file no longer exists";
                if (IsReparse(fi)) return "reparse point (not touched)";
            }
            catch (Exception e) { return e.Message; }
            return Native.RecycleOne(path);
        }
    }
}
