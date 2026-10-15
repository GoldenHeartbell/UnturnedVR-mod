using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Makes the player's body follow the headset's yaw and the player's look pitch follow the headset's pitch.
    /// The mouse adds to PlayerLook's private _yaw in Update, which the same method then writes to the body
    /// transform. A prefix adds the headset's turn since last frame to _yaw before that write; VRRig
    /// counter-rotates the rig by the same amount so the view itself still equals the physical head yaw (no
    /// double rotation). _pitch (0 = up, 90 = level, 180 = down) is set to the headset's pitch, so look.aim,
    /// MainCamera (and the scope camera under it) and the pitch sent to the server and other players follow the
    /// head. The VR rig is a sibling of MainCamera, so this does not rotate the view a second time.
    /// Harmony exposes the private fields as "____yaw" / "____pitch" (three underscores + field name).
    /// Hallucination (some berries) makes the game flip the look input (PlayerLook.onVisionUpdated sets
    /// yawInputMultiplier to -1 with 25% chance when it starts, the same for pitch). The mouse path multiplies by it, the head does not, so
    /// the same multiplier is applied to the head turn and the stick turn here: the body (and so the view and the aim)
    /// turns the opposite way to the head. The rig bookkeeping stays as is (view relative to the body is unchanged), only
    /// the body yaw added is mirrored. The pitch multiplier cannot be applied to a head pitch that is absolute, so it is
    /// handed to VRRig.SetPitchFlip, which (when the VR setting is on) mirrors the VR camera's pitch (VRPitchFlip); the
    /// game's pitch then follows that mirrored view like it follows any head pitch.
    /// </summary>
    [HarmonyPatch(typeof(PlayerLook), "Update")]
    internal static class PlayerLookHeadYawPatch
    {
        private const float SmoothTurnSpeed = 120f; // degrees per second at full deflection

        private static bool wasSeated;
        private static bool wasBodyFollowsHead;

        private static void Prefix(PlayerLook __instance, ref float ____yaw, ref float ____pitch, float ___yawInputMultiplier, float ___pitchInputMultiplier)
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

            bool seated = stance == EPlayerStance.DRIVING || stance == EPlayerStance.SITTING;

            // -1 while a hallucination flips the look input (1 otherwise): head left -> view right.
            float yawMultiplier = ___yawInputMultiplier < 0f ? -1f : 1f;

            // Same for pitch, but through the camera (VRPitchFlip), and only with the VR setting on.
            VR.VRRig.SetPitchFlip(___pitchInputMultiplier < 0f, bodyFollowsHead);

            ____yaw += VR.VRRig.ConsumeHeadYawDelta(bodyFollowsHead) * yawMultiplier;

            // Seats: the body does not follow the head there, so the view and the body drift apart while you look
            // around. Sitting down puts the view straight ahead in the seat; getting out turns the body to where you
            // look (the game puts it at the exit angle), so the stick moves you where you face.
            if (seated && !wasSeated)
            {
                VR.VRRig.AlignViewToBody();
            }
            else if (bodyFollowsHead && !wasBodyFollowsHead)
            {
                ____yaw += VR.VRRig.AlignBodyToView();
            }
            wasSeated = seated;
            wasBodyFollowsHead = bodyFollowsHead;

            // Absolute, so it cannot drift; PlayerLook.clampPitch still applies the stance limits afterwards.
            if (bodyFollowsHead && !__instance.IsControllingFreecam && VR.VRRig.TryGetHeadPitch(out float headPitch))
            {
                ____pitch = headPitch;
            }

            // M5: right stick X = smooth turn. Rotates the body (and the rig parented to it) without
            // touching the head-delta bookkeeping above. Up/down is deliberately ignored. Not while the game itself ignores
            // look input (turning a barricade before placing it: the same stick rotates the item, see BuildingRotatePatch).
            if (bodyFollowsHead && !PlayerUI.window.showCursor && !LoadingUI.isBlocked && !__instance.isIgnoringInput)
            {
                VR.VRControllerInput.Refresh();
                ____yaw += VR.VRControllerInput.RightStick.x * SmoothTurnSpeed * Time.deltaTime * yawMultiplier;
            }
        }
    }
}
