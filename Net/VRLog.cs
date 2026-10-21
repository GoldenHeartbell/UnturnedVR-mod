using System;
using System.Runtime.CompilerServices;
using SDG.Unturned;

namespace UnturnedVR
{
    /// <summary>
    /// Single logging entry point for the mod, client and server. Writes to Unturned's log as "[UnturnedVR] ClassName: message";
    /// the class name is taken from the calling source file's name (files are named after their class), so call sites only
    /// pass the message. Lives in the shared assembly because a dedicated server only loads UnturnedVR.Net.dll; the client
    /// assembly sees it through InternalsVisibleTo.
    /// </summary>
    internal static class VRLog
    {
        private const string Tag = "[UnturnedVR] ";

        public static void Info(string message, [CallerFilePath] string caller = "")
        {
            UnturnedLog.info(Format(message, caller));
        }

        public static void Warn(string message, [CallerFilePath] string caller = "")
        {
            UnturnedLog.warn(Format(message, caller));
        }

        public static void Error(string message, [CallerFilePath] string caller = "")
        {
            UnturnedLog.error(Format(message, caller));
        }

        public static void Exception(Exception exception, [CallerFilePath] string caller = "")
        {
            UnturnedLog.exception(exception, Format("exception:", caller));
        }

        private static string Format(string message, string caller)
        {
            return Tag + ClassName(caller) + ": " + message;
        }

        /// <summary>
        /// File name without extension of a [CallerFilePath]. That path is the one on the machine that built the mod (Windows),
        /// so it is split on both separators: System.IO.Path only knows the running machine's own, and on a Linux server it
        /// would print the whole build path.
        /// </summary>
        private static string ClassName(string path)
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
