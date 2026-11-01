using System;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Server side of the physical grenade throw (protocol 7). UseableThrowable.tick throws on the server and on the owning client
    /// alike, once the private isThrowable getter says so (60% into the Use anim), from player.look.aim (the eye) along it; the
    /// server's throwable is the one that does damage. A VR client throws by hand (VR/VRThrow in UnturnedVR.dll): at the release it
    /// sends a shot pose (VRNetRpc.ReceiveShotPose) whose muzzle is the hand and the throw direction, then presses the attack.
    /// For such a remote player, while that shot pose is queued:
    /// - isThrowable is true at once, so the server throws in the tick it starts the attack, like the client.
    /// - tick puts the aim on the shot pose for the call and restores it afterwards (the same way UseableGunServerMuzzlePatch does
    ///   for a launcher), with the launcher's checks: within a few metres of the server's eye and no wall in between.
    /// Without a shot pose (lost, or it arrives late) the vanilla timing and eye aim are kept. Force, fuse and damage stay vanilla.
    /// </summary>
    internal static class VRNetServerThrow
    {
        /// <summary>The same allowance as a launcher shot: the shot pose is sent just before the input packet that starts the throw.</summary>
        public const float MaxShotPoseAgeSeconds = VRNetServerMuzzle.MaxShotPoseAgeSeconds;

        public static readonly FieldInfo SwingingField = AccessTools.Field(typeof(UseableThrowable), "isSwinging");

        /// <summary>A remote player's throwable, on the server.</summary>
        public static bool IsRemote(UseableThrowable throwable, out ulong id)
        {
            id = 0;
            if (!VRRole.CanRunServerCode || throwable == null || throwable.channel == null || throwable.channel.IsLocalPlayer
                || throwable.channel.owner == null)
            {
                return false;
            }
            id = throwable.channel.owner.playerID.steamID.m_SteamID;
            return true;
        }
    }

    [HarmonyPatch(typeof(UseableThrowable), "isThrowable", MethodType.Getter)]
    internal static class UseableThrowableServerInstantPatch
    {
        private static void Postfix(UseableThrowable __instance, ref bool __result)
        {
            if (__result)
            {
                return;
            }
            try
            {
                if (VRNetServerThrow.IsRemote(__instance, out ulong id) && VRNetPoseStore.HasShot(id, VRNetServerThrow.MaxShotPoseAgeSeconds))
                {
                    __result = true;
                }
            }
            catch (Exception e)
            {
                VRLog.Warn("net: instant throw check failed, the vanilla timing is used: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(UseableThrowable), nameof(UseableThrowable.tick))]
    internal static class UseableThrowableServerAimPatch
    {
        private struct SavedPose
        {
            public bool Swapped;
            public Transform Aim;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static void Prefix(UseableThrowable __instance, out SavedPose __state)
        {
            __state = default;
            try
            {
                // tick runs every frame for every remote player holding a throwable: the queued-shot lookup first, so the
                // reflection below only runs for a hand throw. isThrowable is not read: with a shot queued for a remote player
                // UseableThrowableServerInstantPatch makes it true, so it would always pass here.
                if (!VRNetServerThrow.IsRemote(__instance, out ulong id) || VRNetServerThrow.SwingingField == null
                    || !VRNetPoseStore.HasShot(id, VRNetServerThrow.MaxShotPoseAgeSeconds)
                    || !__instance.player.equipment.IsEquipAnimationFinished
                    || !(bool)VRNetServerThrow.SwingingField.GetValue(__instance))
                {
                    return; // tick does not throw this call
                }

                Transform aim = __instance.player.look != null ? __instance.player.look.aim : null;
                if (aim == null || !VRNetPoseStore.TryTakeShot(id, VRNetServerThrow.MaxShotPoseAgeSeconds, out VRPoseData pose, out _))
                {
                    return;
                }
                Vector3 serverEye = aim.position;
                LauncherMuzzleResult result = VRNetServerMuzzle.CheckPose(pose, serverEye);
                if (result == LauncherMuzzleResult.Muzzle && Physics.Linecast(serverEye, pose.MuzzlePosition, RayMasks.DAMAGE_SERVER))
                {
                    result = LauncherMuzzleResult.BlockedByWall;
                }
                if (result != LauncherMuzzleResult.Muzzle)
                {
                    VRLog.Info("net: hand throw of " + id + " from the eye instead: " + result + ".");
                    return;
                }

                __state.Swapped = true;
                __state.Aim = aim;
                __state.Position = aim.position;
                __state.Rotation = aim.rotation;
                aim.SetPositionAndRotation(pose.MuzzlePosition, Quaternion.LookRotation(pose.MuzzleDirection));
            }
            catch (Exception e)
            {
                VRLog.Warn("net: hand throw failed, the vanilla aim is used: " + e.Message);
            }
        }

        // Finalizer, not Postfix: the aim must be restored even if tick() throws.
        private static void Finalizer(SavedPose __state)
        {
            if (__state.Swapped && __state.Aim != null)
            {
                __state.Aim.SetPositionAndRotation(__state.Position, __state.Rotation);
            }
        }
    }
}
