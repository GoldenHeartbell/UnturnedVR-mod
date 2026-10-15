using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace UnturnedVR.VR
{
    /// <summary>
    /// M5: thumbstick state of both controllers, refreshed at most once per frame.
    /// Source order: SteamVR Input actions (our own bindings) per stick / button, then for what has no active action
    /// Unity XR InputDevices (works when the XR input subsystem reports controllers), then, in the shipped build, a
    /// direct OpenVR GetControllerState read (the same reason VROpenVRPoseFallback exists: SteamVR Input isn't
    /// initialized for XR, so XR reports no devices). The later sources are only read for what the earlier one could
    /// not answer: with every action bound, no XR or legacy button / stick call is made at all.
    /// Legacy GetControllerState works without an action manifest and rAxis0 is the thumbstick
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

        // "l_trigger" etc. built once: ResolveButtons runs per hand per frame.
        private static readonly string[] LeftKeys = { "l_trigger", "l_grip", "l_a", "l_b", "l_stickclick" };
        private static readonly string[] RightKeys = { "r_trigger", "r_grip", "r_a", "r_b", "r_stickclick" };

        private const VRButton AllButtons = VRButton.Trigger | VRButton.Grip | VRButton.A | VRButton.B | VRButton.StickClick;

        private static VRButton prevLeftButtons;
        private static VRButton prevRightButtons;
        private static ulong prevLeftRaw;
        private static ulong prevRightRaw;

        private static readonly List<InputDevice> deviceBuffer = new List<InputDevice>();
        private static readonly TrackedDevicePose_t[] fallbackPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private static int lastFrame = -1;
        private static string loggedSource;
        private static string loggedPointerSource;

        // When Unity XR reports no controller (the usual case here, see VROpenVRPoseFallback) the query is not repeated every
        // call: it is skipped for XrRetrySeconds and then tried again, so XR devices that appear later are still found.
        private const float XrRetrySeconds = 1f;
        private static readonly float[] xrRetryAt = new float[2]; // 0 = left, 1 = right

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
            // SteamVR Input (our own bindings) wins per action when it has an active binding for the controller; the XR /
            // legacy OpenVR read only runs for a side that has none (with all actions bound it is skipped entirely).
            VRSteamInput.Update();
            string source = "SteamVR Input";
            bool leftOk = VRSteamInput.TryGetVector2("l_stick", out Vector2 left);
            bool rightOk = VRSteamInput.TryGetVector2("r_stick", out Vector2 right);
            if (!leftOk)
            {
                leftOk = ReadLegacyStick(InputDeviceCharacteristics.Left, ETrackedControllerRole.LeftHand, out left, ref source);
            }
            if (!rightOk)
            {
                rightOk = ReadLegacyStick(InputDeviceCharacteristics.Right, ETrackedControllerRole.RightHand, out right, ref source);
            }

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

            LeftButtons = ResolveButtons(LeftKeys, InputDeviceCharacteristics.Left, ETrackedControllerRole.LeftHand, "L", ref prevLeftRaw);
            RightButtons = ResolveButtons(RightKeys, InputDeviceCharacteristics.Right, ETrackedControllerRole.RightHand, "R", ref prevRightRaw);
            if (VRDebugFlags.LogButtons)
            {
                LogButtonChange("L", LeftButtons, ref prevLeftButtons);
                LogButtonChange("R", RightButtons, ref prevRightButtons);
            }
        }

        // A stick without an active SteamVR action: Unity XR first, then the legacy OpenVR controller state.
        private static bool ReadLegacyStick(InputDeviceCharacteristics hand, ETrackedControllerRole role, out Vector2 axis, ref string source)
        {
            if (TryReadXR(hand, out axis))
            {
                source = "XR";
                return true;
            }
            if (TryReadOpenVR(role, out axis))
            {
                source = "OpenVR";
                return true;
            }
            return false;
        }

        // The buttons of one controller: each from its SteamVR Input action when that action has an active binding, the rest
        // from the legacy source (Unity XR features, then the OpenVR button bits). When all five actions are bound the legacy
        // read is not made (VRDebugFlags.LogButtons keeps it, for the raw OpenVR bits it logs).
        private static VRButton ResolveButtons(string[] keys, InputDeviceCharacteristics hand, ETrackedControllerRole role,
            string name, ref ulong prevRaw)
        {
            VRButton active = VRButton.None;
            VRButton pressed = VRButton.None;
            ProbeAction(keys[0], VRButton.Trigger, ref active, ref pressed);
            ProbeAction(keys[1], VRButton.Grip, ref active, ref pressed);
            ProbeAction(keys[2], VRButton.A, ref active, ref pressed);
            ProbeAction(keys[3], VRButton.B, ref active, ref pressed);
            ProbeAction(keys[4], VRButton.StickClick, ref active, ref pressed);

            if (active == AllButtons && !VRDebugFlags.LogButtons)
            {
                return pressed;
            }

            VRButton legacy = ReadButtons(hand, role, name, ref prevRaw);
            return (legacy & ~active) | pressed;
        }

        private static void ProbeAction(string key, VRButton button, ref VRButton active, ref VRButton pressed)
        {
            if (VRSteamInput.TryGetBool(key, out bool state))
            {
                active |= button;
                if (state)
                {
                    pressed |= button;
                }
            }
        }

        // Fills deviceBuffer with the XR controllers of that hand; false (and an empty buffer) when there are none or the
        // last query found none less than XrRetrySeconds ago.
        private static bool QueryControllers(InputDeviceCharacteristics hand)
        {
            int side = hand == InputDeviceCharacteristics.Left ? 0 : 1;
            deviceBuffer.Clear();
            if (Time.unscaledTime < xrRetryAt[side])
            {
                return false;
            }

            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand | hand, deviceBuffer);
            if (deviceBuffer.Count == 0)
            {
                xrRetryAt[side] = Time.unscaledTime + XrRetrySeconds;
                return false;
            }
            return true;
        }

        // Button roles of one controller: Unity XR features first, then the legacy OpenVR button bits.
        // Index bits come from EVRButtonId (A = 2, B = 1, JoyStick click = 35, trigger = 33 / rAxis1.x). The grip
        // source is rAxis2.x. Whether the legacy call reports them without an action manifest is
        // unverified; the LogButtons output shows the real bits, so correct them here.
        private static VRButton ReadButtons(InputDeviceCharacteristics hand, ETrackedControllerRole role, string name, ref ulong prevRaw)
        {
            if (QueryControllers(hand))
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
        // The OpenVR pose origin points out of the controller tip (+Z after conversion) well enough to aim with.
        private static void RefreshRightPointer()
        {
            // The trigger comes from the r_trigger action when it is bound; the legacy trigger read is then skipped.
            bool hasActionTrigger = VRSteamInput.TryGetBool("r_trigger", out bool actionTrigger);

            string source = "XR";
            bool ok = TryReadRightXR(out Vector3 position, out Quaternion rotation, out bool trigger, !hasActionTrigger);
            if (!ok)
            {
                source = "OpenVR";
                ok = TryReadRightOpenVR(out position, out rotation, out trigger, !hasActionTrigger);
            }

            RightPoseValid = ok;
            RightTrigger = ok && (hasActionTrigger ? actionTrigger : trigger);
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

        private static bool TryReadRightXR(out Vector3 position, out Quaternion rotation, out bool trigger, bool readTrigger)
        {
            trigger = false;
            if (!TryReadPoseXR(InputDeviceCharacteristics.Right, out position, out rotation))
            {
                return false;
            }

            // TryReadPoseXR left the buffer holding the right controller.
            if (readTrigger && deviceBuffer[0].TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed))
            {
                trigger = pressed;
            }
            return true;
        }

        private static bool TryReadRightOpenVR(out Vector3 position, out Quaternion rotation, out bool trigger, bool readTrigger)
        {
            trigger = false;
            if (!TryReadPoseOpenVR(ETrackedControllerRole.RightHand, out position, out rotation, out uint index))
            {
                return false;
            }

            if (readTrigger)
            {
                VRControllerState_t state = default;
                if (OpenVR.System.GetControllerState(index, ref state, ControllerStateSize))
                {
                    ulong triggerBit = 1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
                    trigger = (state.ulButtonPressed & triggerBit) != 0 || state.rAxis1.x > TriggerPressAxis;
                }
            }
            return true;
        }

        // Pose of one controller in tracking space. XR first, then the compositor's render pose.
        private static bool TryReadPoseXR(InputDeviceCharacteristics hand, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            return QueryControllers(hand)
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

            // The compositor's render pose of this one device (GetLastPoses would copy the poses of all 64 devices, twice).
            TrackedDevicePose_t pose = default;
            TrackedDevicePose_t gamePose = default;
            CVRCompositor compositor = OpenVR.Compositor;
            if (compositor == null || compositor.GetLastPoseForTrackedDeviceIndex(index, ref pose, ref gamePose) != EVRCompositorError.None)
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
            return QueryControllers(hand) && deviceBuffer[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out axis);
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
