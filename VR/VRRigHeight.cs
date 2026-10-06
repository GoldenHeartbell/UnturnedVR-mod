using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.XR;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Sits on the rig root; the HMD camera child is moved by a TrackedPoseDriver (same approach
    /// as VHVR's SteamVR Player prefab). The pose it applies is relative to the XR tracking
    /// origin, whose Y is either floor- or device-start-relative (never verified which), so this
    /// only applies the delta from the first tracked frame on top of the game's own stance-aware
    /// eye height (which PlayerLook keeps computing on the disabled MainCamera).
    /// Also logs why tracking is missing while no pose arrives.
    /// </summary>
    internal class VRRigHeight : MonoBehaviour
    {
        private const float FallbackStandingEyeHeight = 1.75f;
        private const float DiagnosticIntervalSeconds = 5f;

        public Transform Hmd;

        public bool HasPose => hasCalibratedHeight;

        private bool hasCalibratedHeight;
        private float calibrationHeadY;
        private float nextDiagnosticTime;
        private readonly List<InputDevice> deviceBuffer = new List<InputDevice>();

        private void OnEnable()
        {
            hasCalibratedHeight = false;
        }

        private void LateUpdate()
        {
            if (Hmd == null)
            {
                return;
            }

            VRRig.SyncGraphicsSettings();

            // TrackedPoseDriver leaves the camera at its local origin until a pose arrives.
            if (!hasCalibratedHeight)
            {
                if (Hmd.localPosition == Vector3.zero)
                {
                    LogNoPose();
                    return;
                }

                calibrationHeadY = Hmd.localPosition.y;
                hasCalibratedHeight = true;
                UnturnedLog.info("[UnturnedVR] VRRigHeight: first HMD pose received, height calibrated.");
            }

            // In the menu the rig hangs directly on the menu camera: no eye height to add.
            float standingEyeHeight = VRRig.InMenu ? 0f : MainCamera.instance != null
                ? MainCamera.instance.transform.localPosition.y
                : FallbackStandingEyeHeight;

            transform.localPosition = new Vector3(0f, standingEyeHeight - calibrationHeadY, 0f);
        }

        private void LogNoPose()
        {
            if (Time.unscaledTime < nextDiagnosticTime)
            {
                return;
            }
            nextDiagnosticTime = Time.unscaledTime + DiagnosticIntervalSeconds;

            var manager = UnityEngine.XR.Management.XRGeneralSettings.Instance?.Manager;
            string loader = manager == null ? "no XRManagerSettings" : manager.activeLoader == null ? "no active loader" : manager.activeLoader.name;

            deviceBuffer.Clear();
            InputDevices.GetDevices(deviceBuffer);
            string devices = deviceBuffer.Count == 0 ? "none" : string.Join(", ", deviceBuffer.ConvertAll(d => "\"" + d.name + "\" [" + d.characteristics + "]"));

            UnturnedLog.warn("[UnturnedVR] VRRigHeight: no HMD pose yet. XR loader: " + loader + ". Input devices: " + devices + ". " + DescribeSubsystems() + ".");
        }

        private static string DescribeSubsystems()
        {
            var inputs = new List<XRInputSubsystem>();
            SubsystemManager.GetInstances(inputs);
            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetInstances(displays);

            string input = inputs.Count == 0 ? "none" : string.Join(",", inputs.ConvertAll(x => x.running ? "running" : "stopped"));
            string display = displays.Count == 0 ? "none" : string.Join(",", displays.ConvertAll(x => x.running ? "running" : "stopped"));
            return "XRInputSubsystem: " + input + ", XRDisplaySubsystem: " + display + ", app focused: " + Application.isFocused;
        }
    }
}
