using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace CleanPC.Pro
{
    /// <summary>Finds duplicate files: group by size, then partial hash (first+last 64 KB), then full SHA-256. Never selects anything.</summary>
    public static class DuplicateFinder
    {
        static readonly HashSet<string> Skip = new HashSet<string>(new[] {
            "windows", "program files", "program files (x86)", "programdata", "system volume information", "$recycle.bin", "recovery", "boot", "efi",
            "winsxs", "installer", "$winreagent", "config.msi", "msocache", "appdata", "$windows.~bt", "$windows.~ws", "windowsapps", ".git",
            "node_modules", "site-packages", "$sysreset", "steamapps", "saved games", "my games" });
        const int Chunk = 64 * 1024;

        public static List<List<string>> Find(IList<string> drives, long minBytes, IProgress<string> prog, CancellationToken ct)
        {
            var roots = drives.Select(d => d + "\\").ToList();
            return FindIn(roots, minBytes, prog, ct);
        }

        public static List<List<string>> FindIn(IList<string> roots, long minBytes, IProgress<string> prog, CancellationToken ct)
        {
            // 1) group by size
            var bySize = new Dictionary<long, List<string>>();
            int dirs = 0;
            foreach (var root in roots)
            {
                var st = new Stack<string>(); st.Push(root);
                while (st.Count > 0)
                {
                    if (ct.IsCancellationRequested) return new List<List<string>>();
                    string cur = st.Pop();
                    if ((++dirs & 255) == 0 && prog != null) prog.Report("Listing files: " + Fmt.Mid(cur, 70));
                    IEnumerable<FileSystemInfo> en;
                    try { en = new DirectoryInfo(cur).EnumerateFileSystemInfos(); } catch { continue; }
                    try
                    {
                        foreach (var fi in en)
                        {
                            try
                            {
                                if (CleanOps.IsReparse(fi)) continue;
                                if ((fi.Attributes & FileAttributes.Directory) != 0)
                                {
                                    if (Skip.Contains(fi.Name.ToLowerInvariant()) || CleanGuard.IsExcluded(fi.FullName)) continue;
                                    st.Push(fi.FullName);
                                }
                                else
                                {
                                    if ((fi.Attributes & FileAttributes.System) != 0) continue;
                                    long len = ((FileInfo)fi).Length;
                                    if (len < minBytes || len == 0) continue;
                                    List<string> l; if (!bySize.TryGetValue(len, out l)) bySize[len] = l = new List<string>();
                                    l.Add(fi.FullName);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            var result = new List<List<string>>();
            int n = 0, total = bySize.Count(kv => kv.Value.Count > 1);
            foreach (var kv in bySize.Where(k => k.Value.Count > 1).OrderByDescending(k => k.Key))
            {
                if (ct.IsCancellationRequested) break;
                n++; if (prog != null && (n % 5 == 0)) prog.Report("Comparing candidates " + n + "/" + total + " (" + Fmt.Size(kv.Key) + " each)");
                // 2) partial hash
                var byPart = new Dictionary<string, List<string>>();
                foreach (var p in kv.Value)
                {
                    string h = HashPartial(p, kv.Key); if (h == null) continue;
                    List<string> l; if (!byPart.TryGetValue(h, out l)) byPart[h] = l = new List<string>();
                    l.Add(p);
                }
                // 3) full SHA-256
                foreach (var pg in byPart.Values.Where(v => v.Count > 1))
                {
                    var byFull = new Dictionary<string, List<string>>();
                    foreach (var p in pg)
                    {
                        if (ct.IsCancellationRequested) break;
                        string h = HashFull(p, ct); if (h == null) continue;
                        List<string> l; if (!byFull.TryGetValue(h, out l)) byFull[h] = l = new List<string>();
                        l.Add(p);
                    }
                    foreach (var g in byFull.Values.Where(v => v.Count > 1)) result.Add(g);
                }
            }
            return result;
        }

        static string HashPartial(string p, long len)
        {
            try
            {
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[Chunk];
                    int r = fs.Read(buf, 0, Chunk);
                    sha.TransformBlock(buf, 0, r, null, 0);
                    if (len > Chunk * 2)
                    {
                        fs.Seek(-Chunk, SeekOrigin.End);
                        r = fs.Read(buf, 0, Chunk);
                        sha.TransformBlock(buf, 0, r, null, 0);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return BitConverter.ToString(sha.Hash);
                }
            }
            catch { return null; }
        }

        public static string HashFull(string p, CancellationToken ct)
        {
            try
            {
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[1 << 16]; int r;
                    while ((r = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (ct.IsCancellationRequested) return null;
                        sha.TransformBlock(buf, 0, r, null, 0);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch { return null; }
        }
    }
}
