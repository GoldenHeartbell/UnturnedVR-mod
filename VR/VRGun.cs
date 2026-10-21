using System.Collections.Generic;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Phase 4a: shots leave the gun's muzzle instead of the head. The equipped gun model hangs on the controller
    /// (VRArms.DriveItem); its "Barrel" hook (Attachments.barrelHook) sits at the muzzle. UseableGun.fire reads
    /// bullet origin and direction from player.look.aim, so Patches/UseableGunMuzzlePatch puts that transform on
    /// the muzzle for the duration of the call.
    /// The barrel direction is a local axis of the hook that differs per model, so it is taken once per equip as
    /// the axis that points from the model origin (the hand) to the hook, snapped to a hook axis (the
    /// barrel hook lies on the barrel line).
    /// Safety: if a wall is between the head and the muzzle (gun pushed into geometry) the shot uses the head aim.
    /// Cost: one cached lookup and one linecast per shot.
    /// </summary>
    internal static class VRGun
    {
        private static Transform cachedModel;
        private static Transform warnedModel;
        private static Transform barrel;
        private static Attachments cachedAttachments;
        private static Vector3 localDirection = Vector3.up;

        /// <summary>Why the last <see cref="TryGetMuzzle"/> returned false (for the shot log in UseableGunMuzzlePatch).</summary>
        public static string MuzzleFailure { get; private set; }

        /// <summary>Muzzle pose of the equipped gun in world space, false if the vanilla head aim must be used.</summary>
        public static bool TryGetMuzzle(Player player, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            Camera eye = VRRig.ActiveCamera;
            if (!VRState.IsActive || !VRArms.Active || VRArms.ItemHand < 0 || eye == null)
            {
                MuzzleFailure = "arms not driving the item (VRArms.Active=" + VRArms.Active + ", ItemHand=" + VRArms.ItemHand + ", eye camera " + (eye != null ? "ok" : "null") + ")";
                return false;
            }

            Transform model = player.equipment != null ? player.equipment.firstModel : null;
            if (model == null)
            {
                MuzzleFailure = "no first-person model";
                return false;
            }
            if (model != cachedModel)
            {
                Resolve(model);
            }
            if (barrel == null)
            {
                MuzzleFailure = "no Barrel hook on \"" + model.name + "\"";
                return false;
            }

            position = barrel.position;
            Vector3 direction = barrel.TransformDirection(localDirection);
            if (TryGetBowShot(model, out Vector3 arrowTip, out Vector3 arrowDirection))
            {
                position = arrowTip; // a bow: from the tip of the arrow that is nocked, along it
                direction = arrowDirection;
            }
            if (Physics.Linecast(eye.transform.position, position, out RaycastHit blocker, RayMasks.DAMAGE_SERVER))
            {
                MuzzleFailure = "eye-to-muzzle linecast hit \"" + blocker.collider.name + "\" (layer " + LayerMask.LayerToName(blocker.collider.gameObject.layer)
                    + ") " + blocker.distance.ToString("0.00") + " m from the eye, muzzle is " + Vector3.Distance(eye.transform.position, position).ToString("0.00") + " m away";
                return false;
            }
            rotation = Quaternion.LookRotation(SmoothDirection(direction));
            return true;
        }

        // Hand-shake smoothing of the barrel direction while aiming down the sights (VRAimFilter, same filter as the
        // binoculars). Both the scope picture (VRAds.PlaceScopeCamera) and the shot direction (TryGetMuzzle) use the calmed
        // direction, so they agree; the gun model and the sight itself stay 1:1 with the hand. Off while hip firing.
        // "smooth gun <minCutoff Hz> [beta]" in gun-offsets.txt tunes it, a minCutoff of 0 switches it off; read when aiming starts.
        private const float DefaultAimMinCutoff = 3f; // Hz (shooting wants less lag than the binoculars; holding breath calms it)
        private const float DefaultAimBeta = 0.1f;
        // Hold breath (the game's steady aim, left stick click = sprint key): the cut-off is scaled down to this fraction at the
        // full hold. The game's own sway is not used in VR (the shot comes from the muzzle), so this is where it counts.
        private const float SteadyCutoffScale = 0.25f;
        private const float SteadyAccuracyMax = 4f; // UseableGun.steadyAccuracy ramps 0..4
        private static AccessTools.FieldRef<UseableGun, uint> steadyAccuracyField;
        private static bool steadyAccuracyLookedUp;
        private static readonly VRAimFilter aimFilter = new VRAimFilter();
        private static bool aimWasAds;
        private static bool aimFilterEnabled;
        private static bool aimFilterOn;
        private static Vector3 aimDirection = Vector3.forward;

        /// <summary>The calmed barrel direction while aiming down the sights with smoothing on, else the raw one.</summary>
        public static Vector3 SmoothDirection(Vector3 raw)
        {
            return aimFilterOn ? aimDirection : raw;
        }

        /// <summary>Once per frame from VRAds.Update, after the gun is placed. <paramref name="ads"/>: the sight is at the eye.</summary>
        public static void UpdateAimFilter(bool ads)
        {
            Player player = Player.LocalPlayer;
            if (!ads || player == null || !TryGetSightLine(player, out _, out Vector3 raw, out _))
            {
                aimWasAds = false;
                aimFilterOn = false;
                return;
            }

            if (!aimWasAds)
            {
                aimWasAds = true;
                VRGunTuning.GetSmoothing("gun", DefaultAimMinCutoff, DefaultAimBeta, out float minCutoff, out float beta);
                aimFilterEnabled = minCutoff > 0f;
                aimFilter.MinCutoff = minCutoff;
                aimFilter.Beta = beta;
                aimFilter.Reset();
                if (VRDebugFlags.LogAds)
                {
                    VRLog.Info("ads: aim smoothing " + (aimFilterEnabled ? minCutoff.ToString("0.##") + " Hz / beta " + beta.ToString("0.###") : "off") + ".");
                }
            }
            if (!aimFilterEnabled)
            {
                aimFilterOn = false;
                return;
            }

            // A scope magnifies the tremor: calmer the higher its zoom (read from the scope camera's field of view).
            float zoom = 1f;
            PlayerLook look = player.look;
            if (look != null && look.isScopeActive && look.scopeCamera != null && look.scopeCamera.fieldOfView > 1f)
            {
                zoom = Mathf.Clamp(OptionsSettings.GetZoomBaseFieldOfView() / look.scopeCamera.fieldOfView, 1f, 20f);
            }

            float steady = Mathf.Clamp01(SteadyAccuracy(player) / SteadyAccuracyMax);
            float cutoffScale = Mathf.Lerp(1f, SteadyCutoffScale, steady) / Mathf.Sqrt(zoom);
            aimDirection = aimFilter.Step(raw.normalized, Mathf.Min(Time.unscaledDeltaTime, 0.1f), cutoffScale);
            aimFilterOn = true;
        }

        /// <summary>
        /// The equipped gun's sight is one the game lets you steady with a held breath (zoom above 2, the same test as
        /// UseableGun.simulate). Needs the gun to be resolved first (any TryGetSightLine call does that).
        /// </summary>
        public static bool HasSteadySight =>
            cachedAttachments != null && cachedAttachments.sightAsset != null && cachedAttachments.sightAsset.zoom > 2f;

        /// <summary>
        /// The game's hold-breath ramp of the equipped gun (UseableGun.steadyAccuracy, 0..4): only rises while aiming through a
        /// scope with breath left and the sprint key held, so the game's own rules (and the breath drain) apply.
        /// </summary>
        private static float SteadyAccuracy(Player player)
        {
            if (!steadyAccuracyLookedUp)
            {
                steadyAccuracyLookedUp = true;
                try
                {
                    steadyAccuracyField = AccessTools.FieldRefAccess<UseableGun, uint>("steadyAccuracy");
                }
                catch (System.Exception e)
                {
                    VRLog.Warn("hold breath: UseableGun.steadyAccuracy not found, aim stays unchanged (" + e.Message + ").");
                }
            }

            UseableGun gun = player.equipment != null ? player.equipment.useable as UseableGun : null;
            return gun != null && steadyAccuracyField != null ? steadyAccuracyField(gun) : 0f;
        }

        /// <summary>
        /// The equipped gun's sight line in world space: the sight reference point (aim hook of the fitted sight, else
        /// the view hook, else the sight hook; the order the game uses to line the viewmodel up while aiming), the
        /// barrel direction, and the gun's up (from the barrel towards the sight, so no extra axis assumption). False for
        /// anything that is not a hand-held gun with a sight reference, or while the item is not on a hand.
        /// No raycast, no allocation; called a few times per frame.
        /// </summary>
        public static bool TryGetSightLine(Player player, out Vector3 point, out Vector3 direction, out Vector3 up)
        {
            point = default;
            direction = Vector3.forward;
            up = Vector3.up;
            if (!VRState.IsActive || !VRArms.Active || VRArms.ItemHand < 0 || player == null || player.equipment == null)
            {
                return false;
            }

            Transform model = player.equipment.firstModel;
            if (model == null)
            {
                return false;
            }
            if (model != cachedModel)
            {
                Resolve(model);
            }
            if (barrel == null || cachedAttachments == null)
            {
                return false;
            }

            ItemGunAsset asset = player.equipment.asset as ItemGunAsset;
            if (asset == null || asset.isTurret || asset.action == EAction.Minigun)
            {
                return false; // the game does not line these up with a sight either
            }

            Transform sight = cachedAttachments.aimHook != null ? cachedAttachments.aimHook
                : cachedAttachments.viewHook != null ? cachedAttachments.viewHook : cachedAttachments.sightHook;
            if (sight == null)
            {
                return false;
            }

            point = sight.position;
            direction = AimDirection(model);
            Vector3 toSight = point - barrel.position;
            toSight -= direction * Vector3.Dot(toSight, direction);
            up = toSight.sqrMagnitude > 0.000025f ? toSight.normalized : Vector3.up;

            // The barrel-to-sight line is only "up" when the Barrel hook sits under the sight. On guns whose hook lies behind
            // the origin and off to the side (the Carolus id 6253 and the Javelin id 6908) it pointed sideways and the scope view
            // was rolled 45 / 90 deg. Then the up of the hand's aim frame is used (the gun is held upright in it).
            Vector3 handUp = Vector3.ProjectOnPlane(VRArms.AimFrame * Vector3.up, direction);
            if (handUp.sqrMagnitude > 0.0001f)
            {
                handUp.Normalize();
                bool sideways = Vector3.Angle(up, handUp) > SightUpTolerance;
                if (sideways)
                {
                    up = handUp;
                }
                if (model != sightUpLoggedModel)
                {
                    sightUpLoggedModel = model;
                    if (sideways)
                    {
                        VRLog.Info("gun: \"" + model.name + "\" sight is not above the Barrel hook, the scope keeps the hand's up.");
                    }
                }
            }
            return true;
        }

        private const float SightUpTolerance = 30f; // degrees between the barrel-to-sight up and the hand's up before the hand's is used
        private static Transform sightUpLoggedModel;

        /// <summary>
        /// The equipped gun's own frame in world space: forward along the barrel (the muzzle axis of its Barrel hook), up towards
        /// its sight when it has one, else the up of <paramref name="fallback"/> made square to the barrel. For hanging things on
        /// the gun so they are straight with the model, whatever hold rotation the animation gave it. False if not resolved.
        /// </summary>
        public static bool TryGetGunFrame(Player player, Quaternion fallback, out Quaternion frame)
        {
            frame = fallback;
            Transform model = player != null && player.equipment != null ? player.equipment.firstModel : null;
            if (model == null)
            {
                return false;
            }
            if (model != cachedModel)
            {
                Resolve(model);
            }
            if (barrel == null)
            {
                return false;
            }

            Vector3 direction = AimDirection(model).normalized;
            Vector3 up = fallback * Vector3.up;
            Transform sight = cachedAttachments != null
                ? (cachedAttachments.aimHook != null ? cachedAttachments.aimHook
                    : cachedAttachments.viewHook != null ? cachedAttachments.viewHook : cachedAttachments.sightHook)
                : null;
            if (sight != null)
            {
                Vector3 toSight = Vector3.ProjectOnPlane(sight.position - barrel.position, direction);
                if (toSight.sqrMagnitude > 0.000025f)
                {
                    up = toSight;
                }
            }

            up = Vector3.ProjectOnPlane(up, direction);
            if (direction.sqrMagnitude < 0.5f || up.sqrMagnitude < 0.0001f)
            {
                return false;
            }
            frame = Quaternion.LookRotation(direction, up);
            return true;
        }

        // Bows: the vanilla hold has the bow slanted (limbs diagonal, its own front about 23 deg left of the view) because the game shoots along the camera,
        // not along the bow, so the mod does not use the animation's hold for them. Instead the model is turned so the arrow runs along the controller's
        // aim frame forward and the model's +y axis (the upper limb in the vanilla pose) points up. "The arrow" is the nocked arrow the player sees (the
        // magazine model on the Magazine hook): its direction in model space is remembered per bow (the arrow is gone after a shot until the next reload),
        // and the shot leaves from its tip along it. Item 353 logged it 8 deg to the left of and 4 deg below the bow's -x axis, which the old shot axis
        // (the snapped hook axis) missed: the arrows flew to the right of the arrow in the bow. Before an arrow was ever seen, the snapped axis is used.
        private struct BowArrow
        {
            public Vector3 Direction; // model space, towards the tip
            public Vector3 Tip;       // model space
        }

        private static readonly Dictionary<string, BowArrow> bowArrows = new Dictionary<string, BowArrow>();
        private static Transform bowHoldModel;
        private static Vector3 bowHoldAxis;
        private static bool bowHoldValid;
        private static bool bowHoldHasArrow;
        private static BowArrow bowHoldArrow;
        private static Quaternion bowHold = Quaternion.identity;

        /// <summary>
        /// The rotation that stands the equipped bow up in the aim frame (model rotation = aim frame * this), replacing the animation's hold
        /// rotation. False for anything that is not a string-action gun or whose axes cannot be read; the caller then keeps the hold rotation.
        /// </summary>
        public static bool TryGetBowHold(Transform model, out Quaternion hold)
        {
            hold = Quaternion.identity;
            Player player = Player.LocalPlayer;
            if (model == null || player == null || player.equipment == null || !(player.equipment.asset is ItemGunAsset gun) || gun.action != EAction.String)
            {
                return false;
            }
            if (model != cachedModel)
            {
                Resolve(model);
            }
            if (barrel == null)
            {
                return false;
            }

            Vector3 snapped = model.InverseTransformDirection(barrel.TransformDirection(localDirection)).normalized;
            string key = gun.GUID.ToString("N");
            if (!bowArrows.ContainsKey(key) && cachedAttachments != null && TryReadArrow(model, cachedAttachments.magazineModel, snapped, out BowArrow read))
            {
                bowArrows[key] = read;
                VRLog.Info("gun: bow \"" + model.name + "\" nocked arrow read: points " + read.Direction.ToString("0.00") + " in model space, "
                    + Vector3.Angle(read.Direction, snapped).ToString("0.0") + " deg from the hook axis " + snapped.ToString("0.00") + ", tip " + read.Tip.ToString("0.00") + ".");
            }
            bool hasArrow = bowArrows.TryGetValue(key, out BowArrow known);
            Vector3 arrow = hasArrow ? known.Direction : snapped;

            if (bowHoldModel != model || bowHoldAxis != arrow || bowHoldHasArrow != hasArrow)
            {
                bowHoldModel = model;
                bowHoldAxis = arrow;
                bowHoldHasArrow = hasArrow;
                bowHoldArrow = known;
                bowHoldValid = arrow.sqrMagnitude > 0.5f && Mathf.Abs(Vector3.Dot(arrow, Vector3.up)) < 0.95f;
                bowHold = bowHoldValid ? Quaternion.Inverse(Quaternion.LookRotation(arrow, Vector3.up)) : Quaternion.identity;
                VRLog.Info("gun: bow \"" + model.name + "\" hold: arrow axis " + arrow.ToString("0.00") + " in model space (" + (hasArrow ? "the nocked arrow" : "the hook axis")
                    + ") along the aim frame forward, model +y up" + (bowHoldValid ? "." : " - not usable, the animation's hold rotation stays."));
            }
            hold = bowHold;
            return bowHoldValid;
        }

        // Guns other than bows: the animation's hold rotation (itemToRoot) is only a hand pose, the game shoots along the camera and never looks at where the
        // barrel points. Rifles happen to hold the barrel almost along the root's forward, but some models (the shotguns 112, 380, 1143, 1436) sit pointing down
        // and to the left, so the controller had to be turned far to line them up. The model's barrel axis (the shot axis from Resolve) under the settled hold is
        // hold * axis; when that is further than StraightenThreshold from the forward axis, a correction swings it the shortest way onto forward (hold = correction * hold).
        // Guns that already point close enough keep the animation's hold exactly (their pitch / offset were tuned on it).
        //
        // The equip animation moves the model while the hold is still being sampled (before the calibration); straightening the barrel every frame would
        // flatten it to a roll about the barrel. So the correction is only known from the settled hold: it is remembered per gun (GUID), applied to the
        // animation from the next equip on (the animation then plays relative to the straight pose and ends on it), and on the first equip of a gun the
        // animation plays raw and eases into the straight pose over StraightenEaseSeconds after the calibration.
        private const float StraightenThreshold = 8f; // deg
        private const float StraightenEaseSeconds = 0.3f;

        private static readonly Dictionary<string, Quaternion> settledCorrections = new Dictionary<string, Quaternion>();
        private static Transform straightenLoggedModel;
        private static bool straightenLogged;
        private static Transform rawShownModel;  // this model's equip animation was shown without a correction
        private static Transform easeModel;
        private static float easeStart;

        /// <summary>
        /// The hold rotation to use for the equipped gun. <paramref name="calibrated"/>: <paramref name="animationHold"/> is the settled hold (else it is the
        /// animated pose of the equip animation). False when the animation's hold is used unchanged (no Barrel hook yet, bows, already straight, or the animation
        /// of a gun that was never settled before).
        /// </summary>
        public static bool TryStraightenHold(Transform model, Quaternion animationHold, bool calibrated, out Quaternion hold)
        {
            hold = animationHold;
            Player player = Player.LocalPlayer;
            if (model == null || player == null || player.equipment == null || !(player.equipment.asset is ItemGunAsset gun) || gun.action == EAction.String)
            {
                return false;
            }
            if (model != cachedModel)
            {
                Resolve(model);
            }
            if (barrel == null)
            {
                return false;
            }

            string key = gun.GUID.ToString("N");
            if (!calibrated)
            {
                if (settledCorrections.TryGetValue(key, out Quaternion known))
                {
                    hold = known * animationHold; // the animation plays relative to the straight pose
                    return true;
                }
                rawShownModel = model;
                return false;
            }

            Vector3 axis = model.InverseTransformDirection(barrel.TransformDirection(localDirection)).normalized;
            Vector3 barrelInFrame = animationHold * axis;
            float angle = Vector3.Angle(barrelInFrame, Vector3.forward);
            if (angle > 90f)
            {
                // The game never holds a gun backwards: the Barrel hook sits behind the model origin (the Carolus, id 6253, read
                // 180 deg off and was turned to point backwards). The muzzle axis is the other way round; flipped once, after
                // which the angle is small, so this does not toggle.
                localDirection = -localDirection;
                axis = -axis;
                barrelInFrame = -barrelInFrame;
                angle = 180f - angle;
                VRLog.Info("gun: \"" + model.name + "\" id " + gun.id + " Barrel hook lies behind the origin, muzzle axis flipped to " + localDirection + ".");
            }
            bool straighten = angle > StraightenThreshold;
            Quaternion correction = straighten ? Quaternion.FromToRotation(barrelInFrame, Vector3.forward) : Quaternion.identity;
            settledCorrections[key] = correction;
            if (model != straightenLoggedModel || straighten != straightenLogged)
            {
                straightenLoggedModel = model;
                straightenLogged = straighten;
                VRLog.Info("gun: \"" + model.name + "\" id " + gun.id + " barrel axis " + axis.ToString("0.00") + " in model space; the settled hold leaves it "
                    + angle.ToString("0.0") + " deg off the controller forward (yaw " + SignedAngle(barrelInFrame, Vector3.up).ToString("0.0") + " right, pitch "
                    + (90f - Vector3.Angle(barrelInFrame, Vector3.up)).ToString("0.0") + " up)"
                    + (straighten ? ", swung straight (limit " + StraightenThreshold.ToString("0") + " deg)." : ", kept (within " + StraightenThreshold.ToString("0") + " deg)."));
            }
            if (!straighten)
            {
                return false;
            }

            float weight = 1f;
            if (rawShownModel == model)
            {
                // First equip of this gun: its animation was shown as it is, so the swing comes in gradually instead of jumping.
                if (easeModel != model)
                {
                    easeModel = model;
                    easeStart = Time.unscaledTime;
                }
                float t = Mathf.Clamp01((Time.unscaledTime - easeStart) / StraightenEaseSeconds);
                weight = t * t * (3f - 2f * t);
            }
            hold = Quaternion.Slerp(Quaternion.identity, correction, weight) * animationHold;
            return true;
        }

        // Angle of the vector's horizontal part to forward around the given axis, + = to the right.
        private static float SignedAngle(Vector3 vector, Vector3 axis)
        {
            Vector3 flat = Vector3.ProjectOnPlane(vector, axis);
            return flat.sqrMagnitude < 0.0001f ? 0f : Vector3.SignedAngle(Vector3.forward, flat, axis);
        }

        // The way the equipped gun shoots, in world space: along the nocked arrow for a bow that has one known, else the barrel hook's axis. The sight line,
        // the gun frame and the ADS smoothing use this too, so they agree with the shot (a bow with a sight, item 357, was smoothed back onto the hook axis,
        // 9 deg from its arrow, while the bow without a sight, 353, was exact).
        private static Vector3 AimDirection(Transform model)
        {
            return TryGetBowShot(model, out _, out Vector3 arrowDirection) ? arrowDirection : barrel.TransformDirection(localDirection);
        }

        /// <summary>Where the arrow of the bow in the hand starts (its tip) and which way it goes, in world space. False without a known arrow.</summary>
        private static bool TryGetBowShot(Transform model, out Vector3 tip, out Vector3 direction)
        {
            tip = default;
            direction = default;
            if (!bowHoldValid || !bowHoldHasArrow || bowHoldModel != model)
            {
                return false;
            }
            tip = model.TransformPoint(bowHoldArrow.Tip);
            direction = model.TransformDirection(bowHoldArrow.Direction).normalized;
            return true;
        }

        // The nocked arrow: the longest mesh under the magazine model, its long axis as a direction in model space towards the side that faces
        // the hint (the hook axis), and its tip. Refused when it is more than 30 deg from the hint (not an arrow mesh).
        private static bool TryReadArrow(Transform model, Transform arrow, Vector3 hint, out BowArrow result)
        {
            result = default;
            if (arrow == null)
            {
                return false;
            }

            MeshFilter best = null;
            float bestLength = 0f;
            foreach (MeshFilter filter in arrow.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }
                Vector3 size = filter.sharedMesh.bounds.size;
                float length = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                if (best == null || length > bestLength)
                {
                    best = filter;
                    bestLength = length;
                }
            }
            if (best == null)
            {
                return false;
            }

            Bounds bounds = best.sharedMesh.bounds;
            Vector3 axis = bounds.size.x >= bounds.size.y && bounds.size.x >= bounds.size.z ? Vector3.right : bounds.size.y >= bounds.size.z ? Vector3.up : Vector3.forward;
            float half = Vector3.Scale(axis, bounds.extents).magnitude;
            Vector3 tail = model.InverseTransformPoint(best.transform.TransformPoint(bounds.center - axis * half));
            Vector3 tip = model.InverseTransformPoint(best.transform.TransformPoint(bounds.center + axis * half));
            Vector3 direction = tip - tail;
            if (direction.sqrMagnitude < 0.0001f)
            {
                return false;
            }
            direction.Normalize();
            if (Vector3.Dot(direction, hint) < 0f)
            {
                direction = -direction;
                tip = tail;
            }
            if (Vector3.Angle(direction, hint) > 30f)
            {
                return false;
            }
            result = new BowArrow { Direction = direction, Tip = tip };
            return true;
        }

        private static void Resolve(Transform model)
        {
            barrel = null;
            cachedAttachments = null;
            Attachments attachments = model.GetComponent<Attachments>();
            if (attachments == null || attachments.barrelHook == null)
            {
                // Not cached: the gun is posed on the hand as soon as it appears, which can be before its attachments
                // are set up, so this is tried again on the next call. Said once per model.
                cachedModel = null;
                if (model != warnedModel)
                {
                    warnedModel = model;
                    VRLog.Warn("gun: \"" + model.name + "\" has no Barrel hook (yet), shots keep the head aim until it appears.");
                }
                return;
            }

            cachedModel = model;
            cachedAttachments = attachments;
            barrel = attachments.barrelHook;
            Vector3 toBarrel = barrel.position - model.position;
            string source = "origin to Barrel hook";
            if (toBarrel.sqrMagnitude <= 0.0004f && attachments.restHook != null)
            {
                // A bow (item 353 logs it): the Barrel hook sits on the model origin, so that line has no direction and the model's
                // own forward was taken, which is the bow's right side. The arrow flies from the string (Rest hook) through the grip.
                toBarrel = barrel.position - attachments.restHook.position;
                source = "Rest hook to Barrel hook";
            }
            Vector3 local = barrel.InverseTransformDirection(toBarrel.sqrMagnitude > 0.0004f ? toBarrel.normalized : model.forward);
            float ax = Mathf.Abs(local.x);
            float ay = Mathf.Abs(local.y);
            float az = Mathf.Abs(local.z);
            if (ay >= ax && ay >= az)
            {
                localDirection = new Vector3(0f, Mathf.Sign(local.y), 0f);
            }
            else if (az >= ax)
            {
                localDirection = new Vector3(0f, 0f, Mathf.Sign(local.z));
            }
            else
            {
                localDirection = new Vector3(Mathf.Sign(local.x), 0f, 0f);
            }
            // Snapped to the hook axis for a bow too (item 353: -x, the bow's own front). A 4.6 deg sideways part of the string line was kept here
            // for the slanted vanilla hold; with the bow stood up from its own axes (TryGetBowHold) it sent the arrows to the left of the bow.
            VRLog.Info("gun: \"" + model.name + "\" muzzle axis " + localDirection + " (match " + Mathf.Max(ax, Mathf.Max(ay, az)).ToString("0.00") + ", from " + source + ").");
        }
    }
}
