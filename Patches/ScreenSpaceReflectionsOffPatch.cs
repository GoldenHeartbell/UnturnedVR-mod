using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine.Rendering.PostProcessing;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Screen-space reflections are always off in VR: even with the stereo matrices (ScreenSpaceReflectionsStereoPatch)
    /// the effect still breaks the headset view. UnturnedPostProcess.syncScreenSpaceReflections (private, called from
    /// applyUserSettings on menu load, player load and every graphics option change) turns the effect on from the
    /// Reflections option; this postfix turns it off again right after. The option itself is not changed, so the player's
    /// saved setting still works when the game is played without VR.
    /// </summary>
    [HarmonyPatch]
    internal static class ScreenSpaceReflectionsOffPatch
    {
        private static bool logged;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(UnturnedPostProcess), "syncScreenSpaceReflections");
        }

        private static bool Prepare()
        {
            if (TargetMethod() != null)
            {
                return true;
            }

            VRLog.Warn("screen-space reflections: UnturnedPostProcess.syncScreenSpaceReflections not found, the effect is not turned off in VR.");
            return false;
        }

        private static void Postfix(UnturnedPostProcess __instance)
        {
            if (!VR.VRState.IsActive)
            {
                return;
            }

            ScreenSpaceReflections reflections = Traverse.Create(__instance).Field("baseProfile")
                .Field("screenSpaceReflections").GetValue<ScreenSpaceReflections>();
            if (reflections == null)
            {
                VRLog.Warn("screen-space reflections: base profile not found, the effect may still be on in VR.");
                return;
            }

            reflections.active = false;
            if (!logged)
            {
                logged = true;
                VRLog.Info("screen-space reflections: turned off in VR (game option is " + GraphicsSettings.reflectionQuality + ").");
            }
        }
    }
}
