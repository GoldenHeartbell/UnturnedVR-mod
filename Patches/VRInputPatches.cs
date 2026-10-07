using System;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// M5: while the VR pointer is active, UnityEngine.Input reports the laser as the mouse (position,
    /// left button = trigger, scroll = right stick Y). Applied manually instead of with [HarmonyPatch]
    /// attributes: these are engine members (possibly extern/inlined), and one failing patch must not
    /// take down PatchAll for the rest of the mod. The log shows which ones took and when each is first used.
    /// GUESS: Harmony can patch these UnityEngine.Input members in the shipped Mono runtime.
    /// </summary>
    internal static class VRInputPatches
    {
        private static bool loggedPosition;
        private static bool loggedRightButton;
        private static bool loggedButton;
        private static bool loggedScroll;
        private static bool loggedKey;

        public static void Apply(Harmony harmony)
        {
            // Controller buttons as keys: InputEx is the managed choke point for every keybind the game reads
            // (the extern UnityEngine.Input.GetKey* cannot be patched here, see below).
            Patch(harmony, AccessTools.Method(typeof(InputEx), nameof(InputEx.GetKey), new[] { typeof(KeyCode) }), nameof(InputExGetKeyPostfix));
            Patch(harmony, AccessTools.Method(typeof(InputEx), nameof(InputEx.GetKeyDown), new[] { typeof(KeyCode) }), nameof(InputExGetKeyDownPostfix));
            Patch(harmony, AccessTools.Method(typeof(InputEx), nameof(InputEx.GetKeyUp), new[] { typeof(KeyCode) }), nameof(InputExGetKeyUpPostfix));

            Patch(harmony, AccessTools.PropertyGetter(typeof(Input), nameof(Input.mousePosition)), nameof(MousePositionPostfix));
            Patch(harmony, AccessTools.PropertyGetter(typeof(Input), nameof(Input.mouseScrollDelta)), nameof(MouseScrollDeltaPostfix));

            // Input.GetMouseButton* are extern methods returning bool: Harmony fails on them in this
            // runtime ("Invalid IL code", seen in the editor log). The UI's input module reads buttons
            // through the managed BaseInput wrappers instead, which patch fine. Looked up by name so the
            // module needs no UnityEngine.UI reference.
            Type baseInput = AccessTools.TypeByName("UnityEngine.EventSystems.BaseInput");
            if (baseInput == null)
            {
                VRLog.Warn("input patch skipped, UnityEngine.EventSystems.BaseInput not found: trigger clicks will not work.");
                return;
            }
            Patch(harmony, AccessTools.Method(baseInput, "GetMouseButton", new[] { typeof(int) }), nameof(GetMouseButtonPostfix));
            Patch(harmony, AccessTools.Method(baseInput, "GetMouseButtonDown", new[] { typeof(int) }), nameof(GetMouseButtonDownPostfix));
            Patch(harmony, AccessTools.Method(baseInput, "GetMouseButtonUp", new[] { typeof(int) }), nameof(GetMouseButtonUpPostfix));
        }

        private static void Patch(Harmony harmony, MethodBase original, string postfix)
        {
            if (original == null)
            {
                VRLog.Warn("input patch skipped, member for " + postfix + " not found.");
                return;
            }

            try
            {
                harmony.Patch(original, postfix: new HarmonyMethod(typeof(VRInputPatches), postfix));
                VRLog.Info("patched " + original.DeclaringType.Name + "." + original.Name + ".");
            }
            catch (Exception e)
            {
                VRLog.Warn("input patch FAILED for " + original.Name + ": " + e.Message);
            }
        }

        // Same guards as InputEx itself: no controller key while typing in a text field or over a UI element.
        private static bool KeyGuardsPass()
        {
            return !MenuConfigurationControlsUI.ShouldGameIgnoreInput && Glazier.Get().ShouldGameProcessKeyDown;
        }

        private static void InputExGetKeyPostfix(KeyCode key, ref bool __result)
        {
            if (!__result && VR.VRState.IsActive && VR.VRKeyMap.GetKey(key) && KeyGuardsPass())
            {
                __result = true;
                LogKeyOnce(key);
            }
        }

        private static void InputExGetKeyDownPostfix(KeyCode key, ref bool __result)
        {
            if (!__result && VR.VRState.IsActive && VR.VRKeyMap.GetKeyDown(key) && KeyGuardsPass())
            {
                __result = true;
                LogKeyOnce(key);
            }
        }

        private static void InputExGetKeyUpPostfix(KeyCode key, ref bool __result)
        {
            if (!__result && VR.VRState.IsActive && VR.VRKeyMap.GetKeyUp(key) && KeyGuardsPass())
            {
                __result = true;
            }
        }

        private static void LogKeyOnce(KeyCode key)
        {
            if (!loggedKey)
            {
                loggedKey = true;
                VRLog.Info("InputEx is being served by the VR key map (first key: " + key + ").");
            }
        }

        private static void MousePositionPostfix(ref Vector3 __result)
        {
            if (!VR.VRPointer.Active)
            {
                return;
            }
            __result = VR.VRPointer.MousePosition;
            if (!loggedPosition)
            {
                loggedPosition = true;
                VRLog.Info("Input.mousePosition is being served by the VR pointer.");
            }
        }

        private static void MouseScrollDeltaPostfix(ref Vector2 __result)
        {
            if (!VR.VRPointer.Active)
            {
                return;
            }
            __result = VR.VRPointer.ScrollDelta;
            if (!loggedScroll && __result != Vector2.zero)
            {
                loggedScroll = true;
                VRLog.Info("Input.mouseScrollDelta is being served by the VR pointer.");
            }
        }

        private static void GetMouseButtonPostfix(int button, ref bool __result)
        {
            if (VR.VRPointer.Active && button <= 1)
            {
                __result = button == 0 ? VR.VRPointer.ButtonHeld : VR.VRPointer.RightButtonHeld;
            }
        }

        private static void GetMouseButtonDownPostfix(int button, ref bool __result)
        {
            if (VR.VRPointer.Active && button <= 1)
            {
                __result = button == 0 ? VR.VRPointer.ButtonDown : VR.VRPointer.RightButtonDown;
                if (button == 1 && __result && !loggedRightButton)
                {
                    loggedRightButton = true;
                    VRLog.Info("BaseInput.GetMouseButtonDown(1) is being served by the VR pointer (right click).");
                }
                if (button == 0 && __result && !loggedButton)
                {
                    loggedButton = true;
                    VRLog.Info("BaseInput.GetMouseButtonDown(0) is being served by the VR pointer.");
                }
            }
        }

        private static void GetMouseButtonUpPostfix(int button, ref bool __result)
        {
            if (VR.VRPointer.Active && button <= 1)
            {
                __result = button == 0 ? VR.VRPointer.ButtonUp : VR.VRPointer.RightButtonUp;
            }
        }
    }
}
