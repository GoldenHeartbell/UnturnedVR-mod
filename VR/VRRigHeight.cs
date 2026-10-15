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

        // Menu camera counts as resting below these speeds for this long (tune in headset).
        private const float MenuStillSpeed = 0.1f;        // m/s
        private const float MenuStillTurnRate = 4f;       // degrees/s
        private const float MenuStillSeconds = 0.3f;

        private bool menuHasPose;
        private float menuStillTime;
        private Vector3 menuPosition;
        private float menuYaw;
        private Vector3 lastCameraPosition;
        private float lastCameraYaw;

        private void OnEnable()
        {
            hasCalibratedHeight = false;
            menuHasPose = false;
        }

        private void LateUpdate()
        {
            if (Hmd == null)
            {
                return;
            }

            VRRig.SyncGraphicsSettings();

            // The scripted menu camera is pitched/rolled (about 10-20 degrees down). Inheriting that tilts the
            // whole tracking space, so the scene stays tilted against the real floor and feels glued to the
            // head. Take only its yaw: the menu camera still drives position and heading, you look around freely.
            // The camera also glides/rotates by itself (fly-in, page pans). A view that moves without your head
            // moving is what makes it feel swimmy, so hold the pose and only adopt the camera's once it rests.
            bool inMenu = VRRig.InMenu && transform.parent != null;
            if (inMenu)
            {
                UpdateMenuHold(transform.parent);
                transform.rotation = Quaternion.Euler(0f, menuYaw, 0f);
            }
            else
            {
                menuHasPose = false;
            }

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
                VRLog.Info("first HMD pose received, height calibrated.");
            }

            // In the menu the rig hangs directly on the menu camera: no eye height to add.
            float standingEyeHeight = VRRig.InMenu ? 0f : MainCamera.instance != null
                ? MainCamera.instance.transform.localPosition.y
                : FallbackStandingEyeHeight;

            if (inMenu)
            {
                transform.position = menuPosition + Vector3.down * calibrationHeadY;
                return;
            }

            transform.localPosition = new Vector3(0f, standingEyeHeight - calibrationHeadY, 0f);
        }

        // Adopts the menu camera's position and yaw only after it has been (nearly) still for a moment.
        private void UpdateMenuHold(Transform camera)
        {
            float yaw = camera.eulerAngles.y;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            bool still = menuHasPose
                && (camera.position - lastCameraPosition).magnitude / dt < MenuStillSpeed
                && Mathf.Abs(Mathf.DeltaAngle(lastCameraYaw, yaw)) / dt < MenuStillTurnRate;
            lastCameraPosition = camera.position;
            lastCameraYaw = yaw;

            menuStillTime = still ? menuStillTime + dt : 0f;

            // The "Menu camera motion" setting: Follow (default) = the rig rides along with the scripted fly-in and page
            // pans as the game plays them (position and heading; the camera's own tilt is still dropped), Hold still =
            // the older comfort behaviour of waiting until the camera rests.
            if (!menuHasPose || VRSettings.MenuCameraFollow || menuStillTime >= MenuStillSeconds)
            {
                menuPosition = camera.position;
                menuYaw = yaw;
                menuHasPose = true;
            }
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

            VRLog.Warn("no HMD pose yet. XR loader: " + loader + ". Input devices: " + devices + ". " + DescribeSubsystems() + ".");
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
