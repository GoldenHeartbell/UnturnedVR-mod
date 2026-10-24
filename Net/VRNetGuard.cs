using System.Collections.Generic;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Server-side checks every mod RPC goes through. Everything a client sends is untrusted: a modified client can send any
    /// bytes at any rate, so a handler must (1) take the sender's identity from the connection and never from the payload,
    /// (2) refuse senders that are not a live player, (3) read the whole payload or drop it, (4) bounds-check what it read
    /// against what the server itself knows about that player, and (5) never log or store anything the client chose
    /// without a size limit. A failed check drops the message; it never changes game state.
    /// </summary>
    internal static class VRNetGuard
    {
        /// <summary>
        /// How far the reported head may be from the server's own position of that player. Generous on purpose: the head is
        /// tracked by the headset, so it differs from the character position by the room-scale offset, plus latency.
        /// </summary>
        public const float MaxHeadDistanceFromPlayer = 6f;

        /// <summary>How far a hand may be from the head: arm length plus a margin for a held tool. A hand further away is not a real pose.</summary>
        public const float MaxHandDistanceFromHead = 2.5f;

        /// <summary>How far the muzzle may be from the head: arm plus the longest gun held out. Longer than a hand, shorter than a thrown pose.</summary>
        public const float MaxMuzzleDistanceFromHead = 3.5f;

        /// <summary>Per-sender bookkeeping is capped, so a flood of connections cannot grow memory.</summary>
        private const int MaxTrackedSenders = 128;

        private const float AcceptedLogInterval = 5f;
        private const float RejectedLogInterval = 10f;

        internal struct Sender
        {
            public SteamPlayer SteamPlayer;
            public Player Player;
            public ulong Id;
            public bool IsRemote;
        }

        private sealed class SenderState
        {
            public float NextAcceptedLog;
            public float NextRejectedLog;
            public int Rejected;
        }

        private static readonly Dictionary<ulong, SenderState> states = new Dictionary<ulong, SenderState>();
        private static bool subscribed;

        /// <summary>Called once at startup on a machine that can act as a server (it is harmless on a plain client).</summary>
        public static void Initialize()
        {
            if (!subscribed)
            {
                subscribed = true;
                Provider.onServerDisconnected += OnServerDisconnected;
            }
        }

        public static void Shutdown()
        {
            if (subscribed)
            {
                subscribed = false;
                Provider.onServerDisconnected -= OnServerDisconnected;
            }
            states.Clear();
            VRNetPoseStore.Clear();
            VRNetRelay.Clear();
        }

        private static void OnServerDisconnected(CSteamID steamID)
        {
            states.Remove(steamID.m_SteamID);
            VRNetPoseStore.Forget(steamID.m_SteamID);
            VRNetServerTicker.Forget(steamID.m_SteamID);
            VRNetRelay.Forget(steamID.m_SteamID);
        }

        /// <summary>
        /// Identity and liveness. The sender comes from the connection the message arrived on, never from the payload. Only
        /// a connected, spawned, living player passes; anything else is dropped without a log line (nothing to report).
        /// </summary>
        public static bool TryAccept(in ServerInvocationContext context, out Sender sender)
        {
            sender = default;

            // Server handler: only ever acts in the process that is the authority. A remote client never receives these calls,
            // so this is a second line of defence, not the first.
            if (!VRRole.CanRunServerCode)
            {
                return false;
            }

            if (context.origin != ServerInvocationContext.EOrigin.Remote && context.origin != ServerInvocationContext.EOrigin.Loopback)
            {
                return false;
            }

            SteamPlayer steamPlayer = context.GetCallingPlayer();
            Player player = steamPlayer != null ? steamPlayer.player : null;
            if (player == null || player.life == null || player.life.isDead)
            {
                return false;
            }

            sender.SteamPlayer = steamPlayer;
            sender.Player = player;
            sender.Id = steamPlayer.playerID.steamID.m_SteamID;
            sender.IsRemote = context.origin == ServerInvocationContext.EOrigin.Remote;
            return true;
        }

        /// <summary>
        /// The payload could not be read. A real client never sends this, so it counts against the game's own bad-packet
        /// limit for that connection, which kicks a sender that keeps doing it.
        /// </summary>
        public static void Malformed(in ServerInvocationContext context, in Sender sender, string what)
        {
            Reject(sender, "unreadable " + what);

            if (sender.IsRemote)
            {
                SDG.NetTransport.ITransportConnection connection = context.GetTransportConnection();
                if (!ReferenceEquals(connection, null))
                {
                    Provider.IncrementBadPacketsFromConnection(connection);
                }
            }
        }

        /// <summary>
        /// A value that was read fine but is not plausible for that player (not finite, or too far from where the server has
        /// them). Dropped, and logged at a low rate. Not a kick: lag, a teleport or a vehicle can look like this.
        /// </summary>
        public static void Implausible(in Sender sender, string what)
        {
            Reject(sender, "implausible " + what);
        }

        /// <summary>Limits proof-of-life logging of accepted messages to one line per sender every few seconds.</summary>
        public static bool ShouldLogAccepted(in Sender sender)
        {
            SenderState state = GetState(sender.Id);
            if (state == null)
            {
                return false;
            }

            float now = Time.realtimeSinceStartup;
            if (now < state.NextAcceptedLog)
            {
                return false;
            }
            state.NextAcceptedLog = now + AcceptedLogInterval;
            return true;
        }

        public static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        /// <summary>A reported head position: finite and within <see cref="MaxHeadDistanceFromPlayer"/> of the server's position of the player.</summary>
        public static bool IsHeadPlausible(in Sender sender, Vector3 head)
        {
            if (!IsFinite(head))
            {
                return false;
            }
            return (head - sender.Player.transform.position).sqrMagnitude <= MaxHeadDistanceFromPlayer * MaxHeadDistanceFromPlayer;
        }

        /// <summary>
        /// A whole pose, checked against what the server knows. The head must be near the server's position of the player, the
        /// hands, the muzzle and the held item's origin within reach of the head, every number finite, and the rotations usable (they
        /// are normalised here, so later code can rely on them). The muzzle and the item pose only exist while an item is held. False
        /// means drop the message.
        /// </summary>
        public static bool TryValidatePose(in Sender sender, ref VRPoseData pose)
        {
            if (!IsHeadPlausible(sender, pose.HeadPosition) || !TryNormalize(ref pose.HeadRotation))
            {
                return false;
            }

            if (pose.LeftTracked && !(IsWithin(pose.LeftPosition, pose.HeadPosition, MaxHandDistanceFromHead) && TryNormalize(ref pose.LeftRotation)))
            {
                return false;
            }
            if (pose.RightTracked && !(IsWithin(pose.RightPosition, pose.HeadPosition, MaxHandDistanceFromHead) && TryNormalize(ref pose.RightRotation)))
            {
                return false;
            }

            if (pose.HasMuzzle)
            {
                if (pose.ItemHand == VRPoseData.ItemHandNone || !IsWithin(pose.MuzzlePosition, pose.HeadPosition, MaxMuzzleDistanceFromHead))
                {
                    return false;
                }
                if (!IsFinite(pose.MuzzleDirection) || pose.MuzzleDirection.sqrMagnitude < 0.25f)
                {
                    return false;
                }
                pose.MuzzleDirection.Normalize();
            }

            if (pose.ItemHand != VRPoseData.ItemHandNone
                && !(IsWithin(pose.ItemPosition, pose.HeadPosition, MaxMuzzleDistanceFromHead) && TryNormalize(ref pose.ItemRotation)))
            {
                return false;
            }

            return true;
        }

        private static bool IsWithin(Vector3 point, Vector3 center, float maxDistance)
        {
            return IsFinite(point) && (point - center).sqrMagnitude <= maxDistance * maxDistance;
        }

        /// <summary>A quaternion with finite parts and a length near 1 is normalised; anything else is not a rotation.</summary>
        private static bool TryNormalize(ref Quaternion rotation)
        {
            float lengthSquared = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            if (!IsFinite(lengthSquared) || lengthSquared < 0.5f || lengthSquared > 1.5f)
            {
                return false;
            }

            float scale = 1f / Mathf.Sqrt(lengthSquared);
            rotation = new Quaternion(rotation.x * scale, rotation.y * scale, rotation.z * scale, rotation.w * scale);
            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void Reject(in Sender sender, string reason)
        {
            SenderState state = GetState(sender.Id);
            if (state == null)
            {
                return;
            }

            ++state.Rejected;
            float now = Time.realtimeSinceStartup;
            if (now < state.NextRejectedLog)
            {
                return;
            }
            state.NextRejectedLog = now + RejectedLogInterval;

            // Only the numeric Steam id is logged: names and anything else the client chose could carry control characters.
            VRLog.Warn("net: dropped " + reason + " from " + sender.Id + " (" + state.Rejected + " dropped from them so far).");
        }

        private static SenderState GetState(ulong id)
        {
            if (states.TryGetValue(id, out SenderState state))
            {
                return state;
            }
            if (states.Count >= MaxTrackedSenders)
            {
                return null;
            }

            state = new SenderState();
            states.Add(id, state);
            return state;
        }
    }
}
