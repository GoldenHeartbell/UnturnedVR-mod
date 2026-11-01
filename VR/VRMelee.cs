using HarmonyLib;
using SDG.Unturned;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Physical melee (plan: docs/plan-physical-melee.md): a real swing of a one-shot melee weapon (knife, bat, axe; not a
    /// chainsaw or blowtorch, ItemMeleeAsset.isRepeated), or a real punch with an empty hand, is the attack.
    ///
    /// The triggers no longer attack. When a swing enters a target, the game's attack key is pressed for one frame (weapon:
    /// primary, or secondary = the strong attack for a hard swing when the stamina allows it; fists: primary for the left punch,
    /// secondary for the right one, as the game maps them), and the game's own ray
    /// is started just before the contact point (UseableMeleeHitPatch for weapons, PlayerEquipmentPunchPatch for fists). So
    /// the game's raycast, limb (Skull = headshot), damage, effects and the server's checks all apply as usual. The game's
    /// cooldown still holds: a contact while it is not ready is dropped.
    /// Timing: a punch hits on the press anyway. A weapon hits at once in singleplayer (InstantHits); online it hits at the
    /// game's own hit frame partway into the anim, because the server only takes the hit then. The contact point is kept
    /// relative to the collider it entered, so the hit stays on that limb while the zombie moves.
    ///
    /// The "real arm swing" rule, per weapon or per fist (Swinger):
    ///   - Armed: the tip slowed down or turned back for a frame (the end of a follow-through, the turn of a wind-up) and
    ///     the blade touches no zombie, animal or player. A blade resting inside a zombie can never re-arm.
    ///   - Swinging: from Armed, the tip moves fast for two frames and not back towards the head (a punch: clearly away from
    ///     it), so pulling the hand back after a hit never hits again. Stab = moving along the blade (tip first), else slash.
    ///     A punch is always a stab of the knuckles.
    ///   - Contact: only while Swinging, rays from each sample point's last position to its new one (stab: the tip only, plus
    ///     the blade itself). A ray never hits a collider it starts inside, so only entering one counts. Else the flat game's
    ///     reach: the line from the head through the tip, out to the weapon's range (fists: the game's 1.75 m), swept with
    ///     the swing, so a zombie does not outreach the player.
    ///   - Spent: after one contact, or when the swing slows without one. Only the Armed rule leads out of it.
    /// All numbers are GUESSES, to be tuned in the headset. Called from VRArms once the item and hands are in their final pose.
    /// </summary>
    internal static class VRMelee
    {
        private const float RearmSpeed = 1.0f; // m/s, tip slower than this (one frame) makes a new swing possible
        private const float TurnBackAngle = 100f; // degrees: the tip reversing between two frames also does
        private const float SwingSpeed = 2.0f; // m/s, tip faster than this (two frames) starts a swing
        private const int SwingFrames = 2;
        private const float SwingMaxSeconds = 0.6f;
        private const float StabMaxAngle = 35f; // degrees between the tip's motion and the blade axis
        private const float PunchMinOutward = 0.5f; // a punch starts only with the knuckles moving clearly away from the head (cos 60 degrees)
        private const float PullOutMaxAngle = 45f; // degrees: a weapon moving this close to backwards along its blade is pulled out, not swung
        private const float BladeRadius = 0.03f;
        private const float BaseFraction = 0.25f; // base sample: this far from the hand towards the tip
        private const float SymmetricEnds = 0.75f; // the shorter end within this fraction of the longer: the head check decides
        private const float RayBackOff = 0.25f; // the game's ray starts this far before the contact point, along the swing
        private const float PendingMaxSeconds = 0.3f; // the game must hit within this time after the press, else it is dropped
        private const float PendingMaxSecondsOnline = 2f; // online a weapon hits at the game's own hit frame, partway into the anim
        // A weapon swing whose hand (the controller, not the tip: a long blade turns a wrist flick into tip speed, and the tip
        // showed one-frame peaks of 30-90 m/s) peaked at this speed or more is the game's strong attack (secondary: more damage,
        // uses stamina, longer cooldown), when the player has the stamina for it; else the weak one (primary). GUESS, tune in the headset.
        private const float StrongSpeed = 8f;

        /// <summary>
        /// Every one-shot melee swing of the local player hits on its first input tick (UseableMeleeDamageablePatch), not partway
        /// into the anim. Only where the server does the same: singleplayer (one process), or a server of protocol 6 or later, which
        /// does it for players whose pose carries the instant-melee bit (Net/UseableMeleeServerInstantPatch; this client always sets
        /// it while it sends poses). A server runs the swing on the same input-tick clock and only takes the hit in the tick it runs
        /// its own, so on any other server the stored contact is used at the game's own hit frame.
        /// </summary>
        public static bool InstantHits => VRState.IsActive && (Provider.isServer || Net.VRNet.ServerAcceptsPoses);
        private const float ReachStepDegrees = 2f; // one reach line per this much turn of the line in a frame
        private const int ReachMaxRays = 8;
        // The reach line counts only while it points within this angle of where the HMD looks, like the flat game's ray along
        // the view. The blade itself always counts. GUESS: 30 degrees still takes a low stab at the body of a zombie in front.
        private const float ReachViewAngle = 30f;
        // Below the view the line may point further down: crawlers lie low in front, and a wind-up (the case the limit is for)
        // raises the blade, it does not lower it. GUESS, tune in the headset.
        private const float ReachViewAngleDown = 60f;

        // Sideways and up: within ReachViewAngle of the view. Down: up to ReachViewAngleDown below it, as long as the line stays
        // within ReachViewAngle of the view's heading.
        private static bool InView(Vector3 direction, Vector3 view)
        {
            if (Vector3.Angle(direction, view) <= ReachViewAngle)
            {
                return true;
            }
            Vector3 flatDirection = new Vector3(direction.x, 0f, direction.z);
            Vector3 flatView = new Vector3(view.x, 0f, view.z);
            if (flatDirection.sqrMagnitude < 1e-6f || flatView.sqrMagnitude < 1e-6f)
            {
                return false; // straight up or down: no heading to compare
            }
            float down = (Mathf.Asin(Mathf.Clamp(view.normalized.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(direction.normalized.y, -1f, 1f))) * Mathf.Rad2Deg;
            return down > 0f && down <= ReachViewAngleDown && Vector3.Angle(flatDirection, flatView) <= ReachViewAngle;
        }

        // Fists. The game's punch: 1.75 m from the head (PlayerEquipment.punch), at most every 5 input ticks, not prone.
        private const float FistReach = 1.75f;
        private const uint PunchTicks = 5;
        private const float KnuckleForward = 0.06f; // GUESS: knuckles this far ahead of the controller grip point
        private const float FistBack = 0.04f; // the fist's "blade" runs from this far behind the grip point to the knuckles

        private enum State { Idle, Armed, Swinging, Spent }

        private static readonly int CreatureMask = RayMasks.ENEMY | RayMasks.ENTITY; // players, zombies and animals

        private static readonly Swinger weapon = new Swinger("weapon");
        private static readonly Swinger[] fists = { new Swinger("left fist"), new Swinger("right fist") };

        private static Transform model;
        private static bool geometryValid;
        private static Vector3 localBase, localMid, localTip; // weapon model space
        private static bool toolRayValid; // this frame's ray of a repeated tool in the hand (chainsaw, blowtorch)
        private static Vector3 toolOrigin, toolDirection;
        private static int toolRayFrame = -10;
        private static bool loggedToolRay;

        private static readonly MethodInfo isUseableGetter = AccessTools.PropertyGetter(typeof(UseableMelee), "isUseable");
        private static readonly FieldInfo lastPunchField = AccessTools.Field(typeof(PlayerEquipment), "lastPunch");
        private static int pressFrame = -1;
        private static KeyCode pressKey = KeyCode.None;
        private static bool pending;
        private static bool pendingFist; // the pending contact is a punch (else a weapon swing)
        private static float pendingSince;
        private static Transform pendingCollider;
        private static Vector3 pendingLocalPoint; // in the collider's space, so a moving zombie is still hit on the same limb
        private static Vector3 pendingDirection;
        private static string pendingWhat;

        /// <summary>
        /// Physical melee replaces the trigger attacks this frame (singleplayer, a one-shot melee weapon or empty hands):
        /// VRKeyMap must not press primary or secondary from the triggers.
        /// </summary>
        public static bool TakesTriggers { get; private set; }

        /// <summary>The attack key pressed by a contact, held for one frame (the game latches it until its next input tick).</summary>
        public static bool KeyHeld(KeyCode key)
        {
            return pressFrame == Time.frameCount && key != KeyCode.None && key == pressKey;
        }

        /// <summary>A weapon contact is waiting for this weapon's hit (UseableMeleeHitPatch makes it hit now).</summary>
        public static bool HasPendingHit(UseableMelee melee)
        {
            return pending && !pendingFist && melee != null && melee.channel != null && melee.channel.IsLocalPlayer;
        }

        /// <summary>
        /// The ray for a weapon hit: from just before the contact point, along the swing, through it. Clears the pending hit.
        /// False when there is none, or the collider is gone (then the game hits from the head as usual).
        /// </summary>
        public static bool TryTakeHitRay(UseableMelee melee, out Vector3 origin, out Quaternion rotation)
        {
            origin = Vector3.zero;
            rotation = Quaternion.identity;
            return HasPendingHit(melee) && TakeRay(out origin, out rotation);
        }

        /// <summary>
        /// The ray of a repeated tool (chainsaw, blowtorch) of the local player, for each of its cuts / repairs: from the blade base
        /// along the blade, so it cuts what the tool points at, not what the head looks at. It keeps the game's own timing (the
        /// trigger), so it works online too: the server checks the point within range + 4 of the head, which a tool in the hand is.
        /// False when the tool is not tracked in a hand (then the game cuts from the head as usual).
        /// </summary>
        public static bool TryGetToolRay(UseableMelee melee, out Vector3 origin, out Quaternion rotation)
        {
            origin = toolOrigin;
            rotation = Quaternion.identity;
            // Set by this frame's or last frame's Update (the game may simulate before it); older = VRMelee no longer runs.
            if (!toolRayValid || Time.frameCount - toolRayFrame > 1 || melee == null || melee.channel == null || !melee.channel.IsLocalPlayer
                || !(melee.player.equipment.asset is ItemMeleeAsset asset) || !asset.isRepeated)
            {
                return false;
            }
            rotation = Quaternion.LookRotation(toolDirection);
            if (!loggedToolRay)
            {
                loggedToolRay = true;
                VRLog.Info("melee: " + asset.name + " cuts along the tool (from the blade base, range " + asset.range.ToString("0.0") + " m), not from the head.");
            }
            return true;
        }

        /// <summary>The same for a punch of the local player (PlayerEquipmentPunchPatch).</summary>
        public static bool TryTakePunchRay(PlayerEquipment equipment, out Vector3 origin, out Quaternion rotation)
        {
            origin = Vector3.zero;
            rotation = Quaternion.identity;
            return pending && pendingFist && equipment != null && equipment.channel != null && equipment.channel.IsLocalPlayer
                && TakeRay(out origin, out rotation);
        }

        private static bool TakeRay(out Vector3 origin, out Quaternion rotation)
        {
            origin = Vector3.zero;
            rotation = Quaternion.identity;
            pending = false;
            if (pendingCollider == null)
            {
                Log("hit target gone, the game hits from the head");
                return false;
            }
            Vector3 point = pendingCollider.TransformPoint(pendingLocalPoint);
            origin = point - pendingDirection * RayBackOff;
            rotation = Quaternion.LookRotation(pendingDirection);
            Log("hit " + pendingWhat + ", " + (Time.time - pendingSince).ToString("0.00") + " s after the contact");
            return true;
        }

        private static void Log(string message)
        {
            if (VRDebugFlags.LogMelee)
            {
                VRLog.Info("melee: " + message);
            }
        }

        /// <summary>Once per frame from VRArms, after the equipped item and the hands have been posed.</summary>
        public static void Update()
        {
            Player player = Player.LocalPlayer;
            PlayerEquipment equipment = player != null ? player.equipment : null;
            Transform rig = VRRig.RigTransform;
            // The real head (the HMD camera), not the game's look.aim: that one stays where the character's head is and does not
            // follow the player walking or turning around in the playspace, so a line from it through the tip pointed backwards.
            Camera eye = VRRig.ActiveCamera;
            Transform aim = eye != null ? eye.transform : null;
            bool uiOpen = VRPointer.Active || (PlayerUI.window != null && PlayerUI.window.showCursor);
            bool play = VRState.IsActive && rig != null && aim != null && equipment != null && player.look != null && !uiOpen;

            int hand = VRArms.ItemHand;
            Transform anchor = hand >= 0 ? VRHands.Anchor(hand) : null;
            bool meleeInHand = play && anchor != null && VRHands.IsTracked(hand)
                && equipment.useable is UseableMelee && equipment.asset is ItemMeleeAsset && equipment.firstModel != null;
            bool repeated = meleeInHand && ((ItemMeleeAsset)equipment.asset).isRepeated;
            bool weaponActive = meleeInHand && !repeated;
            // Chainsaw, blowtorch: the trigger runs them as in the game, but they cut / repair along the tool (ToolRay), not from the head.
            // Empty hands (the game punches with them), on foot, first person (the game's own punch condition for the view).
            bool fistsActive = play && equipment.asset == null && equipment.useable == null
                && player.movement.getVehicle() == null && player.look.perspective == EPlayerPerspective.FIRST;

            TakesTriggers = weaponActive || fistsActive;
            if (pending && Time.time - pendingSince > (InstantHits || pendingFist ? PendingMaxSeconds : PendingMaxSecondsOnline))
            {
                pending = false;
                Log("the game did not attack for the contact on " + pendingWhat + ", dropped");
            }

            Vector3 head = aim != null ? aim.position : Vector3.zero;
            Quaternion unturn = aim != null ? Quaternion.Euler(0f, -aim.eulerAngles.y, 0f) : Quaternion.identity;
            Vector3 view = aim != null ? aim.forward : Vector3.forward;

            UpdateWeapon(meleeInHand, repeated, player, equipment, anchor, hand, rig, head, unturn, view);
            for (int side = 0; side < 2; side++)
            {
                Transform fist = VRHands.Anchor(side);
                bool active = fistsActive && fist != null && VRHands.IsTracked(side);
                if (!active)
                {
                    fists[side].Reset();
                    continue;
                }
                fists[side].Samples[2] = fist.position + fist.forward * KnuckleForward;
                fists[side].Samples[0] = fist.position - fist.forward * FistBack;
                fists[side].Samples[1] = (fists[side].Samples[0] + fists[side].Samples[2]) * 0.5f;
                fists[side].Hand = fist.position;
                if (fists[side].Step(rig, head, unturn, view, FistReach, true, out RaycastHit hit, out int sample, out Vector3 direction, out float speed))
                {
                    Contact(fists[side], side, equipment, true, hit, sample, direction, speed);
                }
            }
        }

        private static void UpdateWeapon(bool active, bool repeated, Player player, PlayerEquipment equipment, Transform anchor, int hand,
            Transform rig, Vector3 head, Quaternion unturn, Vector3 view)
        {
            toolRayValid = false;
            if (!active)
            {
                weapon.Reset();
                model = null;
                geometryValid = false;
                return;
            }
            if (equipment.firstModel != model)
            {
                weapon.Reset();
                model = equipment.firstModel;
                geometryValid = ResolveBlade(model, anchor.position, equipment.asset);
            }
            if (!geometryValid)
            {
                return;
            }
            weapon.Samples[0] = model.TransformPoint(localBase);
            weapon.Samples[1] = model.TransformPoint(localMid);
            weapon.Samples[2] = model.TransformPoint(localTip);
            weapon.Hand = anchor.position;
            float reach = ((ItemWeaponAsset)equipment.asset).range;
            if (repeated)
            {
                // No swing: the game's cut / repair ray starts at the blade base and runs along the blade for the tool's range.
                Vector3 axis = weapon.Samples[2] - weapon.Samples[0];
                if (axis.sqrMagnitude > 1e-6f)
                {
                    toolRayValid = true;
                    toolRayFrame = Time.frameCount;
                    toolOrigin = weapon.Samples[0];
                    toolDirection = axis.normalized;
                    weapon.EnterToolMode();
                }
                return;
            }
            if (weapon.Step(rig, head, unturn, view, reach, false, out RaycastHit hit, out int sample, out Vector3 direction, out float speed))
            {
                Contact(weapon, hand, equipment, false, hit, sample, direction, speed);
            }
        }

        private static void Contact(Swinger swinger, int hand, PlayerEquipment equipment, bool fist, RaycastHit hit, int sample,
            Vector3 direction, float speed)
        {
            Transform target = hit.collider.transform; // as the game (RaycastInfo): hit.transform is the rigidbody root, a zombie's on a server
            string kind = TargetKind(target);
            bool creature = kind == "zombie" || kind == "player" || kind == "animal";
            string limb = creature ? DamageTool.getLimb(target).ToString() : "-";
            Log(swinger.Name + " contact " + kind + " '" + hit.collider.name + "' limb=" + limb + ", "
                + (fist ? "punch" : swinger.Stab ? "stab" : "slash") + " with the " + SampleName(sample) + ", tip "
                + speed.ToString("0.0") + " m/s, peak " + swinger.PeakSpeed.ToString("0.0") + " m/s, "
                + swinger.SwingSeconds.ToString("0.00") + " s into the swing");
            bool ready = fist ? CanPunch(equipment) : !equipment.isBusy && IsUseable(equipment.useable as UseableMelee);
            if (pending || !ready)
            {
                Log("not ready (the game's " + (fist ? "punch" : "swing") + " cooldown), contact dropped");
                VRHaptics.Pulse(hand, 0.12f, 0.02f, 80f);
                return;
            }
            pending = true;
            pendingFist = fist;
            pendingSince = Time.time;
            pendingCollider = hit.collider.transform;
            pendingLocalPoint = pendingCollider.InverseTransformPoint(hit.point);
            pendingDirection = direction;
            pendingWhat = kind + " '" + hit.collider.name + "' limb=" + limb;
            bool strong = false;
            if (fist)
            {
                // The game punches left with primary and right with secondary.
                pressKey = hand == VRHands.Right ? ControlsSettings.secondary : ControlsSettings.primary;
            }
            else
            {
                // A hard swing is the strong attack (secondary), checked here with the game's own stamina rule so a swing the game
                // would refuse is sent as a weak one instead of being lost.
                ItemMeleeAsset asset = (ItemMeleeAsset)equipment.asset;
                bool hard = swinger.PeakHandSpeed >= StrongSpeed;
                bool enough = HasStrongStamina(equipment.player, asset, out float stamina, out float needed);
                strong = hard && enough;
                pressKey = strong ? ControlsSettings.secondary : ControlsSettings.primary;
                string peaks = "hand peak " + swinger.PeakHandSpeed.ToString("0.0") + " m/s, tip peak " + swinger.PeakSpeed.ToString("0.0") + " m/s";
                Log(strong ? "strong attack (" + peaks + ")"
                    : hard ? "weak attack: hard swing, but stamina " + stamina.ToString("0") + " < " + needed.ToString("0.#") + " (" + peaks + ")"
                    : "weak attack (" + peaks + ", strong from hand " + StrongSpeed.ToString("0.0") + " m/s)");
            }
            pressFrame = Time.frameCount + 1; // read by the game's Update next frame (this runs in LateUpdate)
            VRHaptics.Pulse(hand, strong ? 1f : creature ? 0.8f : 0.45f, strong ? 0.1f : 0.06f, 160f);
        }

        // The game's own condition for a strong melee attack (UseableMelee.startSecondary): stamina at least the item's stamina cost,
        // lowered by up to 75% with the Exercise skill.
        private static bool HasStrongStamina(Player player, ItemMeleeAsset asset, out float stamina, out float needed)
        {
            stamina = player.life.stamina;
            needed = asset.stamina * (1f - player.skills.mastery((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.EXERCISE) * 0.75f);
            return stamina >= needed;
        }

        // The game's own punch conditions (PlayerEquipment.simulate_PunchInput): not busy, not prone, 5 input ticks since the last.
        private static bool CanPunch(PlayerEquipment equipment)
        {
            if (equipment.isBusy || equipment.player.stance.stance == EPlayerStance.PRONE)
            {
                return false;
            }
            if (lastPunchField == null)
            {
                return true;
            }
            uint last = (uint)lastPunchField.GetValue(equipment);
            return equipment.player.input.simulation - last > PunchTicks;
        }

        private static bool IsUseable(UseableMelee melee)
        {
            if (melee == null || isUseableGetter == null)
            {
                return melee != null;
            }
            return (bool)isUseableGetter.Invoke(melee, null);
        }

        private static string TargetKind(Transform target)
        {
            if (target.CompareTag("Zombie")) return "zombie";
            if (target.CompareTag("Enemy")) return "player";
            if (target.CompareTag("Animal")) return "animal";
            if (target.CompareTag("Resource")) return "resource";
            if (target.CompareTag("Barricade")) return "barricade";
            if (target.CompareTag("Structure")) return "structure";
            if (target.CompareTag("Vehicle")) return "vehicle";
            return "object(" + LayerMask.LayerToName(target.gameObject.layer) + ")";
        }

        private static string SampleName(int sample)
        {
            switch (sample)
            {
                case 0: return "base";
                case 1: return "middle";
                case 2: return "tip";
                case 3: return "blade";
                default: return "reach line (game range)";
            }
        }

        /// <summary>
        /// The swing detector for one weapon or one fist. The caller puts the three sample points (base, middle, tip) in
        /// <see cref="Samples"/> each frame, then calls <see cref="Step"/>.
        /// </summary>
        private sealed class Swinger
        {
            public readonly string Name;
            public readonly Vector3[] Samples = new Vector3[3];
            public Vector3 Hand; // the controller, set by the caller each frame with the samples
            public bool Stab { get; private set; }
            public float PeakSpeed { get; private set; }
            public float PeakHandSpeed { get; private set; } // the hand's peak in this swing, measured like the tip's (strong attack)
            public float SwingSeconds => Time.time - swingStart;

            private State state;
            private readonly Vector3[] previous = new Vector3[3];
            private bool previousValid;
            private Vector3 previousRigTip;
            private Vector3 previousHeadTip;
            private Vector3 previousRigHand;
            private Vector3 previousHeadHand;
            private Vector3 previousRigStep;
            private Vector3 previousReachDirection = Vector3.forward;
            private int fastFrames;
            private float swingStart;

            public Swinger(string name)
            {
                Name = name;
            }

            /// <summary>One frame. True when a swing entered a target this frame (then the swing is spent).</summary>
            public bool Step(Transform rig, Vector3 head, Quaternion unturn, Vector3 view, float reach, bool alwaysStab,
                out RaycastHit hit, out int sample, out Vector3 direction, out float speed)
            {
                hit = default;
                sample = -1;
                direction = Vector3.forward;
                // The reach line goes on from the tip along the blade (the knuckles' punch direction for a fist), so it points
                // where the weapon points: a stab reaches the zombie in front, not the wall beside it. Its end is the game's range
                // from the head, as far as the flat game reaches.
                Vector3 reachOrigin = Samples[2];
                Vector3 reachDirection = (Samples[2] - Samples[0]).normalized;
                float reachLength = Mathf.Max(0f, reach - Vector3.Distance(head, Samples[2]));

                // The tip's motion in two frames: the rig (walking or a vehicle does not count) and the head turned to its
                // heading (turning the whole body with the weapon held out does not count). A swing must be fast in both.
                Vector3 rigTip = rig.InverseTransformPoint(Samples[2]);
                Vector3 headTip = unturn * (Samples[2] - head);
                Vector3 rigHand = rig.InverseTransformPoint(Hand);
                Vector3 headHand = unturn * (Hand - head);
                float dt = Time.deltaTime;
                if (!previousValid || dt <= 0f)
                {
                    speed = 0f;
                    Remember(rigTip, headTip, rigHand, headHand, reachDirection, Vector3.zero);
                    return false;
                }
                Vector3 rigStep = rigTip - previousRigTip;
                Vector3 headStep = headTip - previousHeadTip;
                speed = Mathf.Min(rigStep.magnitude, headStep.magnitude) / dt;
                float handSpeed = Mathf.Min((rigHand - previousRigHand).magnitude, (headHand - previousHeadHand).magnitude) / dt;
                // How much the tip moves away from the head (1 = straight out, -1 = straight back). Pulling the hand back after a
                // hit is fast and turns at the hit, so it re-arms; it must not count as a new swing.
                float outward = headStep.sqrMagnitude > 1e-8f && headTip.sqrMagnitude > 1e-8f
                    ? Vector3.Dot(headStep.normalized, headTip.normalized) : 0f;
                // A weapon only must not be pulled back along its blade (out of a stab). A slash moves across the blade, and a
                // straight backhand runs in towards the head for its first half, so the distance to the head says nothing there.
                Vector3 bladeAxis = rig.InverseTransformDirection(Samples[2] - Samples[0]);
                bool goingOut = alwaysStab ? outward >= PunchMinOutward
                    : rigStep.sqrMagnitude <= 1e-8f || Vector3.Angle(rigStep, -bladeAxis) > PullOutMaxAngle;
                bool contact = false;

                switch (state)
                {
                    case State.Idle:
                    case State.Spent:
                        bool touching = Physics.CheckCapsule(Samples[0], Samples[2], BladeRadius, CreatureMask, QueryTriggerInteraction.UseGlobal);
                        // Ready again the moment the tip slows or turns back (the end of a follow-through, the turn of a
                        // wind-up), so a wind-up flows into the swing without a pause. Never while the blade is in a body.
                        bool turned = previousRigStep.sqrMagnitude > 1e-8f && rigStep.sqrMagnitude > 1e-8f
                            && Vector3.Angle(rigStep, previousRigStep) > TurnBackAngle;
                        if (!touching && (speed < RearmSpeed || turned))
                        {
                            state = State.Armed;
                            fastFrames = 0;
                            Log(Name + " armed");
                        }
                        break;

                    case State.Armed:
                        fastFrames = speed > SwingSpeed && goingOut ? fastFrames + 1 : 0;
                        if (fastFrames >= SwingFrames)
                        {
                            Stab = alwaysStab || Vector3.Angle(rigStep, bladeAxis) <= StabMaxAngle;
                            state = State.Swinging;
                            swingStart = Time.time;
                            PeakSpeed = speed;
                            PeakHandSpeed = handSpeed;
                            previousReachDirection = reachDirection;
                            Log(Name + " swing start, " + (alwaysStab ? "punch" : Stab ? "stab" : "slash") + ", tip " + speed.ToString("0.0") + " m/s");
                        }
                        break;

                    case State.Swinging:
                        PeakSpeed = Mathf.Max(PeakSpeed, speed);
                        PeakHandSpeed = Mathf.Max(PeakHandSpeed, handSpeed);
                        if (TryContact(out hit, out sample, out direction) || TryReach(reachOrigin, reachDirection, reachLength, view, out hit, out sample, out direction))
                        {
                            contact = true;
                            state = State.Spent;
                        }
                        else if (speed < RearmSpeed || SwingSeconds > SwingMaxSeconds)
                        {
                            Log(Name + " swing missed, peak " + PeakSpeed.ToString("0.0") + " m/s (hand " + PeakHandSpeed.ToString("0.0") + " m/s)" + MissDiagnostic(reachOrigin, reachDirection, reachLength));
                            state = State.Spent;
                        }
                        break;
                }

                Remember(rigTip, headTip, rigHand, headHand, reachDirection, rigStep);
                return contact;
            }

            // The flat game's reach, along the swing: a line on from the tip along the blade, out to the game's range from the
            // head (the server allows range + 4 from the head). It sweeps with the blade from last frame's direction to this
            // frame's, a few lines between so a fast slash cannot step over a head. Only lines near the view count (InView): the
            // long line of a wind-up must not land high on a wall before the blade comes down in front. The ground and walls are
            // skipped (a low slash does not spend the swing on the floor); only something the weapon can damage counts.
            private bool TryReach(Vector3 head, Vector3 current, float range, Vector3 view, out RaycastHit hit, out int sample, out Vector3 direction)
            {
                sample = 4;
                int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Angle(previousReachDirection, current) / ReachStepDegrees), 1, ReachMaxRays);
                for (int i = 1; i <= steps; i++)
                {
                    direction = Vector3.Slerp(previousReachDirection, current, (float)i / steps);
                    if (InView(direction, view) && Physics.Raycast(head, direction, out hit, range, RayMasks.DAMAGE_CLIENT, QueryTriggerInteraction.UseGlobal)
                        && !hit.collider.CompareTag("Ground") && !hit.collider.CompareTag("Environment"))
                    {
                        return true;
                    }
                }
                hit = default;
                direction = current;
                return false;
            }

            private bool TryContact(out RaycastHit best, out int bestSample, out Vector3 direction)
            {
                best = default;
                bestSample = -1;
                direction = Vector3.forward;
                float bestDistance = float.MaxValue;
                for (int i = Stab ? 2 : 0; i < Samples.Length; i++)
                {
                    Vector3 step = Samples[i] - previous[i];
                    float length = step.magnitude;
                    if (length > 1e-4f && Physics.Raycast(previous[i], step / length, out RaycastHit hit, length, RayMasks.DAMAGE_CLIENT, QueryTriggerInteraction.UseGlobal)
                        && hit.distance < bestDistance)
                    {
                        best = hit;
                        bestSample = i;
                        bestDistance = hit.distance;
                        direction = step / length;
                    }
                }
                if (bestSample < 0 && Stab)
                {
                    // A stab pushes the whole blade in: the blade from base to tip, once it has entered something.
                    Vector3 blade = Samples[2] - Samples[0];
                    float length = blade.magnitude;
                    if (length > 1e-4f && Physics.Raycast(Samples[0], blade / length, out RaycastHit hit, length, RayMasks.DAMAGE_CLIENT, QueryTriggerInteraction.UseGlobal))
                    {
                        best = hit;
                        bestSample = 3;
                        direction = blade / length;
                    }
                }
                return bestSample >= 0;
            }

            private void Remember(Vector3 rigTip, Vector3 headTip, Vector3 rigHand, Vector3 headHand, Vector3 reachDirection, Vector3 rigStep)
            {
                for (int i = 0; i < Samples.Length; i++)
                {
                    previous[i] = Samples[i];
                }
                previousRigTip = rigTip;
                previousHeadTip = headTip;
                previousRigHand = rigHand;
                previousHeadHand = headHand;
                previousReachDirection = reachDirection;
                previousRigStep = rigStep;
                previousValid = true;
            }

            public void Reset()
            {
                if (state != State.Idle)
                {
                    Log(Name + " off");
                }
                state = State.Idle;
                fastFrames = 0;
                previousValid = false;
            }

            /// <summary>A repeated tool (chainsaw, blowtorch): no swing states, the game's own timing.</summary>
            public void EnterToolMode()
            {
                state = State.Idle;
            }
        }

        // Diagnostic for a missed swing: what the reach line passes through on any layer, triggers included, out to twice the
        // range, so a target the damage mask leaves out shows up with its layer, tag and trigger flag.
        private static string MissDiagnostic(Vector3 head, Vector3 direction, float range)
        {
            if (!VRDebugFlags.LogMelee)
            {
                return "";
            }
            if (!Physics.Raycast(head, direction, out RaycastHit hit, range * 2f, ~0, QueryTriggerInteraction.Collide))
            {
                return "; the line passes nothing within " + (range * 2f).ToString("0.0") + " m (range " + range.ToString("0.0") + " m)";
            }
            int layer = hit.collider.gameObject.layer;
            return "; the line passes '" + hit.collider.name + "' at " + hit.distance.ToString("0.00") + " m (range " + range.ToString("0.0")
                + " m), layer " + layer + " " + LayerMask.LayerToName(layer) + ", tag " + hit.collider.tag + ", trigger " + hit.collider.isTrigger
                + ", in the damage mask " + ((RayMasks.DAMAGE_CLIENT & (1 << layer)) != 0);
        }

        private static readonly List<Vector3> meshPoints = new List<Vector3>();

        // The blade line in model space: the model's longest mesh extent (vertices when the mesh is readable, else renderer
        // bounds corners, like VRArms.TryPrepareSlide), through the middle of the cross-section. The tip is the end further
        // from the hand; when both ends are about as far (a rake or a long tool held near its middle), the wider end, the head,
        // as VRArms does for axes. GUESS: odd models (frying pan) show up in the log line.
        private static bool ResolveBlade(Transform model, Vector3 handWorld, ItemAsset asset)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            bool readable = false;
            string parts = "";
            meshPoints.Clear();
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(false))
            {
                parts += (parts.Length > 0 ? ", " : "") + renderer.name + " (" + renderer.GetType().Name + ", "
                    + (renderer is SkinnedMeshRenderer s ? s.sharedMesh : renderer.TryGetComponent(out MeshFilter f) ? f.sharedMesh : null)?.bounds.size.ToString("0.000") + ")";
                Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                    : renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }
                if (mesh.isReadable)
                {
                    readable = true;
                    foreach (Vector3 vertex in mesh.vertices)
                    {
                        meshPoints.Add(model.InverseTransformPoint(renderer.transform.TransformPoint(vertex)));
                    }
                }
                Bounds bounds = mesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                    Vector3 point = model.InverseTransformPoint(renderer.transform.TransformPoint(local));
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                    any = true;
                }
            }
            if (!any)
            {
                Log("no mesh on " + asset.name + ", no swing detection");
                return false;
            }

            Vector3 size = max - min;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            Vector3 center = (min + max) * 0.5f;
            Vector3 handLocal = model.InverseTransformPoint(handWorld);
            float hand = Mathf.Clamp(handLocal[axis], min[axis], max[axis]);
            float toMax = Mathf.Abs(max[axis] - hand);
            float toMin = Mathf.Abs(hand - min[axis]);
            float tip = toMax >= toMin ? max[axis] : min[axis];
            string tipRule = "further end";
            if (Mathf.Min(toMax, toMin) > Mathf.Max(toMax, toMin) * SymmetricEnds)
            {
                if (!readable)
                {
                    tipRule = "further end (ends about as far, mesh not readable for the head check)";
                }
                else
                {
                    // Widest cross-section over the two other axes in the outer quarter at each end.
                    float quarter = size[axis] * 0.25f;
                    int a = (axis + 1) % 3;
                    int b = (axis + 2) % 3;
                    Vector2 lowMin = new Vector2(float.MaxValue, float.MaxValue), lowMax = new Vector2(float.MinValue, float.MinValue);
                    Vector2 highMin = lowMin, highMax = lowMax;
                    foreach (Vector3 point in meshPoints)
                    {
                        Vector2 across = new Vector2(point[a], point[b]);
                        if (point[axis] <= min[axis] + quarter)
                        {
                            lowMin = Vector2.Min(lowMin, across);
                            lowMax = Vector2.Max(lowMax, across);
                        }
                        if (point[axis] >= max[axis] - quarter)
                        {
                            highMin = Vector2.Min(highMin, across);
                            highMax = Vector2.Max(highMax, across);
                        }
                    }
                    float lowWidth = Mathf.Max(lowMax.x - lowMin.x, lowMax.y - lowMin.y);
                    float highWidth = Mathf.Max(highMax.x - highMin.x, highMax.y - highMin.y);
                    if (highWidth > lowWidth * 1.2f)
                    {
                        tip = max[axis];
                        tipRule = "wider end";
                    }
                    else if (lowWidth > highWidth * 1.2f)
                    {
                        tip = min[axis];
                        tipRule = "wider end";
                    }
                    tipRule += " (widths " + lowWidth.ToString("0.00") + " / " + highWidth.ToString("0.00") + ")";
                }
            }

            localTip = center;
            localTip[axis] = tip;
            localBase = center;
            localBase[axis] = Mathf.Lerp(hand, tip, BaseFraction);
            localMid = (localBase + localTip) * 0.5f;

            float scale = model.lossyScale.x;
            meshPoints.Clear();
            Log(asset.name + ": blade along model axis " + "xyz"[axis] + (tip >= hand ? "+" : "-") + " (" + tipRule + "), "
                + (Mathf.Abs(tip - localBase[axis]) * scale).ToString("0.00") + " m from base to tip, hand "
                + (Mathf.Abs(tip - hand) * scale).ToString("0.00") + " m from the tip; model size " + size.ToString("0.000")
                + " (model units), lossy scale " + model.lossyScale.ToString("0.000") + ", hand at " + handLocal.ToString("0.000")
                + ", renderers: " + parts);
            return true;
        }
    }
}
