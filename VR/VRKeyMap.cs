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
            // AllowInUi, but only while this returns true (a cursor UI that is meant to be driven from this button).
            public Func<bool> AllowInUiWhen;
            // A press that is already held when a cursor UI opens stays held while this returns true. For an item that locks
            // the cursor with its own menu while the key is down (the housing planner's T menu), so the menu it opened does
            // not release the key and close itself.
            public Func<bool> KeepHeldInUi;
            // 1 = primary (use/fire) trigger, 2 = secondary (aim) trigger, 3 = interact grip, 4 = fire mode grip: the side
            // follows the item's hand (the holding hand fires and interacts, the free hand aims and switches fire mode)
            // 5 = hold breath: the same grip as 3, but only while a scope (zoom > 2) is at the eye; it then replaces interact
            public int Role;

            public bool Held;
            public bool Down;
            public bool Up;
            public bool Latched;
            // Roles 3 / 5: whether this entry was the meaning of the grip last frame (see the latch in Refresh)
            public bool ModeActive = true;
        }

        // Layout for Valve Index. Hotbar flick on the right stick is phase 2.
        private static readonly Entry[] entries =
        {
            new Entry { Right = false, Button = VRButton.Trigger,    Key = () => ControlsSettings.primary, Role = 1 },
            new Entry { Right = true,  Button = VRButton.Trigger,    Key = () => ControlsSettings.secondary, Role = 2 },
            new Entry { Right = true,  Button = VRButton.Grip,       Key = () => ControlsSettings.interact, Role = 3 },
            new Entry { Right = false, Button = VRButton.Grip,       Key = () => ControlsSettings.firemode, Role = 4 },
            // Hold breath: the game's steady aim is the sprint key. While a scope is at the eye the gun hand's grip is that key
            // (the interact grip above is switched off for the same time); left stick click does the same as before.
            new Entry { Right = true,  Button = VRButton.Grip,       Key = () => ControlsSettings.sprint, Role = 5 },
            // The game's snap key (round to 15 degrees while turning a barricade before it is placed) on the free hand's grip,
            // next to the fire mode entry above: nothing else reads it while an item is in hand.
            new Entry { Right = false, Button = VRButton.Grip,       Key = () => ControlsSettings.snap, Role = 4 },
            new Entry { Right = true,  Button = VRButton.A,          Key = () => ControlsSettings.jump },
            // Right B is reload with a gun (or nothing) in hand, the item's own attach key (T) for any other item, the lock in a
            // vehicle, and the interact key (next page / skip) while an NPC dialogue is open. Left A (stance on foot, which
            // does nothing in a seat) is the vehicle bonus (siren, tow hook, blimp float) in a vehicle. The fire mode grip
            // stays, a passenger can hold a gun.
            new Entry { Right = true,  Button = VRButton.B,          Key = RightBKey,
                AllowInUiWhen = () => PlayerNPCDialogueUI.active,
                KeepHeldInUi = () => itemUsesAttachKey && !inVehicle },
            new Entry { Right = false, Button = VRButton.A,          Key = () => inVehicle ? ControlsSettings.other : StanceKey() },
            // Menus only: the same button rotates the item that is being dragged in the inventory (the game's rotate key, R).
            // Stance does nothing over a cursor UI, so left A is free there; the entry above stays latched while a UI is open.
            new Entry { Right = false, Button = VRButton.A,          Key = () => ControlsSettings.rotate, AllowInUi = true, UiOnly = true },
            // Menus only: the game's "other" modifier, e.g. right-click on an inventory item = quick equip/dequip/move.
            new Entry { Right = true,  Button = VRButton.Grip,       Key = () => ControlsSettings.other, AllowInUi = true, UiOnly = true },
            new Entry { Right = false, Button = VRButton.B,          Key = () => KeyCode.Escape, AllowInUi = true },
            new Entry { Right = false, Button = VRButton.StickClick, Key = () => ControlsSettings.sprint },
            new Entry { Right = true,  Button = VRButton.StickClick, Key = () => ControlsSettings.inventory, AllowInUi = true },
        };

        // Left A: the game's own stance key. PlayerStance.Update reads it as tap (< 0.33 s) = toggle crouch / stand and hold =
        // prone, the same tap / long press split as B. It only acts while crouching and proning are in Toggle mode (the
        // default): in Hold mode PlayerStance resets the wish from the crouch / prone keys every frame. There, and when the
        // stance key is unbound, fall back to the crouch key (held while A is held, no prone).
        private static KeyCode StanceKey()
        {
            bool toggles = ControlsSettings.crouching == EControlMode.TOGGLE && ControlsSettings.proning == EControlMode.TOGGLE;
            return toggles && ControlsSettings.stance != KeyCode.None ? ControlsSettings.stance : ControlsSettings.crouch;
        }

        // The game's attach key (T by default) is read by guns (attachment mode, handled by VRAttachMenu) and by other items
        // such as the housing planner (hold it to open the item menu). Guns and melee weapons keep B as reload; any other
        // equipped item gets the attach key on B.
        private static KeyCode RightBKey()
        {
            // The dialogue screen advances a page / skips on the interact key (PlayerInteract: key up while a cursor UI is open).
            if (PlayerNPCDialogueUI.active)
            {
                return ControlsSettings.interact;
            }
            if (inVehicle)
            {
                return ControlsSettings.locker;
            }
            return itemUsesAttachKey ? ControlsSettings.attach : ControlsSettings.reload;
        }

        private static int lastFrame = -1;
        private static bool itemUsesAttachKey; // an item that is not a gun or melee weapon is equipped, set per frame
        private static bool primaryRight; // the use/fire trigger is on the right controller
        private static bool interactRight = true; // the interact grip is on the right controller (fire mode on the other)
        private static bool inVehicle; // the local player sits in a vehicle (any seat, like the game's own vehicle keys), set per frame

        /// <summary>The hand whose grip interacts (and whose aim ray picks up): the holding hand, the right one when nothing is held.</summary>
        public static int InteractHand => interactRight ? VRHands.Right : VRHands.Left;

        public static bool GetKey(KeyCode key)
        {
            if (key == KeyCode.None) return false; // an unbound action is never pressed
            Refresh();
            if (VRAttachMenu.Wanted && key == ControlsSettings.attach) return true;
            foreach (Entry e in entries)
            {
                if (e.Held && e.Key() == key) return true;
            }
            return false;
        }

        public static bool GetKeyDown(KeyCode key)
        {
            if (key == KeyCode.None) return false; // an unbound action is never pressed
            Refresh();
            if (VRAttachMenu.KeyDown && key == ControlsSettings.attach) return true;
            if (VRHolsters.KeyDown(key) || VRGestures.KeyDown(key)) return true;
            foreach (Entry e in entries)
            {
                if (e.Down && e.Key() == key) return true;
            }
            return false;
        }

        public static bool GetKeyUp(KeyCode key)
        {
            if (key == KeyCode.None) return false; // an unbound action is never pressed
            Refresh();
            if (VRAttachMenu.KeyUp && key == ControlsSettings.attach) return true;
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

            Player localPlayer = Player.LocalPlayer;
            inVehicle = localPlayer != null && localPlayer.movement != null && localPlayer.movement.getVehicle() != null;
            Useable useable = localPlayer != null && localPlayer.equipment != null ? localPlayer.equipment.useable : null;
            itemUsesAttachKey = useable != null && !(useable is UseableGun) && !(useable is UseableMelee);

            VRControllerInput.Refresh();
            bool uiOpen = VRPointer.Active || (PlayerUI.window != null && PlayerUI.window.showCursor);

            // A scope with zoom above 2 at the eye: the gun hand's grip holds the breath instead of interacting. That grip is
            // then used for nothing else: not by the holster zones (no holstering when the hand is near the body), not by
            // interact (pickup, doors, vehicles), see the Role 3 / 5 entries below.
            bool holdBreathGrip = VRAds.Active && VRGun.HasSteadySight;
            int breathHand = holdBreathGrip ? (interactRight ? VRHands.Right : VRHands.Left) : -1;

            // Holster zones first: a grip press used to draw/holster is hidden from the grab logic and the game keys.
            bool leftGrip = (VRControllerInput.LeftButtons & VRButton.Grip) != 0;
            bool rightGrip = (VRControllerInput.RightButtons & VRButton.Grip) != 0;
            VRHolsters.Update(leftGrip, rightGrip, uiOpen, breathHand);
            VRHold.Update(leftGrip && !VRHolsters.Consumed(false), rightGrip && !VRHolsters.Consumed(true), !uiOpen);

            // Trigger gestures (night vision at the head, tactical with the second hand): the trigger press they use is
            // hidden from the fire/aim entries below until released.
            VRGestures.Update((VRControllerInput.LeftButtons & VRButton.Trigger) != 0,
                (VRControllerInput.RightButtons & VRButton.Trigger) != 0, uiOpen);

            // Long press of B on the gun hand = attachment menu; a tap is delivered on release (see VRAttachMenu).
            VRAttachMenu.Update((VRControllerInput.LeftButtons & VRButton.B) != 0,
                (VRControllerInput.RightButtons & VRButton.B) != 0, uiOpen);

            // Fire/use is on the trigger of the hand that holds the item (the trigger hand of a two-handed gun); the
            // other trigger is the secondary (aim). Nothing held: left fires. Only switched while no trigger is down.
            // The grips follow the same rule: the holding hand interacts (the right hand when nothing is held), the free hand
            // switches the fire mode, so a gun in the left hand behaves like one in the right. Switched only while no trigger
            // or grip is down, so a held button never changes meaning.
            if (((VRControllerInput.LeftButtons | VRControllerInput.RightButtons) & (VRButton.Trigger | VRButton.Grip)) == 0)
            {
                int itemHand = VRArms.ItemHand;
                primaryRight = itemHand >= 0 && itemHand == VRHands.Right;
                interactRight = itemHand < 0 || itemHand == VRHands.Right;
            }

            // Aiming down the sights: for a gun with a sight reference the aim "key" is the sight at the eye, not the
            // other trigger (VRAds, evaluated in VRArms once the gun is placed). The game's toggle-aim setting reacts to
            // key presses, so there it gets one press when the sight arrives and one when it leaves.
            bool toggleAim = ControlsSettings.aiming == EControlMode.TOGGLE;

            // Binoculars work the same way: the game's zoom (secondary) follows the item being held up to the eyes.
            bool gunAds = VRAds.Applies;
            bool eyeAim = gunAds || VRBinoculars.Applies;
            bool eyeActive = gunAds ? VRAds.Active : VRBinoculars.Active;
            bool eyeChanged = gunAds ? VRAds.Changed : VRBinoculars.Changed;

            foreach (Entry e in entries)
            {
                if (e.Role == 2 && eyeAim)
                {
                    bool adsHeld = !toggleAim && eyeActive;
                    bool adsDown = toggleAim ? eyeChanged : adsHeld && !e.Held;
                    e.Down = adsDown;
                    e.Up = !toggleAim && !adsHeld && e.Held;
                    e.Held = adsHeld;
                    e.Latched = false;
                    continue;
                }

                bool right;
                switch (e.Role)
                {
                    case 1: right = primaryRight; break;
                    case 2: right = !primaryRight; break;
                    case 3: right = interactRight; break;
                    case 4: right = !interactRight; break;
                    case 5: right = interactRight; break;
                    default: right = e.Right; break;
                }
                VRButton mask = right ? VRControllerInput.RightButtons : VRControllerInput.LeftButtons;
                bool raw = (mask & e.Button) != 0 && (!e.UiOnly || uiOpen)
                    && !(e.Button == VRButton.Grip && (VRHold.Consumed(right) || VRHolsters.Consumed(right)));
                if (e.Button == VRButton.Trigger && VRGestures.TriggerConsumed(right))
                {
                    raw = false;
                }
                if (e.Role == 3 || e.Role == 5)
                {
                    // The grip is either interact or hold breath, never both. A grip that is already down when the meaning
                    // switches (scope raised or lowered while squeezing) stays dead until released, so lowering the gun
                    // does not turn the breath squeeze into an interact (pickup, vehicle) and raising it does not start a hold.
                    bool active = (e.Role == 5) == holdBreathGrip;
                    if (!active)
                    {
                        raw = false;
                    }
                    else if (!e.ModeActive && raw)
                    {
                        e.Latched = true;
                    }
                    e.ModeActive = active;
                }
                if (e.Button == VRButton.B && e.Role == 0)
                {
                    int hand = e.Right ? VRHands.Right : VRHands.Left;
                    if (VRAttachMenu.ControlsB(hand))
                    {
                        raw = VRAttachMenu.ControlledBValue(hand) && (!e.UiOnly || uiOpen);
                    }
                }

                if (!raw)
                {
                    e.Latched = false;
                }
                else if (uiOpen && !e.AllowInUi && !(e.AllowInUiWhen != null && e.AllowInUiWhen())
                    && !(e.Held && e.KeepHeldInUi != null && e.KeepHeldInUi()))
                {
                    e.Latched = true;
                }

                bool held = raw && !e.Latched;
                e.Down = held && !e.Held;
                e.Up = !held && e.Held;
                e.Held = held;
            }
            VRAds.AcknowledgeChange();
            VRBinoculars.AcknowledgeChange();
            VRPerf.End(VRPerf.KeyMap, perfStart);
        }
    }
}
