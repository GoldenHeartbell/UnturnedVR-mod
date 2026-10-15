using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// The red flash when you are hit, the flashbang flash and the blindfold are one full-window image on the 2D HUD canvas
    /// (PlayerUI.colorOverlayImage, coloured every frame in UpdateOverlayColor). In VR that canvas is only a panel in front
    /// of you, so the flash covered the panel and not your view. The postfix takes the colour the game just computed and
    /// hands it to VRScreenEffects, which draws it over the whole headset view, then blanks the canvas image so it is not
    /// also drawn on the panel. The game's own pain / stun timing and colours are untouched.
    /// </summary>
    [HarmonyPatch(typeof(PlayerUI), "UpdateOverlayColor")]
    internal static class PlayerUIOverlayColorPatch
    {
        private static void Postfix(ISleekImage ___colorOverlayImage)
        {
            if (!VR.VRState.IsActive || ___colorOverlayImage == null)
            {
                return;
            }

            VR.VRScreenEffects.SetTint(___colorOverlayImage.TintColor);
            ___colorOverlayImage.TintColor = new Color(0f, 0f, 0f, 0f);
        }
    }
}
