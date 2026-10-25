using System;
using HarmonyLib;
using SDG.Unturned;

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
    /// (chainsaw, blowtorch) is left alone: its isDamageable sets the cut rate. What stays as vanilla: the swing cooldown (isUseable),
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
