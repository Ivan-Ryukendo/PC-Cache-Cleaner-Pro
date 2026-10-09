using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CleanPC.Pro
{
    public class DriveEntry : ObservableObject
    {
        public string Letter { get; set; }     // "C:"
        public string Label { get; set; }
        public long Size { get; set; }
        public long Free { get; set; }
        bool _sel; public bool IsSelected { get { return _sel; } set { Set(ref _sel, value); } }
        public void RaiseText() { Raise("Text"); }
        public string Text
        {
            get { return Letter + (string.IsNullOrEmpty(Label) ? "" : " " + Label) + "  (" + Fmt.Size(Free) + " free of " + Fmt.Size(Size) + ")"; }
        }
    }

    public static class Drives
    {
        /// <summary>Fixed local disks only: removable, network and optical drives are excluded.</summary>
        public static List<DriveEntry> Fixed()
        {
            var r = new List<DriveEntry>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        if (d.TotalSize < (1L << 30) || string.Equals(d.VolumeLabel, "System Reserved", StringComparison.OrdinalIgnoreCase)) continue;
                        r.Add(new DriveEntry { Letter = d.Name.Substring(0, 2).ToUpperInvariant(), Label = d.VolumeLabel, Size = d.TotalSize, Free = d.AvailableFreeSpace });
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return r;
        }

        /// <summary>Accepts "c", "C:", "C:\" ... and keeps only real fixed drives.</summary>
        public static List<string> Resolve(IEnumerable<string> want, bool all)
        {
            var fixedL = Fixed().Select(d => d.Letter).ToList();
            if (all) return fixedL;
            var o = new List<string>();
            foreach (var w in want ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                string l = char.ToUpperInvariant(w.Trim()[0]) + ":";
                if (fixedL.Contains(l) && !o.Contains(l)) o.Add(l);
            }
            return o;
        }

        public static long FreeOf(string letter)
        {
            try { return new DriveInfo(letter.Substring(0, 1)).AvailableFreeSpace; } catch { return -1; }
        }
    }
}
