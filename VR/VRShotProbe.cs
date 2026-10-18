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

        /// <summary>
        /// Bows only (EAction.String), always on, one line per shot: the data needed to see why an arrow does not go where the
        /// bow points. Compares the shot direction (the Barrel hook's guessed axis) with the head, and with the nock / rest to
        /// barrel lines (the arrow's physical line), and prints the hook layout of the model, the spread and the drop at 20 m.
        /// Called from UseableGunMuzzlePatch with the muzzle pose the shot is about to use; never throws.
        /// </summary>
        public static void LogBow(UseableGun gun, Vector3 muzzle, Quaternion rotation)
        {
            try
            {
                ItemGunAsset asset = gun.equippedGunAsset;
                Transform model = gun.player != null && gun.player.equipment != null ? gun.player.equipment.firstModel : null;
                if (asset == null || asset.action != EAction.String || model == null)
                {
                    return;
                }

                Attachments attachments = model.GetComponent<Attachments>();
                Vector3 shot = rotation * Vector3.forward;
                Camera eye = VRRig.ActiveCamera;

                string line = "bow shot: \"" + model.name + "\" size " + asset.size_x + "x" + asset.size_y + " aiming=" + (gun.isAiming ? "yes" : "no");
                if (eye != null)
                {
                    line += " | shot vs head forward " + Vector3.Angle(shot, eye.transform.forward).ToString("0.0") + " deg"
                        + " (pitch " + PitchOf(shot).ToString("0.0") + " vs head " + PitchOf(eye.transform.forward).ToString("0.0") + ")";
                }
                if (attachments != null)
                {
                    Transform barrel = attachments.barrelHook;
                    Transform nock = attachments.nockHook;
                    Transform rest = attachments.restHook;
                    // Which way the model's axes point in the world (up component, right component), to read how the bow is turned in the hand.
                    line += " | model axes (world up/right): +y " + model.up.y.ToString("+0.00;-0.00") + "/" + Vector3.Dot(model.up, eye != null ? eye.transform.right : Vector3.right).ToString("+0.00;-0.00")
                        + ", +z " + model.forward.y.ToString("+0.00;-0.00") + "/" + Vector3.Dot(model.forward, eye != null ? eye.transform.right : Vector3.right).ToString("+0.00;-0.00");
                    if (barrel != null && nock != null)
                    {
                        line += " | shot vs nock->barrel " + Vector3.Angle(shot, barrel.position - nock.position).ToString("0.0") + " deg";
                    }
                    if (barrel != null && rest != null)
                    {
                        Vector3 stringLine = barrel.position - rest.position;
                        line += " | shot vs rest->barrel " + Vector3.Angle(shot, stringLine).ToString("0.0") + " deg";
                        // Signed, in the model's own frame (left negative / right positive; down negative / up positive): how far the shot
                        // is turned from the string line sideways (yaw) and up or down (pitch).
                        Vector3 shotLocal = model.InverseTransformDirection(shot);
                        Vector3 lineLocal = model.InverseTransformDirection(stringLine);
                        line += " (yaw " + SignedYaw(lineLocal, shotLocal).ToString("+0.0;-0.0") + ", pitch " + SignedPitch(lineLocal, shotLocal).ToString("+0.0;-0.0") + " deg)";
                    }
                    line += " | more hooks: magazine " + Local(model, attachments.magazineHook) + " left " + Local(model, attachments.leftHook) + " right " + Local(model, attachments.rightHook);
                    line += " | nocked arrow (magazine model) " + ArrowAxis(model, attachments.magazineModel, shot);
                    line += " | hooks in model space: barrel " + Local(model, barrel) + " nock " + Local(model, nock) + " rest " + Local(model, rest)
                        + " sight " + Local(model, attachments.sightHook) + " aim " + Local(model, attachments.aimHook) + " view " + Local(model, attachments.viewHook);
                    if (barrel != null)
                    {
                        Vector3 up = model.InverseTransformDirection(barrel.up);
                        Vector3 fwd = model.InverseTransformDirection(barrel.forward);
                        line += " | barrel up " + up.ToString("0.00") + " forward " + fwd.ToString("0.00") + " in model space";
                    }
                }
                else
                {
                    line += " | no Attachments component";
                }

                float acceleration = Physics.gravity.y * asset.bulletGravityMultiplier;
                if (gravityMethod != null)
                {
                    acceleration = (float) gravityMethod.Invoke(gun, null);
                }
                float seconds = 20f / Mathf.Max(asset.muzzleVelocity, 0.01f);
                line += " | speed " + asset.muzzleVelocity.ToString("0") + " m/s, drop at 20 m " + (0.5f * Mathf.Abs(acceleration) * seconds * seconds * 100f).ToString("0") + " cm";
                if (spreadMethod != null)
                {
                    float radians = (float) spreadMethod.Invoke(gun, null);
                    line += ", spread radius at 20 m " + (20f * Mathf.Tan(radians) * 100f).ToString("0") + " cm";
                }
                VRLog.Info(line + ".");
            }
            catch (Exception e)
            {
                VRLog.Warn("bow shot probe failed: " + e.Message);
            }
        }

        // The nocked arrow (the magazine model on the Magazine hook): where it sits and which way its longest mesh axis points, in the model's
        // frame, signed towards the shot, with the angle to the shot and its yaw (+ = to the right of the shot, +z is right for the upright bow)
        // and pitch (+ = above the shot, +y is up). This is the arrow the player sees, to compare with the direction it flies.
        private static string ArrowAxis(Transform model, Transform arrow, Vector3 shot)
        {
            if (arrow == null)
            {
                return "none";
            }

            MeshFilter best = null;
            float bestVolume = 0f;
            foreach (MeshFilter filter in arrow.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }
                Vector3 size = filter.sharedMesh.bounds.size;
                float volume = size.x * size.y * size.z + size.x + size.y + size.z; // flat meshes still count
                if (best == null || volume > bestVolume)
                {
                    best = filter;
                    bestVolume = volume;
                }
            }
            if (best == null)
            {
                return "at " + Local(model, arrow) + ", no mesh";
            }

            Vector3 extent = best.sharedMesh.bounds.size;
            Vector3 localAxis = extent.x >= extent.y && extent.x >= extent.z ? Vector3.right : extent.y >= extent.z ? Vector3.up : Vector3.forward;
            Vector3 direction = model.InverseTransformDirection(best.transform.TransformDirection(localAxis)).normalized;
            Vector3 shotLocal = model.InverseTransformDirection(shot).normalized;
            if (Vector3.Dot(direction, shotLocal) < 0f)
            {
                direction = -direction;
            }
            float yaw = Vector3.SignedAngle(new Vector3(shotLocal.x, 0f, shotLocal.z), new Vector3(direction.x, 0f, direction.z), Vector3.up);
            float pitch = Elevation(direction) - Elevation(shotLocal);
            return "\"" + best.name + "\" centre " + Local(model, best.transform) + " mesh size " + extent.ToString("0.00") + " points " + direction.ToString("0.00")
                + " in model space, " + Vector3.Angle(direction, shotLocal).ToString("0.0") + " deg from the shot (yaw " + yaw.ToString("+0.0;-0.0") + ", pitch " + pitch.ToString("+0.0;-0.0") + ")";
        }

        // Turn from the line to the shot around the model's up (y): clockwise seen from above = to the right, positive.
        private static float SignedYaw(Vector3 line, Vector3 shot)
        {
            return Vector3.SignedAngle(new Vector3(line.x, 0f, line.z), new Vector3(shot.x, 0f, shot.z), Vector3.up);
        }

        private static float SignedPitch(Vector3 line, Vector3 shot)
        {
            return Elevation(shot) - Elevation(line);
        }

        private static float Elevation(Vector3 v)
        {
            return Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) * Mathf.Rad2Deg;
        }

        private static float PitchOf(Vector3 direction)
        {
            return Mathf.Asin(Mathf.Clamp(direction.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        private static string Local(Transform model, Transform hook)
        {
            return hook != null ? model.InverseTransformPoint(hook.position).ToString("0.00") : "none";
        }

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
