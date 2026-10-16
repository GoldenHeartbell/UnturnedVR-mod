using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Net
{
    internal enum LauncherMuzzleResult
    {
        /// <summary>Start the rocket at the player's muzzle.</summary>
        Muzzle,

        /// <summary>This is not a server-side shot of a launcher by a remote player: nothing to do.</summary>
        NotApplicable,

        NoFreshPose,
        NoMuzzleInPose,
        TooFarFromEye,
        BlockedByWall,
    }

    /// <summary>
    /// Server side: where a rocket launcher's rocket starts. The client says where its muzzle is; the server only uses that when it
    /// holds up against what the server knows itself, and otherwise starts the rocket where vanilla would (the eye). The pose only
    /// moves where a server-created rocket starts, within a few metres of the player and never through a wall.
    ///
    /// Which pose: the one the client sent at the instant of this shot (<see cref="VRNetRpc.ReceiveShotPose"/>, queued in
    /// <see cref="VRNetPoseStore"/>), because the server runs the shot after the client fired it and the newest 20-a-second pose is
    /// from later, when the gun has moved (in aim-down-sights a fraction of a degree is metres at range). Without a shot pose (it
    /// was lost, or an older client) the newest ordinary pose is the fallback.
    /// </summary>
    internal static class VRNetServerMuzzle
    {
        /// <summary>
        /// A queued shot pose is used if it is at most this old. The server runs a shot when it processes the input packet that
        /// carries it, which is after the client plays its 0.08 s step out (about 0.06 s after fire ran), the network and the server's input queue, so a second is generous; it is not
        /// longer so that a shot the server never ran (say an ammo mismatch) cannot be picked up by the next one, launchers being
        /// seconds apart.
        /// </summary>
        public const float MaxShotPoseAgeSeconds = 1f;

        /// <summary>The fallback ordinary pose is used if it is at most this old: poses arrive 20 times a second, so this allows a few lost ones.</summary>
        public const float MaxPoseAgeSeconds = 0.4f;

        /// <summary>
        /// How far the muzzle may be from the server's own eye of the player: arm and a long launcher held out, plus the gap
        /// between the client's head and the server's (the server simulates the player a little behind the client).
        /// </summary>
        public const float MaxMuzzleDistanceFromServerEye = 4f;

        /// <summary>The checks that need no physics, so they can be tested alone. The wall check follows in <see cref="TryGet"/>.</summary>
        public static LauncherMuzzleResult CheckPose(in VRPoseData pose, Vector3 serverEye)
        {
            if (!pose.HasMuzzle || pose.ItemHand == VRPoseData.ItemHandNone)
            {
                return LauncherMuzzleResult.NoMuzzleInPose;
            }

            if (!VRNetGuard.IsFinite(pose.MuzzlePosition) || !VRNetGuard.IsFinite(serverEye)
                || (pose.MuzzlePosition - serverEye).sqrMagnitude > MaxMuzzleDistanceFromServerEye * MaxMuzzleDistanceFromServerEye)
            {
                return LauncherMuzzleResult.TooFarFromEye;
            }

            return LauncherMuzzleResult.Muzzle;
        }

        /// <summary>
        /// For a remote player's shot with a launcher on the server: the muzzle pose to fire from, and the aim transform to put there.
        /// Anything but <see cref="LauncherMuzzleResult.Muzzle"/> means the vanilla aim is kept.
        /// </summary>
        public static LauncherMuzzleResult TryGet(UseableGun gun, out Vector3 position, out Quaternion rotation, out Transform aim)
        {
            position = default;
            rotation = default;
            aim = null;

            if (!VRRole.CanRunServerCode || gun == null || gun.channel == null || gun.channel.IsLocalPlayer || gun.channel.owner == null)
            {
                return LauncherMuzzleResult.NotApplicable;
            }

            Player player = gun.player;
            aim = player != null && player.look != null ? player.look.aim : null;
            ItemGunAsset asset = player != null && player.equipment != null ? player.equipment.asset as ItemGunAsset : null;
            if (aim == null || asset == null || asset.projectile == null)
            {
                aim = null;
                return LauncherMuzzleResult.NotApplicable; // not a launcher
            }

            // Taking the shot pose uses it up, so each shot gets its own; it is taken even if the checks below then refuse it.
            ulong id = gun.channel.owner.playerID.steamID.m_SteamID;
            if (!VRNetPoseStore.TryTakeShot(id, MaxShotPoseAgeSeconds, out VRPoseData pose, out _)
                && !VRNetPoseStore.TryGetFresh(id, MaxPoseAgeSeconds, out pose, out _))
            {
                aim = null;
                return LauncherMuzzleResult.NoFreshPose;
            }

            Vector3 serverEye = aim.position;
            LauncherMuzzleResult result = CheckPose(pose, serverEye);
            if (result == LauncherMuzzleResult.Muzzle && Physics.Linecast(serverEye, pose.MuzzlePosition, RayMasks.DAMAGE_SERVER))
            {
                result = LauncherMuzzleResult.BlockedByWall;
            }

            if (result != LauncherMuzzleResult.Muzzle)
            {
                aim = null;
                return result;
            }

            position = pose.MuzzlePosition;
            rotation = Quaternion.LookRotation(pose.MuzzleDirection);
            return result;
        }
    }
}
