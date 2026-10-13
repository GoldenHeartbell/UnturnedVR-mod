using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Turning a barricade before it is placed. The game does it with the mouse: UseableBarricade.startSecondary (the
    /// secondary key held) sets inputWantsToRotate and player.look.isIgnoringInput, and processRotationInput (every tick
    /// while held) adds Input.GetAxis("mouse_x") to the yaw, and on freeform items "mouse_y" to the pitch and "mouse_z"
    /// (the wheel) to the roll; the snap key rounds the result to 15 degrees. In VR the mouse axes are always 0 (extern
    /// UnityEngine.Input, the mod does not serve axes), so holding the secondary trigger changed nothing.
    /// This prefix adds the same quantities from the thumbsticks to the game's own input_x / input_y / input_z (degrees)
    /// right before the game turns them into rotate_x / y / z, so the snap key, the preview and the placement packet
    /// (angle + rotate) all stay the game's code. Right stick: X = yaw, Y = pitch (freeform only). Left stick X = roll
    /// (freeform only; the stick then does not walk, see BuildingRotate.OwnsLeftStick). The snap key is on the free grip
    /// (VRKeyMap). Structures and the housing planner step on every secondary press (90 / 180 / 30 degrees, the game's
    /// shift reverses) and need nothing: the trigger press is already the game's key.
    /// </summary>
    internal static class BuildingRotate
    {
        // Full stick deflection at the slowest setting; the VR page scales it by 1x to 3x (VRSettings.BuildingRotateSpeedPercent).
        // Finer than the 120 deg/s body turn.
        private const float DegreesPerSecond = 90f;
        private static bool logged;

        private static bool Freeform(UseableBarricade barricade)
        {
            ItemBarricadeAsset asset = barricade.equippedBarricadeAsset;
            return asset != null && (asset.build == EBuild.FREEFORM || asset.build == EBuild.SENTRY_FREEFORM);
        }

        /// <summary>The left stick rolls the freeform barricade that is being turned, so it must not also walk.</summary>
        public static bool OwnsLeftStick(Player player)
        {
            if (player == null || player.look == null || !player.look.isIgnoringInput || player.equipment == null)
            {
                return false;
            }
            UseableBarricade barricade = player.equipment.useable as UseableBarricade;
            return barricade != null && Freeform(barricade);
        }

        public static void AddInput(UseableBarricade barricade, ref float inputX, ref float inputY, ref float inputZ)
        {
            if (!VR.VRState.IsActive || barricade.channel == null || !barricade.channel.IsLocalPlayer)
            {
                return;
            }

            VR.VRControllerInput.Refresh();
            Vector2 right = VR.VRControllerInput.RightStick;
            Vector2 left = VR.VRControllerInput.LeftStick;
            float step = DegreesPerSecond * (VR.VRSettings.BuildingRotateSpeedPercent / 100f) * Time.deltaTime;

            inputY += right.x * step;
            if (Freeform(barricade))
            {
                // Same sign rule as the game: up on the mouse lowers input_x unless the invert setting is on.
                inputX += (ControlsSettings.invert ? right.y : -right.y) * step;
                inputZ += left.x * step;
            }

            if (!logged && (right != Vector2.zero || left != Vector2.zero))
            {
                logged = true;
                VRLog.Info("building: barricade rotation is being driven from the thumbsticks.");
            }
        }
    }

    // UseableBarricade.processRotationInput() (private) runs every tick while the secondary trigger is held.
    [HarmonyPatch(typeof(UseableBarricade), "processRotationInput")]
    internal static class BuildingRotatePatch
    {
        private static void Prefix(UseableBarricade __instance, ref float ___input_x, ref float ___input_y, ref float ___input_z)
        {
            BuildingRotate.AddInput(__instance, ref ___input_x, ref ___input_y, ref ___input_z);
        }
    }
}
