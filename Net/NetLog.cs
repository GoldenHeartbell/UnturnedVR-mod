using System;
using System.Runtime.CompilerServices;
using SDG.Unturned;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Logging for the shared (client and server) assembly: "[UnturnedVR] ClassName: message" in Unturned's log, the class name
    /// taken from the calling source file like VRLog does.
    /// </summary>
    internal static class NetLog
    {
        private const string Tag = "[UnturnedVR] ";

        public static void Info(string message, [CallerFilePath] string caller = "")
        {
            UnturnedLog.info(Tag + ClassName(caller) + ": " + message);
        }

        public static void Warn(string message, [CallerFilePath] string caller = "")
        {
            UnturnedLog.warn(Tag + ClassName(caller) + ": " + message);
        }

        public static void Error(string message, [CallerFilePath] string caller = "")
        {
            UnturnedLog.error(Tag + ClassName(caller) + ": " + message);
        }

        /// <summary>
        /// File name without extension of a [CallerFilePath]. That path is the one on the machine that built the mod (Windows),
        /// so it is split on both separators: System.IO.Path only knows the running machine's own, and on a Linux server it
        /// would print the whole build path.
        /// </summary>
        internal static string ClassName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            string name = slash >= 0 ? path.Substring(slash + 1) : path;
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }
    }
}
