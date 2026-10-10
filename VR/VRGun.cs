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
            rotation = Quaternion.LookRotation(barrel.TransformDirection(localDirection));
            return true;
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
            cachedModel = model;
            barrel = null;
            cachedAttachments = null;
            Attachments attachments = model.GetComponent<Attachments>();
            cachedAttachments = attachments;
            if (attachments == null || attachments.barrelHook == null)
            {
                VRLog.Warn("gun: \"" + model.name + "\" has no Barrel hook, shots keep the head aim.");
                return;
            }

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
