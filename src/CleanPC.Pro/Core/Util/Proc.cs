using System;
using System.Diagnostics;
using System.Text;

namespace CleanPC.Pro
{
    public static class Proc
    {
        /// <summary>Runs a console tool hidden and returns its stdout+stderr. Never throws.</summary>
        public static string Run(string file, string args, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Default, StandardErrorEncoding = Encoding.Default
                };
                using (var p = Process.Start(psi))
                {
                    var sb = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } lock (sb) return sb + "\r\n[timed out]"; }
                    p.WaitForExit();
                    exitCode = p.ExitCode;
                    lock (sb) return sb.ToString();
                }
            }
            catch (Exception e) { return "[failed to start " + file + ": " + e.Message + "]"; }
        }

        public static string Run(string file, string args, int timeoutMs = 60000)
        {
            int c; return Run(file, args, timeoutMs, out c);
        }
    }
}
