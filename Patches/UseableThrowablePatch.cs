using HarmonyLib;
using SDG.Unturned;
using System.Reflection;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Physical grenade throw (VRThrow): the throwable leaves the hand at the release, the way the hand threw it.
    /// - UseableThrowable.isThrowable (private getter): true while a released throw is pending, so tick throws at once instead of
    ///   60% into the Use anim.
    /// - UseableThrowable.tick spawns the throwable 1 m along player.look.aim. For a released throw the aim is put on the hand,
    ///   looking the way the hand threw, and restored afterwards (the same way UseableFisherCastPatch casts the bobber).
    /// - Player.playSound: the use sound the game plays when the throw starts begins after its pin part, which already played at
    ///   the arming (VRThrow.SoundPlayed).
    /// Only for the local player, and only where the server does the same (VRThrow.HandAimed); the server's side is
    /// Net/UseableThrowableServerPatch.
    /// </summary>
    [HarmonyPatch(typeof(UseableThrowable), "isThrowable", MethodType.Getter)]
    internal static class UseableThrowableInstantPatch
    {
        private static void Postfix(UseableThrowable __instance, ref bool __result)
        {
            if (!__result && VR.VRThrow.HasPendingThrow(__instance))
            {
                __result = true;
            }
        }
    }

    // Every playSound overload ends in this one.
    [HarmonyPatch(typeof(Player), nameof(Player.playSound), new[] { typeof(AudioClip), typeof(float), typeof(float), typeof(float) })]
    internal static class UseableThrowableSoundPatch
    {
        private static void Postfix(Player __instance, AudioClip clip, OneShotAudioHandle __result)
        {
            // Runs for every sound of every player: a static bool first, the Unity object compare only while a throw waits for its sound.
            if (VR.VRThrow.ExpectsSound && __instance == Player.LocalPlayer)
            {
                VR.VRThrow.SoundPlayed(clip, __result);
            }
        }
    }

    [HarmonyPatch(typeof(UseableThrowable), nameof(UseableThrowable.tick))]
    internal static class UseableThrowableAimPatch
    {
        private static readonly FieldInfo swingingField = AccessTools.Field(typeof(UseableThrowable), "isSwinging");

        private struct SavedPose
        {
            public bool Swapped;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static void Prefix(UseableThrowable __instance, out SavedPose __state)
        {
            __state = default;
            // tick runs every frame for every player holding a throwable: the static pending flag first, the rest only for a release.
            if (!VR.VRThrow.HasPendingThrow(__instance) || swingingField == null)
            {
                return;
            }
            Transform aim = __instance.player != null && __instance.player.look != null ? __instance.player.look.aim : null;
            if (aim == null || !(bool)swingingField.GetValue(__instance))
            {
                return;
            }
            VR.VRThrow.GetThrowRay(out Vector3 position, out Quaternion rotation);
            __state.Swapped = true;
            __state.Position = aim.position;
            __state.Rotation = aim.rotation;
            aim.SetPositionAndRotation(position, rotation);
        }

        // Finalizer, not Postfix: the aim must be restored even if tick() throws.
        private static void Finalizer(UseableThrowable __instance, SavedPose __state)
        {
            if (!__state.Swapped)
            {
                return;
            }
            __instance.player.look.aim.SetPositionAndRotation(__state.Position, __state.Rotation);
            if (!(bool)swingingField.GetValue(__instance))
            {
                VR.VRThrow.ThrowSpawned();
            }
        }
    }
}
