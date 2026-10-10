using System;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// M5: controller buttons as virtual keys. Each entry ties one button to a KeyCode taken from
    /// ControlsSettings at query time (so player rebinds keep working). Held/down/up are computed once
    /// per frame, so GetKeyDown is true for exactly one frame for every reader.
    /// Patches/VRInputPatches ORs this into InputEx.GetKey/GetKeyDown/GetKeyUp.
    /// While a cursor UI is open only entries marked AllowInUi act (pause, inventory), so the right
    /// trigger clicks the UI instead of firing, jumping does not happen behind a menu, etc. A button
    /// held when it becomes suppressed stays dead until released, so a UI click never turns into a shot.
    /// </summary>
    internal static class VRKeyMap
    {
        private sealed class Entry
        {
            public bool Right;
            public VRButton Button;
            public Func<KeyCode> Key;
            public bool AllowInUi;
            public bool UiOnly;
            public int Role; // 1 = primary (use/fire) trigger, 2 = secondary (aim) trigger: the side follows the item's hand

            public bool Held;
            public bool Down;
            public bool Up;
            public bool Latched;
        }

        // Layout for Valve Index (see docs/progress.md). Hotbar flick on the right stick is phase 2.
        private static readonly Entry[] entries =
        {
            new Entry { Right = false, Button = VRButton.Trigger,    Key = () => ControlsSettings.primary, Role = 1 },
            new Entry { Right = true,  Button = VRButton.Trigger,    Key = () => ControlsSettings.secondary, Role = 2 },
            new Entry { Right = true,  Button = VRButton.Grip,       Key = () => ControlsSettings.interact },
            new Entry { Right = false, Button = VRButton.Grip,       Key = () => ControlsSettings.firemode },
            new Entry { Right = true,  Button = VRButton.A,          Key = () => ControlsSettings.jump },
            new Entry { Right = true,  Button = VRButton.B,          Key = () => ControlsSettings.reload },
            new Entry { Right = false, Button = VRButton.A,          Key = () => ControlsSettings.crouch },
            // Menus only: the game's "other" modifier, e.g. right-click on an inventory item = quick equip/dequip/move.
            new Entry { Right = true,  Button = VRButton.Grip,       Key = () => ControlsSettings.other, AllowInUi = true, UiOnly = true },
            new Entry { Right = false, Button = VRButton.B,          Key = () => KeyCode.Escape, AllowInUi = true },
            new Entry { Right = false, Button = VRButton.StickClick, Key = () => ControlsSettings.sprint },
            new Entry { Right = true,  Button = VRButton.StickClick, Key = () => ControlsSettings.inventory, AllowInUi = true },
        };

        private static int lastFrame = -1;
        private static bool primaryRight; // the use/fire trigger is on the right controller

        public static bool GetKey(KeyCode key)
        {
            Refresh();
            foreach (Entry e in entries)
            {
                if (e.Held && e.Key() == key) return true;
            }
            return false;
        }

        public static bool GetKeyDown(KeyCode key)
        {
            Refresh();
            foreach (Entry e in entries)
            {
                if (e.Down && e.Key() == key) return true;
            }
            return false;
        }

        public static bool GetKeyUp(KeyCode key)
        {
            Refresh();
            foreach (Entry e in entries)
            {
                if (e.Up && e.Key() == key) return true;
            }
            return false;
        }

        private static void Refresh()
        {
            if (lastFrame == Time.frameCount)
            {
                return;
            }
            lastFrame = Time.frameCount;
            long perfStart = VRPerf.Begin();

            VRControllerInput.Refresh();
            bool uiOpen = VRPointer.Active || (PlayerUI.window != null && PlayerUI.window.showCursor);

            VRHold.Update((VRControllerInput.LeftButtons & VRButton.Grip) != 0,
                (VRControllerInput.RightButtons & VRButton.Grip) != 0, !uiOpen);

            // Fire/use is on the trigger of the hand that holds the item (the trigger hand of a two-handed gun); the
            // other trigger is the secondary (aim). Nothing held: left fires. Only switched while no trigger is down.
            if (((VRControllerInput.LeftButtons | VRControllerInput.RightButtons) & VRButton.Trigger) == 0)
            {
                int itemHand = VRArms.ItemHand;
                primaryRight = itemHand >= 0 && itemHand == VRHands.Right;
            }

            foreach (Entry e in entries)
            {
                bool right = e.Role == 0 ? e.Right : (e.Role == 1) == primaryRight;
                VRButton mask = right ? VRControllerInput.RightButtons : VRControllerInput.LeftButtons;
                bool raw = (mask & e.Button) != 0 && (!e.UiOnly || uiOpen)
                    && !(e.Button == VRButton.Grip && VRHold.Consumed(e.Right));

                if (!raw)
                {
                    e.Latched = false;
                }
                else if (uiOpen && !e.AllowInUi)
                {
                    e.Latched = true;
                }

                bool held = raw && !e.Latched;
                e.Down = held && !e.Held;
                e.Up = !held && e.Held;
                e.Held = held;
            }
            VRPerf.End(VRPerf.KeyMap, perfStart);
        }
    }
}
