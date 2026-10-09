using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CleanPC.Pro
{
    /// <summary>A running program that holds one or more skipped files. Read-only facts plus a little UI state.</summary>
    public class LockHolder : ObservableObject
    {
        public int Pid { get; set; }
        public long StartFileTime { get; set; }    // identity check so a reused PID is never mistaken for this process
        public string Name { get; set; }
        public string ExePath { get; set; }
        public string Service { get; set; }
        public int AppType { get; set; }           // RM_APP_TYPE
        public List<SkipInfo> Files { get; set; } = new List<SkipInfo>();
        public long Bytes { get { return Files.Sum(f => f.Bytes); } }
        public int FileCount { get { return Files.Count; } }
        public string BytesText { get { return "about " + Fmt.Size(Bytes); } }
        public string TypeText { get { return RestartManager.AppTypeText(AppType, Service); } }
        public string TypeAndState { get { return string.IsNullOrEmpty(StateText) ? TypeText : TypeText + " - " + StateText; } }

        public string ProtectedReason { get; set; }     // null = may be offered for closing
        public bool CanClose { get { return string.IsNullOrEmpty(ProtectedReason) && string.IsNullOrEmpty(_state); } }
        public string CloseTip { get { return string.IsNullOrEmpty(ProtectedReason) ? "Ask this program to close (you confirm first; unsaved work can be lost)" : "Not offered: " + ProtectedReason; } }
        public string PathText { get { return string.IsNullOrEmpty(ExePath) ? "(path not readable)" : ExePath; } }

        string _state = ""; public string State { get { return _state; } set { if (Set(ref _state, value ?? "")) { Raise("CanClose"); Raise("StateText"); Raise("TypeAndState"); } } }
        public string StateText { get { return !string.IsNullOrEmpty(_state) ? _state : (string.IsNullOrEmpty(ProtectedReason) ? "" : "Protected: " + ProtectedReason); } }
    }

    public class LockDiagnosisResult
    {
        public List<LockHolder> Holders = new List<LockHolder>();
        public int Analysed, NotAnalysed, NoHolderFound;
        public string Error;
    }

    /// <summary>Windows Restart Manager (rstrtmgr.dll): asks Windows which running processes hold a file open. Read-only.</summary>
    public static class RestartManager
    {
        const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmRegisterResources(uint dwSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);
        [DllImport("rstrtmgr.dll")]
        static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);
        [DllImport("rstrtmgr.dll")]
        static extern int RmEndSession(uint dwSessionHandle);

        public class Holder { public int Pid; public long StartFileTime; public string AppName, Service; public int AppType; }

        public static string AppTypeText(int t, string service)
        {
            switch (t)
            {
                case 1: return "Window app";
                case 2: return "Window (other)";
                case 3: return string.IsNullOrEmpty(service) ? "Windows service" : "Windows service (" + service + ")";
                case 4: return "Windows Explorer";
                case 5: return "Console app";
                case 1000: return "Critical system process";
                default: return "Unknown type";
            }
        }

        /// <summary>Processes that currently hold any of the given files open. Never throws; error text goes to err.</summary>
        public static List<Holder> WhoHolds(IEnumerable<string> files, out string err)
        {
            err = null;
            var result = new List<Holder>();
            uint h; var key = new StringBuilder(64);
            int rc = RmStartSession(out h, 0, key);
            if (rc != 0) { err = "RmStartSession failed (" + rc + ")"; return result; }
            try
            {
                var arr = files.ToArray();
                if (arr.Length == 0) return result;
                rc = RmRegisterResources(h, (uint)arr.Length, arr, 0, null, 0, null);
                if (rc != 0) { err = "RmRegisterResources failed (" + rc + ")"; return result; }
                uint needed = 0, count = 0, reasons = 0;
                rc = RmGetList(h, out needed, ref count, null, ref reasons);
                int guard = 0;
                while (rc == ERROR_MORE_DATA && guard++ < 5)
                {
                    var info = new RM_PROCESS_INFO[needed];
                    count = needed;
                    rc = RmGetList(h, out needed, ref count, info, ref reasons);
                    if (rc == 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var p = info[i];
                            long ft = ((long)(uint)p.Process.ProcessStartTime.dwHighDateTime << 32) | (uint)p.Process.ProcessStartTime.dwLowDateTime;
                            result.Add(new Holder { Pid = p.Process.dwProcessId, StartFileTime = ft, AppName = p.strAppName, Service = p.strServiceShortName, AppType = p.ApplicationType });
                        }
                    }
                }
                if (rc != 0 && rc != ERROR_MORE_DATA) err = "RmGetList failed (" + rc + ")";
                else if (rc == ERROR_MORE_DATA) err = "RmGetList kept growing; list incomplete";
            }
            catch (Exception e) { err = e.Message; }
            finally { try { RmEndSession(h); } catch { } }
            return result;
        }

        /// <summary>
        /// Attributes each locked file to the process(es) holding it. One Restart Manager session per file keeps the
        /// per-process file count exact; capped (default 500 files) so a huge skip list cannot stall the UI thread's helper.
        /// </summary>
        public static LockDiagnosisResult Diagnose(IList<SkipInfo> locked, CancellationToken ct, IProgress<string> prog, int maxFiles = 500)
        {
            var res = new LockDiagnosisResult();
            var byKey = new Dictionary<string, LockHolder>();
            var distinct = locked.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            res.NotAnalysed = Math.Max(0, distinct.Count - maxFiles);
            int n = 0;
            foreach (var it in distinct.Take(maxFiles))
            {
                if (ct.IsCancellationRequested) { res.NotAnalysed += distinct.Count - n; break; }
                n++;
                if (prog != null && n % 10 == 1) prog.Report("Asking Windows who holds the locked files (" + n + "/" + Math.Min(distinct.Count, maxFiles) + ")...");
                string err;
                var hs = WhoHolds(new[] { it.Path }, out err);
                res.Analysed++;
                if (err != null && res.Error == null) res.Error = err;
                if (hs.Count == 0) { res.NoHolderFound++; continue; }
                foreach (var x in hs)
                {
                    string k = x.Pid + ":" + x.StartFileTime;
                    LockHolder lh;
                    if (!byKey.TryGetValue(k, out lh))
                    {
                        lh = new LockHolder { Pid = x.Pid, StartFileTime = x.StartFileTime, Name = x.AppName, Service = x.Service, AppType = x.AppType };
                        Enrich(lh);
                        byKey[k] = lh; res.Holders.Add(lh);
                    }
                    lh.Files.Add(it);
                }
            }
            res.Holders.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            return res;
        }

        static void Enrich(LockHolder lh)
        {
            string friendly = lh.Name;
            lh.ExePath = ProcessGuard.ImagePath(lh.Pid);
            string pname = null;
            try { using (var p = Process.GetProcessById(lh.Pid)) pname = p.ProcessName; } catch { }
            if (!string.IsNullOrEmpty(lh.ExePath)) lh.Name = Path.GetFileNameWithoutExtension(lh.ExePath);
            else if (!string.IsNullOrEmpty(pname)) lh.Name = pname;
            else lh.Name = string.IsNullOrEmpty(friendly) ? "PID " + lh.Pid : friendly;
            lh.ProtectedReason = ProcessGuard.ProtectedReason(lh.Pid, lh.Name, lh.ExePath, lh.AppType, lh.Service);
        }
    }
}
