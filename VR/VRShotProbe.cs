using System;
using System.Reflection;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Diagnostic (VRDebugFlags.LogShotOffset): for each shot, how far below the reticle the bullet ends up at the
    /// distance the reticle points at. The sight line and the shot are parallel (the shot leaves the muzzle along the
    /// barrel), so the difference is the sight's height over the bore plus the game's bullet drop over that distance.
    /// Spread and the viewmodel recoil kick are not included; the spread radius is logged for comparison.
    /// Cost: only while the flag is on, one raycast and two cached reflection calls per shot; never throws.
    /// </summary>
    internal static class VRShotProbe
    {
        private const float MaxReticleDistance = 200f;
        private const float AssumedDistance = 100f;

        private static readonly MethodInfo gravityMethod =
            typeof(UseableGun).GetMethod("CalculateBulletGravity", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
        private static readonly MethodInfo spreadMethod =
            typeof(UseableGun).GetMethod("CalculateSpreadAngleRadians", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);

        /// <summary>Called from UseableGunMuzzlePatch with the muzzle pose the shot is about to use.</summary>
        public static void Log(UseableGun gun, Vector3 muzzle, Quaternion rotation)
        {
            if (!VRDebugFlags.LogShotOffset)
            {
                return;
            }

            try
            {
                Player player = gun.player;
                ItemGunAsset asset = gun.equippedGunAsset;
                if (player == null || asset == null
                    || !VRGun.TryGetSightLine(player, out Vector3 point, out Vector3 direction, out _))
                {
                    return;
                }

                bool hit = Physics.Raycast(point, direction, out RaycastHit info, MaxReticleDistance, RayMasks.DAMAGE_CLIENT);
                float distance = hit ? info.distance : AssumedDistance;

                // Muzzle relative to the sight line: `ahead` is how far the muzzle is in front of the sight along the
                // barrel (does not count), `offset` is the sideways offset between the two parallel lines (the height
                // over the bore, as a world vector). Only the offset decides where the bullet is relative to the reticle.
                Vector3 delta = muzzle - point;
                float ahead = Vector3.Dot(delta, direction);
                Vector3 offset = delta - ahead * direction;
                float overBore = offset.magnitude;

                float acceleration = Physics.gravity.y * asset.bulletGravityMultiplier;
                if (gravityMethod != null)
                {
                    acceleration = (float) gravityMethod.Invoke(gun, null);
                }
                float speed = Mathf.Max(asset.muzzleVelocity, 0.01f);
                float seconds = distance / speed;
                float drop = 0.5f * Mathf.Abs(acceleration) * seconds * seconds;

                // Bullet height relative to the point the reticle sits on, in the world (negative = below).
                float relative = offset.y - drop;
                bool muzzlePastTarget = hit && distance < ahead; // gun pushed into a surface: the shot starts behind it

                string spread = "?";
                if (spreadMethod != null)
                {
                    float radians = (float) spreadMethod.Invoke(gun, null);
                    spread = (distance * Mathf.Tan(radians) * 100f).ToString("0") + " cm";
                }

                VRLog.Info("shot: aiming=" + (gun.isAiming ? "yes" : "no")
                    + " | sight over bore " + (overBore * 100f).ToString("0.0") + " cm"
                    + " | reticle point " + distance.ToString("0.0") + " m" + (hit ? "" : " (no hit, assumed)")
                    + " | drop at that distance " + (drop * 100f).ToString("0.0") + " cm"
                    + " | bullet ends " + Mathf.Abs(relative * 100f).ToString("0.0") + " cm " + (relative <= 0f ? "below" : "above") + " the reticle"
                    + " | spread radius " + spread
                    + (muzzlePastTarget ? " | NOTE the muzzle is past the surface the reticle is on, ignore the offset" : "") + ".");
            }
            catch (Exception e)
            {
                VRLog.Warn("shot probe failed: " + e.Message);
            }
        }
    }
}
