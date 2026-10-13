using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace UnturnedVR.VR
{
    /// <summary>
    /// With the Unity OpenVR loader the XR input subsystem reported no devices
    /// (log: "Input devices: none") because SteamVR Input is not initialized - VHVR initializes it
    /// before the XR SDK ("to ensure SteamVR_Input is enabled", VRCore/VRManager.cs), which we defer
    /// to M5. Until then, when no head device exists in InputDevices, read the HMD pose directly from
    /// the OpenVR API (Unity.XR.OpenVR.dll already ships the Valve.VR wrapper) and apply it to this
    /// transform in the same space TrackedPoseDriver would use (local to the rig root).
    /// Whether Valve.VR.OpenVR.System is usable without our own OpenVR.Init is untested.
    /// </summary>
    internal class VROpenVRPoseFallback : MonoBehaviour
    {
        // When XR reports no head device the query is not repeated twice a frame: it is skipped for this long, then retried.
        private const float XrRetrySeconds = 1f;

        private readonly List<InputDevice> deviceBuffer = new List<InputDevice>();
        private readonly TrackedDevicePose_t[] fallbackPoses = new TrackedDevicePose_t[1];
        private float xrRetryAt;
        private bool loggedNoSystem;
        private bool loggedFirstPose;

        private void OnEnable()
        {
            Application.onBeforeRender += Apply;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= Apply;
        }

        // Applied twice per frame like TrackedPoseDriver's UpdateAndBeforeRender: once here and again
        // right before rendering, so the camera uses the freshest pose.
        private void LateUpdate()
        {
            Apply();
        }

        private void Apply()
        {
            if (Time.unscaledTime >= xrRetryAt)
            {
                deviceBuffer.Clear();
                InputDevices.GetDevicesWithCharacteristics(
                    InputDeviceCharacteristics.HeadMounted | InputDeviceCharacteristics.TrackedDevice, deviceBuffer);
                if (deviceBuffer.Count > 0)
                {
                    return; // TrackedPoseDriver has a real device to follow.
                }
                xrRetryAt = Time.unscaledTime + XrRetrySeconds;
            }

            CVRSystem system = OpenVR.System;
            if (system == null)
            {
                if (!loggedNoSystem)
                {
                    loggedNoSystem = true;
                    VRLog.Warn("OpenVR.System is null, cannot read HMD pose directly.");
                }
                return;
            }

            // Prefer the compositor's render pose: the predicted pose for this frame's photon time,
            // i.e. exactly what the eyes are rendered/reprojected with. Asking the system for "now"
            // (prediction 0) mismatches it and shows up as shaky camera movement. Only the HMD's pose is fetched
            // (GetLastPoses would copy the poses of all 64 devices, twice, on each of the two calls per frame).
            TrackedDevicePose_t pose = default;
            TrackedDevicePose_t gamePose = default;
            CVRCompositor compositor = OpenVR.Compositor;
            if (compositor == null
                || compositor.GetLastPoseForTrackedDeviceIndex(OpenVR.k_unTrackedDeviceIndex_Hmd, ref pose, ref gamePose) != EVRCompositorError.None
                || !pose.bPoseIsValid)
            {
                system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, fallbackPoses);
                pose = fallbackPoses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];
            }

            if (!pose.bPoseIsValid)
            {
                return;
            }

            ConvertPose(pose.mDeviceToAbsoluteTracking, out Vector3 position, out Quaternion rotation);
            transform.localPosition = position;
            transform.localRotation = rotation;

            if (!loggedFirstPose)
            {
                loggedFirstPose = true;
                VRLog.Info("using direct OpenVR HMD pose (XR input has no head device).");
            }
        }

        /// <summary>
        /// Same conversion as SteamVR_Utils.RigidTransform (OpenVR right-handed -> Unity left-handed).
        /// The result is in tracking space, i.e. local to the rig root.
        /// </summary>
        public static void ConvertPose(HmdMatrix34_t p, out Vector3 position, out Quaternion rotation)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m[0, 0] = p.m0; m[0, 1] = p.m1; m[0, 2] = -p.m2; m[0, 3] = p.m3;
            m[1, 0] = p.m4; m[1, 1] = p.m5; m[1, 2] = -p.m6; m[1, 3] = p.m7;
            m[2, 0] = -p.m8; m[2, 1] = -p.m9; m[2, 2] = p.m10; m[2, 3] = -p.m11;

            position = new Vector3(m[0, 3], m[1, 3], m[2, 3]);
            rotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
        }
    }
}
