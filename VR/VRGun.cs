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
    /// the axis that points from the model origin (the hand) to the hook, snapped to a hook axis (GUESS that the
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

        /// <summary>Muzzle pose of the equipped gun in world space, false if the vanilla head aim must be used.</summary>
        public static bool TryGetMuzzle(Player player, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            Camera eye = VRRig.ActiveCamera;
            if (!VRState.IsActive || !VRArms.Active || VRArms.ItemHand < 0 || eye == null)
            {
                return false;
            }

            Transform model = player.equipment != null ? player.equipment.firstModel : null;
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

            position = barrel.position;
            if (Physics.Linecast(eye.transform.position, position, RayMasks.DAMAGE_SERVER))
            {
                return false;
            }
            rotation = Quaternion.LookRotation(SmoothDirection(barrel.TransformDirection(localDirection)));
            return true;
        }

        // Hand-shake smoothing of the barrel direction while aiming down the sights (VRAimFilter, same filter as the
        // binoculars). Both the scope picture (VRAds.PlaceScopeCamera) and the shot direction (TryGetMuzzle) use the calmed
        // direction, so they agree; the gun model and the sight itself stay 1:1 with the hand. Off while hip firing.
        // "smooth gun <minCutoff Hz> [beta]" in gun-offsets.txt tunes it, a minCutoff of 0 switches it off; read when aiming starts.
        private const float DefaultAimMinCutoff = 3f; // Hz, GUESS (shooting wants less lag than the binoculars; holding breath calms it)
        private const float DefaultAimBeta = 0.1f;    // GUESS
        // Hold breath (the game's steady aim, left stick click = sprint key): the cut-off is scaled down to this fraction at the
        // full hold. GUESS. The game's own sway is not used in VR (the shot comes from the muzzle), so this is where it counts.
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
        /// barrel direction, and the gun's up (from the barrel towards the sight, so no extra axis guess). False for
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
            direction = barrel.TransformDirection(localDirection);
            Vector3 toSight = point - barrel.position;
            toSight -= direction * Vector3.Dot(toSight, direction);
            up = toSight.sqrMagnitude > 0.000025f ? toSight.normalized : Vector3.up;
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
            VRLog.Info("gun: \"" + model.name + "\" muzzle axis " + localDirection + " (match " + Mathf.Max(ax, Mathf.Max(ay, az)).ToString("0.00") + ").");
        }
    }
}
