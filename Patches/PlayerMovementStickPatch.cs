using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// M5: right thumbstick walks. The game's movement input is digital (-1/0/1 per axis, set from
    /// key binds in PlayerMovement.Update and published as _horizontal/_vertical = value + 1), so
    /// after Update we override those with the stick, thresholded per axis (8-way). When the stick
    /// is idle the keyboard result is left alone. Forward is the body's facing, which follows the head.
    /// </summary>
    [HarmonyPatch(typeof(PlayerMovement), "Update")]
    internal static class PlayerMovementStickPatch
    {
        private const float AxisThreshold = 0.5f;

        private static void Postfix(PlayerMovement __instance, ref int ___input_x, ref int ___input_y,
            ref byte ____horizontal, ref byte ____vertical)
        {
            if (!VR.VRState.IsActive || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }

            if (PlayerUI.window.showCursor || LoadingUI.isBlocked || __instance.player.look.IsControllingFreecam)
            {
                return;
            }

            // The left stick rolls a freeform barricade while it is being turned (BuildingRotatePatch).
            if (BuildingRotate.OwnsLeftStick(__instance.player))
            {
                return;
            }

            VR.VRControllerInput.Refresh();
            Vector2 stick = VR.VRControllerInput.LeftStick;
            if (stick == Vector2.zero)
            {
                return;
            }

            ___input_x = stick.x > AxisThreshold ? 1 : stick.x < -AxisThreshold ? -1 : 0;
            ___input_y = stick.y > AxisThreshold ? 1 : stick.y < -AxisThreshold ? -1 : 0;
            ____horizontal = (byte)(___input_x + 1);
            ____vertical = (byte)(___input_y + 1);
        }
    }
}
