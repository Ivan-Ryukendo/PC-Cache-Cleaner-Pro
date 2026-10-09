using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;

namespace CleanPC.Pro
{
    /// <summary>Static facts about the running program: version, paths, elevation.</summary>
    public static class AppInfo
    {
        public const string Name = "PC Cache Cleaner Pro";
        public const string Version = "1.3.0";
        public const string ProRepo = "Ivan-Ryukendo/PC-Cache-Cleaner-Pro";
        public const string StandardRepo = "Ivan-Ryukendo/PC-Cache-Cleaner";
        public const string ProAsset = "CleanPC-Pro.exe";
        public const string StandardAsset = "CleanPC.exe";

        public static string InformationalVersion
        {
            get
            {
                try
                {
                    var a = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                        Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));
                    if (a != null && !string.IsNullOrEmpty(a.InformationalVersion)) return a.InformationalVersion;
                }
                catch { }
                return Version;
            }
        }

        public static string ExePath
        {
            get
            {
                try { return Process.GetCurrentProcess().MainModule.FileName; }
                catch { return Assembly.GetExecutingAssembly().Location; }
            }
        }

        public static string ExeName() { try { return Path.GetFileName(ExePath); } catch { return "CleanPC-Pro.exe"; } }

        public static string ExeDir
        {
            get { try { return Path.GetDirectoryName(ExePath); } catch { return AppDomain.CurrentDomain.BaseDirectory; } }
        }

        public static string DataDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CleanPC-Pro");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        static bool? _admin;
        public static bool IsAdmin
        {
            get
            {
                if (_admin == null)
                {
                    try { _admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                    catch { _admin = false; }
                }
                return _admin.Value;
            }
        }

        public static string SystemDrive
        {
            get { return (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant(); }
        }
    }
}
