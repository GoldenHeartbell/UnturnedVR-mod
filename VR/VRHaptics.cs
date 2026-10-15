using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.XR;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Controller rumble. A pulse goes through the SteamVR haptic action (actions.json out/l_haptic, out/r_haptic) when
    /// that action is bound to the controller; otherwise through the Unity XR device (SendHapticImpulse), which
    /// needs no binding. The first pulse logs which path is used.
    /// Gun shots: strength, length and pitch depend on the gun's action and size (pistol short and sharp, rifle
    /// heavier, shotgun/bolt/rocket long and deep, minigun a light buzz); the hand holding the gun rumbles fully, the
    /// second hand on a two-handed gun at 60%. Hovering something to pick up gives a short tick in the pickup hand.
    /// The values are tuned by feel.
    /// Cost: one OpenVR call per pulse.
    /// </summary>
    internal static class VRHaptics
    {
        private const float SupportHandShare = 0.6f;
        private const float MinShotGap = 0.03f; // shoot() can run twice for one shot (local effect + server reply)

        private static readonly List<InputDevice> devices = new List<InputDevice>(2);
        private static bool loggedPath;
        private static float lastShotAt = -1f;
        private static InteractableItem lastFocus;

        /// <summary>One rumble pulse. hand = VRHands.Left / VRHands.Right, amplitude 0-1, frequency in Hz.</summary>
        public static void Pulse(int hand, float amplitude, float seconds, float frequency)
        {
            if (!VRState.IsActive || hand < 0)
            {
                return;
            }
            bool right = hand == VRHands.Right;
            amplitude = Mathf.Clamp01(amplitude);

            // Both paths, every time: the SteamVR action says "ok" even when the user's saved binding has nothing
            // for it, so it cannot be trusted to be the one that rumbles. On a controller where both work the second
            // impulse just replaces the first (same amplitude and length).
            bool action = VRSteamInput.TryHaptic(right, seconds, frequency, amplitude);

            devices.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller | (right ? InputDeviceCharacteristics.Right : InputDeviceCharacteristics.Left), devices);
            bool xr = devices.Count > 0 && devices[0].SendHapticImpulse(0, amplitude, seconds);

            if (!loggedPath)
            {
                loggedPath = true;
                VRLog.Info("haptics: first pulse, SteamVR action " + (action ? "accepted" : "unavailable")
                    + ", Unity XR " + (devices.Count == 0 ? "no controller device" : xr ? "accepted" : "refused (no haptic support)") + ".");
            }
        }

        private static void LogPath(string path)
        {
            if (!loggedPath)
            {
                loggedPath = true;
                VRLog.Info("haptics: rumble goes through " + path + ".");
            }
        }

        /// <summary>A shot of the equipped gun was fired (local player).</summary>
        public static void GunShot(ItemGunAsset gun)
        {
            int hand = VRArms.ItemHand;
            if (gun == null || hand < 0 || Time.unscaledTime - lastShotAt < MinShotGap)
            {
                return;
            }
            lastShotAt = Time.unscaledTime;

            float amplitude;
            float seconds;
            float frequency;
            switch (gun.action)
            {
                case EAction.Minigun:
                    amplitude = 0.45f; seconds = 0.03f; frequency = 90f;
                    break;
                case EAction.Rocket:
                    amplitude = 1f; seconds = 0.22f; frequency = 50f;
                    break;
                case EAction.String: // bow
                    amplitude = 0.3f; seconds = 0.07f; frequency = 120f;
                    break;
                case EAction.Bolt: // sniper
                    amplitude = 1f; seconds = 0.16f; frequency = 70f;
                    break;
                case EAction.Pump:
                case EAction.Break: // shotguns
                    amplitude = 1f; seconds = 0.14f; frequency = 60f;
                    break;
                case EAction.Rail:
                    amplitude = 0.9f; seconds = 0.12f; frequency = 80f;
                    break;
                default: // semi/auto/burst by size: pistol, compact, rifle
                    if (gun.size_x <= 2)
                    {
                        amplitude = 0.55f; seconds = 0.05f; frequency = 200f;
                    }
                    else if (gun.size_x == 3)
                    {
                        amplitude = 0.7f; seconds = 0.06f; frequency = 150f;
                    }
                    else
                    {
                        amplitude = 0.85f; seconds = 0.07f; frequency = 120f;
                    }
                    break;
            }
            // Fast guns: never longer than the gap between shots (firerate counts 50 Hz ticks), or the pulses blur together.
            if (gun.firerate > 0)
            {
                seconds = Mathf.Min(seconds, gun.firerate * 0.02f * 0.8f);
            }

            Pulse(hand, amplitude, seconds, frequency);
            if (VRHold.Both)
            {
                Pulse(hand == VRHands.Left ? VRHands.Right : VRHands.Left, amplitude * SupportHandShare, seconds, frequency);
            }
        }

        /// <summary>
        /// Once per PlayerInteract.Update: a short tick in the pickup hand (the right) when the ray starts pointing at
        /// an item that can be picked up.
        /// </summary>
        public static void InteractFocus(Interactable focus)
        {
            InteractableItem item = focus as InteractableItem;
            if (item == lastFocus)
            {
                return;
            }
            lastFocus = item;
            if (item != null)
            {
                Pulse(VRKeyMap.InteractHand, 0.5f, 0.08f, 160f); // the hand that points (and whose grip picks up)
            }
        }
    }
}
