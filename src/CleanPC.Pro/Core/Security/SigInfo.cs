using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace CleanPC.Pro
{
    public class SigResult { public string Status = "Unknown"; public string Signer = ""; }

    /// <summary>Authenticode status (WinVerifyTrust, no network revocation lookups) + publisher name. Read-only.</summary>
    public static class SigInfo
    {
        static readonly ConcurrentDictionary<string, SigResult> Cache = new ConcurrentDictionary<string, SigResult>(StringComparer.OrdinalIgnoreCase);

        public static SigResult Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return new SigResult();
            return Cache.GetOrAdd(path, p =>
            {
                var r = new SigResult();
                try
                {
                    if (!File.Exists(p)) { r.Status = "FileNotFound"; return r; }
                    r.Status = Native.VerifyFile(p);
                    try
                    {
                        var c = new X509Certificate2(X509Certificate.CreateFromSignedFile(p));
                        r.Signer = c.GetNameInfo(X509NameType.SimpleName, false) ?? "";
                    }
                    catch { }
                }
                catch { }
                return r;
            });
        }

        public static string Sha256(string path, System.Threading.CancellationToken ct)
        {
            try { return File.Exists(path) ? DuplicateFinder.HashFull(path, ct) : null; } catch { return null; }
        }
    }
}
