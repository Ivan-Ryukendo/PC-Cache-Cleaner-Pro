using System;
using System.Management;

namespace CleanPC.Pro
{
    public static class RestorePoint
    {
        /// <summary>Creates a System Restore point. Returns null on success, else a reason (failure never blocks cleaning).</summary>
        public static string Create(string description)
        {
            try
            {
                var mc = new ManagementClass(@"\\.\root\default:SystemRestore");
                var p = mc.GetMethodParameters("CreateRestorePoint");
                p["Description"] = description; p["RestorePointType"] = 12; p["EventType"] = 100;
                var r = mc.InvokeMethod("CreateRestorePoint", p, null);
                uint code = Convert.ToUInt32(r["ReturnValue"]);
                if (code == 0) { Log.Write("Restore point created: " + description); return null; }
                return "System Restore returned " + code + " (it may be turned off, or a point was already made in the last 24 h)";
            }
            catch (Exception e) { return e.Message; }
        }
    }
}
