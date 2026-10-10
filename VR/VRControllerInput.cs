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

        /// <summary>Right controller pose in tracking space (local to the rig root), valid when <see cref="RightPoseValid"/>.</summary>
        public static bool RightPoseValid { get; private set; }
        public static Vector3 RightPosition { get; private set; }
        public static Quaternion RightRotation { get; private set; }
        public static bool RightTrigger { get; private set; }

        /// <summary>Left controller pose in tracking space, valid when <see cref="LeftPoseValid"/>.</summary>
        public static bool LeftPoseValid { get; private set; }
        public static Vector3 LeftPosition { get; private set; }
        public static Quaternion LeftRotation { get; private set; }

        /// <summary>Controller velocity in tracking space (m/s, finite difference per frame); zero while untracked.</summary>
        public static Vector3 LeftVelocity { get; private set; }
        public static Vector3 RightVelocity { get; private set; }

        /// <summary>Buttons currently held on each controller (zero when that controller is not tracked).</summary>
        public static VRButton LeftButtons { get; private set; }
        public static VRButton RightButtons { get; private set; }

        private const float TriggerPressAxis = 0.75f;
        private const float GripPressAxis = 0.75f;
        private const float StickClickMaxDeflection = 0.7f;

        private static readonly uint ControllerStateSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VRControllerState_t));

        private static bool prevLeftPoseValid;
        private static bool prevRightPoseValid;
        private static Vector3 prevLeftPosition;
        private static Vector3 prevRightPosition;

        // "l_trigger" etc. built once: ApplyActions runs per hand per frame.
        private static readonly string[] LeftKeys = { "l_trigger", "l_grip", "l_a", "l_b", "l_stickclick" };
        private static readonly string[] RightKeys = { "r_trigger", "r_grip", "r_a", "r_b", "r_stickclick" };

        private static VRButton prevLeftButtons;
        private static VRButton prevRightButtons;
        private static ulong prevLeftRaw;
        private static ulong prevRightRaw;

        private static readonly List<InputDevice> deviceBuffer = new List<InputDevice>();
        private static readonly TrackedDevicePose_t[] renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private static readonly TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private static readonly TrackedDevicePose_t[] fallbackPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private static int lastFrame = -1;
        private static string loggedSource;
        private static string loggedPointerSource;

        /// <summary>Call before reading the sticks; cheap if already refreshed this frame.</summary>
        public static void Refresh()
        {
            if (lastFrame == Time.frameCount)
            {
                return;
            }
            lastFrame = Time.frameCount;
            long perfStart = VRPerf.Begin();
            RefreshCore();
            VRPerf.End(VRPerf.Controllers, perfStart);
        }

        private static void RefreshCore()
        {
            bool leftOk = TryReadXR(InputDeviceCharacteristics.Left, out Vector2 left);
            bool rightOk = TryReadXR(InputDeviceCharacteristics.Right, out Vector2 right);
            string source = "XR";

            if (!leftOk && !rightOk)
            {
                leftOk = TryReadOpenVR(ETrackedControllerRole.LeftHand, out left);
                rightOk = TryReadOpenVR(ETrackedControllerRole.RightHand, out right);
                source = "OpenVR";
            }

            // SteamVR Input (our own bindings) wins per action when it has an active binding for the controller.
            VRSteamInput.Update();
            if (VRSteamInput.TryGetVector2("l_stick", out Vector2 actionLeft)) { left = actionLeft; leftOk = true; source = "SteamVR Input"; }
            if (VRSteamInput.TryGetVector2("r_stick", out Vector2 actionRight)) { right = actionRight; rightOk = true; source = "SteamVR Input"; }

            LeftStick = leftOk ? ApplyDeadzone(left) : Vector2.zero;
            RightStick = rightOk ? ApplyDeadzone(right) : Vector2.zero;
            LogStick("L", LeftStick, ref prevLeftStickActive);
            LogStick("R", RightStick, ref prevRightStickActive);

            if ((leftOk || rightOk) && loggedSource != source)
            {
                loggedSource = source;
                VRLog.Info("using " + source + " source for controller sticks.");
            }

            RefreshRightPointer();
            ReadHandPose(false);
            UpdateVelocity();

            LeftButtons = ApplyActions("l", ReadButtons(InputDeviceCharacteristics.Left, ETrackedControllerRole.LeftHand, "L", ref prevLeftRaw));
            RightButtons = ApplyActions("r", ReadButtons(InputDeviceCharacteristics.Right, ETrackedControllerRole.RightHand, "R", ref prevRightRaw));
            if (RightPoseValid && VRSteamInput.TryGetBool("r_trigger", out bool actionTrigger))
            {
                RightTrigger = actionTrigger;
            }
            if (VRDebugFlags.LogButtons)
            {
                LogButtonChange("L", LeftButtons, ref prevLeftButtons);
                LogButtonChange("R", RightButtons, ref prevRightButtons);
            }
        }

        // Overrides each button with its SteamVR Input action when that action has an active binding.
        private static VRButton ApplyActions(string hand, VRButton mask)
        {
            string[] keys = hand == "l" ? LeftKeys : RightKeys;
            mask = ApplyAction(keys[0], VRButton.Trigger, mask);
            mask = ApplyAction(keys[1], VRButton.Grip, mask);
            mask = ApplyAction(keys[2], VRButton.A, mask);
            mask = ApplyAction(keys[3], VRButton.B, mask);
            return ApplyAction(keys[4], VRButton.StickClick, mask);
        }

        private static VRButton ApplyAction(string key, VRButton button, VRButton mask)
        {
            if (!VRSteamInput.TryGetBool(key, out bool state))
            {
                return mask;
            }
            return state ? mask | button : mask & ~button;
        }

        // Button roles of one controller: Unity XR features first, then the legacy OpenVR button bits.
        // Index bits come from EVRButtonId (A = 2, B = 1, JoyStick click = 35, trigger = 33 / rAxis1.x). The grip
        // source is still a GUESS (rAxis2.x). Whether the legacy call reports them without an action manifest is
        // unverified; the LogButtons output shows the real bits, so correct them here.
        private static VRButton ReadButtons(InputDeviceCharacteristics hand, ETrackedControllerRole role, string name, ref ulong prevRaw)
        {
            deviceBuffer.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand | hand, deviceBuffer);
            if (deviceBuffer.Count > 0)
            {
                InputDevice device = deviceBuffer[0];
                VRButton xr = VRButton.None;
                if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger) && trigger) xr |= VRButton.Trigger;
                if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool grip) && grip) xr |= VRButton.Grip;
                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool a) && a) xr |= VRButton.A;
                if (device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool b) && b) xr |= VRButton.B;
                if (device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool click) && click) xr |= VRButton.StickClick;
                return xr;
            }

            CVRSystem system = OpenVR.System;
            if (system == null)
            {
                LogHandStatus(name, "OpenVR.System is null");
                return VRButton.None;
            }

            uint index = system.GetTrackedDeviceIndexForControllerRole(role);
            if (index == OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                if (VRDebugFlags.LogButtons) LogHandStatus(name, "no controller has the " + role + " role");
                return VRButton.None;
            }

            VRControllerState_t state = default;
            if (!system.GetControllerState(index, ref state, ControllerStateSize))
            {
                if (VRDebugFlags.LogButtons) LogHandStatus(name, "OpenVR device " + index + ": GetControllerState failed");
                return VRButton.None;
            }
            if (VRDebugFlags.LogButtons) LogHandStatus(name, "OpenVR device " + index + " (" + role + ")");

            ulong pressed = state.ulButtonPressed;
            if (VRDebugFlags.LogButtons && pressed != prevRaw)
            {
                prevRaw = pressed;
                string bits = "";
                for (int bit = 0; bit < 64; bit++)
                {
                    if ((pressed & (1UL << bit)) != 0)
                    {
                        bits += (bits.Length > 0 ? "," : "") + bit;
                    }
                }
                VRLog.Info("raw OpenVR buttons " + name + ": bits [" + bits + "] axis0=(" + state.rAxis0.x.ToString("0.00") + "," + state.rAxis0.y.ToString("0.00")
                    + ") axis1.x=" + state.rAxis1.x.ToString("0.00") + " axis2=(" + state.rAxis2.x.ToString("0.00") + "," + state.rAxis2.y.ToString("0.00")
                    + ") axis3=(" + state.rAxis3.x.ToString("0.00") + "," + state.rAxis3.y.ToString("0.00") + ") axis4=(" + state.rAxis4.x.ToString("0.00") + "," + state.rAxis4.y.ToString("0.00") + ")");
            }

            // Index legacy bits as seen in the shipped game's log (right hand; the left hand is assumed to match):
            // trigger = 33 (+ rAxis1.x), B = 1, A = 7, stick click = 32, grip = bit 2 but that sets already at a light
            // squeeze, so the grip is taken from the analog rAxis2.x instead. The enum's "IndexController_A = 2" and
            // "IndexController_JoyStick = 35" did NOT match what the legacy call reports.
            VRButton mask = VRButton.None;
            if ((pressed & (1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger)) != 0 || state.rAxis1.x > TriggerPressAxis) mask |= VRButton.Trigger;
            if ((pressed & (1UL << (int)EVRButtonId.k_EButton_A)) != 0) mask |= VRButton.A;
            if ((pressed & (1UL << (int)EVRButtonId.k_EButton_ApplicationMenu)) != 0) mask |= VRButton.B;
            // The legacy Index binding emulates a touchpad click when the stick is pushed to its rim, so a click only
            // counts while the stick is away from the edge (a real click with the stick fully pushed is lost).
            float stickMagnitude = new Vector2(state.rAxis0.x, state.rAxis0.y).magnitude;
            if ((pressed & (1UL << (int)EVRButtonId.k_EButton_SteamVR_Touchpad)) != 0 && stickMagnitude < StickClickMaxDeflection) mask |= VRButton.StickClick;
            if (state.rAxis2.x > GripPressAxis) mask |= VRButton.Grip;
            return mask;
        }

        // Which controller each hand resolved to, logged when it changes (the left hand reported nothing in one test).
        private static string lastLeftStatus;
        private static string lastRightStatus;

        private static void LogHandStatus(string name, string status)
        {
            if (!VRDebugFlags.LogButtons)
            {
                return;
            }

            ref string last = ref (name == "L" ? ref lastLeftStatus : ref lastRightStatus);
            if (last != status)
            {
                last = status;
                VRLog.Info("hand " + name + ": " + status);
            }
        }

        private static bool prevLeftStickActive;
        private static bool prevRightStickActive;

        private static void LogStick(string name, Vector2 stick, ref bool prevActive)
        {
            bool active = stick != Vector2.zero;
            if (VRDebugFlags.LogButtons && active != prevActive)
            {
                VRLog.Info("stick " + name + (active ? " active " + stick.ToString("0.00") : " released"));
            }
            prevActive = active;
        }

        private static void LogButtonChange(string name, VRButton now, ref VRButton prev)
        {
            if (now != prev)
            {
                VRLog.Info("buttons " + name + ": " + (now == VRButton.None ? "(none)" : now.ToString()));
                prev = now;
            }
        }

        // Pose + trigger of the right hand, for the UI laser pointer. Same source order as the sticks.
        // GUESS: the OpenVR pose origin points out of the controller tip (+Z after conversion) well enough to aim with.
        private static void RefreshRightPointer()
        {
            string source = "XR";
            bool ok = TryReadRightXR(out Vector3 position, out Quaternion rotation, out bool trigger);
            if (!ok)
            {
                source = "OpenVR";
                ok = TryReadRightOpenVR(out position, out rotation, out trigger);
            }

            RightPoseValid = ok;
            RightTrigger = ok && trigger;
            if (!ok)
            {
                return;
            }

            RightPosition = position;
            RightRotation = rotation;
            if (loggedPointerSource != source)
            {
                loggedPointerSource = source;
                VRLog.Info("using " + source + " source for the right controller pose/trigger (pos=" + position + ").");
            }
        }

        private static bool TryReadRightXR(out Vector3 position, out Quaternion rotation, out bool trigger)
        {
            trigger = false;
            if (!TryReadPoseXR(InputDeviceCharacteristics.Right, out position, out rotation))
            {
                return false;
            }

            // TryReadPoseXR left the buffer holding the right controller.
            if (deviceBuffer[0].TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed))
            {
                trigger = pressed;
            }
            return true;
        }

        private static bool TryReadRightOpenVR(out Vector3 position, out Quaternion rotation, out bool trigger)
        {
            trigger = false;
            if (!TryReadPoseOpenVR(ETrackedControllerRole.RightHand, out position, out rotation, out uint index))
            {
                return false;
            }

            VRControllerState_t state = default;
            if (OpenVR.System.GetControllerState(index, ref state, ControllerStateSize))
            {
                ulong triggerBit = 1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
                trigger = (state.ulButtonPressed & triggerBit) != 0 || state.rAxis1.x > TriggerPressAxis;
            }
            return true;
        }

        // Pose of one controller in tracking space. XR first, then the compositor's render pose.
        private static bool TryReadPoseXR(InputDeviceCharacteristics hand, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            deviceBuffer.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand | hand, deviceBuffer);
            return deviceBuffer.Count > 0
                && deviceBuffer[0].TryGetFeatureValue(CommonUsages.devicePosition, out position)
                && deviceBuffer[0].TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
        }

        private static bool TryReadPoseOpenVR(ETrackedControllerRole role, out Vector3 position, out Quaternion rotation, out uint index)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            index = OpenVR.k_unTrackedDeviceIndexInvalid;

            CVRSystem system = OpenVR.System;
            if (system == null)
            {
                return false;
            }

            index = system.GetTrackedDeviceIndexForControllerRole(role);
            if (index == OpenVR.k_unTrackedDeviceIndexInvalid || index >= OpenVR.k_unMaxTrackedDeviceCount)
            {
                return false;
            }

            TrackedDevicePose_t pose;
            CVRCompositor compositor = OpenVR.Compositor;
            if (compositor != null && compositor.GetLastPoses(renderPoses, gamePoses) == EVRCompositorError.None)
            {
                pose = renderPoses[index];
            }
            else
            {
                system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, fallbackPoses);
                pose = fallbackPoses[index];
            }

            if (!pose.bPoseIsValid)
            {
                return false;
            }

            VROpenVRPoseFallback.ConvertPose(pose.mDeviceToAbsoluteTracking, out position, out rotation);
            return true;
        }

        /// <summary>
        /// Re-reads both controller poses without touching buttons or velocity. Called right before rendering
        /// (VRHands) so hands use the freshest predicted pose instead of the frame-start sample.
        /// </summary>
        public static void RefreshPoses()
        {
            ReadHandPose(false);
            ReadHandPose(true);
        }

        private static void ReadHandPose(bool right)
        {
            InputDeviceCharacteristics xrHand = right ? InputDeviceCharacteristics.Right : InputDeviceCharacteristics.Left;
            ETrackedControllerRole role = right ? ETrackedControllerRole.RightHand : ETrackedControllerRole.LeftHand;

            bool ok = TryReadPoseXR(xrHand, out Vector3 position, out Quaternion rotation)
                || TryReadPoseOpenVR(role, out position, out rotation, out _);

            if (right)
            {
                RightPoseValid = ok;
                if (ok) { RightPosition = position; RightRotation = rotation; }
            }
            else
            {
                LeftPoseValid = ok;
                if (ok) { LeftPosition = position; LeftRotation = rotation; }
            }
        }

        // Velocity by finite difference of the frame-start poses (works with any pose source).
        private static void UpdateVelocity()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0.0001f)
            {
                return;
            }

            LeftVelocity = LeftPoseValid && prevLeftPoseValid ? (LeftPosition - prevLeftPosition) / dt : Vector3.zero;
            RightVelocity = RightPoseValid && prevRightPoseValid ? (RightPosition - prevRightPosition) / dt : Vector3.zero;
            prevLeftPoseValid = LeftPoseValid;
            prevRightPoseValid = RightPoseValid;
            prevLeftPosition = LeftPosition;
            prevRightPosition = RightPosition;
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
            if (!system.GetControllerState(index, ref state, ControllerStateSize))
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
