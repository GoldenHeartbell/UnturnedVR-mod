using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Vehicle turrets aim with the head. The game turns a turret by PlayerLook's seat-relative _yaw / _pitch, which only the
    /// mouse changes, so in VR it stayed straight ahead. The prefix sets them from the headset; Update then clamps them to the
    /// turret's range, turns it and sends the angles to the server as usual.
    /// A seat that sits on the turret (tank gunner) turns with it, so the postfix turns the rig back by the turret yaw
    /// (VRRig.SetSeatCounterYaw) and the head yaw is measured from the turret's base.
    /// Right stick turns the view in the seat (the turret follows). With VRSettings.TurretControllerOnly the stick sets _yaw
    /// directly and only the pitch follows the head; a turret-carried seat then turns the view with the turret.
    /// Neither applies to the driver, whose right stick flies aircraft (PlayerLookAircraftStickPatch).
    /// </summary>
    [HarmonyPatch(typeof(PlayerLook), "Update")]
    internal static class PlayerLookTurretHeadAimPatch
    {
        private const float StickTurnSpeed = 120f; // degrees per second at full deflection, the same as smooth turn on foot

        private static Passenger loggedSeat;
        private static bool wasControllerOnly; // last frame in a turret seat was controller only (for switching without a jump)

        private static void Prefix(PlayerLook __instance, ref float ____yaw, ref float ____pitch)
        {
            if (!IsLocalVR(__instance))
            {
                return;
            }

            Passenger seat = TurretSeat(__instance);
            if (seat == null)
            {
                return;
            }
            if (PlayerUI.window.showCursor || LoadingUI.isBlocked || __instance.IsControllingFreecam)
            {
                return;
            }

            bool controllerOnly = ControllerOnly(__instance);
            if (!__instance.player.movement.getVehicle().isDriver)
            {
                VR.VRControllerInput.Refresh();
                float stick = VR.VRControllerInput.RightStick.x;
                if (stick != 0f)
                {
                    float degrees = stick * StickTurnSpeed * Time.deltaTime;
                    if (controllerOnly)
                    {
                        ____yaw += degrees;
                    }
                    else
                    {
                        VR.VRRig.TurnSeatView(degrees);
                    }
                }
            }

            if (VR.VRRig.TryGetHeadAnglesInBody(out float yaw, out float pitch))
            {
                if (controllerOnly)
                {
                    ____pitch = pitch;
                    return;
                }

                // On a turret-carried seat the body already holds last frame's turret yaw (and the rig is turned back by it).
                float raw = SeatTurnsWithTurret(seat) ? yaw + ____yaw : yaw;
                ____yaw = IntoRange(raw, seat.turret.yawMin, seat.turret.yawMax);
                ____pitch = pitch;
            }
        }

        private static void Postfix(PlayerLook __instance)
        {
            if (!IsLocalVR(__instance))
            {
                return;
            }

            Passenger seat = TurretSeat(__instance);
            if (seat == null)
            {
                wasControllerOnly = false;
                VR.VRRig.SetSeatCounterYaw(0f);
                return;
            }

            // Controller only: the rig turns with a turret-carried seat. Switching the setting while seated keeps the view where it is.
            bool controllerOnly = ControllerOnly(__instance);
            float counterYaw = !controllerOnly && SeatTurnsWithTurret(seat) ? __instance.yaw : 0f;
            if (controllerOnly != wasControllerOnly)
            {
                wasControllerOnly = controllerOnly;
                VRLog.Info("turret seat: turning " + (controllerOnly ? "controller only" : "head and controller") + ".");
                VR.VRRig.SetSeatCounterYawKeepView(counterYaw);
            }
            else
            {
                VR.VRRig.SetSeatCounterYaw(counterYaw);
            }
        }

        private static bool ControllerOnly(PlayerLook look)
        {
            return VR.VRSettings.TurretControllerOnly && !look.player.movement.getVehicle().isDriver;
        }

        // The same direction, written so it falls inside the turret's yaw range if it can (clampYaw only wraps by %360, so 200
        // on a -180..180 turret would stick at 180 instead of being -160).
        private static float IntoRange(float yaw, float min, float max)
        {
            float wrapped = Mathf.DeltaAngle(0f, yaw);
            if (wrapped < min && wrapped + 360f <= max)
            {
                return wrapped + 360f;
            }
            if (wrapped > max && wrapped - 360f >= min)
            {
                return wrapped - 360f;
            }
            return wrapped;
        }

        private static bool IsLocalVR(PlayerLook look)
        {
            return VR.VRState.IsActive && look.channel != null && look.channel.IsLocalPlayer;
        }

        private static Passenger TurretSeat(PlayerLook look)
        {
            Passenger seat = look.player.movement.getVehicleSeat();
            return seat != null && seat.turret != null ? seat : null;
        }

        private static bool SeatTurnsWithTurret(Passenger seat)
        {
            bool onYaw = seat.seat != null && seat.turretYaw != null && seat.seat.IsChildOf(seat.turretYaw);
            if (seat != loggedSeat)
            {
                loggedSeat = seat;
                bool onPitch = seat.seat != null && seat.turretPitch != null && seat.seat.IsChildOf(seat.turretPitch);
                VRLog.Info("turret seat: on turret yaw " + onYaw + ", on turret pitch " + onPitch + ", aim camera " + seat.turret.useAimCamera
                    + ", yaw " + seat.turret.yawMin + ".." + seat.turret.yawMax + ", pitch " + seat.turret.pitchMin + ".." + seat.turret.pitchMax + ".");
            }
            return onYaw;
        }
    }
}
