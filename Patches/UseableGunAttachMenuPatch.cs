using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Attachment menu in VR: while the local player's gun is in attach mode, UseableGun.tick places the hook buttons
    /// where the gun's hooks appear on the first-person viewmodel camera. That camera does not see the controller-held
    /// gun, so the spots are meaningless. After tick the buttons are moved to the fixed compact layout (VRAttachMenu),
    /// so the whole menu sits in one known canvas rectangle.
    /// </summary>
    [HarmonyPatch(typeof(UseableGun), nameof(UseableGun.tick))]
    internal static class UseableGunAttachMenuPatch
    {
        private static void Postfix(UseableGun __instance)
        {
            if (!VR.VRState.IsActive || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }

            if (VR.VRAttachMenu.IsAttaching(__instance))
            {
                VR.VRAttachMenu.ApplyLayout(__instance);
            }
        }
    }
}
