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
    }
}
