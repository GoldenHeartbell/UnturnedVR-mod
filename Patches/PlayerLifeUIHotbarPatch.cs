using System;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnturnedVR.VR;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Takes the whole hotbar (weapon slots 1 and 2 and the hotkey items) off the HUD. It is not shown anywhere else in
    /// VR: the items are on the body holsters (VRHolsters) and the game's own keys / inventory still work. The game's
    /// PlayerLifeUI.updateHotbar sets the container visible again on every call, so the postfix hides it again after each
    /// one (the hotbar logic itself keeps running, only the widget is not drawn).
    /// </summary>
    internal static class PlayerLifeUIHotbarPatch
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo containerField = typeof(PlayerLifeUI).GetField("hotbarContainer", Any);
        private static bool logged;
        private static bool failed;

        [HarmonyPatch(typeof(PlayerLifeUI), nameof(PlayerLifeUI.updateHotbar))]
        internal static class UpdateHotbar
        {
            private static void Postfix()
            {
                if (failed)
                {
                    return;
                }

                try
                {
                    ISleekElement container = containerField != null ? containerField.GetValue(null) as ISleekElement : null;
                    if (container == null)
                    {
                        failed = true;
                        VRLog.Warn("hotbar: PlayerLifeUI.hotbarContainer not found, the hotbar stays on the HUD.");
                        return;
                    }

                    if (container.IsVisible)
                    {
                        container.IsVisible = false;
                        if (!logged)
                        {
                            logged = true;
                            VRLog.Info("hotbar: hidden on the HUD (the body holsters are the way to draw items).");
                        }
                    }
                }
                catch (Exception e)
                {
                    failed = true;
                    VRLog.Warn("hotbar: could not hide the hotbar: " + e.Message);
                }
            }
        }
    }
}
