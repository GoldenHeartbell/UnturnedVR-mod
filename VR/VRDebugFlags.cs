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

        /// <summary>Draws the body holster zones (VRHolsters) as rings: yellow = has an item, green = held now, grey = empty.</summary>
        public static bool HolsterMarkers = false;

        /// <summary>Logs every grip press near the body with the zone it hit, and which slot is held (VRHolsters).</summary>
        public static bool LogHolsters = false;

        /// <summary>Logs when aiming down the sights switches on and off, with the eye-to-sight distances (VRAds).</summary>
        public static bool LogAds = false;

        /// <summary>
        /// One line per shot: sight height over the bore, distance of the reticle point, bullet drop there, and how far
        /// below the reticle the bullet ends up (VRShotProbe). Measured once; the result is in docs/progress.md.
        /// </summary>
        public static bool LogShotOffset = false;

        /// <summary>VRGui pipeline report every 10 s (eye, panel, overlay camera, texture coverage via ReadPixels).</summary>
        public static bool LogGui = false;

        /// <summary>
        /// Frame cost report every 5 s (VRPerf): fps, longest frame, mod script ms per frame, GC, and what each equipped
        /// item adds to the scene; plus a trend line every 30 s (memory, object counts, players, XR render size). Off by
        /// default; flip to true to investigate a frame time problem (the instrumentation costs nothing while off).
        /// </summary>
        public static bool LogPerf = false;

        /// <summary>
        /// Logs what the SteamVR keyboard reports per keystroke (read text, events, the text written into the field), at
        /// most 120 lines per run, plus grip presses near the keyboard (VRKeyboard). Off by default; flip to debug typing.
        /// </summary>
        public static bool LogKeyboard = false;

        // Runtime bisect switches for the item slowdown (RightCtrl + keypad 1-4, see VRPerf). All off = normal behaviour.
        public static bool PerfNoViewmodel;
        public static bool PerfNoItemPose;
        public static bool PerfHideItem;
        public static bool PerfNoEffects;
    }
}
