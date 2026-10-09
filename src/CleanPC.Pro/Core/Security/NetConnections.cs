using System;
using System.Collections.Generic;

namespace CleanPC.Pro
{
    public class NetConn { public int Pid; public string Text; }

    /// <summary>Parses `netstat -ano` (read-only) so flagged processes can show their TCP/UDP endpoints.</summary>
    public static class NetConnections
    {
        public static List<NetConn> Read()
        {
            var list = new List<NetConn>();
            try
            {
                string o = Proc.Run("netstat.exe", "-ano", 30000);
                foreach (var raw in o.Split('\n'))
                {
                    var t = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    int pid;
                    if (t.Length >= 5 && t[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) && int.TryParse(t[t.Length - 1], out pid))
                        list.Add(new NetConn { Pid = pid, Text = "TCP " + t[1] + " -> " + t[2] + "  " + t[3] });
                    else if (t.Length >= 4 && t[0].Equals("UDP", StringComparison.OrdinalIgnoreCase) && int.TryParse(t[t.Length - 1], out pid))
                        list.Add(new NetConn { Pid = pid, Text = "UDP " + t[1] + " (listening)" });
                }
            }
            catch (Exception e) { Log.Error("netstat", e); }
            return list;
        }
    }
}
