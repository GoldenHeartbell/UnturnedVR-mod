using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Rumble on every shot of the local player's gun. UseableGun.shoot (private) is the per-shot effect (muzzle
    /// flash, sound): it runs only for a shot that really happened, unlike fire() which throws without ammo.
    /// </summary>
    [HarmonyPatch(typeof(UseableGun), "shoot")]
    internal static class UseableGunHapticsPatch
    {
        private static void Postfix(UseableGun __instance)
        {
            if (!VR.VRState.IsActive || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }
            VR.VRHaptics.GunShot(__instance.equippedGunAsset);
        }
    }
}
