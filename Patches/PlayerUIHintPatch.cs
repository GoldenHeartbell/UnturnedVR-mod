using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// M4: the "pick up X" label (and every other targeted hint, e.g. climb). In VR it is not shown on the far
    /// HUD panel but on a small panel right in front of the eyes (VRGui hint panel). The game positions the box
    /// over the target through the disabled MainCamera, which makes no sense here, so a postfix pins it to the
    /// game's own fixed spot (bottom centre, above the second message box), the same for every hint. VRGui
    /// then reads the rect of all visible hint/message boxes (this one, messageBox2 and the enemy card, which
    /// already sit at fixed spots), crops it out of the UI texture for the near panel and cuts a hole at the
    /// same spot in the HUD panel, so they show only once. hint runs every frame while the target is focused.
    /// </summary>
    [HarmonyPatch(typeof(PlayerUI), nameof(PlayerUI.hint), new[] { typeof(Transform), typeof(EPlayerMessage), typeof(string), typeof(Color), typeof(object[]) })]
    internal static class PlayerUIHintPatch
    {
        // Same numbers as the game's own non-targeted layout in PlayerUI.hint.
        private const float BottomMargin = 80f;
        private const float BoxGap = 10f;

        private static void Postfix()
        {
            if (!VR.VRState.IsActive || PlayerUI.messageBox == null)
            {
                return;
            }

            ISleekBox box = PlayerUI.messageBox;
            float width = box.SizeOffset_X;
            float height = box.SizeOffset_Y;
            float y = -BottomMargin - height;
            float extra = 0f;
            if (PlayerUI.messageBox2 != null && PlayerUI.messageBox2.IsVisible)
            {
                extra = BoxGap + PlayerUI.messageBox2.SizeOffset_Y;
                y -= extra;
            }

            box.PositionScale_X = 0.5f;
            box.PositionScale_Y = 1f;
            box.PositionOffset_X = -width / 2f;
            box.PositionOffset_Y = y;
        }
    }
}
