using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace CleanPC.Pro
{
    public class SemVer : IComparable<SemVer>
    {
        public int Major, Minor, Patch; public string Pre = "";
        public static SemVer Parse(string s)
        {
            var v = new SemVer();
            if (string.IsNullOrWhiteSpace(s)) return v;
            s = s.Trim().TrimStart('v', 'V');
            int plus = s.IndexOf('+'); if (plus >= 0) s = s.Substring(0, plus);
            int dash = s.IndexOf('-'); if (dash >= 0) { v.Pre = s.Substring(dash + 1); s = s.Substring(0, dash); }
            var parts = s.Split('.');
            int x;
            if (parts.Length > 0 && int.TryParse(parts[0], out x)) v.Major = x;
            if (parts.Length > 1 && int.TryParse(parts[1], out x)) v.Minor = x;
            if (parts.Length > 2 && int.TryParse(parts[2], out x)) v.Patch = x;
            return v;
        }
        public int CompareTo(SemVer o)
        {
            int c = Major.CompareTo(o.Major); if (c != 0) return c;
            c = Minor.CompareTo(o.Minor); if (c != 0) return c;
            c = Patch.CompareTo(o.Patch); if (c != 0) return c;
            if (Pre == o.Pre) return 0;
            if (Pre.Length == 0) return 1;          // release > prerelease
            if (o.Pre.Length == 0) return -1;
            var a = Pre.Split('.'); var b = o.Pre.Split('.');
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                int ia, ib; bool na = int.TryParse(a[i], out ia), nb = int.TryParse(b[i], out ib);
                int r = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(a[i], b[i]);
                if (r != 0) return r;
            }
            return a.Length.CompareTo(b.Length);
        }
        public override string ToString() { return Major + "." + Minor + "." + Patch + (Pre.Length > 0 ? "-" + Pre : ""); }
    }

    public class ReleaseInfo
    {
        public string Tag = "", Name = "", HtmlUrl = "", AssetUrl = "", AssetName = "", Body = "", Published = "";
        public long AssetSize; public bool Prerelease;
    }

    public enum CheckStatus { UpToDate, UpdateAvailable, NoRelease, Error }

    public class CheckResult
    {
        public CheckStatus Status; public ReleaseInfo Release; public string Message = "";
    }

    /// <summary>User-clicked update check against the GitHub releases API. No automatic network calls anywhere in the app.</summary>
    public static class UpdateChecker
    {
        public static void Prepare()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        public static HttpWebRequest NewRequest(string url, int timeoutMs)
        {
            Prepare();
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "CleanPC-Pro/" + AppInfo.InformationalVersion + " (+https://github.com/" + AppInfo.ProRepo + ")";
            rq.Accept = "application/vnd.github+json";
            rq.Timeout = timeoutMs; rq.ReadWriteTimeout = timeoutMs;
            rq.AllowAutoRedirect = true;
            return rq;
        }

        /// <summary>Fetches repos/{repo}/releases/latest (never returns prereleases). 404 = no stable release published yet.</summary>
        public static CheckResult Check(string repo, string assetName, string currentVersion)
        {
            var res = new CheckResult();
            try
            {
                string url = "https://api.github.com/repos/" + repo + "/releases/latest";
                var rq = NewRequest(url, 15000);
                using (var rp = (HttpWebResponse)rq.GetResponse())
                using (var sr = new StreamReader(rp.GetResponseStream(), Encoding.UTF8))
                {
                    var j = Json.Parse(sr.ReadToEnd()) as Dictionary<string, object>;
                    if (j == null) { res.Status = CheckStatus.Error; res.Message = "Unexpected reply from GitHub."; return res; }
                    var r = new ReleaseInfo
                    {
                        Tag = S(j, "tag_name"), Name = S(j, "name"), HtmlUrl = S(j, "html_url"), Body = S(j, "body"), Published = S(j, "published_at"),
                        Prerelease = j.ContainsKey("prerelease") && j["prerelease"] is bool && (bool)j["prerelease"]
                    };
                    var assets = j.ContainsKey("assets") ? j["assets"] as object[] : null;
                    if (assets != null)
                        foreach (var a in assets.OfType<Dictionary<string, object>>())
                            if (string.Equals(S(a, "name"), assetName, StringComparison.OrdinalIgnoreCase))
                            {
                                r.AssetName = S(a, "name"); r.AssetUrl = S(a, "browser_download_url");
                                try { r.AssetSize = Convert.ToInt64(a["size"]); } catch { }
                            }
                    res.Release = r;
                    if (string.IsNullOrEmpty(r.AssetUrl)) { res.Status = CheckStatus.Error; res.Message = "Latest release " + r.Tag + " has no " + assetName + " asset."; return res; }
                    bool newer = SemVer.Parse(r.Tag).CompareTo(SemVer.Parse(currentVersion)) > 0;
                    res.Status = newer ? CheckStatus.UpdateAvailable : CheckStatus.UpToDate;
                    res.Message = newer ? "New version " + r.Tag + " available." : "You have the latest version (" + r.Tag + ").";
                    return res;
                }
            }
            catch (WebException we)
            {
                var hr = we.Response as HttpWebResponse;
                if (hr != null && hr.StatusCode == HttpStatusCode.NotFound) { res.Status = CheckStatus.NoRelease; res.Message = "No stable release has been published yet."; return res; }
                if (hr != null && (int)hr.StatusCode == 403) { res.Status = CheckStatus.Error; res.Message = "GitHub refused the request (rate limit?). Try again later."; return res; }
                res.Status = CheckStatus.Error;
                res.Message = we.Status == WebExceptionStatus.Timeout ? "Timed out contacting GitHub." : "Could not reach GitHub: " + we.Message;
                return res;
            }
            catch (Exception e) { res.Status = CheckStatus.Error; res.Message = "Update check failed: " + e.Message; return res; }
        }

        static string S(Dictionary<string, object> d, string k) { return d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k]) : ""; }
    }
}
