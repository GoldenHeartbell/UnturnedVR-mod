using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Physical punches (VRMelee): PlayerEquipment.punch (private) casts the punch ray from player.look.aim, 1.75 m. While
    /// VRMelee has a pending punch contact, the aim is put just before the contact point, looking along the punch, for that
    /// call and restored afterwards, as UseableMeleeHitPatch does for melee weapons. The game's raycast, limb, damage,
    /// sound and checks then apply. Only for the local player and only while a punch contact is pending.
    /// </summary>
    [HarmonyPatch(typeof(PlayerEquipment), "punch")]
    internal static class PlayerEquipmentPunchPatch
    {
        private struct SavedPose
        {
            public bool Swapped;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static void Prefix(PlayerEquipment __instance, out SavedPose __state)
        {
            __state = default;
            Transform aim = __instance.player != null && __instance.player.look != null ? __instance.player.look.aim : null;
            if (aim == null || !VR.VRMelee.TryTakePunchRay(__instance, out Vector3 position, out Quaternion rotation))
            {
                return;
            }
            __state.Swapped = true;
            __state.Position = aim.position;
            __state.Rotation = aim.rotation;
            aim.SetPositionAndRotation(position, rotation);
        }

        // Finalizer, not Postfix: the aim must be restored even if punch() throws.
        private static void Finalizer(PlayerEquipment __instance, SavedPose __state)
        {
            if (__state.Swapped)
            {
                __instance.player.look.aim.SetPositionAndRotation(__state.Position, __state.Rotation);
            }
        }
    }
}
