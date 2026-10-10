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

        /// <summary>
        /// Draws a small axis cross on each controller (VRHands): cyan = left, orange = right. Flip to true to
        /// check controller tracking; off until real hands exist (phase 2).
        /// </summary>
        public static bool HandMarkers = false;

        /// <summary>Logs the pointer state (hit, pixel, trigger) about once a second while it is active.</summary>
        public static bool LogPointer = false;

        /// <summary>VRGui pipeline report every 10 s (eye, panel, overlay camera, texture coverage via ReadPixels).</summary>
        public static bool LogGui = false;

        /// <summary>
        /// Frame cost report every 5 s (VRPerf): fps, longest frame, mod script ms per frame, GC, and what each equipped
        /// item adds to the scene. On while the item pickup slowdown is investigated.
        /// </summary>
        public static bool LogPerf = false;

        // Runtime bisect switches for the item slowdown (RightCtrl + keypad 1-4, see VRPerf). All off = normal behaviour.
        public static bool PerfNoViewmodel;
        public static bool PerfNoItemPose;
        public static bool PerfHideItem;
        public static bool PerfNoEffects;
    }
}
