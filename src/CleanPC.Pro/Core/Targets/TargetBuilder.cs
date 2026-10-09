using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CleanPC.Pro
{
    /// <summary>
    /// Port of the Standard edition's Build-Targets: target names, paths, categories, ticked defaults and RISKY notes
    /// are kept IDENTICAL on purpose so both editions can merge later. Returns deletable rows + report-only rows.
    /// </summary>
    public static class TargetBuilder
    {
        static string Env(string n) { return Environment.GetEnvironmentVariable(n) ?? ""; }

        static bool Exists(string p) { try { return Directory.Exists(p) || File.Exists(p); } catch { return false; } }

        static string Friendly(string full, string L, string R)
        {
            string rel = full;
            if (full.StartsWith(L, StringComparison.OrdinalIgnoreCase)) rel = full.Substring(L.Length).TrimStart('\\');
            else if (full.StartsWith(R, StringComparison.OrdinalIgnoreCase)) rel = full.Substring(R.Length).TrimStart('\\');
            string p0 = rel.Split('\\')[0];
            if (Regex.IsMatch(rel, @"Google\\Chrome")) return "Google Chrome (cache)";
            if (rel.Contains("BraveSoftware")) return "Brave Browser (cache)";
            if (Regex.IsMatch(rel, @"Microsoft\\Edge")) return "Microsoft Edge (cache)";
            if (rel.Contains("Vivaldi")) return "Vivaldi (cache)";
            if (rel.Contains("Opera Software")) return "Opera (cache)";
            if (rel.Contains("Mozilla")) return "Firefox (cache)";
            var m = Regex.Match(rel, @"^Packages\\([^\\]+)");
            if (m.Success) return m.Groups[1].Value + " (cache)";
            return p0 + " (cache)";
        }

        public static string VdiskHowTo(string p)
        {
            if (Regex.IsMatch(p, "WindowsSubsystemForAndroid")) return "Windows Subsystem for Android (discontinued): Settings > Apps > Installed apps > Windows Subsystem for Android > Uninstall (removes its userdata disks).";
            if (Regex.IsMatch(p, @"\\Containers\\")) return "Windows Defender Application Guard / Sandbox container image: turn the feature off in \"Turn Windows features on or off\" (or Disable-WindowsOptionalFeature -Online -FeatureName Windows-Defender-ApplicationGuard in admin PowerShell).";
            if (Regex.IsMatch(p, @"(ext4|docker_data)\.vhdx$", RegexOptions.IgnoreCase))
                return "WSL/Docker disk. Compact it (not run by this tool): 1) wsl --shutdown  2) diskpart: select vdisk file=\"" + p + "\" / attach vdisk readonly / compact vdisk / detach vdisk / exit   (Hyper-V edition: Optimize-VHD -Path \"" + p + "\" -Mode Full). "
                     + "Or shrink with: wsl --manage <distro> --set-sparse true. \"wsl --unregister <distro>\" DELETES that distro and its files. Docker Desktop: Troubleshoot > Clean / Purge data.";
            if (Regex.IsMatch(p, @"\.(vmdk|vdi)$", RegexOptions.IgnoreCase)) return "Virtual machine disk: remove the VM in VirtualBox/VMware and choose to delete its files. Do not delete it by hand if the VM is still needed.";
            return "Virtual disk: remove it only through the app/feature that owns it (Hyper-V Manager, Settings > Apps) after confirming it is unused. To shrink a .vhdx: diskpart > select vdisk file=\"" + p + "\" > attach vdisk readonly > compact vdisk > detach vdisk.";
        }

        public static ScanResult Build(IList<string> drivesIn, IProgress<string> prog, CancellationToken ct)
        {
            if (prog == null) prog = new Progress<string>();
            var drives = drivesIn.ToList();
            string L = Env("LOCALAPPDATA"), R = Env("APPDATA"), U = Env("USERPROFILE"), PD = Env("ProgramData");
            string sys = AppInfo.SystemDrive, WIN = Env("SystemRoot"), TEMP = Env("TEMP");
            bool doSys = drives.Contains(sys), admin = AppInfo.IsAdmin;
            var S = new List<Target>();
            var Rep = new List<Target>();
            var explicitP = new List<string>();

            Action<string, string, IEnumerable<string>, TargetKind, bool, string, string, string> addS = (name, cat, paths, kind, chk, note, drv, svc) =>
            {
                var ex = new List<string>();
                foreach (var p in paths)
                    if (!string.IsNullOrEmpty(p) && !ex.Contains(p, StringComparer.OrdinalIgnoreCase) && Exists(p)) { ex.Add(p); explicitP.Add(p.TrimEnd('\\')); }
                if (ex.Count == 0) return;
                S.Add(new Target { Name = name, Category = cat, Paths = ex, Kind = kind, DefaultChecked = chk, Note = note, Drive = drv, Service = svc, IsChecked = chk });
            };
            Action<string, string, IEnumerable<string>, TargetKind, bool, string, string, long> addPre = (name, cat, paths, kind, chk, note, drv, size) =>
            {
                var pl = paths.ToList();
                if (size <= 0 || pl.Count == 0) return;
                S.Add(new Target { Name = name, Category = cat, Paths = pl, Kind = kind, DefaultChecked = chk, Note = note, Drive = drv, Size = size, IsChecked = chk });
            };
            Action<string, IEnumerable<string>, string, long> addRep = (name, paths, note, size) =>
                Rep.Add(new Target { Name = name, Category = "Report", Paths = paths.ToList(), Kind = TargetKind.Report, Note = note, Drive = "", Size = size });

            // 1) background junk walk: one task per drive, in parallel
            var walkers = new List<DriveWorker>(); var tasks = new List<Task>();
            if (drives.Count > 0)
            {
                prog.Report("Starting drive scan...");
                foreach (var dv in drives)
                {
                    var w = new DriveWorker(); walkers.Add(w);
                    string root = dv + "\\";
                    tasks.Add(Task.Run(() => { try { w.Walk(root, 8, 90, 7, ct); } catch (Exception e) { Log.Error("walk " + root, e); } }));
                }
            }
            // 2) sweep the user's AppData for browser/app caches (and big virtual disks)
            DriveWorker sweep = null; var sweepTasks = new List<Task>();
            if (doSys)
            {
                sweep = new DriveWorker();
                var names = new HashSet<string>(new[] { "cache", "cache_data", "code cache", "gpucache", "cachedata", "dawngraphitecache", "dawnwebgpucache", "dawncache", "grshadercache", "shadercache" });
                foreach (var r in new[] { L, R, PD + "\\Microsoft\\Windows\\Containers" })
                {
                    if (string.IsNullOrEmpty(r) || !Directory.Exists(r)) continue;
                    string rr = r;
                    sweepTasks.Add(Task.Run(() => { try { sweep.Sweep(rr, names, 6, ct); } catch (Exception e) { Log.Error("sweep " + rr, e); } }));
                }
            }

            // 3) explicit, known locations (cheap existence checks; sizes measured later)
            if (doSys)
            {
                addS("User Temp files", "Temp", new[] { TEMP, L + "\\Temp" }, TargetKind.Contents, true, "", sys, null);
                addS("Application crash dumps", "Temp", new[] { L + "\\CrashDumps" }, TargetKind.Contents, true, "", sys, null);
                addS("Windows Error Reporting (user)", "Temp", new[] { L + "\\Microsoft\\Windows\\WER" }, TargetKind.Contents, true, "", sys, null);
                addS("Internet/WinINet cache", "Temp", new[] { L + "\\Microsoft\\Windows\\INetCache" }, TargetKind.Contents, true, "", sys, null);
                if (admin)
                {
                    addS("Windows Temp (system)", "Temp", new[] { WIN + "\\Temp" }, TargetKind.Contents, true, "admin", sys, null);
                    addS("Windows Update download cache", "Windows", new[] { WIN + "\\SoftwareDistribution\\Download" }, TargetKind.Contents, true, "admin; update service is paused briefly", sys, "wuauserv");
                    addS("Delivery Optimization cache", "Windows", new[] { WIN + "\\ServiceProfiles\\NetworkService\\AppData\\Local\\Microsoft\\Windows\\DeliveryOptimization\\Cache" }, TargetKind.Contents, true, "admin; re-downloaded if needed", sys, null);
                    addS("Windows Error Reporting (system)", "Temp", new[] { PD + "\\Microsoft\\Windows\\WER\\ReportArchive", PD + "\\Microsoft\\Windows\\WER\\ReportQueue", PD + "\\Microsoft\\Windows\\WER\\Temp" }, TargetKind.Contents, true, "admin", sys, null);
                    addS("Kernel crash dumps (Minidump, LiveKernelReports)", "Windows", new[] { WIN + "\\Minidump", WIN + "\\LiveKernelReports" }, TargetKind.Contents, false, "RISKY: blue-screen diagnostics - keep if you are troubleshooting crashes", sys, null);
                    try
                    {
                        var mf = new FileInfo(WIN + "\\MEMORY.DMP");
                        if (mf.Exists) addPre("Full memory dump (MEMORY.DMP)", "Windows", new[] { mf.FullName }, TargetKind.Files, false, "RISKY: blue-screen diagnostics - keep if you are troubleshooting crashes", sys, mf.Length);
                    }
                    catch { }
                    try
                    {
                        var cutoff = DateTime.Now.AddDays(-14);
                        var cbs = new DirectoryInfo(WIN + "\\Logs\\CBS").EnumerateFiles().Where(f => f.LastWriteTime < cutoff && (f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".cab", StringComparison.OrdinalIgnoreCase))).ToList();
                        if (cbs.Count > 0) addPre("Old Windows servicing logs (Logs\\CBS, 14+ days)", "Windows", cbs.Select(f => f.FullName), TargetKind.Files, true, "admin; only files older than 14 days", sys, cbs.Sum(f => f.Length));
                    }
                    catch { }
                }
                addS("NVIDIA shader cache", "GPU", new[] { L + "\\NVIDIA\\DXCache", L + "\\NVIDIA\\GLCache", PD + "\\NVIDIA Corporation\\NV_Cache" }, TargetKind.Contents, true, "", sys, null);
                addS("NVIDIA App update installers (ota-artifacts)", "GPU", new[] { PD + "\\NVIDIA Corporation\\NVIDIA App\\UpdateFramework\\ota-artifacts" }, TargetKind.Contents, true, "downloaded installers; re-downloaded on next update", sys, null);
                addS("AMD shader cache", "GPU", new[] { L + "\\AMD\\DxCache", L + "\\AMD\\GLCache", L + "\\AMD\\VkCache" }, TargetKind.Contents, true, "", sys, null);
                addS("Intel shader cache", "GPU", new[] { L + "\\Intel\\ShaderCache" }, TargetKind.Contents, true, "", sys, null);
                addS("DirectX shader cache", "GPU", new[] { L + "\\D3DSCache" }, TargetKind.Contents, true, "", sys, null);
                // Teams (new): WebView cache sits deeper than the generic sweep reaches
                var tp = new List<string>();
                try
                {
                    foreach (var pk in new DirectoryInfo(L + "\\Packages").EnumerateDirectories("MSTeams*"))
                    {
                        string eb = pk.FullName + "\\LocalCache\\Microsoft\\MSTeams\\EBWebView";
                        foreach (var cn in new[] { "Cache", "Code Cache", "GPUCache" })
                            FindDirs(eb, cn, 3, tp);
                    }
                }
                catch { }
                if (tp.Count > 0) addS("Microsoft Teams (cache)", "Browser/App", tp, TargetKind.Contents, true, "cache folders only", sys, null);
                // Firefox cache2
                var ff = new List<string>();
                try { foreach (var pf in new DirectoryInfo(L + "\\Mozilla\\Firefox\\Profiles").EnumerateDirectories()) { string c = Path.Combine(pf.FullName, "cache2"); if (Directory.Exists(c)) ff.Add(c); } } catch { }
                if (ff.Count > 0) addS("Firefox (cache)", "Browser/App", ff, TargetKind.Contents, true, "", sys, null);
                // Thumbnail / icon cache
                try
                {
                    var th = new DirectoryInfo(L + "\\Microsoft\\Windows\\Explorer").EnumerateFiles().Where(f => Regex.IsMatch(f.Name, @"^(thumb|icon)cache_.*\.db$", RegexOptions.IgnoreCase)).ToList();
                    if (th.Count > 0) addPre("Thumbnail / icon cache", "Windows", th.Select(f => f.FullName), TargetKind.Files, true, "rebuilds automatically", sys, th.Sum(f => f.Length));
                }
                catch { }
                // Dev / package caches
                var uvc = new List<string> { L + "\\uv\\cache" }; if (!string.IsNullOrEmpty(Env("UV_CACHE_DIR"))) uvc.Add(Env("UV_CACHE_DIR"));
                addS("pip cache (Python)", "Dev", new[] { L + "\\pip\\Cache", L + "\\pip\\cache" }, TargetKind.Contents, true, "", sys, null);
                addS("uv cache (Python)", "Dev", uvc, TargetKind.Contents, true, "", sys, null);
                addS("npm cache (Node)", "Dev", new[] { L + "\\npm-cache", R + "\\npm-cache" }, TargetKind.Contents, true, "", sys, null);
                addS("Yarn cache (Node)", "Dev", new[] { L + "\\Yarn\\Cache" }, TargetKind.Contents, true, "", sys, null);
                addS("Go build cache", "Dev", new[] { L + "\\go-build" }, TargetKind.Contents, true, "", sys, null);
                addS("NuGet http cache (.NET)", "Dev", new[] { L + "\\NuGet\\v3-cache" }, TargetKind.Contents, true, "", sys, null);
                var sc = new List<string> { U + "\\scoop\\cache" }; if (!string.IsNullOrEmpty(Env("SCOOP"))) sc.Add(Env("SCOOP") + "\\cache");
                addS("Scoop download cache", "Dev", sc, TargetKind.Contents, true, "installer downloads; re-downloaded if needed", sys, null);
                addS("Arduino staging (downloads)", "Dev", new[] { L + "\\Arduino15\\staging" }, TargetKind.Contents, true, "downloaded packages; re-downloaded if needed", sys, null);
                addS("User .cache (tool caches)", "Dev", new[] { U + "\\.cache" }, TargetKind.Contents, true, "", sys, null);
                addS("Gradle dependency cache", "Dev", new[] { U + "\\.gradle\\caches" }, TargetKind.Contents, false, "large re-download on next build", sys, null);
                addS("Playwright browsers", "Dev", new[] { L + "\\ms-playwright" }, TargetKind.Contents, false, "RISKY: tests need these; re-download is several hundred MB", sys, null);
                // Codex: old versioned release folders. Keeps the newest, the highest version AND whatever 'current' points to.
                try
                {
                    foreach (var pkgDir in new DirectoryInfo(U + "\\.codex\\packages").EnumerateDirectories())
                    {
                        string rel = Path.Combine(pkgDir.FullName, "releases");
                        if (!Directory.Exists(rel)) continue;
                        var vers = new DirectoryInfo(rel).EnumerateDirectories().Where(d => !CleanOps.IsReparse(d)).ToList();
                        if (vers.Count < 2) continue;
                        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        keep.Add(vers.OrderByDescending(v => v.LastWriteTime).First().FullName);
                        Version best = null; DirectoryInfo bestD = null;
                        foreach (var v in vers) { Version pv; if (Version.TryParse(v.Name.Split('-')[0], out pv) && (best == null || pv > best)) { best = pv; bestD = v; } }
                        if (bestD != null) keep.Add(bestD.FullName);
                        string curLink = Path.Combine(pkgDir.FullName, "current");
                        bool curOk = true;
                        if (Directory.Exists(curLink) || File.Exists(curLink))
                        {
                            string tgt = Native.FinalPath(curLink);
                            if (tgt == null) curOk = false;   // cannot tell what is in use: be safe, offer nothing
                            else keep.Add(Path.Combine(rel, Path.GetFileName(tgt.TrimEnd('\\'))));
                        }
                        if (!curOk) continue;
                        var old = vers.Where(v => !keep.Contains(v.FullName)).Select(v => v.FullName).ToList();
                        if (old.Count > 0)
                            addS("Codex old releases (" + pkgDir.Name + ")", "Dev", old, TargetKind.RemoveDirs, false,
                                "RISKY: removes whole old version folders; keeps: " + string.Join(", ", keep.Select(k => Path.GetFileName(k))), sys, null);
                    }
                }
                catch { }
                // Optional / heavy
                addS("Claude Desktop VM cache", "Optional", new[] { R + "\\Claude\\vm_bundles" }, TargetKind.Contents, false, "Close Claude Desktop first to free it fully", sys, null);
                // fnm node versions: LIST ONLY, never deleted by this tool
                var nvSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var nv in new[] { R + "\\fnm\\node-versions", string.IsNullOrEmpty(Env("FNM_DIR")) ? null : Env("FNM_DIR") + "\\node-versions" })
                {
                    if (string.IsNullOrEmpty(nv) || !nvSeen.Add(nv) || !Directory.Exists(nv)) continue;
                    try
                    {
                        foreach (var v in new DirectoryInfo(nv).EnumerateDirectories().Where(d => !d.Name.StartsWith(".")))
                            Rep.Add(new Target { Name = "Node.js " + v.Name + " (fnm)", Category = "Report", Paths = new List<string> { v.FullName }, Kind = TargetKind.Report, Size = -1, Drive = sys,
                                Note = "Installed Node version - report only. If unused, remove with: fnm uninstall " + v.Name });
                    }
                    catch { }
                }
            }

            foreach (var d in drives)
            {
                addS("Recycle Bin [" + d + "]", "Optional", new[] { d + "\\$Recycle.Bin" }, TargetKind.RecycleBin, false, "Permanently empties the Recycle Bin of this drive", d, null);
                addS("Root \\Temp folder [" + d + "]", "Drive junk", new[] { d + "\\Temp" }, TargetKind.Contents, false, "Check what is in it first", d, null);
                addS("Root \\tmp folder [" + d + "]", "Drive junk", new[] { d + "\\tmp" }, TargetKind.Contents, false, "Check what is in it first", d, null);
                if (d != sys && Directory.Exists(d + "\\Windows\\System32"))
                    addS("Windows Temp of other install [" + d + "]", "Drive junk", new[] { d + "\\Windows\\Temp" }, TargetKind.Contents, true, "another Windows installation on this drive", d, null);
                if (Directory.Exists(d + "\\Users"))
                {
                    var skipU = new[] { "Public", "Default", "Default User", "All Users" };
                    try
                    {
                        foreach (var ud in new DirectoryInfo(d + "\\Users").EnumerateDirectories().Where(x => !skipU.Contains(x.Name, StringComparer.OrdinalIgnoreCase) && !CleanOps.IsReparse(x)))
                        {
                            if (ud.FullName.TrimEnd('\\').Equals(U.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                            addS("Temp of user '" + ud.Name + "' [" + d + "]", "Other users", new[] { ud.FullName + "\\AppData\\Local\\Temp" }, TargetKind.Contents, true, "another profile; locked files are skipped", d, null);
                        }
                    }
                    catch { }
                }
            }

            // 4) collect the AppData sweep and group by application
            if (sweep != null)
            {
                WaitTasks(sweepTasks, () => sweep.Current, "Looking for app caches:", prog, ct);
                var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                string[] hits; lock (sweep.NamedHits) hits = sweep.NamedHits.ToArray();
                foreach (var hit in hits)
                {
                    string h = hit.TrimEnd('\\');
                    if (explicitP.Any(e => h.Equals(e, StringComparison.OrdinalIgnoreCase) || h.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                    string key = Friendly(hit, L, R);
                    List<string> g; if (!groups.TryGetValue(key, out g)) groups[key] = g = new List<string>();
                    g.Add(hit);
                }
                foreach (var kv in groups) addS(kv.Key, "Browser/App", kv.Value, TargetKind.Contents, true, "", sys, null);
            }

            // 5) measure everything in the background (4 at a time)
            var pending = S.Where(t => t.Size < 0).ToList();
            if (pending.Count > 0)
            {
                string curSize = "";
                var po = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
                var t0 = Task.Run(() =>
                {
                    try
                    {
                        Parallel.ForEach(pending, po, t =>
                        {
                            long s = 0;
                            foreach (var p in t.Paths) { curSize = p; s += CleanOps.DirSize(p, ct, null); }
                            t.Size = s;
                        });
                    }
                    catch (OperationCanceledException) { }
                });
                WaitTasks(new List<Task> { t0 }, () => curSize, "Measuring:", prog, ct);
            }

            // 6) junk-file findings from the drive walk and the AppData sweep
            WaitTasks(tasks, () => { var a = walkers.FirstOrDefault(w => !string.IsNullOrEmpty(w.Current)); return a == null ? "" : walkers.Last().Current; }, "Scanning drives (" + drives.Count + "):", prog, ct);
            var finds = new List<Finding>();
            foreach (var w in walkers) lock (w.Findings) finds.AddRange(w.Findings);
            if (sweep != null) lock (sweep.Findings) finds.AddRange(sweep.Findings);
            var defs = new Dictionary<string, object[]>
            {
                { "PyCache",   new object[] { "Python __pycache__ folders", TargetKind.Contents, true, "compiled Python bytecode; regenerated automatically" } },
                { "ToolCache", new object[] { "Python tool caches (.pytest/.mypy/.ruff)", TargetKind.Contents, true, "regenerated automatically" } },
                { "NodeCache", new object[] { "node_modules\\.cache folders", TargetKind.Contents, true, "build-tool caches; regenerated" } },
                { "Thumbs",    new object[] { "Thumbs.db files", TargetKind.Files, true, "old Explorer thumbnail files" } },
                { "Gid",       new object[] { "*.gid files (old Help cache)", TargetKind.Files, true, "obsolete Windows Help cache files" } },
                { "TmpFiles",  new object[] { "Old *.tmp / *.temp / ~$* files", TargetKind.Files, false, "RISKY: 7+ days old, but apps may still use some - review the list (double-click)" } },
                { "ChkFile",   new object[] { "*.chk files (CHKDSK fragments)", TargetKind.Files, false, "RISKY: may be fragments of recoverable files; 30+ days old" } },
                { "ChkFolder", new object[] { "found.NNN folders (CHKDSK)", TargetKind.Contents, false, "RISKY: may hold recoverable file fragments - check first" } },
                { "OldFiles",  new object[] { "*.old files (30+ days)", TargetKind.Files, false, "RISKY: can be backups made by installers or you - review the list" } },
                { "DumpFiles", new object[] { "*.dmp crash dumps (7+ days)", TargetKind.Files, false, "RISKY: you may want them for debugging - review the list" } },
            };
            var seenDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in finds.GroupBy(f => f.Kind + "|" + f.Root))
            {
                string kind = grp.First().Kind; string root = grp.First().Root.Substring(0, 2).ToUpperInvariant();
                var items = grp.ToList();
                if (kind != "BigDisk")
                {
                    items = items.Where(f => { string fp = f.FullPath.TrimEnd('\\'); return !explicitP.Any(e => fp.Equals(e, StringComparison.OrdinalIgnoreCase) || fp.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase)); }).ToList();
                    if (items.Count == 0) continue;
                }
                var paths = items.Select(f => f.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                long size = items.Sum(f => f.Size);
                if (kind == "BigDisk")
                {
                    foreach (var g in items)
                    {
                        if (!seenDisk.Add(g.FullPath)) continue;
                        addRep("Virtual disk: " + Path.GetFileName(g.FullPath), new[] { g.FullPath }, "REPORT ONLY - this tool never deletes it. " + VdiskHowTo(g.FullPath), g.Size);
                    }
                    continue;
                }
                object[] dd; if (!defs.TryGetValue(kind, out dd)) continue;
                addPre(string.Format("{0} [{1}]", dd[0], root), "Drive junk", paths, (TargetKind)dd[1], (bool)dd[2], (string)dd[3], root, size);
            }

            // 7) split into deletable vs report-only
            var targets = new List<Target>();
            var repMeasure = new List<Target>();
            foreach (var t in S) { if (t.Kind == TargetKind.Report) continue; if (t.Size > 0) targets.Add(t); }
            foreach (var t in Rep.Where(r => r.Size < 0).ToList())
            {   // fnm rows: measure now
                long s = 0; foreach (var p in t.Paths) s += CleanOps.DirSize(p, ct, null); t.Size = s;
            }
            // shadow copies / System Restore usage (information only)
            if (admin && !ct.IsCancellationRequested)
            {
                prog.Report("Reading shadow copy storage...");
                try
                {
                    string vs = Proc.Run("vssadmin.exe", "list shadowstorage", 20000);
                    string cur = null;
                    foreach (var line in vs.Split('\n'))
                    {
                        var m = Regex.Match(line, @"\((\w:)\)");
                        if (cur == null && m.Success && line.IndexOf("Used", StringComparison.OrdinalIgnoreCase) < 0) cur = m.Groups[1].Value.ToUpperInvariant();   // first "(X:)" of an association block = "For volume"
                        else if (cur != null && line.IndexOf("Used", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var mm = Regex.Match(line, @":\s*([\d\.,]+)\s*(KB|MB|GB|TB|bytes?)\b", RegexOptions.IgnoreCase);
                            if (mm.Success)
                            {
                                double num; double.TryParse(mm.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out num);
                                long mult = 1; switch (mm.Groups[2].Value.ToUpperInvariant()) { case "KB": mult = 1L << 10; break; case "MB": mult = 1L << 20; break; case "GB": mult = 1L << 30; break; case "TB": mult = 1L << 40; break; }
                                if (drives.Contains(cur))
                                    addRep("Shadow copies / System Restore (" + cur + ")", new string[0],
                                        "REPORT ONLY - manage in System Properties > System Protection > Configure (Delete removes ALL restore points), or \"vssadmin delete shadows /for=" + cur + " /oldest\". Cap the space with: vssadmin resize shadowstorage /for=" + cur + " /on=" + cur + " /maxsize=10GB",
                                        (long)(num * mult));
                                cur = null;
                            }
                        }
                    }
                }
                catch { }
            }
            // unregistered profile folders (report only)
            var reg = ProfileManager.RegisteredPaths();
            foreach (var d in drives)
            {
                try
                {
                    var skipU = new[] { "Public", "Default", "Default User", "All Users" };
                    if (!Directory.Exists(d + "\\Users")) continue;
                    foreach (var ud in new DirectoryInfo(d + "\\Users").EnumerateDirectories().Where(x => !skipU.Contains(x.Name, StringComparer.OrdinalIgnoreCase) && !CleanOps.IsReparse(x)))
                        if (reg.Count > 0 && !reg.Contains(ud.FullName.TrimEnd('\\').ToLowerInvariant()))
                        {
                            long sz = CleanOps.DirSize(ud.FullName, ct, null);
                            addRep("Unregistered profile folder [" + d + "]: " + ud.Name, new[] { ud.FullName },
                                "REPORT ONLY - a Users folder with no Windows profile (left over from a removed account). Review it in Explorer and delete it yourself if you are sure.", sz);
                        }
                }
                catch { }
            }

            return new ScanResult
            {
                Targets = targets,
                Report = Rep,
                Info = new ScanInfo { WalkTimedOut = walkers.Any(w => w.TimedOut), WalkCancelled = ct.IsCancellationRequested, DirsVisited = walkers.Sum(w => w.DirsVisited) }
            };
        }

        static void WaitTasks(List<Task> tasks, Func<string> cur, string label, IProgress<string> prog, CancellationToken ct)
        {
            if (tasks.Count == 0) return;
            try { while (!Task.WaitAll(tasks.ToArray(), 150)) prog.Report(label + " " + Fmt.Mid(cur() ?? "", 70)); }
            catch (AggregateException) { }
        }

        static void FindDirs(string root, string name, int depth, List<string> acc)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var d in new DirectoryInfo(root).EnumerateDirectories())
                {
                    if (CleanOps.IsReparse(d)) continue;
                    if (d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) acc.Add(d.FullName);
                    else if (depth > 0) FindDirs(d.FullName, name, depth - 1, acc);
                }
            }
            catch { }
        }
    }
}
