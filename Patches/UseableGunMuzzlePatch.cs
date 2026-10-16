using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Hands phase 4a: UseableGun.fire (private, runs on the owning client and the server; one process in
    /// singleplayer) takes bullet origin and direction from player.look.aim. For the local player the aim
    /// transform is put on the gun's muzzle (VRGun) for the duration of the call and restored afterwards, so
    /// PlayerLook.updateAim and everything else keep their head-based aim.
    /// </summary>
    [HarmonyPatch(typeof(UseableGun), "fire")]
    internal static class UseableGunMuzzlePatch
    {
        private struct SavedPose
        {
            public bool Swapped;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static void Prefix(UseableGun __instance, out SavedPose __state)
        {
            __state = default;
            if (!VR.VRState.IsActive || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }

            Player player = __instance.player;
            Transform aim = player != null && player.look != null ? player.look.aim : null;
            if (aim == null)
            {
                return;
            }
            if (!VR.VRGun.TryGetMuzzle(player, out Vector3 position, out Quaternion rotation))
            {
                VRLog.Warn("shot from the head: " + VR.VRGun.MuzzleFailure + ".");
                return;
            }

            VR.VRShotProbe.Log(__instance, position, rotation); // diagnostic, off unless VRDebugFlags.LogShotOffset

            // On a server that runs the mod, a launcher shot tells the server this exact muzzle pose, so its rocket leaves the same line.
            VR.VRNetPoseSender.SendLauncherShot(player, position, rotation);

            __state.Swapped = true;
            __state.Position = aim.position;
            __state.Rotation = aim.rotation;
            aim.SetPositionAndRotation(position, rotation);
        }

        // Finalizer, not Postfix: fire() throws on insufficient ammo and the aim must be restored anyway.
        private static void Finalizer(UseableGun __instance, SavedPose __state)
        {
            if (__state.Swapped)
            {
                __instance.player.look.aim.SetPositionAndRotation(__state.Position, __state.Rotation);
            }
        }
    }
}
