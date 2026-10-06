using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// M3: PlayerLook.InitializePlayer runs once per local player spawn (confirmed in
    /// docs/unturned-internals.md - it sets up characterCamera/scopeCamera/MainCamera's parent).
    /// It's an internal method (verify: HarmonyLib resolves it by name via reflection regardless
    /// of accessibility), so patch it by string rather than a typeof(...) method group.
    /// </summary>
    [HarmonyPatch(typeof(PlayerLook), "InitializePlayer")]
    internal static class PlayerLookInitializePatch
    {
        private static void Postfix(PlayerLook __instance)
        {
            if (!VR.VRState.IsActive)
            {
                return;
            }

            if (__instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }

            VR.VRRig.AttachToLocalPlayer(__instance.player);
        }
    }
}
