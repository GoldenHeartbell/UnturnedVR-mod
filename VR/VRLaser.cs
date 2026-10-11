using System.Reflection;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The tactical laser's red dot marks where the gun points instead of where the head looks.
    /// UseableGun.Update places the game's dot (private laserTransform / laserGameObject) from player.look.aim, which in
    /// VR follows the head. The gun is only in its final pose later in the frame (VRArms.Apply), so the dot is
    /// repositioned from there, from the muzzle (VRGun.TryGetMuzzle: the ray the shot uses).
    /// The game's rules are kept: the dot only exists while the fitted tactical attachment is a laser and switched on
    /// (the game creates and destroys it, this class never does), first person only, hidden in freecam, same ray mask
    /// (RayMasks.BLOCK_LASER) and range as the game, 5 cm back from the hit, hidden on a miss, no drop compensation,
    /// size by TacticalLaserScale. When the muzzle pose is not usable (gun pushed into a wall: the shot uses the head
    /// aim then) the game's own head-based placement is left alone, so the dot always shows the shot's ray.
    /// Cost: two reflection reads, one linecast and one raycast per call (twice a frame); nothing without a laser.
    /// </summary>
    internal static class VRLaser
    {
        private const float MaxDistance = 2048f; // as UseableGun.Update
        private const float BackOff = 0.05f;     // as UseableGun.Update

        private static readonly FieldInfo objectField =
            typeof(UseableGun).GetField("laserGameObject", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo transformField =
            typeof(UseableGun).GetField("laserTransform", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool loggedMissing;
        private static bool loggedTakeover;

        /// <summary>From VRArms.Apply right after the item is placed (LateUpdate and before-render).</summary>
        public static void Update()
        {
            Player player = Player.LocalPlayer;
            if (!VRState.IsActive || player == null || player.equipment == null || player.look == null)
            {
                return;
            }

            UseableGun gun = player.equipment.useable as UseableGun;
            if (gun == null)
            {
                return;
            }

            if (objectField == null || transformField == null)
            {
                if (!loggedMissing)
                {
                    loggedMissing = true;
                    VRLog.Warn("laser: UseableGun.laserGameObject/laserTransform not found, the dot keeps the head aim.");
                }
                return;
            }

            GameObject dot = objectField.GetValue(gun) as GameObject;
            Transform dotTransform = transformField.GetValue(gun) as Transform;
            if (dot == null || dotTransform == null)
            {
                return; // no laser fitted or switched off: the game owns that
            }

            if (player.look.perspective != EPlayerPerspective.FIRST || player.look.IsLocallyUsingFreecam)
            {
                return;
            }

            if (!VRGun.TryGetMuzzle(player, out Vector3 origin, out Quaternion rotation))
            {
                return; // same fallback as the shot: head aim, which is what the game already did
            }

            Vector3 direction = rotation * Vector3.forward;
            if (Physics.Raycast(origin, direction, out RaycastHit hit, MaxDistance, RayMasks.BLOCK_LASER))
            {
                dotTransform.position = hit.point - direction * BackOff;
                if (!dot.activeSelf)
                {
                    dot.SetActive(true);
                }
            }
            else if (dot.activeSelf)
            {
                dot.SetActive(false);
            }

            if (!loggedTakeover)
            {
                loggedTakeover = true;
                VRLog.Info("laser: dot follows the muzzle (" + (hit.collider != null ? "hit at " + hit.distance.ToString("0.0") + " m" : "no hit") + ").");
            }
        }
    }
}
