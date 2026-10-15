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
    /// SteamVR accepts the manifest for a game that is not launched as Steam app 304930; confirm from the log.
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
        private static readonly VRActiveActionSet_t[] activeSets = new VRActiveActionSet_t[1];
        private static readonly uint ActiveSetSize = (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t));
        private static readonly uint DigitalSize = (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t));
        private static readonly uint AnalogSize = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));

        public static bool Ready { get { return ready && !updateFailed; } }

        private static readonly ulong[] hapticHandles = new ulong[2]; // 0 = left, 1 = right
        private static readonly bool[] hapticBound = new bool[2];
        private static readonly float[] hapticCheckedAt = { -100f, -100f };
        private static readonly ulong[] originBuffer = new ulong[8];

        /// <summary>
        /// Rumble through the SteamVR haptic action. False when the action is missing or not bound to this controller
        /// (the user's SteamVR binding for the game may predate the haptic actions): the caller then uses another path.
        /// The binding state is looked up again every few seconds, so binding it in SteamVR takes effect while playing.
        /// </summary>
        public static bool TryHaptic(bool right, float seconds, float frequency, float amplitude)
        {
            int side = right ? 1 : 0;
            if (!Ready || hapticHandles[side] == 0)
            {
                if (!loggedHapticMissing[side])
                {
                    loggedHapticMissing[side] = true;
                    VRLog.Warn("SteamVR Input: no " + (right ? "right" : "left") + " haptic action handle (ready=" + Ready + "), rumble uses the XR fallback.");
                }
                return false;
            }

            // Only for the log: does SteamVR report an origin (a binding) for the action? Not used to decide, because
            // GetActionOrigins may not report output actions at all.
            if (Time.unscaledTime - hapticCheckedAt[side] > 3f)
            {
                hapticCheckedAt[side] = Time.unscaledTime;
                for (int i = 0; i < originBuffer.Length; i++)
                {
                    originBuffer[i] = 0;
                }
                EVRInputError originError = OpenVR.Input.GetActionOrigins(setHandle, hapticHandles[side], originBuffer);
                bool bound = originError == EVRInputError.None && originBuffer[0] != 0;
                if (bound != hapticBound[side] || !loggedHapticOrigins[side])
                {
                    loggedHapticOrigins[side] = true;
                    hapticBound[side] = bound;
                    VRLog.Info("SteamVR Input: " + (right ? "right" : "left") + " haptic action origins: "
                        + (bound ? "bound" : "none reported (" + originError + ")")
                        + ". If rumble never works, open SteamVR > Controller bindings > Unturned and reset to the default binding.");
                }
            }

            EVRInputError error = OpenVR.Input.TriggerHapticVibrationAction(hapticHandles[side], 0f, seconds, frequency, amplitude,
                OpenVR.k_ulInvalidInputValueHandle);
            if (error != EVRInputError.None && !loggedHapticError[side])
            {
                loggedHapticError[side] = true;
                VRLog.Warn("SteamVR Input: " + (right ? "right" : "left") + " haptic call failed (" + error + ").");
            }
            return error == EVRInputError.None;
        }

        private static readonly bool[] loggedHapticMissing = new bool[2];
        private static readonly bool[] loggedHapticOrigins = new bool[2];
        private static readonly bool[] loggedHapticError = new bool[2];

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

            activeSets[0].ulActionSet = setHandle;
            EVRInputError err = OpenVR.Input.UpdateActionState(activeSets, ActiveSetSize);
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
            if (OpenVR.Input.GetDigitalActionData(handle, ref data, DigitalSize, OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None
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
            if (OpenVR.Input.GetAnalogActionData(handle, ref data, AnalogSize, OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None
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

            for (int side = 0; side < 2; side++)
            {
                string key = (side == 0 ? "l" : "r") + "_haptic";
                ulong handle = 0;
                err = OpenVR.Input.GetActionHandle(ActionSet + "/out/" + key, ref handle);
                if (err != EVRInputError.None || handle == 0)
                {
                    VRLog.Warn("SteamVR Input: GetActionHandle(" + key + ") failed (" + err + "), rumble uses the XR fallback.");
                    continue;
                }
                hapticHandles[side] = handle;
            }

            VRLog.Info("SteamVR Input: manifest registered (" + path + "), " + handles.Count + " action handles.");
            return handles.Count > 0;
        }
    }
}
