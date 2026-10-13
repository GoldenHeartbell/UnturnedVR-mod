using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// GraphicsSettings.apply is the single place the game writes its graphics settings (render path, HDR, MSAA, far clip,
    /// layer cull distances) to MainCamera. It runs on every change in the options, on menu load and on player load. The
    /// VR camera is a separate camera that only gets those values by being synced (VRRig.SyncGraphicsSettings), which
    /// otherwise happens a few times a second; this postfix makes the next frame do it right away, so a changed setting
    /// shows up at once and not up to a quarter of a second later. The timed sync stays as the safety net (the clear
    /// flags / background of the menu camera and the highlighting settings are not written by apply).
    /// </summary>
    [HarmonyPatch(typeof(GraphicsSettings), nameof(GraphicsSettings.apply), new[] { typeof(string) })]
    internal static class GraphicsSettingsApplyPatch
    {
        private static void Postfix()
        {
            if (!VR.VRState.IsActive)
            {
                return;
            }

            VR.VRRig.RequestSettingsSync();
        }
    }
}
