using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CleanPC.Pro
{
    public class StaleProject : ObservableObject, ITickable
    {
        public string Path { get; set; }              // the regenerable folder itself
        public string Project { get; set; }           // project folder
        public string Kind { get; set; }              // node_modules, target, .venv, ...
        public long Size { get; set; }
        public string SizeText { get { return Fmt.Size(Size); } }
        public DateTime LastActive { get; set; }
        public string LastActiveText { get { return LastActive.ToString("yyyy-MM-dd"); } }
        public int IdleDays { get; set; }
        bool _c; public bool IsChecked { get { return _c; } set { Set(ref _c, value); } }
        string _st = ""; public string Status { get { return _st; } set { Set(ref _st, value); } }
    }

    /// <summary>
    /// Finds regenerable dev folders (node_modules, Rust target, .venv, __pycache__, .gradle, build) whose project has not been
    /// touched for N days. Only regenerable artifact folders are ever listed; they start UNTICKED.
    /// </summary>
    public static class StaleProjectScanner
    {
        static readonly HashSet<string> Skip = new HashSet<string>(new[] {
            "windows", "program files", "program files (x86)", "programdata", "system volume information", "$recycle.bin", "recovery", "boot", "efi",
            "winsxs", "installer", "$winreagent", "config.msi", "msocache", "appdata", "$windows.~bt", "$windows.~ws", "windowsapps", ".git", "$sysreset",
            "onedrive", "steamapps", "saved games", "my games", "site-packages" });
        public static readonly HashSet<string> ArtifactNames = new HashSet<string>(
            new[] { "node_modules", "target", ".venv", "venv", "__pycache__", ".gradle", "build" }, StringComparer.OrdinalIgnoreCase);

        static bool Has(string dir, params string[] files)
        {
            foreach (var f in files) { try { if (File.Exists(Path.Combine(dir, f))) return true; } catch { } }
            return false;
        }

        /// <summary>Decides whether folder name `name` under `parent` is a regenerable artifact of a recognisable project.</summary>
        public static string Classify(string parent, string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "node_modules": return Has(parent, "package.json") ? "node_modules" : null;
                case "target": return Has(parent, "Cargo.toml") ? "Rust target" : (Has(parent, "pom.xml") ? "Maven target" : null);
                case ".venv": case "venv": return File.Exists(Path.Combine(parent, name, "pyvenv.cfg")) ? "Python venv" : null;
                case "__pycache__": return "__pycache__";
                case ".gradle": return Has(parent, "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts") ? "Gradle project cache" : null;
                case "build": return Has(parent, "build.gradle", "build.gradle.kts", "CMakeLists.txt", "setup.py", "pyproject.toml") ? "build output" : null;
            }
            return null;
        }

        static DateTime LastActivity(string project, string artifactPath)
        {
            DateTime best = DateTime.MinValue;
            try
            {
                foreach (var fi in new DirectoryInfo(project).EnumerateFileSystemInfos())
                {
                    if (fi.FullName.Equals(artifactPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ArtifactNames.Contains(fi.Name) && (fi.Attributes & FileAttributes.Directory) != 0) continue;
                    if (fi.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var g in new[] { "index", "HEAD", "FETCH_HEAD" })
                        { try { var gf = new FileInfo(Path.Combine(fi.FullName, g)); if (gf.Exists && gf.LastWriteTime > best) best = gf.LastWriteTime; } catch { } }
                        continue;
                    }
                    if (fi.LastWriteTime > best) best = fi.LastWriteTime;
                }
            }
            catch { }
            return best == DateTime.MinValue ? Directory.GetLastWriteTime(artifactPath) : best;
        }

        public static List<StaleProject> Scan(IList<string> roots, int staleDays, IProgress<string> prog, CancellationToken ct)
        {
            var res = new List<StaleProject>();
            DateTime cut = DateTime.Now.AddDays(-staleDays);
            int dirs = 0;
            foreach (var root in roots)
            {
                var st = new Stack<string>(); st.Push(root);
                while (st.Count > 0)
                {
                    if (ct.IsCancellationRequested) return res;
                    string cur = st.Pop();
                    if ((++dirs & 127) == 0 && prog != null) prog.Report("Dev projects: " + Fmt.Mid(cur, 70));
                    IEnumerable<DirectoryInfo> en;
                    try { en = new DirectoryInfo(cur).EnumerateDirectories(); } catch { continue; }
                    try
                    {
                        foreach (var d in en)
                        {
                            try
                            {
                                if (CleanOps.IsReparse(d)) continue;
                                string ln = d.Name.ToLowerInvariant();
                                if (ArtifactNames.Contains(ln))
                                {
                                    string kind = Classify(cur, d.Name);
                                    if (kind != null && !CleanGuard.IsExcluded(d.FullName))
                                    {
                                        string why;
                                        if (!CleanGuard.Check(d.FullName, true, out why)) continue;
                                        DateTime last = LastActivity(cur, d.FullName);
                                        if (last < cut)
                                        {
                                            long size = CleanOps.DirSize(d.FullName, ct, null);
                                            if (size > 0) res.Add(new StaleProject { Path = d.FullName, Project = cur, Kind = kind, Size = size, LastActive = last, IdleDays = (int)(DateTime.Now - last).TotalDays });
                                        }
                                    }
                                    continue;     // never descend into artifact folders
                                }
                                if (Skip.Contains(ln) || CleanGuard.IsExcluded(d.FullName)) continue;
                                st.Push(d.FullName);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            return res.OrderByDescending(r => r.Size).ToList();
        }
    }
}
