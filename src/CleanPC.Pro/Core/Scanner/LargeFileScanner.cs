using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CleanPC.Pro
{
    public class FileRow : ObservableObject, ITickable
    {
        public string Path { get; set; }
        public string Name { get { return System.IO.Path.GetFileName(Path); } }
        public string Dir { get { return System.IO.Path.GetDirectoryName(Path); } }
        public long Size { get; set; }
        public string SizeText { get { return Fmt.Size(Size); } }
        public DateTime Modified { get; set; }
        public string ModifiedText { get { return Modified.ToString("yyyy-MM-dd"); } }
        public string Drive { get { return Path.Length >= 2 ? Path.Substring(0, 2).ToUpperInvariant() : ""; } }
        public int Group { get; set; }              // duplicates: group index
        public int GroupParity { get { return Group % 2; } }
        public string GroupText { get { return Group > 0 ? "#" + Group : ""; } }
        bool _c; public bool IsChecked { get { return _c; } set { Set(ref _c, value); } }
        string _st = ""; public string Status { get { return _st; } set { Set(ref _st, value); } }
    }

    /// <summary>Top-N biggest files per drive. Uses EnumerateFileSystemInfos (size comes with the directory listing, no per-file stat).</summary>
    public static class LargeFileScanner
    {
        static readonly HashSet<string> Skip = new HashSet<string>(new[] {
            "windows", "$recycle.bin", "system volume information", "winsxs", "$windows.~bt", "$windows.~ws", "recovery", "$winreagent", "config.msi", "msocache", "$sysreset" });

        public static List<FileRow> Scan(IList<string> roots, int topN, IProgress<string> prog, CancellationToken ct)
        {
            var all = new List<FileRow>();
            foreach (var d in roots)
            {
                var heap = new SortedList<long, List<FileRow>>();    // ascending by size
                int count = 0; long minKept = 0;
                var st = new Stack<string>(); st.Push(d.EndsWith("\\") ? d : d + "\\");
                int dirs = 0;
                while (st.Count > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    string cur = st.Pop();
                    if ((++dirs & 255) == 0 && prog != null) prog.Report("Large files " + d + " " + Fmt.Mid(cur, 70));
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
                                    if (cur.TrimEnd((char)92).Length <= 2 && Skip.Contains(fi.Name.ToLowerInvariant())) continue;
                                    if (CleanGuard.IsExcluded(fi.FullName)) continue;
                                    st.Push(fi.FullName);
                                }
                                else
                                {
                                    if ((fi.Attributes & FileAttributes.System) != 0) continue;     // pagefile/hiberfil etc.
                                    long len = ((FileInfo)fi).Length;
                                    if (count >= topN && len <= minKept) continue;
                                    List<FileRow> l;
                                    if (!heap.TryGetValue(len, out l)) heap[len] = l = new List<FileRow>();
                                    l.Add(new FileRow { Path = fi.FullName, Size = len, Modified = fi.LastWriteTime });
                                    count++;
                                    while (count > topN)
                                    {
                                        var first = heap.First();
                                        first.Value.RemoveAt(first.Value.Count - 1); count--;
                                        if (first.Value.Count == 0) heap.Remove(first.Key);
                                    }
                                    minKept = heap.Count > 0 ? heap.Keys[0] : 0;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                foreach (var kv in heap) all.AddRange(kv.Value);
            }
            return all.OrderByDescending(f => f.Size).ToList();
        }
    }
}
