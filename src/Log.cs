using System;
using System.IO;

namespace DearEstherVR
{
    /// <summary>
    /// DearEstherVR.log in the mod folder (beside DearEstherVR.dll). Replaced at every start. The first line names
    /// the version, which is what to check when a test PC seems to run an old build.
    /// </summary>
    internal static class Log
    {
        static readonly object Gate = new object();
        static string _path;

        /// <summary>The mod folder: game\DearEstherVR.</summary>
        public static readonly string Dir = Path.GetDirectoryName(typeof(Log).Assembly.Location);

        /// <summary>Developer test channel and mock headset files.</summary>
        public static readonly string LabDir = Path.Combine(Path.Combine(Path.Combine(Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE"), "AppData"), "LocalLow"), "DearEstherVR"), "lab");

        static string FilePath
        {
            get
            {
                if (_path == null)
                {
                    _path = Path.Combine(Dir, "DearEstherVR.log");
                    try { File.WriteAllText(_path, "Dear Esther VR " + Mod.Version + " log " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine); } catch { }
                }
                return _path;
            }
        }

        public static void Early(string msg) => Write("[Info] " + msg);
        public static void Info(string msg) => Write("[Info] " + msg);
        public static void Warn(string msg) => Write("[Warn] " + msg);
        public static void Error(string msg) => Write("[Error] " + msg);
        public static void Error(Exception e) => Write("[Error] " + e);

        static void Write(string msg)
        {
            lock (Gate)
            {
                try { File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine); }
                catch { }
            }
        }
    }
}
