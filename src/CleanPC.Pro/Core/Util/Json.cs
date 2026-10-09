using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CleanPC.Pro
{
    /// <summary>Tiny JSON helper (JavaScriptSerializer ships with .NET Framework - no NuGet needed).</summary>
    public static class Json
    {
        static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 }; }
        public static string To(object o) { return Ser().Serialize(o); }
        public static T From<T>(string s) { return Ser().Deserialize<T>(s); }
        public static object Parse(string s) { return Ser().DeserializeObject(s); }

        public static T Load<T>(string path) where T : class, new()
        {
            try { if (File.Exists(path)) return From<T>(File.ReadAllText(path, Encoding.UTF8)) ?? new T(); }
            catch (Exception e) { Log.Error("load " + path, e); }
            return new T();
        }

        public static bool Save(string path, object o)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, To(o), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Log.Error("save " + path, e); return false; }
        }
    }
}
