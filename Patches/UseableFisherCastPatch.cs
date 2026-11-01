using HarmonyLib;
using SDG.Unturned;
using System.Reflection;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Physical fishing cast (VRFishing): the rod is thrown, not charged with the trigger.
    /// - UseableFisher.tock: while the gauge is open, strengthMultiplier (the bobber's force and, on the server, the bite time)
    ///   is the throw strength instead of the game's swinging gauge, and the gauge bar shows it.
    /// - UseableFisher.HasReachedAnimationTrigger (private getter): true for a thrown cast, so the bobber leaves the hand at once
    ///   instead of 45% into the cast anim.
    /// - UseableFisher.tick spawns the bobber from player.look.aim. For a thrown cast the aim is put on the hand, looking the way
    ///   the rod was thrown, and restored afterwards (the same way UseableMeleeHitPatch moves it for a melee hit).
    /// Only for the local player's rod.
    /// </summary>
    [HarmonyPatch(typeof(UseableFisher), nameof(UseableFisher.tock))]
    internal static class UseableFisherStrengthPatch
    {
        private static readonly FieldInfo strengthField = AccessTools.Field(typeof(UseableFisher), "strengthMultiplier");
        private static readonly FieldInfo barField = AccessTools.Field(typeof(UseableFisher), "castStrengthBar");

        private static void Postfix(UseableFisher __instance)
        {
            if (strengthField == null || !VR.VRFishing.DrivesStrength(__instance) || VR.VRFishing.GetState(__instance) != VR.VRFishing.StatePreparing)
            {
                return;
            }
            float strength = VR.VRFishing.Strength;
            strengthField.SetValue(__instance, strength);
            if (barField != null && barField.GetValue(__instance) is ISleekImage bar)
            {
                bar.PositionScale_Y = 1f - strength;
                bar.SizeScale_Y = strength;
                bar.TintColor = ItemTool.getQualityColor(strength);
            }
        }
    }

    [HarmonyPatch(typeof(UseableFisher), "HasReachedAnimationTrigger", MethodType.Getter)]
    internal static class UseableFisherInstantCastPatch
    {
        private static readonly FieldInfo castingField = AccessTools.Field(typeof(UseableFisher), "isPlayingCastAnimation");

        private static void Postfix(UseableFisher __instance, ref bool __result)
        {
            if (!__result && castingField != null && VR.VRFishing.HasPendingCast(__instance) && (bool)castingField.GetValue(__instance))
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(UseableFisher), nameof(UseableFisher.tick))]
    internal static class UseableFisherCastPatch
    {
        private static readonly FieldInfo waitingField = AccessTools.Field(typeof(UseableFisher), "isWaitingForAnimationTrigger");
        private static readonly FieldInfo castingField = AccessTools.Field(typeof(UseableFisher), "isPlayingCastAnimation");

        private struct SavedPose
        {
            public bool Swapped;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static void Prefix(UseableFisher __instance, out SavedPose __state)
        {
            __state = default;
            // tick runs every frame while a rod is held: the static pending flag first, the rest only for a thrown cast.
            if (!VR.VRFishing.HasPendingCast(__instance) || waitingField == null || castingField == null)
            {
                return;
            }
            Transform aim = __instance.player != null && __instance.player.look != null ? __instance.player.look.aim : null;
            if (aim == null || !(bool)waitingField.GetValue(__instance) || !(bool)castingField.GetValue(__instance))
            {
                return;
            }
            VR.VRFishing.GetCastRay(out Vector3 position, out Quaternion rotation);
            __state.Swapped = true;
            __state.Position = aim.position;
            __state.Rotation = aim.rotation;
            aim.SetPositionAndRotation(position, rotation);
        }

        // Finalizer, not Postfix: the aim must be restored even if tick() throws.
        private static void Finalizer(UseableFisher __instance, SavedPose __state)
        {
            if (!__state.Swapped)
            {
                return;
            }
            __instance.player.look.aim.SetPositionAndRotation(__state.Position, __state.Rotation);
            if (!(bool)waitingField.GetValue(__instance))
            {
                VR.VRFishing.CastSpawned();
            }
        }
    }
}
