using SDG.Unturned;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Set once <see cref="VRManager"/> has successfully started the XR subsystems (M2). Other
    /// patches/components gate on this so the module behaves like vanilla when no headset/XR
    /// loader is available (e.g. SteamVR not running).
    /// </summary>
    internal static class VRState
    {
        /// <summary>
        /// Launch option for testing multiplayer with a second, flat-screen client: XR is never started and no VR patches are
        /// applied, only the networking (opt-in to the relay) and the remote arms, so this game shows the VR player's hands.
        /// </summary>
        public static readonly CommandLineFlag DisableVR = new CommandLineFlag(false, "-disableVR");

        public static bool IsActive { get; set; }

        /// <summary>Other VR players' arms follow their relayed poses: in VR, or a flat client started with <see cref="DisableVR"/>.</summary>
        public static bool ShowsRemoteArms => IsActive || DisableVR;
    }
}
