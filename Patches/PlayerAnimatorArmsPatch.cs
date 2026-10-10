using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Hands phase 2: PlayerAnimator.InitializePlayer (internal, runs once per local spawn) creates the "View"
    /// root and resolves the first-person skeleton and ViewmodelCamera. VRArms hooks onto that root afterwards.
    /// </summary>
    [HarmonyPatch(typeof(PlayerAnimator), "InitializePlayer")]
    internal static class PlayerAnimatorArmsPatch
    {
        private static void Postfix(PlayerAnimator __instance)
        {
            if (!VR.VRState.IsActive || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }

            VR.VRArms.Attach(__instance);
        }
    }
}
