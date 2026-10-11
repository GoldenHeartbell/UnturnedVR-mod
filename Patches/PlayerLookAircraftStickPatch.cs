using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Aircraft pitch and roll on the right thumbstick (helicopter, plane, blimp: the only engines that read look_x /
    /// look_y, see InteractableVehicle.simulate). The left stick already flies them through PlayerMovementStickPatch
    /// (input_x = yaw, input_y = throttle). Pitch and roll are the game's look_y and look_x, which turn into torque
    /// (clamped to airTurnResponsiveness; a plane only applies it while no wheel is on the ground).
    /// PlayerLook.Update rewrites _look_x / _look_y every frame from the mouse and the roll / pitch keys; VR has no
    /// mouse, so they stay 0. After Update the stick is written there, scaled like the keys
    /// (vehicle.asset.airTurnResponsiveness) but analog:
    ///   stick up / back    = pitch: up = nose down = forward (flipped by ControlsSettings.invertFlight, like the keys)
    ///   stick right / left = roll right / left (the rollRight key writes a positive look_x; invertFlight leaves roll alone)
    /// An idle axis leaves the keyboard result alone. Harmony exposes the private fields as "____look_x" / "____look_y".
    /// </summary>
    [HarmonyPatch(typeof(PlayerLook), "Update")]
    internal static class PlayerLookAircraftStickPatch
    {
        private static void Postfix(PlayerLook __instance, ref float ____look_x, ref float ____look_y)
        {
            // Is this the VR local player at all? PlayerLook.Update runs for every player, so the others stop here, cheaply.
            if (!VR.VRState.IsActive || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }

            // Is the player's input blocked right now? A cursor UI is open, the game is loading, or freecam is on.
            if (PlayerUI.window.showCursor || LoadingUI.isBlocked || __instance.IsControllingFreecam)
            {
                return;
            }

            // Is the player driving an aircraft? The most work of the three, so it comes last.
            InteractableVehicle vehicle = __instance.player.movement.getVehicle();
            if (vehicle == null || vehicle.asset == null || !IsAircraft(vehicle.asset.engine) || !vehicle.isDriver)
            {
                return;
            }

            VR.VRControllerInput.Refresh();
            Vector2 stick = VR.VRControllerInput.RightStick;
            float responsiveness = vehicle.asset.airTurnResponsiveness;

            if (stick.y != 0f)
            {
                float pitch = stick.y * responsiveness;
                ____look_y = ControlsSettings.invertFlight ? -pitch : pitch;
            }

            if (stick.x != 0f)
            {
                ____look_x = stick.x * responsiveness;
            }
        }

        private static bool IsAircraft(EEngine engine)
        {
            return engine == EEngine.HELICOPTER || engine == EEngine.PLANE || engine == EEngine.BLIMP;
        }
    }
}
