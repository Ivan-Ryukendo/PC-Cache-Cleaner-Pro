using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace CleanPC.Pro
{
    public class Finding
    {
        public string Kind, Root, FullPath; public long Size; public bool IsDir; public DateTime Modified;
    }

    /// <summary>
    /// Background drive walk (junk files / cache folders) and AppData sweep (named cache folders, big virtual disks).
    /// Port of the Standard edition's CleanWorker. Never follows reparse points.
    /// </summary>
    public class DriveWorker
    {
        public volatile string Current = "";
        public volatile bool TimedOut;
        public int DirsVisited;
        public readonly List<Finding> Findings = new List<Finding>();
        public readonly List<string> NamedHits = new List<string>();
        public long BigDiskBytes = 1073741824L;

        static readonly HashSet<string> WalkSkip = new HashSet<string>(new[] {
            "windows", "program files", "program files (x86)", "programdata", "system volume information", "$recycle.bin",
            "recovery", "boot", "efi", "perflogs", "winsxs", "installer", "$winreagent", "config.msi", "msocache", "appdata",
            "$windows.~bt", "$windows.~ws", "windowsapps", ".git", "site-packages", ".venv", "venv", "$sysreset",
            "documents and settings", "steamapps", "steamlibrary", "epic games", "gog galaxy", "riot games", "xboxgames",
            "saved games", "my games", "onedrive" });
        static readonly HashSet<string> SweepSkip = new HashSet<string>(new[] { "node_modules", "site-packages", ".git", "temp", "vm_bundles" });
        static readonly Regex FoundRx = new Regex(@"^found\.\d{3}$", RegexOptions.IgnoreCase);

        static bool IsDiskExt(string ext) { return ext == ".vhdx" || ext == ".vhd" || ext == ".vmdk" || ext == ".vdi"; }

        void Add(string kind, string root, FileSystemInfo fi, bool isDir, long size)
        {
            lock (Findings)
            {
                if (Findings.Count > 30000) return;
                Findings.Add(new Finding { Kind = kind, Root = root, FullPath = fi.FullName, IsDir = isDir, Size = size, Modified = fi.LastWriteTimeUtc });
            }
        }

        public void Walk(string root, int maxDepth, int seconds, int minAgeDays, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var st = new Stack<KeyValuePair<string, int>>();
            st.Push(new KeyValuePair<string, int>(root, 0));
            DateTime ageCut = DateTime.UtcNow.AddDays(-minAgeDays), oldCut = DateTime.UtcNow.AddDays(-30);
            while (st.Count > 0)
            {
                if (ct.IsCancellationRequested) return;
                if (DateTime.UtcNow > deadline) { TimedOut = true; return; }
                var f = st.Pop();
                Current = f.Key; Interlocked.Increment(ref DirsVisited);
                IEnumerable<FileSystemInfo> en;
                try { en = new DirectoryInfo(f.Key).EnumerateFileSystemInfos(); } catch (Exception) { continue; }
                try
                {
                    foreach (FileSystemInfo fi in en)
                    {
                        try
                        {
                            if (CleanOps.IsReparse(fi)) continue;
                            string name = fi.Name, ln = name.ToLowerInvariant();
                            if ((fi.Attributes & FileAttributes.Directory) != 0)
                            {
                                if (WalkSkip.Contains(ln) || CleanGuard.IsExcluded(fi.FullName)) continue;
                                if (ln == "__pycache__") { Add("PyCache", root, fi, true, CleanOps.DirSize(fi.FullName, ct, null)); continue; }
                                if (ln == ".pytest_cache" || ln == ".mypy_cache" || ln == ".ruff_cache") { Add("ToolCache", root, fi, true, CleanOps.DirSize(fi.FullName, ct, null)); continue; }
                                if (ln == "node_modules")
                                {
                                    string nc = Path.Combine(fi.FullName, ".cache");
                                    var nd = new DirectoryInfo(nc);
                                    if (nd.Exists && !CleanOps.IsReparse(nd)) Add("NodeCache", root, nd, true, CleanOps.DirSize(nc, ct, null));
                                    continue;
                                }
                                if (f.Value == 0 && FoundRx.IsMatch(name)) { Add("ChkFolder", root, fi, true, CleanOps.DirSize(fi.FullName, ct, null)); continue; }
                                if (f.Value + 1 <= maxDepth) st.Push(new KeyValuePair<string, int>(fi.FullName, f.Value + 1));
                            }
                            else
                            {
                                var file = (FileInfo)fi;
                                string ext = file.Extension.ToLowerInvariant();
                                DateTime wt = file.LastWriteTimeUtc;
                                if (CleanGuard.IsExcluded(file.FullName)) continue;
                                if (ln == "thumbs.db") Add("Thumbs", root, fi, false, file.Length);
                                else if (ext == ".gid") Add("Gid", root, fi, false, file.Length);
                                else if (ext == ".tmp" || ext == ".temp" || ln.StartsWith("~$")) { if (wt < ageCut) Add("TmpFiles", root, fi, false, file.Length); }
                                else if (ext == ".chk") { if (wt < oldCut) Add("ChkFile", root, fi, false, file.Length); }
                                else if (ext == ".old") { if (wt < oldCut) Add("OldFiles", root, fi, false, file.Length); }
                                else if (ext == ".dmp") { if (wt < ageCut) Add("DumpFiles", root, fi, false, file.Length); }
                                else if (IsDiskExt(ext) && file.Length >= BigDiskBytes) Add("BigDisk", root, fi, false, file.Length);
                            }
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }
        }

        public void Sweep(string root, HashSet<string> names, int maxDepth, CancellationToken ct)
        {
            var st = new Stack<KeyValuePair<string, int>>();
            st.Push(new KeyValuePair<string, int>(root, 0));
            while (st.Count > 0)
            {
                if (ct.IsCancellationRequested) return;
                var f = st.Pop();
                Current = f.Key; Interlocked.Increment(ref DirsVisited);
                IEnumerable<FileSystemInfo> en;
                try { en = new DirectoryInfo(f.Key).EnumerateFileSystemInfos(); } catch (Exception) { continue; }
                try
                {
                    foreach (FileSystemInfo fi in en)
                    {
                        try
                        {
                            if (CleanOps.IsReparse(fi)) continue;
                            string ln = fi.Name.ToLowerInvariant();
                            if ((fi.Attributes & FileAttributes.Directory) != 0)
                            {
                                if (names.Contains(ln)) { lock (NamedHits) NamedHits.Add(fi.FullName); continue; }
                                if (SweepSkip.Contains(ln) || CleanGuard.IsExcluded(fi.FullName)) continue;
                                if (f.Value + 1 <= maxDepth) st.Push(new KeyValuePair<string, int>(fi.FullName, f.Value + 1));
                            }
                            else
                            {
                                var file = (FileInfo)fi;
                                if (IsDiskExt(file.Extension.ToLowerInvariant()) && file.Length >= BigDiskBytes) Add("BigDisk", root, fi, false, file.Length);
                            }
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }
        }
    }
}
