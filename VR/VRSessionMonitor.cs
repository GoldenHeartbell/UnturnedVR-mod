using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.XR;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Diagnostics only: logs every change in the XR session health (app focus, subsystem running
    /// state, device connect/disconnect, device count) with a
    /// timestamp, so a flapping headset link or an unfocused OpenXR session shows up in the log.
    /// </summary>
    internal class VRSessionMonitor : MonoBehaviour
    {
        private string lastSnapshot;
        private readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        private readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        private readonly List<InputDevice> devices = new List<InputDevice>();

        private void OnEnable()
        {
            InputDevices.deviceConnected += OnDeviceConnected;
            InputDevices.deviceDisconnected += OnDeviceDisconnected;
            lastSnapshot = null;
        }

        private void OnDisable()
        {
            InputDevices.deviceConnected -= OnDeviceConnected;
            InputDevices.deviceDisconnected -= OnDeviceDisconnected;
        }

        private void Update()
        {
            string snapshot = Snapshot();
            if (snapshot == lastSnapshot)
            {
                return;
            }

            UnturnedLog.info("[UnturnedVR] XR session change @" + Time.unscaledTime.ToString("F1") + "s: " + snapshot);
            lastSnapshot = snapshot;
        }

        private string Snapshot()
        {
            SubsystemManager.GetInstances(inputs);
            SubsystemManager.GetInstances(displays);
            devices.Clear();
            InputDevices.GetDevices(devices);

            string input = inputs.Count == 0 ? "none" : string.Join(",", inputs.ConvertAll(x => x.running ? "running" : "stopped"));
            string display = displays.Count == 0 ? "none" : string.Join(",", displays.ConvertAll(x => x.running ? "running" : "stopped"));

            string runtime = "";
#if UNITY_EDITOR
            runtime = ", OpenXR runtime: " + UnityEngine.XR.OpenXR.OpenXRRuntime.name + " " + UnityEngine.XR.OpenXR.OpenXRRuntime.version;
#endif

            return "focused=" + Application.isFocused
                + ", input=" + input
                + ", display=" + display
                + ", deviceActive=" + XRSettings.isDeviceActive
                + ", devices=" + devices.Count
                + runtime;
        }

        private static void OnDeviceConnected(InputDevice device)
        {
            UnturnedLog.info("[UnturnedVR] XR device connected: \"" + device.name + "\" [" + device.characteristics + "]");
        }

        private static void OnDeviceDisconnected(InputDevice device)
        {
            UnturnedLog.warn("[UnturnedVR] XR device disconnected: \"" + device.name + "\"");
        }
    }
}
