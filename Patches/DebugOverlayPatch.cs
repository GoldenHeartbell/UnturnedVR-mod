using System.Reflection;
using System.Text;
using HarmonyLib;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Takes the game's debug overlay (top left: FPS, ping, version, the admin keys F1-F7 and the loading words) off the
    /// HUD in VR. It is drawn when the player has the Debug option on; GlazierBase.UpdateDebugString (internal class,
    /// protected method, called each frame by every Glazier before it sets the text) fills debugBuilder, and the postfix
    /// empties it again, so the label draws nothing. The option itself and the admin keys are unchanged.
    /// </summary>
    [HarmonyPatch]
    internal static class DebugOverlayPatch
    {
        private static bool logged;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("SDG.Unturned.GlazierBase"), "UpdateDebugString");
        }

        private static bool Prepare()
        {
            if (AccessTools.TypeByName("SDG.Unturned.GlazierBase") != null && TargetMethod() != null)
            {
                return true;
            }

            VRLog.Warn("debug overlay: GlazierBase.UpdateDebugString not found, the debug overlay stays on the HUD.");
            return false;
        }

        private static void Postfix(StringBuilder ___debugBuilder)
        {
            if (!VR.VRState.IsActive || ___debugBuilder == null || ___debugBuilder.Length == 0)
            {
                return;
            }

            ___debugBuilder.Length = 0;
            if (!logged)
            {
                logged = true;
                VRLog.Info("debug overlay: hidden on the HUD (the Debug option is on).");
            }
        }
    }
}
