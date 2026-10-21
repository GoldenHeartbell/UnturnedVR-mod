using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Sync of VR hands, milestone 2: PlayerAnimator.InitializePlayer runs once per spawned player and resolves the third-person
    /// skeleton. For every player that is not the local one, VRRemoteArms hooks onto that rig, so a VR viewer sees the arms of another
    /// VR player follow that player's controllers (relayed by the server). Only a client in VR does anything with it.
    /// </summary>
    [HarmonyPatch(typeof(PlayerAnimator), "InitializePlayer")]
    internal static class RemotePlayerArmsPatch
    {
        private static void Postfix(PlayerAnimator __instance)
        {
            if (!VR.VRState.IsActive || __instance.channel == null || __instance.channel.IsLocalPlayer || Provider.isServer)
            {
                return;
            }

            VR.VRRemoteArms.Attach(__instance);
        }
    }
}
