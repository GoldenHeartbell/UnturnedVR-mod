using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Server side: relays each VR player's validated pose (<see cref="VRNetPoseStore"/>) to the other players whose game said it can
    /// show it (<see cref="VRNetRpc.ReceiveRelayOptIn"/>), 20 times a second, as one batched <see cref="VRNetRpc.ReceiveRelayPoses"/>
    /// message per recipient. A player gets only the VR players within <see cref="MaxDistance"/> of them, nearest first, at most
    /// <see cref="VRRelayPose.MaxPerMessage"/>, and never their own. The poses are put in the sender's frame
    /// (<see cref="VRRelayPose.FromWorld"/>) using the server's own position and look yaw of that player; nothing in them is the
    /// sender's choice beyond what <see cref="VRNetGuard.TryValidatePose"/> already accepted. A player without the mod, or one that
    /// did not opt in, gets nothing. Does nothing unless this process is the server.
    /// </summary>
    internal sealed class VRNetRelay : MonoBehaviour
    {
        private const float Interval = 0.05f;

        /// <summary>A pose older than this is not shown: that player stopped sending (loading, died, left VR).</summary>
        private const float MaxPoseAge = 0.25f;

        /// <summary>Hands are only worth sending for players you can see them on. Beyond this the vanilla animation shows.</summary>
        private const float MaxDistance = 48f;

        private const int MaxOptedIn = 128;
        private const float LogInterval = 5f;

        private struct Candidate
        {
            public VRRelayPose Pose;
            public Vector3 Position;
        }

        /// <summary>The poses of one message while it is being built, and written by <see cref="WriteBatch"/>. Reused, so a tick allocates nothing.</summary>
        private sealed class Batch
        {
            public readonly VRRelayPose[] Poses = new VRRelayPose[VRRelayPose.MaxPerMessage];
            public readonly float[] DistancesSquared = new float[VRRelayPose.MaxPerMessage];
            public int Count;
        }

        private static readonly HashSet<ulong> optedIn = new HashSet<ulong>();
        private static readonly List<Candidate> candidates = new List<Candidate>(MaxOptedIn);
        private static readonly Batch batch = new Batch();
        private static readonly Action<NetPakWriter, Batch> writeBatch = WriteBatch;
        private static ClientStaticMethod sendRelay;
        private static bool giveUp;

        private float nextTick;
        private float nextLog;
        private int sentMessages;
        private int sentPoses;

        public static void Create()
        {
            GameObject host = new GameObject("UnturnedVR net relay");
            DontDestroyOnLoad(host);
            host.AddComponent<VRNetRelay>();
        }

        /// <summary>The player's game said it can show relayed poses. False when the table is full.</summary>
        public static bool OptIn(ulong id)
        {
            if (optedIn.Contains(id))
            {
                return true;
            }
            if (optedIn.Count >= MaxOptedIn)
            {
                return false;
            }

            optedIn.Add(id);
            VRLog.Info("net: relay: " + id + " can show other players' VR poses (" + optedIn.Count + " in all).");
            return true;
        }

        public static void Forget(ulong id)
        {
            optedIn.Remove(id);
        }

        public static void Clear()
        {
            optedIn.Clear();
        }

        private void Update()
        {
            if (giveUp || !Provider.isServer || !VRNet.Registered || optedIn.Count == 0 || Time.unscaledTime < nextTick)
            {
                return;
            }

            // Add the interval to the last due time so the rate stays exact; after a stall, start over instead of sending a burst.
            nextTick += Interval;
            if (nextTick <= Time.unscaledTime)
            {
                nextTick = Time.unscaledTime + Interval;
            }

            List<SteamPlayer> clients = Provider.clients;
            if (clients == null || clients.Count == 0)
            {
                return;
            }

            GatherCandidates(clients);
            if (candidates.Count > 0)
            {
                for (int index = 0; index < clients.Count; ++index)
                {
                    SteamPlayer recipient = clients[index];
                    if (recipient == null || recipient.player == null || ReferenceEquals(recipient.transportConnection, null)
                        || !optedIn.Contains(recipient.playerID.steamID.m_SteamID))
                    {
                        continue;
                    }

                    if (BuildBatch(recipient) && Send(recipient.transportConnection))
                    {
                        ++sentMessages;
                        sentPoses += batch.Count;
                    }
                }
            }

            LogProgress();
        }

        /// <summary>Every living VR player with a fresh pose, already in its own frame. Built once per tick and filtered per recipient.</summary>
        private static void GatherCandidates(List<SteamPlayer> clients)
        {
            candidates.Clear();
            for (int index = 0; index < clients.Count && candidates.Count < MaxOptedIn; ++index)
            {
                SteamPlayer client = clients[index];
                Player player = client != null ? client.player : null;
                if (player == null || player.life == null || player.life.isDead)
                {
                    continue;
                }

                if (!VRNetPoseStore.TryGetFresh(client.playerID.steamID.m_SteamID, MaxPoseAge, out VRPoseData pose, out _))
                {
                    continue;
                }

                Vector3 position = player.transform.position;
                candidates.Add(new Candidate
                {
                    Pose = VRRelayPose.FromWorld((byte) client.channel, pose, position, player.look.yaw),
                    Position = position
                });
            }
        }

        /// <summary>Fills <see cref="batch"/> with the nearest candidates around the recipient. False when there are none.</summary>
        private static bool BuildBatch(SteamPlayer recipient)
        {
            batch.Count = 0;
            byte ownChannel = (byte) recipient.channel;
            Vector3 origin = recipient.player.transform.position;
            float maxSquared = MaxDistance * MaxDistance;

            for (int index = 0; index < candidates.Count; ++index)
            {
                Candidate candidate = candidates[index];
                if (candidate.Pose.ChannelId == ownChannel)
                {
                    continue;
                }

                float distanceSquared = (candidate.Position - origin).sqrMagnitude;
                if (distanceSquared > maxSquared)
                {
                    continue;
                }

                if (batch.Count < VRRelayPose.MaxPerMessage)
                {
                    batch.Poses[batch.Count] = candidate.Pose;
                    batch.DistancesSquared[batch.Count] = distanceSquared;
                    ++batch.Count;
                    continue;
                }

                // Full: this one replaces the farthest of those kept, if it is nearer.
                int farthest = 0;
                for (int kept = 1; kept < batch.Count; ++kept)
                {
                    if (batch.DistancesSquared[kept] > batch.DistancesSquared[farthest])
                    {
                        farthest = kept;
                    }
                }
                if (distanceSquared < batch.DistancesSquared[farthest])
                {
                    batch.Poses[farthest] = candidate.Pose;
                    batch.DistancesSquared[farthest] = distanceSquared;
                }
            }

            return batch.Count > 0;
        }

        private static bool Send(ITransportConnection connection)
        {
            if (sendRelay == null)
            {
                sendRelay = ClientStaticMethod.Get(typeof(VRNetRpc), nameof(VRNetRpc.ReceiveRelayPoses));
                if (sendRelay == null)
                {
                    VRLog.Warn("net: the game has no handle for the relay RPC, relaying is off.");
                    giveUp = true;
                    return false;
                }
            }

            // Unreliable: a lost message is replaced by the next one 50 ms later.
            sendRelay.Invoke(ENetReliability.Unreliable, connection, writeBatch, batch);
            return true;
        }

        private static void WriteBatch(NetPakWriter writer, Batch toWrite)
        {
            writer.WriteBits((uint) toWrite.Count, 5);
            for (int index = 0; index < toWrite.Count; ++index)
            {
                toWrite.Poses[index].Write(writer);
            }
        }

        private void LogProgress()
        {
            if (!VRNetDebug.LogPoses || Time.unscaledTime < nextLog)
            {
                return;
            }

            nextLog = Time.unscaledTime + LogInterval;
            VRLog.Info("net: relay: " + optedIn.Count + " player(s) can show VR poses, " + candidates.Count + " VR player(s) with a fresh pose, "
                + sentMessages + " message(s) with " + sentPoses + " pose(s) sent in the last " + LogInterval.ToString("0") + " s.");
            sentMessages = 0;
            sentPoses = 0;
        }
    }
}
