namespace UnturnedVR.VR
{
    /// <summary>
    /// Switches for optional diagnostics, all off by default. Flip the default here to enable one at
    /// startup (in the editor F5 also toggles the session monitor at runtime, see VRDebugKeys).
    /// </summary>
    internal static class VRDebugFlags
    {
        /// <summary>Logs XR session/device changes and the periodic "no HMD pose yet" state (VRSessionMonitor).</summary>
        public static bool SessionMonitor = false;

        /// <summary>
        /// Logs every controller button change (named buttons, plus the raw OpenVR button bits), which hand resolved
        /// to which device, and stick activity. Used to confirm the Index button bits; flip to true to debug controllers.
        /// </summary>
        public static bool LogButtons = false;
    }
}
