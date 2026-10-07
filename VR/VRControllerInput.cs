using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace UnturnedVR.VR
{
    /// <summary>
    /// M5: thumbstick state of both controllers, refreshed at most once per frame.
    /// Source order: Unity XR InputDevices (works when the XR input subsystem reports controllers),
    /// then, in the shipped build, a direct OpenVR GetControllerState read (the same reason
    /// VROpenVRPoseFallback exists: SteamVR Input isn't initialized, so XR reports no devices).
    /// GUESS: legacy GetControllerState works without an action manifest and rAxis0 is the thumbstick
    /// on Touch/Index/WMR controllers. Confirm from the "using ... source" log line / in headset.
    /// </summary>
    internal static class VRControllerInput
    {
        public const float Deadzone = 0.2f;

        public static Vector2 LeftStick { get; private set; }
        public static Vector2 RightStick { get; private set; }

        private static readonly List<InputDevice> deviceBuffer = new List<InputDevice>();
        private static int lastFrame = -1;
        private static string loggedSource;

        /// <summary>Call before reading the sticks; cheap if already refreshed this frame.</summary>
        public static void Refresh()
        {
            if (lastFrame == Time.frameCount)
            {
                return;
            }
            lastFrame = Time.frameCount;

            bool leftOk = TryReadXR(InputDeviceCharacteristics.Left, out Vector2 left);
            bool rightOk = TryReadXR(InputDeviceCharacteristics.Right, out Vector2 right);
            string source = "XR";

            if (!leftOk && !rightOk)
            {
                leftOk = TryReadOpenVR(ETrackedControllerRole.LeftHand, out left);
                rightOk = TryReadOpenVR(ETrackedControllerRole.RightHand, out right);
                source = "OpenVR";
            }

            LeftStick = leftOk ? ApplyDeadzone(left) : Vector2.zero;
            RightStick = rightOk ? ApplyDeadzone(right) : Vector2.zero;

            if ((leftOk || rightOk) && loggedSource != source)
            {
                loggedSource = source;
                VRLog.Info("using " + source + " source for controller sticks.");
            }
        }

        private static bool TryReadXR(InputDeviceCharacteristics hand, out Vector2 axis)
        {
            axis = Vector2.zero;
            deviceBuffer.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand | hand, deviceBuffer);
            return deviceBuffer.Count > 0 && deviceBuffer[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out axis);
        }

        private static bool TryReadOpenVR(ETrackedControllerRole role, out Vector2 axis)
        {
            axis = Vector2.zero;
            CVRSystem system = OpenVR.System;
            if (system == null)
            {
                return false;
            }

            uint index = system.GetTrackedDeviceIndexForControllerRole(role);
            if (index == OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                return false;
            }

            VRControllerState_t state = default;
            if (!system.GetControllerState(index, ref state, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VRControllerState_t))))
            {
                return false;
            }

            axis = new Vector2(state.rAxis0.x, state.rAxis0.y);
            return true;
        }

        private static Vector2 ApplyDeadzone(Vector2 v)
        {
            return v.magnitude < Deadzone ? Vector2.zero : v;
        }
    }
}
