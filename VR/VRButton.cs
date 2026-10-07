using System;

namespace UnturnedVR.VR
{
    /// <summary>Controller buttons the mod cares about, by role (same names on Touch/Index/WMR; Index: A and B are the face buttons).</summary>
    [Flags]
    internal enum VRButton
    {
        None = 0,
        Trigger = 1,
        Grip = 2,
        A = 4,
        B = 8,
        StickClick = 16,
    }
}
