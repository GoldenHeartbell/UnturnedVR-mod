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
        private readonly List<InputDevice> deviceBuffer = new List<InputDevice>();
        private readonly TrackedDevicePose_t[] renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private readonly TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private readonly TrackedDevicePose_t[] fallbackPoses = new TrackedDevicePose_t[1];
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
            deviceBuffer.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.HeadMounted | InputDeviceCharacteristics.TrackedDevice, deviceBuffer);
            if (deviceBuffer.Count > 0)
            {
                return; // TrackedPoseDriver has a real device to follow.
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
            // (prediction 0) mismatches it and shows up as shaky camera movement.
            TrackedDevicePose_t pose;
            CVRCompositor compositor = OpenVR.Compositor;
            if (compositor != null
                && compositor.GetLastPoses(renderPoses, gamePoses) == EVRCompositorError.None
                && renderPoses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd].bPoseIsValid)
            {
                pose = renderPoses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];
            }
            else
            {
                system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, fallbackPoses);
                pose = fallbackPoses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];
            }

            if (!pose.bPoseIsValid)
            {
                return;
            }

            // Same conversion as SteamVR_Utils.RigidTransform (OpenVR right-handed -> Unity left-handed).
            HmdMatrix34_t p = pose.mDeviceToAbsoluteTracking;
            Matrix4x4 m = Matrix4x4.identity;
            m[0, 0] = p.m0; m[0, 1] = p.m1; m[0, 2] = -p.m2; m[0, 3] = p.m3;
            m[1, 0] = p.m4; m[1, 1] = p.m5; m[1, 2] = -p.m6; m[1, 3] = p.m7;
            m[2, 0] = -p.m8; m[2, 1] = -p.m9; m[2, 2] = p.m10; m[2, 3] = -p.m11;

            transform.localPosition = new Vector3(m[0, 3], m[1, 3], m[2, 3]);
            transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));

            if (!loggedFirstPose)
            {
                loggedFirstPose = true;
                VRLog.Info("using direct OpenVR HMD pose (XR input has no head device).");
            }
        }
    }
}
