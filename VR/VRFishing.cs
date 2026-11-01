using HarmonyLib;
using SDG.Unturned;
using System.Reflection;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Physical fishing cast: a fishing rod is cast by throwing it, like a real rod. The trigger no longer charges a cast; it
    /// still reels in, takes the bite and plays the catch challenge (the game's own use of primary once the line is out).
    ///
    /// The game (SDK UseableFisher): in Idle, startPrimary opens the strength gauge (PreparingToCast), stopPrimary casts: the
    /// cast anim plays and at 45% of it the client spawns the bobber 1 m in front of player.look.aim and pushes it along the aim
    /// with a force of 500 to 1000 by strengthMultiplier (0..1, from the gauge). The bobber is the client's own; the server only
    /// hears in which water it landed. So none of this needs the server.
    ///
    /// The gesture, per cast:
    ///   - Ready: the rod has pointed forward (or anywhere but back) since the last cast.
    ///   - Charging: the rod points backwards (its tip behind the head, over the shoulder) for BackHoldSeconds while the game is
    ///     idle and not busy. Primary is held (the game opens its gauge); the gauge shows the throw speed so far.
    ///   - Throw: the rod comes forward past ReleaseDot. Primary is let go (the game casts). Strength = how fast the hand (the
    ///     controller, not the rod: a long rod turns a wrist flick into 15-40 m/s at a metre up) moved forward during the throw, held for two frames (rig space: walking and vehicles do not count). The cast leaves the hand
    ///     at once (UseableFisherCastPatch: no wait for the anim), heading the way the hand moved during the throw (its flat
    ///     motion while going forward, summed, so the faster part counts most; the rod's own direction pointed 30-50 degrees
    ///     to the side of a straight throw), CastElevation up.
    ///   - Yank (VRSettings.FishingYank): with the line out, a fish on the hook (the game's catch window, the same time the
    ///     trigger would take it) and the hand jerked back and up fast for two frames, primary is pressed for one frame. The
    ///     game then starts the catch challenge (still played with the trigger) or reels the catch in. Outside the window a
    ///     yank does nothing, so moving the rod around never reels in or loses a fish.
    /// All numbers are GUESSES, to be tuned in the headset. Called from VRArms once the item is in its final pose.
    /// </summary>
    internal static class VRFishing
    {
        private const float BackDot = -0.25f; // rod axis . view heading below this = pointing backwards (about 15 degrees past upright)
        private const float BackMinUp = -0.3f; // and not hanging down behind the player
        private const float BackHoldSeconds = 0.1f;
        private const float ReleaseDot = 0.3f; // rod axis . heading at the charge above this = thrown forward (about 17 degrees past upright)
        private const float MinThrowSpeed = 1.0f; // m/s forward, hand: below this the cast has no strength (a lazy flop)
        private const float FullThrowSpeed = 8f; // m/s forward, hand: from this the cast has full strength
        private const float CastElevation = 30f; // degrees above the horizon the bobber leaves at
        private const float StartTimeout = 0.5f; // seconds the game may take to open its gauge after the press
        private const float PendingMaxSeconds = 2f;
        private const float YankSpeed = 6f; // m/s, hand up and back (both parts together) in rig space, slower of two frames: a yank
        private const float YankCooldown = 0.5f; // seconds after a yank (or an ignored one) before the next counts
        // UseableFisher's WARNING_DURATION and CATCH_WINDOW (private consts): the fish is on the hook from 1 s after the
        // notification for 1.4 s. A press in the warning (bubbles) reels in empty, so the yank only presses inside.
        private const float CatchWindowStart = 1.0f;
        private const float CatchWindowEnd = 1.0f + 1.4f;

        // UseableFisher.EFishingState (private): Idle, PreparingToCast, LineDeployed, CatchChallenge.
        public const int StateIdle = 0;
        public const int StatePreparing = 1;
        public const int StateLineDeployed = 2;

        private enum Phase { Waiting, Ready, Back, Charging }

        private static readonly FieldInfo stateField = AccessTools.Field(typeof(UseableFisher), "fishingState");
        private static readonly FieldInfo notificationField = AccessTools.Field(typeof(UseableFisher), "timeSinceFishNotification");

        private static Phase phase = Phase.Waiting;
        private static float phaseSince;
        private static Vector3 heading; // flat view heading frozen at the start of the charge
        private static Vector3 lastHandPosition; // rig space
        private static bool hasLastHandPosition;
        private static float peakSpeed;
        private static float lastForwardSpeed;
        private static Vector3 throwMotion; // world, flat: the hand's motion summed over the frames it went forward while charging
        private static int hand = -1;

        private static bool pending;
        private static float pendingSince;
        private static Vector3 pendingOrigin;
        private static Quaternion pendingRotation;

        private static Vector3 yankLastHand; // rig space
        private static bool hasYankLastHand;
        private static float yankLastSpeed;
        private static float yankSince = -999f;
        private static int pressFrame = -1;

        /// <summary>The physical cast replaces the trigger's charge this frame: VRKeyMap must not press primary from the trigger.</summary>
        public static bool TakesTrigger { get; private set; }

        /// <summary>The cast strength (0..1) the game's gauge and bobber force use while charging and for the cast just thrown.</summary>
        public static float Strength { get; private set; }

        /// <summary>Primary held by the gesture (charging), or pressed for one frame by a yank.</summary>
        public static bool KeyHeld(KeyCode key)
        {
            // Called for every InputEx.GetKey query: the key compare first (nearly every query is another key), the frame count last.
            return key != KeyCode.None && key == ControlsSettings.primary && (phase == Phase.Charging || pressFrame == Time.frameCount);
        }

        /// <summary>The local player's rod is driven by the gesture: its gauge and strength come from <see cref="Strength"/>.</summary>
        public static bool DrivesStrength(UseableFisher fisher)
        {
            return TakesTrigger && IsLocal(fisher);
        }

        /// <summary>A thrown cast waits for the game to spawn its bobber (UseableFisherCastPatch spawns it now, from the hand).</summary>
        public static bool HasPendingCast(UseableFisher fisher)
        {
            return pending && IsLocal(fisher);
        }

        /// <summary>Where the bobber leaves from and the way it flies (for player.look.aim during the spawn).</summary>
        public static void GetCastRay(out Vector3 origin, out Quaternion rotation)
        {
            origin = pendingOrigin;
            rotation = pendingRotation;
        }

        /// <summary>The game spawned the bobber of the pending cast.</summary>
        public static void CastSpawned()
        {
            if (pending)
            {
                pending = false;
                VRLog.Info("fishing: bobber cast " + (Time.time - pendingSince).ToString("0.00") + " s after the throw.");
            }
        }

        public static int GetState(UseableFisher fisher)
        {
            return stateField != null && fisher != null ? (int)stateField.GetValue(fisher) : -1;
        }

        private static bool IsLocal(UseableFisher fisher)
        {
            return fisher != null && fisher.channel != null && fisher.channel.IsLocalPlayer;
        }

        /// <summary>Once per frame from VRArms, after the equipped item has been posed.</summary>
        public static void Update()
        {
            Player player = Player.LocalPlayer;
            PlayerEquipment equipment = player != null ? player.equipment : null;
            UseableFisher fisher = equipment != null ? equipment.useable as UseableFisher : null;
            if (fisher == null)
            {
                // No rod in hand (nearly every frame): nothing to track, just leave the gesture reset.
                TakesTrigger = false;
                pending = false;
                phase = Phase.Waiting;
                hasLastHandPosition = false;
                return;
            }
            Transform rig = VRRig.RigTransform;
            Camera eye = VRRig.ActiveCamera;
            bool uiOpen = VRPointer.Active || (PlayerUI.window != null && PlayerUI.window.showCursor);
            int itemHand = VRArms.ItemHand;
            Transform anchor = itemHand >= 0 ? VRHands.Anchor(itemHand) : null;
            bool active = VRState.IsActive && rig != null && eye != null && !uiOpen && stateField != null
                && anchor != null && VRHands.IsTracked(itemHand) && VRArms.ItemTipFrame == Time.frameCount;

            if (pending && Time.time - pendingSince > PendingMaxSeconds)
            {
                pending = false;
                VRLog.Info("fishing: the game did not cast the thrown bobber, dropped.");
            }

            int state = active ? GetState(fisher) : -1;
            TakesTrigger = active && (state == StateIdle || state == StatePreparing);
            if (!TakesTrigger)
            {
                if (phase == Phase.Charging)
                {
                    VRLog.Info("fishing: charge given up (rod not tracked, menu open or the game left the cast).");
                }
                phase = Phase.Waiting;
                hasLastHandPosition = false;
                if (active && state == StateLineDeployed && VRSettings.FishingYank)
                {
                    UpdateYank(fisher, equipment, rig, anchor, eye);
                }
                else
                {
                    hasYankLastHand = false;
                }
                return;
            }
            hasYankLastHand = false;

            Vector3 axis = VRArms.ItemTipAxis;
            Vector3 view = eye.transform.forward;
            Vector3 flatView = new Vector3(view.x, 0f, view.z);
            if (flatView.sqrMagnitude < 1e-4f)
            {
                flatView = eye.transform.up * -Mathf.Sign(view.y); // looking straight down or up: the top of the head points the way
                flatView.y = 0f;
            }
            flatView.Normalize();
            bool back = Vector3.Dot(axis, flatView) < BackDot && axis.y > BackMinUp;
            bool busy = equipment.isBusy;

            Vector3 handPosition = rig.InverseTransformPoint(anchor.position);
            float forwardSpeed = 0f;
            Vector3 velocity = Vector3.zero;
            if (hasLastHandPosition && Time.deltaTime > 1e-5f)
            {
                velocity = rig.TransformVector(handPosition - lastHandPosition) / Time.deltaTime;
                forwardSpeed = Vector3.Dot(velocity, phase == Phase.Charging ? heading : flatView);
            }
            lastHandPosition = handPosition;
            hasLastHandPosition = true;

            switch (phase)
            {
                case Phase.Waiting:
                    // After a cast (or on equip) the rod must come out of the back pose first, so one throw is one cast.
                    if (!back && state == StateIdle)
                    {
                        phase = Phase.Ready;
                    }
                    break;
                case Phase.Ready:
                    if (back && state == StateIdle && !busy)
                    {
                        phase = Phase.Back;
                        phaseSince = Time.time;
                    }
                    break;
                case Phase.Back:
                    if (!back || state != StateIdle || busy)
                    {
                        phase = Phase.Ready;
                    }
                    else if (Time.time - phaseSince >= BackHoldSeconds)
                    {
                        phase = Phase.Charging;
                        phaseSince = Time.time;
                        heading = flatView;
                        peakSpeed = 0f;
                        lastForwardSpeed = 0f;
                        throwMotion = Vector3.zero;
                        Strength = 0f;
                        hand = itemHand;
                        VRHaptics.Pulse(hand, 0.3f, 0.04f, 120f);
                        VRLog.Info("fishing: rod back, charging the cast.");
                    }
                    break;
                case Phase.Charging:
                    if (state == StateIdle && Time.time - phaseSince > StartTimeout)
                    {
                        phase = Phase.Waiting;
                        VRLog.Info("fishing: the game did not open the cast gauge, charge given up.");
                        break;
                    }
                    // The slower of two frames: a one-frame tracking jump (seen in melee as 30-90 m/s tip peaks) is no throw.
                    peakSpeed = Mathf.Max(peakSpeed, Mathf.Min(forwardSpeed, lastForwardSpeed));
                    lastForwardSpeed = forwardSpeed;
                    if (forwardSpeed > 0f)
                    {
                        throwMotion += new Vector3(velocity.x, 0f, velocity.z) * Time.deltaTime;
                    }
                    Strength = Mathf.InverseLerp(MinThrowSpeed, FullThrowSpeed, peakSpeed);
                    if (state == StatePreparing && Vector3.Dot(axis, heading) > ReleaseDot)
                    {
                        Throw(anchor.position, axis);
                    }
                    break;
            }
        }

        /// <summary>Line out: a fast jerk of the hand back and up presses primary once, but only while a fish is on the hook.</summary>
        private static void UpdateYank(UseableFisher fisher, PlayerEquipment equipment, Transform rig, Transform anchor, Camera eye)
        {
            Vector3 handPosition = rig.InverseTransformPoint(anchor.position);
            float speed = 0f;
            if (hasYankLastHand && Time.deltaTime > 1e-5f)
            {
                Vector3 view = eye.transform.forward;
                Vector3 flatView = new Vector3(view.x, 0f, view.z);
                flatView = flatView.sqrMagnitude > 1e-4f ? flatView.normalized : Vector3.zero;
                // Up and back towards the player both count, together (a yank is mostly up: on a 45-degree line its upward
                // part was cut, so it took a long pull back). Down and forward do not.
                Vector3 velocity = rig.TransformVector(handPosition - yankLastHand) / Time.deltaTime;
                float up = Mathf.Max(0f, velocity.y);
                float back = Mathf.Max(0f, -Vector3.Dot(velocity, flatView));
                speed = Mathf.Sqrt(up * up + back * back);
            }
            yankLastHand = handPosition;
            hasYankLastHand = true;
            float twoFrames = Mathf.Min(speed, yankLastSpeed); // a one-frame tracking jump is no yank
            yankLastSpeed = speed;
            if (twoFrames < YankSpeed || Time.time - yankSince < YankCooldown || notificationField == null)
            {
                return;
            }
            yankSince = Time.time;
            float sinceBite = (float)notificationField.GetValue(fisher);
            if (sinceBite < CatchWindowStart || sinceBite > CatchWindowEnd || equipment.isBusy)
            {
                VRLog.Info("fishing: yank ignored (" + twoFrames.ToString("0.0") + " m/s), no fish on the hook"
                    + (sinceBite < CatchWindowStart ? " yet (bubbles)" : "") + ".");
                return;
            }
            pressFrame = Time.frameCount + 1; // read by the game's Update next frame (this runs in LateUpdate)
            int itemHand = VRArms.ItemHand;
            VRHaptics.Pulse(itemHand >= 0 ? itemHand : VRHands.Right, 0.8f, 0.08f, 160f);
            VRLog.Info("fishing: yanked, hand " + twoFrames.ToString("0.0") + " m/s up and back (from " + YankSpeed.ToString("0.0")
                + " m/s), " + (sinceBite - CatchWindowStart).ToString("0.00") + " s into the catch window.");
        }

        private static void Throw(Vector3 handPosition, Vector3 axis)
        {
            // The hand's throw; a throw too short to have a direction (a few cm) falls back to the rod's direction.
            Vector3 flat = throwMotion.sqrMagnitude > 0.05f * 0.05f ? throwMotion : new Vector3(axis.x, 0f, axis.z);
            Vector3 direction = flat.sqrMagnitude > 1e-4f ? flat.normalized : heading;
            direction = Quaternion.AngleAxis(-CastElevation, Vector3.Cross(Vector3.up, direction)) * direction;
            pending = true;
            pendingSince = Time.time;
            pendingOrigin = handPosition;
            pendingRotation = Quaternion.LookRotation(direction);
            phase = Phase.Waiting; // primary is let go: the game casts on its next input tick
            VRHaptics.Pulse(hand, Mathf.Lerp(0.4f, 1f, Strength), 0.08f, 160f);
            VRLog.Info("fishing: thrown, hand forward peak " + peakSpeed.ToString("0.0") + " m/s, strength " + Strength.ToString("0.00")
                + " (full from " + FullThrowSpeed.ToString("0.0") + " m/s), heading " + Vector3.SignedAngle(heading, direction, Vector3.up).ToString("0")
                + " degrees from the charge view (" + (throwMotion.sqrMagnitude > 0.05f * 0.05f ? "hand throw" : "rod, throw too short")
                + "; the rod pointed " + Vector3.SignedAngle(heading, new Vector3(axis.x, 0f, axis.z), Vector3.up).ToString("0") + ").");
        }
    }
}
