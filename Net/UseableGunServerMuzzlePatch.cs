using System;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Server side of the rocket launcher fix. UseableGun.fire runs on the server for a remote player, and for a launcher it spawns the
    /// damaging rocket at player.look.aim (the eye) and tells the other clients to spawn a copy from the same origin and direction. For
    /// the duration of the call the aim transform is put on the muzzle the player's client reported (VRNetServerMuzzle), and restored
    /// afterwards, so PlayerLook and everything else keep their eye-based aim. The client's own shot, with the same swap, is
    /// Patches/UseableGunMuzzlePatch in UnturnedVR.dll; the two never both apply: this one is for remote players only.
    /// If anything is missing or does not hold up the vanilla aim is kept.
    /// </summary>
    [HarmonyPatch(typeof(UseableGun), "fire")]
    internal static class UseableGunServerMuzzlePatch
    {
        private struct SavedPose
        {
            public bool Swapped;
            public Transform Aim;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static void Prefix(UseableGun __instance, out SavedPose __state)
        {
            __state = default;

            try
            {
                if (VRNetServerMuzzle.TryGet(__instance, out Vector3 position, out Quaternion rotation, out Transform aim) != LauncherMuzzleResult.Muzzle)
                {
                    return;
                }

                __state.Swapped = true;
                __state.Aim = aim;
                __state.Position = aim.position;
                __state.Rotation = aim.rotation;
                aim.SetPositionAndRotation(position, rotation);
            }
            catch (Exception e)
            {
                NetLog.Warn("net: launcher muzzle failed, the vanilla aim is used: " + e.Message);
            }
        }

        // Finalizer, not Postfix: fire() throws on insufficient ammo and the aim must be restored anyway.
        private static void Finalizer(SavedPose __state)
        {
            if (__state.Swapped && __state.Aim != null)
            {
                __state.Aim.SetPositionAndRotation(__state.Position, __state.Rotation);
            }
        }
    }
}
