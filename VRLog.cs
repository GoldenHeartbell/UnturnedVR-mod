using System;
using System.IO;
using System.Runtime.CompilerServices;
using SDG.Unturned;

namespace UnturnedVR
{
    /// <summary>
    /// Single logging entry point for the mod. Writes to Unturned's log as "[UnturnedVR] ClassName: message";
    /// the class name is taken from the calling source file's name (files are named after their class),
    /// so call sites only pass the message.
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
            return Tag + Net.NetLog.ClassName(caller) + ": " + message;
        }
    }
}
