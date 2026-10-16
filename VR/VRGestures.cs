using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Two trigger gestures that press game keys:
    ///   - Night vision: a hand beside the head (the ear zones of VRHolsters, left or right) and its trigger toggles the
    ///     night vision goggles (ControlsSettings.vision) when goggles are worn. That trigger press is used up, so it
    ///     never also fires the gun.
    ///   - Tactical attachment (light, laser, rangefinder, bayonet jab): while a gun that has one mounted is held with two
    ///     hands, the trigger of the hand that is NOT the gun's trigger hand presses ControlsSettings.tactical.
    ///     One-handed, or without a tactical attachment, that trigger keeps its normal job.
    ///   - Hand-held flashlight (a melee item with a light): the trigger of the hand that does NOT hold it presses the same
    ///     key, so the light can be switched on and off (that trigger would otherwise be the strong attack).
    /// The key is pressed for one frame and served through VRKeyMap, like the holster and attach keys; the game then
    /// applies its own rules (equip state, safety, network).
    /// Cost: two distance checks per hand per frame.
    /// </summary>
    internal static class VRGestures
    {
        private static readonly bool[] previousTrigger = new bool[2];
        private static readonly bool[] consumed = new bool[2];
        private static readonly int[] previousEar = { -1, -1 };
        private static readonly float[] lastAction = { -10f, -10f };
        private const float DebounceSeconds = 0.25f;
        private static KeyCode downKey = KeyCode.None;
        private static Transform cachedModel;
        private static Attachments cachedAttachments;

        /// <summary>The key a gesture presses this frame (one frame).</summary>
        public static bool KeyDown(KeyCode key)
        {
            return downKey != KeyCode.None && key == downKey;
        }

        /// <summary>A gesture used this hand's trigger press; it must not fire / aim until released.</summary>
        public static bool TriggerConsumed(bool right)
        {
            return consumed[right ? VRHands.Right : VRHands.Left];
        }

        /// <summary>Once per frame from VRKeyMap with the raw trigger states, after VRHolsters.</summary>
        public static void Update(bool leftTrigger, bool rightTrigger, bool uiOpen)
        {
            downKey = KeyCode.None;

            Player player = Player.LocalPlayer;
            PlayerEquipment equipment = player != null ? player.equipment : null;
            bool play = VRState.IsActive && VRHolsters.FrameValid && equipment != null && !uiOpen;
            bool goggles = play && player.clothing != null && player.clothing.glassesAsset != null
                && player.clothing.glassesAsset.vision != ELightingVision.NONE;
            bool tacticalReady = play && VRArms.ItemHand >= 0 && ((VRHold.Both && HasTactical(equipment)) || IsHandLight(equipment));

            for (int hand = 0; hand < 2; hand++)
            {
                bool trigger = hand == VRHands.Left ? leftTrigger : rightTrigger;
                bool down = trigger && !previousTrigger[hand];
                previousTrigger[hand] = trigger;
                if (!trigger)
                {
                    consumed[hand] = false;
                }

                // Hand beside the head: near an ear zone (only matters while goggles are worn).
                int ear = -1;
                Transform anchor = VRHands.Anchor(hand);
                if (goggles && anchor != null && VRHands.IsTracked(hand))
                {
                    float best = float.MaxValue;
                    for (int side = 0; side < 2; side++)
                    {
                        if (VRHolsters.InEar(side, anchor.position, out float score) && score < best)
                        {
                            best = score;
                            ear = side;
                        }
                    }
                }
                if (ear >= 0 && ear != previousEar[hand])
                {
                    VRHaptics.Pulse(hand, 0.18f, 0.04f, 160f); // entering the ear zone: a light tick
                }
                previousEar[hand] = ear;

                if (!down)
                {
                    continue;
                }

                // A trigger that chatters around its click point gives two presses in a few ms (seen in the log: every
                // toggle twice, which switches the light straight back off). One action per hand per quarter second.
                if (Time.unscaledTime - lastAction[hand] < DebounceSeconds)
                {
                    continue;
                }

                if (ear >= 0)
                {
                    downKey = ControlsSettings.vision;
                    consumed[hand] = true;
                    lastAction[hand] = Time.unscaledTime;
                    VRHaptics.Pulse(hand, 0.8f, 0.15f, 120f);
                    VRLog.Info("gestures: night vision toggled (" + (hand == VRHands.Left ? "left" : "right") + " hand at the "
                        + (ear == 0 ? "left" : "right") + " of the head).");
                }
                else if (tacticalReady && hand != VRArms.ItemHand)
                {
                    downKey = ControlsSettings.tactical;
                    consumed[hand] = true;
                    lastAction[hand] = Time.unscaledTime;
                    VRHaptics.Pulse(hand, 0.5f, 0.08f, 160f);
                    VRLog.Info("gestures: tactical attachment toggled with the second hand's trigger.");
                }
            }
        }

        // The equipped item is a hand-held flashlight (a melee item with a light): the game switches it with the tactical key.
        private static bool IsHandLight(PlayerEquipment equipment)
        {
            return equipment.useable is UseableMelee && equipment.asset is ItemMeleeAsset melee && melee.isLight;
        }

        // The equipped gun has a tactical attachment mounted (light, laser, rangefinder, bayonet).
        private static bool HasTactical(PlayerEquipment equipment)
        {
            if (!(equipment.useable is UseableGun))
            {
                return false;
            }

            Transform model = equipment.firstModel;
            if (model == null)
            {
                return false;
            }
            if (model != cachedModel)
            {
                cachedModel = model;
                cachedAttachments = model.GetComponent<Attachments>();
            }
            return cachedAttachments != null && cachedAttachments.tacticalAsset != null;
        }
    }
}
