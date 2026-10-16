using System.Collections.Generic;
using SDG.NetTransport;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Server side: tells each joining player that this server runs the mod's network protocol (<see cref="VRNetRpc.ReceiveHello"/>),
    /// so a VR client knows it may send poses. The hello is repeated every few seconds, a few times, until that player's first pose
    /// arrives, because a client that is still loading the level can miss it. A player without the mod sees at most a few harmless
    /// "out of bounds method index" warnings in their own log. Does nothing unless this process is the server; the local player of a
    /// hosted game or singleplayer has no connection and is skipped.
    /// </summary>
    internal sealed class VRNetServerTicker : MonoBehaviour
    {
        private const float HelloInterval = 3f;
        private const int MaxHellosPerPlayer = 5;
        private const int MaxTracked = 128;

        private sealed class HelloState
        {
            public float NextHello;
            public int Sent;
        }

        private static readonly Dictionary<ulong, HelloState> states = new Dictionary<ulong, HelloState>();
        private static ClientStaticMethod sendHello;
        private static bool giveUp;

        public static void Create()
        {
            GameObject host = new GameObject("UnturnedVR net server");
            DontDestroyOnLoad(host);
            host.AddComponent<VRNetServerTicker>();
        }

        /// <summary>A player left: drop its bookkeeping.</summary>
        public static void Forget(ulong id)
        {
            states.Remove(id);
        }

        private void Update()
        {
            if (giveUp || !Provider.isServer || !VRNet.Registered)
            {
                return;
            }

            List<SteamPlayer> clients = Provider.clients;
            if (clients == null || clients.Count == 0)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            for (int index = 0; index < clients.Count; ++index)
            {
                SteamPlayer client = clients[index];
                if (client == null || client.player == null || ReferenceEquals(client.transportConnection, null))
                {
                    continue; // not spawned yet, or the local player of a hosted game
                }

                ulong id = client.playerID.steamID.m_SteamID;
                if (VRNetPoseStore.Has(id))
                {
                    continue; // it heard the hello and answered
                }

                if (!states.TryGetValue(id, out HelloState state))
                {
                    if (states.Count >= MaxTracked)
                    {
                        continue;
                    }
                    state = new HelloState();
                    states.Add(id, state);
                }

                if (state.Sent >= MaxHellosPerPlayer || now < state.NextHello)
                {
                    continue;
                }

                state.Sent++;
                state.NextHello = now + HelloInterval;
                SendHello(client.transportConnection);
            }
        }

        private static void SendHello(ITransportConnection connection)
        {
            if (sendHello == null)
            {
                sendHello = ClientStaticMethod.Get(typeof(VRNetRpc), nameof(VRNetRpc.ReceiveHello));
                if (sendHello == null)
                {
                    NetLog.Warn("net: the game has no handle for the hello RPC, hellos are off.");
                    giveUp = true;
                    return;
                }
            }

            sendHello.Invoke(ENetReliability.Reliable, connection, WriteHello);
        }

        private static void WriteHello(NetPakWriter writer)
        {
            writer.WriteUInt8(VRNetProtocol.Version);
        }
    }
}
