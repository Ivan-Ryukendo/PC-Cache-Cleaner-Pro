using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CleanPC.Pro
{
    /// <summary>One folder attributed to a program.</summary>
    public class OwnerFolder
    {
        public string Path, Kind;          // Kind: "Install folder" or "Likely data"
        public long Size = -1;
        public string SizeText { get { return Fmt.Size(Size); } }
        public bool IsInstall { get { return Kind == "Install folder"; } }
    }

    /// <summary>A running program (all its processes) with the folders it most likely owns. Read-only.</summary>
    public class OwnerRow : ObservableObject
    {
        public string Name { get; set; }
        public string ExePath { get; set; }
        public string InstallDir { get; set; }
        public string Company { get; set; }
        public string Product { get; set; }
        public List<string> ProcessNames { get; set; } = new List<string>();
        public int ProcessCount { get; set; }
        public List<OwnerFolder> Folders { get; set; } = new List<OwnerFolder>();

        public long InstallSize { get { return Folders.Where(f => f.IsInstall && f.Size > 0).Sum(f => f.Size); } }
        public long DataSize { get { return Folders.Where(f => !f.IsInstall && f.Size > 0).Sum(f => f.Size); } }
        public long Total { get { return InstallSize + DataSize; } }
        public bool Pending { get { return Folders.Any(f => f.Size < 0); } }
        public string TotalText { get { return Pending ? "..." : Fmt.Size(Total); } }
        public string InstallText { get { return Folders.Any(f => f.IsInstall && f.Size < 0) ? "..." : Fmt.Size(InstallSize); } }
        public string DataText
        {
            get
            {
                int d = Folders.Count(f => !f.IsInstall);
                if (d == 0) return "none found";
                return (Folders.Any(f => !f.IsInstall && f.Size < 0) ? "..." : Fmt.Size(DataSize)) + "  (" + d + " folder" + (d == 1 ? "" : "s") + ", likely)";
            }
        }
        public string ProcessesText { get { return ProcessCount == 1 ? "1 process" : ProcessCount + " processes"; } }
        public string MatchText
        {
            get
            {
                var data = Folders.Where(f => !f.IsInstall).Select(f => f.Path).ToList();
                return data.Count == 0 ? "Install folder only" : "Install folder + likely: " + string.Join("; ", data);
            }
        }
        public string OpenPath { get { return InstallDir; } }
        public void Recalc() { Raise("Total"); Raise("TotalText"); Raise("InstallText"); Raise("DataText"); Raise("InstallSize"); Raise("DataSize"); Raise("Pending"); }
    }

    /// <summary>What was found for one running process (separate from System.Diagnostics so tests can feed fake processes).</summary>
    public class ProcInfo
    {
        public int Pid; public string Name, ExePath, Product, Company;
    }

    public static class StorageOwners
    {
        // folder names too generic to ever treat as "this program's data"
        static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "microsoft", "windows", "temp", "tmp", "bin", "app", "apps", "program", "programs", "common", "commonfiles", "system32", "syswow64",
            "packages", "local", "roaming", "locallow", "data", "cache", "caches", "tools", "dev", "windowsapps", "users", "default", "public",
            "installer", "setup", "update", "updates", "x64", "x86", "win32", "win64", "release", "debug", "node", "java", "python", "host", "service", "services" };
        static readonly HashSet<string> CompanySuffix = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "inc", "corporation", "corp", "ltd", "llc", "co", "gmbh", "limited", "company", "sa", "ag", "plc" };

        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        static string NormCompany(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var words = s.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries).Where(w => !CompanySuffix.Contains(w));
            return Norm(string.Join("", words.ToArray()));
        }

        static bool Usable(string norm) { return norm.Length >= 3 && !Generic.Contains(norm); }

        static string Sep { get { return Path.DirectorySeparatorChar.ToString(); } }

        static bool Under(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
            string f = folder.TrimEnd(Path.DirectorySeparatorChar);
            return path.Equals(f, StringComparison.OrdinalIgnoreCase) || path.StartsWith(f + Sep, StringComparison.OrdinalIgnoreCase);
        }

        public static string[] DefaultContainers()
        {
            string la = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            var l = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432") ?? "", la.Length > 0 ? Path.Combine(la, "Programs") : "", la,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };
            return l.Where(x => !string.IsNullOrEmpty(x)).Select(x => x.TrimEnd(Path.DirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Length).ToArray();
        }

        public static string[] DefaultDataRoots()
        {
            return new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }
                   .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>
        /// Folder that "is" the program: the first folder below a known apps container (Program Files, %LOCALAPPDATA%\Programs, ...),
        /// otherwise the exe's own folder. Null for drive roots / Windows itself / when nothing sensible exists.
        /// </summary>
        public static string InstallRoot(string exePath, string[] containers)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            string dir;
            try { dir = Path.GetDirectoryName(exePath); } catch { return null; }
            if (string.IsNullOrEmpty(dir)) return null;
            dir = dir.TrimEnd(Path.DirectorySeparatorChar);
            string win = (Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows").TrimEnd(Path.DirectorySeparatorChar);
            if (Under(dir, win)) return null;                                     // Windows' own programs: not "who is using space"
            if (dir.Length <= 3) return null;                                     // drive root
            foreach (var c in containers)
            {
                if (!Under(dir, c) || dir.Equals(c, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = dir.Substring(c.Length).TrimStart(Path.DirectorySeparatorChar);
                string first = rest.Split(Path.DirectorySeparatorChar)[0];
                return Path.Combine(c, first);
            }
            return dir;
        }

        /// <summary>Groups processes by install folder and attaches likely per-user/per-machine data folders. No sizing here.</summary>
        public static List<OwnerRow> Build(IEnumerable<ProcInfo> procs, string[] containers, string[] dataRoots, CancellationToken ct)
        {
            var groups = new Dictionary<string, OwnerRow>(StringComparer.OrdinalIgnoreCase);
            var order = new List<OwnerRow>();
            foreach (var p in procs)
            {
                if (ct.IsCancellationRequested) break;
                string root = InstallRoot(p.ExePath, containers);
                if (root == null) continue;
                OwnerRow r;
                if (!groups.TryGetValue(root, out r))
                {
                    r = new OwnerRow { InstallDir = root, ExePath = p.ExePath, Company = p.Company, Product = p.Product };
                    r.Folders.Add(new OwnerFolder { Path = root, Kind = "Install folder" });
                    groups[root] = r; order.Add(r);
                }
                r.ProcessCount++;
                string pn = p.Name ?? "";
                if (pn.Length > 0 && !r.ProcessNames.Contains(pn, StringComparer.OrdinalIgnoreCase)) r.ProcessNames.Add(pn);
                if (string.IsNullOrEmpty(r.Product) && !string.IsNullOrEmpty(p.Product)) r.Product = p.Product;
                if (string.IsNullOrEmpty(r.Company) && !string.IsNullOrEmpty(p.Company)) r.Company = p.Company;
            }
            foreach (var r in order)
            {
                string prod = (r.Product ?? "").Trim();
                // generic product strings such as "Microsoft(R) Windows(R) Operating System" are never trusted for names
                r.Name = !string.IsNullOrEmpty(prod) && prod.Length <= 60 && prod.IndexOf("Operating System", StringComparison.OrdinalIgnoreCase) < 0 ? prod
                       : (r.ProcessNames.Count > 0 ? r.ProcessNames[0] : Path.GetFileName(r.InstallDir));
            }

            // data folders: list each root's direct child folders once (no reparse points)
            var kids = new List<KeyValuePair<string, List<KeyValuePair<string, string>>>>();
            foreach (var dr in dataRoots)
            {
                var l = new List<KeyValuePair<string, string>>();
                try
                {
                    if (Directory.Exists(dr))
                        foreach (var d in new DirectoryInfo(dr).EnumerateDirectories())
                        {
                            try { if (!CleanOps.IsReparse(d)) l.Add(new KeyValuePair<string, string>(Norm(d.Name), d.FullName)); } catch { }
                        }
                }
                catch { }
                kids.Add(new KeyValuePair<string, List<KeyValuePair<string, string>>>(dr, l));
            }
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // pass 1: names taken from the folder/exe names (strongest), pass 2: product and vendor names
            for (int pass = 1; pass <= 2; pass++)
                foreach (var r in order)
                {
                    if (ct.IsCancellationRequested) break;
                    var names = new List<string>();
                    if (pass == 1)
                    {
                        names.Add(Norm(Path.GetFileName(r.InstallDir)));
                        foreach (var pn in r.ProcessNames) names.Add(Norm(Path.GetFileNameWithoutExtension(pn)));
                    }
                    else
                    {
                        string prod = Norm(r.Product);
                        names.Add(prod);
                        string comp = NormCompany(r.Company);
                        if (comp.Length > 0 && prod.StartsWith(comp) && prod.Length > comp.Length) names.Add(prod.Substring(comp.Length));
                    }
                    names = names.Where(Usable).Distinct().ToList();
                    string compN = NormCompany(r.Company);
                    foreach (var kv in kids)
                    {
                        foreach (var k in kv.Value)
                        {
                            string full = k.Value;
                            if (claimed.Contains(full) || Under(full, r.InstallDir) || Under(r.InstallDir, full)) continue;
                            bool hit = names.Contains(k.Key);
                            if (!hit && pass == 2 && compN.Length >= 3 && k.Key == compN && !Generic.Contains(compN))
                            {
                                // vendor folder: only its product-named child counts ("Google" -> "Google\Chrome"), never the whole vendor folder
                                try
                                {
                                    foreach (var sub in new DirectoryInfo(full).EnumerateDirectories())
                                    {
                                        if (CleanOps.IsReparse(sub)) continue;
                                        string sn = Norm(sub.Name);
                                        string full2 = sub.FullName;
                                        if (Usable(sn) && !claimed.Contains(full2) && !Under(full2, r.InstallDir) && !Under(r.InstallDir, full2) &&
                                            (sn == Norm(Path.GetFileName(r.InstallDir)) || sn == Norm(r.Product) || names.Contains(sn)))
                                        { claimed.Add(full2); r.Folders.Add(new OwnerFolder { Path = full2, Kind = "Likely data" }); }
                                    }
                                }
                                catch { }
                                continue;
                            }
                            if (hit) { claimed.Add(full); r.Folders.Add(new OwnerFolder { Path = full, Kind = "Likely data" }); }
                        }
                    }
                }
            return order;
        }

        /// <summary>Running processes with a readable exe path, as plain facts.</summary>
        public static List<ProcInfo> RunningProcesses(CancellationToken ct)
        {
            var l = new List<ProcInfo>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (ct.IsCancellationRequested) break;
                    string path = ProcessGuard.ImagePath(p.Id);
                    if (string.IsNullOrEmpty(path)) continue;
                    string prod = null, comp = null;
                    try { var vi = FileVersionInfo.GetVersionInfo(path); prod = vi.ProductName; comp = vi.CompanyName; } catch { }
                    l.Add(new ProcInfo { Pid = p.Id, Name = p.ProcessName, ExePath = path, Product = prod, Company = comp });
                }
                catch { }
                finally { p.Dispose(); }
            }
            return l;
        }

        /// <summary>Sizes every folder (reparse points never followed, honours cancel). onRow fires after each folder.</summary>
        public static void SizeAll(IList<OwnerRow> rows, CancellationToken ct, IProgress<string> prog, Action<OwnerRow> onRow)
        {
            var work = new List<KeyValuePair<OwnerRow, OwnerFolder>>();
            foreach (var r in rows) foreach (var f in r.Folders) work.Add(new KeyValuePair<OwnerRow, OwnerFolder>(r, f));
            int done = 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct };
            try
            {
                Parallel.ForEach(work, po, kv =>
                {
                    kv.Value.Size = CleanOps.DirSize(kv.Value.Path, ct, null);
                    int n = Interlocked.Increment(ref done);
                    kv.Key.Recalc();
                    if (onRow != null) onRow(kv.Key);
                    if (prog != null && n % 5 == 0) prog.Report("Measuring folders (" + n + "/" + work.Count + ")...");
                });
            }
            catch (OperationCanceledException) { }
        }
    }
}
