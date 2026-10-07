using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace UnturnedVR.VR
{
    /// <summary>
    /// SteamVR Input (action manifest + bindings) for the buttons and sticks. Registered at runtime from
    /// StreamingAssets\SteamVR\actions.json (NOT through OpenVRSettings.asset, which crashed the game when edited wrongly).
    /// Why: the legacy GetControllerState read gets SteamVR's default Index bindings, where a fully deflected
    /// thumbstick counts as a click. With our own bindings the thumbstick "click" is the physical click.
    /// Every read is per action: if an action is not active (no binding for that controller type) the caller keeps
    /// its XR / legacy value, so a missing binding or a failed registration never removes input.
    /// GUESS: SteamVR accepts the manifest for a game that is not launched as Steam app 304930; confirm from the log.
    /// </summary>
    internal static class VRSteamInput
    {
        private const string ActionSet = "/actions/unturnedvr";

        private static bool initTried;
        private static bool ready;
        private static ulong setHandle;
        private static readonly Dictionary<string, ulong> handles = new Dictionary<string, ulong>();
        private static readonly HashSet<string> loggedActive = new HashSet<string>();
        private static int lastUpdateFrame = -1;
        private static bool updateFailed;

        public static bool Ready { get { return ready && !updateFailed; } }

        /// <summary>Registers the manifest once (first call after OpenVR is up) and refreshes the action state once per frame.</summary>
        public static void Update()
        {
            if (!initTried)
            {
                if (OpenVR.Input == null || OpenVR.System == null)
                {
                    return;
                }
                initTried = true;
                ready = TryInit();
            }
            if (!ready || updateFailed || lastUpdateFrame == Time.frameCount)
            {
                return;
            }
            lastUpdateFrame = Time.frameCount;

            VRActiveActionSet_t[] sets = { new VRActiveActionSet_t { ulActionSet = setHandle, nPriority = 0 } };
            EVRInputError err = OpenVR.Input.UpdateActionState(sets, (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t)));
            if (err != EVRInputError.None)
            {
                updateFailed = true;
                VRLog.Warn("SteamVR Input: UpdateActionState failed (" + err + "), falling back to XR / legacy input.");
            }
        }

        /// <summary>The state of a boolean action, or false when it has no active binding (caller keeps its own value).</summary>
        public static bool TryGetBool(string action, out bool state)
        {
            state = false;
            ulong handle;
            if (!Ready || !handles.TryGetValue(action, out handle))
            {
                return false;
            }
            InputDigitalActionData_t data = default;
            if (OpenVR.Input.GetDigitalActionData(handle, ref data, (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t)), OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None
                || !data.bActive)
            {
                return false;
            }
            LogFirstActive(action);
            state = data.bState;
            return true;
        }

        /// <summary>The value of a vector2 action, or false when it has no active binding.</summary>
        public static bool TryGetVector2(string action, out Vector2 value)
        {
            value = Vector2.zero;
            ulong handle;
            if (!Ready || !handles.TryGetValue(action, out handle))
            {
                return false;
            }
            InputAnalogActionData_t data = default;
            if (OpenVR.Input.GetAnalogActionData(handle, ref data, (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t)), OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None
                || !data.bActive)
            {
                return false;
            }
            LogFirstActive(action);
            value = new Vector2(data.x, data.y);
            return true;
        }

        private static void LogFirstActive(string action)
        {
            if (loggedActive.Add(action))
            {
                VRLog.Info("SteamVR Input: action '" + action + "' is active (bound).");
            }
        }

        private static bool TryInit()
        {
            string path = Path.Combine(Application.streamingAssetsPath, "SteamVR" + Path.DirectorySeparatorChar + "actions.json").Replace('/', '\\');
            if (!File.Exists(path))
            {
                VRLog.Warn("SteamVR Input: " + path + " not found, using XR / legacy input.");
                return false;
            }

            EVRInputError err = OpenVR.Input.SetActionManifestPath(path);
            if (err != EVRInputError.None)
            {
                VRLog.Warn("SteamVR Input: SetActionManifestPath failed (" + err + "), using XR / legacy input.");
                return false;
            }

            err = OpenVR.Input.GetActionSetHandle(ActionSet, ref setHandle);
            if (err != EVRInputError.None || setHandle == 0)
            {
                VRLog.Warn("SteamVR Input: GetActionSetHandle failed (" + err + "), using XR / legacy input.");
                return false;
            }

            foreach (string hand in new[] { "l", "r" })
            {
                foreach (string name in new[] { "trigger", "grip", "a", "b", "stickclick", "stick" })
                {
                    string key = hand + "_" + name;
                    ulong handle = 0;
                    err = OpenVR.Input.GetActionHandle(ActionSet + "/in/" + key, ref handle);
                    if (err != EVRInputError.None || handle == 0)
                    {
                        VRLog.Warn("SteamVR Input: GetActionHandle(" + key + ") failed (" + err + ").");
                        continue;
                    }
                    handles[key] = handle;
                }
            }

            VRLog.Info("SteamVR Input: manifest registered (" + path + "), " + handles.Count + " action handles.");
            return handles.Count > 0;
        }
    }
}
