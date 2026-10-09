using System;
using System.Globalization;

namespace CleanPC.Pro
{
    public static class Fmt
    {
        public static string Size(long b)
        {
            var ci = CultureInfo.CurrentCulture;
            if (b < 0) return "...";
            if (b >= 1L << 40) return ((double)b / (1L << 40)).ToString("N2", ci) + " TB";
            if (b >= 1L << 30) return ((double)b / (1L << 30)).ToString("N2", ci) + " GB";
            if (b >= 1L << 20) return ((double)b / (1L << 20)).ToString("N1", ci) + " MB";
            if (b >= 1L << 10) return ((double)b / (1L << 10)).ToString("N0", ci) + " KB";
            return b + " B";
        }

        public static string Mid(string s, int max)
        {
            if (s == null || s.Length <= max) return s ?? "";
            int a = max / 3, b = max - a - 3;
            return s.Substring(0, a) + "..." + s.Substring(s.Length - b);
        }

        public static string Html(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public static string Csv(string s)
        {
            s = s ?? "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
