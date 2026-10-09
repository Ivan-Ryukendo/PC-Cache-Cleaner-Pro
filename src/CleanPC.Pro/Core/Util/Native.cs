using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CleanPC.Pro
{
    /// <summary>Win32 P/Invoke surface (Recycle Bin, Authenticode verify, console attach, link resolve).</summary>
    internal static class Native
    {
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);

        // ---- SHFileOperation (Recycle Bin) ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        struct SHFOS32 { public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags; public int fAnyAborted; public IntPtr hMap; public string title; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFOS64 { public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags; public int fAnyAborted; public IntPtr hMap; public string title; }
        [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)] static extern int SHFileOp32(ref SHFOS32 op);
        [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)] static extern int SHFileOp64(ref SHFOS64 op);

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>
        /// Sends ONE path to the Recycle Bin (FOF_ALLOWUNDO). Never a deliberate permanent delete: if Windows would have
        /// to destroy the file instead of recycling it, FOF_WANTNUKEWARNING makes the shell ask first.
        /// Returns null on success, otherwise a reason.
        /// </summary>
        public static string RecycleOne(string path)
        {
            int rc; bool aborted;
            string from = path + "\0";   // marshaller appends the second NUL
            ushort flags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT | FOF_NOERRORUI);
            try
            {
                if (IntPtr.Size == 8)
                {
                    var o = new SHFOS64 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
                    rc = SHFileOp64(ref o); aborted = o.fAnyAborted != 0;
                }
                else
                {
                    var o = new SHFOS32 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
                    rc = SHFileOp32(ref o); aborted = o.fAnyAborted != 0;
                }
            }
            catch (Exception e) { return e.Message; }
            if (aborted) return "cancelled / would not fit in the Recycle Bin (kept)";
            if (rc != 0) return "Windows error 0x" + rc.ToString("X");
            return System.IO.File.Exists(path) ? "file still exists after the operation" : null;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
        public static int EmptyRecycleBin(string driveRoot)
        {
            return SHEmptyRecycleBin(IntPtr.Zero, driveRoot, 0x1 | 0x2 | 0x4);   // no confirm, no progress, no sound
        }

        // ---- WinVerifyTrust ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class WtFileInfo { public uint cb = (uint)Marshal.SizeOf(typeof(WtFileInfo)); public string path; public IntPtr hFile; public IntPtr known; }
        [StructLayout(LayoutKind.Sequential)]
        class WtData
        {
            public uint cb = (uint)Marshal.SizeOf(typeof(WtData)); public IntPtr policy, sip; public uint ui = 2, revoke = 0, union = 1;
            public IntPtr file; public uint state = 0; public IntPtr stateData, url; public uint provFlags = 0x10 | 0x1000, uiCtx = 0; public IntPtr sigSettings;
        }
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, WtData data);

        public static string VerifyFile(string path)
        {
            IntPtr pf = IntPtr.Zero;
            try
            {
                var fi = new WtFileInfo { path = path };
                pf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WtFileInfo)));
                Marshal.StructureToPtr(fi, pf, false);
                var d = new WtData { file = pf };
                int r = WinVerifyTrust(IntPtr.Zero, new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), d);
                uint u = unchecked((uint)r);
                switch (u)
                {
                    case 0: return "Valid";
                    case 0x800B0100: return "NotSigned";
                    case 0x80096010: return "HashMismatch";
                    case 0x800B0109: return "NotTrusted";
                    case 0x800B010C: return "Revoked";
                    default: return "Invalid(0x" + u.ToString("X") + ")";
                }
            }
            catch { return "Unknown"; }
            finally { if (pf != IntPtr.Zero) Marshal.FreeHGlobal(pf); }
        }

        // ---- resolve a directory link (junction/symlink) to its final target ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(IntPtr h, StringBuilder sb, uint len, uint flags);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        public static string FinalPath(string path)
        {
            IntPtr h = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);   // FILE_FLAG_BACKUP_SEMANTICS
            if (h == new IntPtr(-1)) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint n = GetFinalPathNameByHandle(h, sb, 1024, 0);
                if (n == 0 || n >= 1024) return null;
                string s = sb.ToString();
                return s.StartsWith(@"\\?\") ? s.Substring(4) : s;
            }
            finally { CloseHandle(h); }
        }
    }
}
