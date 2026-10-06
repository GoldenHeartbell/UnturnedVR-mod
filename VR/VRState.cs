namespace UnturnedVR.VR
{
    /// <summary>
    /// Set once <see cref="VRManager"/> has successfully started the XR subsystems (M2). Other
    /// patches/components gate on this so the module behaves like vanilla when no headset/XR
    /// loader is available (e.g. SteamVR not running).
    /// </summary>
    internal static class VRState
    {
        public static bool IsActive { get; set; }
    }
}
