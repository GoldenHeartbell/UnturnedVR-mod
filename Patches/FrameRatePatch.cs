using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// M3: removes the frame rate cap while VR is active.
    /// that GraphicsSettings.ApplyVSyncAndTargetFrameRate is the single call site setting
    /// Application.targetFrameRate (unlike FOV, which has no single choke point). A postfix that
    /// overrides its result is simpler than VHVR's transpiler and has the same effect: the XR
    /// compositor (not Unity's own vsync/target-frame-rate logic) should drive frame timing.
    /// </summary>
    [HarmonyPatch(typeof(GraphicsSettings), "ApplyVSyncAndTargetFrameRate")]
    internal static class FrameRatePatch
    {
        private static void Postfix()
        {
            if (!VR.VRState.IsActive)
            {
                return;
            }

            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }
    }
}
