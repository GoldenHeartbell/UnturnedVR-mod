using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// M1 proof that Harmony patching works: a harmless postfix on a method confirmed
    /// in M0 recon (docs/unturned-internals.md) to run exactly once, when the main camera spawns.
    /// </summary>
    [HarmonyPatch(typeof(MainCamera), "Awake")]
    internal static class HelloWorldPatch
    {
        private static void Postfix()
        {
            UnturnedLog.info("[UnturnedVR] Harmony patch on MainCamera.Awake fired - patching works.");
        }
    }
}
