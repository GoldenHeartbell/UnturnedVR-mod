using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Physical melee (VRMelee): a swing that entered a target hits now, along the swing.
    /// - UseableMelee.isDamageable (private getter, read in simulate right after startPrimary in the same input tick) is
    ///   true for every one-shot swing while VRMelee.InstantHits, so fire() runs at once instead of halfway through the anim.
    /// - UseableMelee.fire (private) casts from player.look.aim. For that call the aim is put just before the contact point,
    ///   looking along the swing, and restored afterwards, the same way UseableGunMuzzlePatch puts it on a gun's muzzle.
    ///   The game's own raycast, limb (Skull = headshot), occlusion and range checks, damage and effects then apply.
    /// - Repeated tools (chainsaw, blowtorch) keep the trigger and the game's timing; each of their cuts / repairs casts from the
    ///   blade base along the tool (VRMelee.TryGetToolRay) instead of from the head.
    /// Only for the local player's weapon. The aim swap only while a contact is pending, or for a repeated tool in a tracked hand. The instant hit only where the server
    /// does the same (VRMelee.InstantHits: singleplayer, or a protocol 6 server); elsewhere the swap runs at the game's own hit frame.
    /// </summary>
    [HarmonyPatch(typeof(UseableMelee), "isDamageable", MethodType.Getter)]
    internal static class UseableMeleeDamageablePatch
    {
        // Every one-shot swing, not only physical contacts: the server (Net/UseableMeleeServerInstantPatch) cannot tell them apart,
        // and both sides must run the hit in the same tick. Repeated melee (chainsaw, blowtorch) is left alone: this sets its cut rate.
        private static void Postfix(UseableMelee __instance, ref bool __result)
        {
            if (!__result && VR.VRMelee.InstantHits && __instance.channel != null && __instance.channel.IsLocalPlayer
                && __instance.player.equipment.asset is ItemMeleeAsset asset && !asset.isRepeated)
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// TEST: a short swing cooldown. UseableMelee.isUseable (private getter: the last attack's anim has played out, checked by
    /// startPrimary / startSecondary and VRMelee) is true for a one-shot swing while VRMelee.InstantHits once
    /// Net.VRNetMeleeCooldown.Seconds (0.5 s) have passed since the last swing, and the last hit has landed (isBusy, cleared by
    /// that hit on the swing's first tick). Stamina for the strong attack still applies. The server
    /// (Net/UseableMeleeServerCooldownPatch, protocol 7) does the same for VR players.
    /// </summary>
    [HarmonyPatch(typeof(UseableMelee), "isUseable", MethodType.Getter)]
    internal static class UseableMeleeCooldownPatch
    {
        private static void Postfix(UseableMelee __instance, ref bool __result)
        {
            if (!__result && VR.VRMelee.InstantHits && __instance.channel != null && __instance.channel.IsLocalPlayer
                && __instance.player.equipment.asset is ItemMeleeAsset asset && !asset.isRepeated
                && Net.VRNetMeleeCooldown.HasPassed(__instance))
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(UseableMelee), "fire")]
    internal static class UseableMeleeHitPatch
    {
        private struct SavedPose
        {
            public bool Swapped;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static void Prefix(UseableMelee __instance, out SavedPose __state)
        {
            __state = default;
            Transform aim = __instance.player != null && __instance.player.look != null ? __instance.player.look.aim : null;
            Vector3 position;
            Quaternion rotation;
            if (aim == null || !(VR.VRMelee.TryTakeHitRay(__instance, out position, out rotation)
                || VR.VRMelee.TryGetToolRay(__instance, out position, out rotation)))
            {
                return;
            }
            __state.Swapped = true;
            __state.Position = aim.position;
            __state.Rotation = aim.rotation;
            aim.SetPositionAndRotation(position, rotation);
        }

        // Finalizer, not Postfix: the aim must be restored even if fire() throws.
        private static void Finalizer(UseableMelee __instance, SavedPose __state)
        {
            if (__state.Swapped)
            {
                __instance.player.look.aim.SetPositionAndRotation(__state.Position, __state.Rotation);
            }
        }
    }
}
