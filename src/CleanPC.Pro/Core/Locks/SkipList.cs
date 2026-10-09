using System;
using System.Collections.Generic;
using System.IO;

namespace CleanPC.Pro
{
    /// <summary>One file a clean could not delete: kept structurally (not only as log text) so locks can be diagnosed and retried.</summary>
    public class SkipInfo
    {
        public string Path, Reason, Root, TargetName;
        public long Bytes;
        /// <summary>True only for a sharing violation (file in use). Access denied etc. are skipped but not "locked".</summary>
        public bool Locked;
        /// <summary>Cleanup path the file was found under (set by CleanRunner); retry re-checks the guard against it.</summary>
        public bool RootIsFile;
    }

    /// <summary>The usual "SKIP path: reason" text list, plus the structured file-level records.</summary>
    public class SkipList : List<string>
    {
        public readonly List<SkipInfo> Items = new List<SkipInfo>();
    }

    public static class Skip
    {
        public static bool IsSharingViolation(Exception e)
        {
            var io = e as IOException;
            if (io == null) return false;
            int code = io.HResult & 0xFFFF;
            return code == 32 || code == 33;   // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        }

        /// <summary>Records a file that could not be deleted. Always adds the text line; adds a structured record when the list supports it.</summary>
        public static void File(List<string> skips, string path, Exception e, long bytes)
        {
            skips.Add("SKIP " + path + ": " + e.Message);
            var sl = skips as SkipList;
            if (sl != null) sl.Items.Add(new SkipInfo { Path = path, Reason = e.Message, Bytes = bytes, Locked = IsSharingViolation(e) });
        }
    }
}
