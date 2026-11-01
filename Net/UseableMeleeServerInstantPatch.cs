using System;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Server side of physical melee (protocol 6). UseableMelee.simulate runs the hit (fire) once the private isDamageable getter
    /// says so, partway into the swing anim, on the server and on the owning client by the same input-tick clock; the server only
    /// takes the client's hit when it runs fire in the same tick, because it replaces its raycast inputs with every input packet.
    /// A VR client of protocol 6 lands every one-shot melee hit on the swing's first tick (VR/VRMelee.InstantHits and
    /// Patches/UseableMeleeHitPatch in UnturnedVR.dll), and says so with the instant-melee bit of its pose. For such a player this
    /// postfix makes isDamageable true as soon as the swing is in use, so the server runs the hit in that same tick.
    ///
    /// Remote players only: the local player of singleplayer or a hosted game is handled by the client patch. Repeated melee
    /// (chainsaw, blowtorch) is left alone: its isDamageable sets the cut rate. What stays as vanilla: the swing cooldown (isUseable; protocol 7 TEST: see UseableMeleeServerCooldownPatch),
    /// stamina, range and occlusion checks, damage. A client that sets the bit without being VR gains the anim's lead time only.
    /// </summary>
    [HarmonyPatch(typeof(UseableMelee), "isDamageable", MethodType.Getter)]
    internal static class UseableMeleeServerInstantPatch
    {
        private static void Postfix(UseableMelee __instance, ref bool __result)
        {
            if (__result)
            {
                return;
            }
            try
            {
                // simulate only asks while the swing is in use (isUsing && isDamageable), so true here means "hit now".
                if (VRNetServerMelee.IsInstant(__instance))
                {
                    __result = true;
                }
            }
            catch (Exception e)
            {
                VRLog.Warn("net: instant melee check failed, the vanilla hit timing is used: " + e.Message);
            }
        }
    }

    /// <summary>
    /// TEST (protocol 7): a short swing cooldown for a remote VR player's one-shot melee. UseableMelee.isUseable (private getter:
    /// the last attack's anim has played out) gates startPrimary / startSecondary; for such a player it is true once
    /// VRNetMeleeCooldown.Seconds have passed since the last swing, matching the client (Patches/UseableMeleeCooldownPatch in
    /// UnturnedVR.dll). A new swing still waits for the last hit (isBusy, cleared on the swing's first tick by the instant hit
    /// above), and the strong attack still needs and uses stamina.
    /// </summary>
    [HarmonyPatch(typeof(UseableMelee), "isUseable", MethodType.Getter)]
    internal static class UseableMeleeServerCooldownPatch
    {
        private static void Postfix(UseableMelee __instance, ref bool __result)
        {
            if (__result)
            {
                return;
            }
            try
            {
                // The cheap simulation-frame compare first, the pose store lookup only once the short guard has passed.
                if (VRNetMeleeCooldown.HasPassed(__instance) && VRNetServerMelee.IsInstant(__instance))
                {
                    __result = true;
                }
            }
            catch (Exception e)
            {
                VRLog.Warn("net: melee cooldown check failed, the vanilla cooldown is used: " + e.Message);
            }
        }
    }

    /// <summary>
    /// The short swing cooldown VR players get instead of the anim-length one (client and server alike): a guard against
    /// spam-swinging that a physical swing does not feel. Counted in input simulation frames (PlayerInput.RATE, 0.08 s), the
    /// clock UseableMelee.startedUse uses, so the client and the server agree on the frame.
    /// </summary>
    internal static class VRNetMeleeCooldown
    {
        public const float Seconds = 0.5f;

        // 0.5 s / 0.08 s = 6.25: 7 frames (0.56 s), the first whole frame count that is at least the guard.
        private static readonly uint Frames = (uint)Mathf.CeilToInt(Seconds / PlayerInput.RATE);

        private static readonly AccessTools.FieldRef<UseableMelee, uint> startedUse = SafeFieldRef();

        /// <summary>At least <see cref="Seconds"/> have passed since this melee's last swing started. False if the field is missing.</summary>
        public static bool HasPassed(UseableMelee melee)
        {
            if (startedUse == null || melee == null || melee.player == null || melee.player.input == null)
            {
                return false;
            }
            return melee.player.input.simulation - startedUse(melee) >= Frames;
        }

        private static AccessTools.FieldRef<UseableMelee, uint> SafeFieldRef()
        {
            try
            {
                return AccessTools.FieldRefAccess<UseableMelee, uint>("startedUse");
            }
            catch (Exception e)
            {
                VRLog.Warn("net: UseableMelee.startedUse not found, VR players keep the vanilla melee cooldown: " + e.Message);
                return null;
            }
        }
    }

    internal static class VRNetServerMelee
    {
        private static ulong lastLogged;

        /// <summary>This swing is a remote VR player's one-shot melee swing, and that player's client hits on the first tick.</summary>
        public static bool IsInstant(UseableMelee melee)
        {
            if (!Provider.isServer || melee == null || melee.channel == null || melee.channel.IsLocalPlayer || melee.channel.owner == null)
            {
                return false;
            }
            if (!(melee.player.equipment.asset is ItemMeleeAsset asset) || asset.isRepeated)
            {
                return false;
            }
            ulong id = melee.channel.owner.playerID.steamID.m_SteamID;
            // The latest pose, whatever its age: the bit is the client version's fixed behaviour for the session, not a per-swing
            // or per-item claim; the item is checked above from the server's own equipment state.
            if (!VRNetPoseStore.TryGetFresh(id, float.MaxValue, out VRPoseData pose, out _) || !pose.HitsOnFirstTick)
            {
                return false;
            }
            if (lastLogged != id)
            {
                lastLogged = id;
                VRLog.Info("net: instant melee hits for VR player " + id + ".");
            }
            return true;
        }
    }
}
