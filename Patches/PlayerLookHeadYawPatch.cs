using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Makes the player's body follow the headset's yaw. The mouse adds to PlayerLook's private _yaw
    /// in Update, which the same method then writes to the body transform. A prefix adds the
    /// headset's turn since last frame to _yaw before that write; VRRig counter-rotates the rig by
    /// the same amount so the view itself still equals the physical head yaw (no double rotation).
    /// Harmony exposes the private field _yaw as "____yaw" (three underscores + field name).
    /// </summary>
    [HarmonyPatch(typeof(PlayerLook), "Update")]
    internal static class PlayerLookHeadYawPatch
    {
        private const float SmoothTurnSpeed = 120f; // degrees per second at full deflection

        private static void Prefix(PlayerLook __instance, ref float ____yaw)
        {
            if (!VR.VRState.IsActive || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }

            // Only free-walking first person. In vehicles/seats yaw has other meanings (turret limits,
            // seat-relative camera), so there the head just looks around without turning the body.
            EPlayerStance stance = __instance.player.stance.stance;
            bool bodyFollowsHead = __instance.perspective == EPlayerPerspective.FIRST
                && stance != EPlayerStance.DRIVING
                && stance != EPlayerStance.SITTING
                && !__instance.player.life.isDead;

            ____yaw += VR.VRRig.ConsumeHeadYawDelta(bodyFollowsHead);

            // M5: left stick X = smooth turn. Rotates the body (and the rig parented to it) without
            // touching the head-delta bookkeeping above. Up/down is deliberately ignored.
            if (bodyFollowsHead && !PlayerUI.window.showCursor && !LoadingUI.isBlocked)
            {
                VR.VRControllerInput.Refresh();
                ____yaw += VR.VRControllerInput.LeftStick.x * SmoothTurnSpeed * Time.deltaTime;
            }
        }
    }
}
