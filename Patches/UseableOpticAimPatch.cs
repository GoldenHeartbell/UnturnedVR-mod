using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Binoculars: while zoomed, the primary action sets a map marker where player.look.aim points (a raycast from the
    /// head). The lens picture follows the (calmed) head direction, so for the duration of the call the aim transform
    /// is put on exactly that view (VRBinoculars.TryGetView) and restored afterwards: the marker lands where the
    /// picture's centre is.
    /// Same shape as UseableGunMuzzlePatch.
    /// </summary>
    [HarmonyPatch(typeof(UseableOptic), nameof(UseableOptic.startPrimary))]
    internal static class UseableOpticAimPatch
    {
        private struct SavedPose
        {
            public bool Swapped;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static void Prefix(UseableOptic __instance, out SavedPose __state)
        {
            __state = default;
            if (!VR.VRState.IsActive || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }

            Player player = __instance.player;
            Transform aim = player != null && player.look != null ? player.look.aim : null;
            if (aim == null || !VR.VRBinoculars.TryGetView(out Vector3 position, out Quaternion rotation))
            {
                return;
            }

            __state.Swapped = true;
            __state.Position = aim.position;
            __state.Rotation = aim.rotation;
            aim.SetPositionAndRotation(position, rotation);
        }

        private static void Finalizer(UseableOptic __instance, SavedPose __state)
        {
            if (__state.Swapped)
            {
                __instance.player.look.aim.SetPositionAndRotation(__state.Position, __state.Rotation);
            }
        }
    }
}
