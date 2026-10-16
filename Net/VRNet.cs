using System;
using System.Reflection;
using HarmonyLib;
using SDG.NetPak;
using SDG.Unturned;
using Steamworks;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Private-server networking: the game has no message type a mod can add, so the mod registers its own RPCs with the
    /// game's attribute-based net invokables (NetReflection.RegisterFromAssembly, which the SDK marks as not officially
    /// supported). Runs on the client and on a dedicated server that has the module installed.
    ///
    /// Only static methods that take just the invocation context are used: the game binds those directly to the method, so
    /// there is no generated read/write code and the payload is written and read by hand (the same way
    /// SteamPlayer.ReceiveGetSteamAuthTicketForWebApiResponse works).
    ///
    /// Safety: RPC ids are positions in a list and are sent with a fixed bit width. If adding the mod's RPCs would make that
    /// width grow, a player without the mod would send ids the other side reads wrongly, so registration is skipped then.
    ///
    /// A client must not send these to a server that does not have the mod: on a vanilla server the id is past the end of its
    /// table and the server closes the connection and refuses that player. So the server says hello first
    /// (<see cref="VRNetRpc.ReceiveHello"/>, see <see cref="VRNetServerTicker"/>) and a client only sends once it heard it.
    /// </summary>
    internal static class VRNet
    {
        private static bool attempted;
        private static CSteamID helloServer;

        /// <summary>The mod's RPCs are known to the game on this machine, so they can be sent and received.</summary>
        public static bool Registered { get; private set; }

        /// <summary>Protocol version the connected server announced in its hello, or 0 while it has not (a server without the mod never does).</summary>
        public static byte ServerProtocol { get; private set; }

        /// <summary>
        /// The client may send poses: this machine is a remote client, and the server it is connected to announced the same
        /// protocol version. False on a vanilla server, on an older or newer mod server, and in singleplayer or when hosting.
        /// </summary>
        public static bool ServerAcceptsPoses =>
            Registered && Provider.isConnected && !Provider.isServer && ServerProtocol == VRNetProtocol.Version && helloServer == Provider.server;

        public static bool Register()
        {
            if (attempted)
            {
                return Registered;
            }
            attempted = true;

            try
            {
                FieldInfo serverLength = AccessTools.Field(typeof(NetReflection), "serverMethodsLength");
                FieldInfo clientLength = AccessTools.Field(typeof(NetReflection), "clientMethodsLength");
                if (serverLength == null || clientLength == null)
                {
                    NetLog.Warn("net: NetReflection has no method counts in this game version, mod RPCs are off.");
                    return false;
                }

                uint serverBefore = (uint) serverLength.GetValue(null);
                uint clientBefore = (uint) clientLength.GetValue(null);
                CountRpcs(out uint serverAdded, out uint clientAdded);
                if (NetPakConst.CountBits(serverBefore + serverAdded) != NetPakConst.CountBits(serverBefore)
                    || NetPakConst.CountBits(clientBefore + clientAdded) != NetPakConst.CountBits(clientBefore))
                {
                    NetLog.Warn("net: adding " + serverAdded + " server and " + clientAdded + " client RPC(s) to the game's " + serverBefore + " and "
                        + clientBefore + " would widen an RPC id field, which breaks players without the mod. Mod RPCs are off.");
                    return false;
                }

                NetReflection.RegisterFromAssembly(typeof(VRNet).Assembly);

                uint serverAfter = (uint) serverLength.GetValue(null);
                uint clientAfter = (uint) clientLength.GetValue(null);
                if (serverAfter != serverBefore + serverAdded || clientAfter != clientBefore + clientAdded)
                {
                    NetLog.Warn("net: registering changed the RPC counts unexpectedly (server " + serverBefore + " -> " + serverAfter
                        + ", client " + clientBefore + " -> " + clientAfter + "), mod RPCs are off.");
                    return false;
                }

                Registered = true;
                VRNetGuard.Initialize();
                Provider.onClientDisconnected += OnClientDisconnected;
                NetLog.Info("net: registered " + serverAdded + " server and " + clientAdded + " client RPC(s); the game now has " + serverAfter
                    + " server RPCs (" + NetPakConst.CountBits(serverAfter) + " bits), " + clientAfter + " client RPCs ("
                    + NetPakConst.CountBits(clientAfter) + " bits). Protocol " + VRNetProtocol.Version + ".");
            }
            catch (Exception e)
            {
                NetLog.Warn("net: registering the mod RPCs failed, they are off: " + e.Message);
            }

            return Registered;
        }

        public static void Shutdown()
        {
            if (Registered)
            {
                Provider.onClientDisconnected -= OnClientDisconnected;
            }
            ServerProtocol = 0;
            VRNetGuard.Shutdown();
        }

        /// <summary>The server's hello arrived (client side).</summary>
        internal static void OnServerHello(byte version)
        {
            bool changed = ServerProtocol != version || helloServer != Provider.server;
            ServerProtocol = version;
            helloServer = Provider.server;

            if (changed)
            {
                NetLog.Info("net: the server runs UnturnedVR network protocol " + version
                    + (version == VRNetProtocol.Version ? "." : "; this client speaks " + VRNetProtocol.Version + ", so it sends nothing to it."));
            }
        }

        private static void OnClientDisconnected()
        {
            ServerProtocol = 0;
        }

        private static void CountRpcs(out uint server, out uint client)
        {
            server = 0;
            client = 0;
            foreach (MethodInfo method in typeof(VRNetRpc).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                SteamCall call = method.GetCustomAttribute<SteamCall>();
                if (call == null)
                {
                    continue;
                }

                if (call.validation == ESteamCallValidation.ONLY_FROM_SERVER)
                {
                    ++client;
                }
                else
                {
                    ++server;
                }
            }
        }
    }

    /// <summary>Marks <see cref="VRNetRpc"/> as the type to scan for RPCs (the game looks for abstract classes with this attribute).</summary>
    [NetInvokableGeneratedClass(typeof(VRNetRpc))]
    internal static class VRNetRpcGenerated
    {
    }

    /// <summary>
    /// The mod's RPCs. They must stay public and static with a single "in ...InvocationContext" parameter; the payload is read from
    /// context.reader in the order the sender wrote it. Server handlers start with <see cref="VRNetGuard.TryAccept"/>.
    /// </summary>
    public static class VRNetRpc
    {
        /// <summary>
        /// Client to server: the player's head, hands and muzzle (<see cref="VRPoseData"/>). The game enforces the rate limit per
        /// player (and kicks a player who keeps exceeding it); a client sends 20 a second, the limit leaves room for network jitter.
        /// </summary>
        [SteamCall(ESteamCallValidation.SERVERSIDE, ratelimitHz = 40)]
        public static void ReceivePose(in ServerInvocationContext context)
        {
            try
            {
                if (!VRNetGuard.TryAccept(context, out VRNetGuard.Sender sender))
                {
                    return;
                }

                if (!VRPoseData.TryRead(context.reader, out VRPoseData pose))
                {
                    VRNetGuard.Malformed(context, sender, "pose");
                    return;
                }

                if (!VRNetGuard.TryValidatePose(sender, ref pose))
                {
                    VRNetGuard.Implausible(sender, "pose");
                    return;
                }

                // Out of order, or the table is full: dropped without a word, an old pose is not worth a log line.
                if (VRNetPoseStore.TryStore(sender.Id, pose))
                {
                    VRNetDebug.LogPose(sender, pose);
                }
            }
            catch (Exception e)
            {
                NetLog.Warn("net: pose handler failed: " + e.Message);
            }
        }

        /// <summary>Server to client: "this server runs the mod's network protocol, version N". A vanilla client logs one warning and ignores it.</summary>
        [SteamCall(ESteamCallValidation.ONLY_FROM_SERVER)]
        public static void ReceiveHello(in ClientInvocationContext context)
        {
            try
            {
                if (context.reader.ReadUInt8(out byte version))
                {
                    VRNet.OnServerHello(version);
                }
            }
            catch (Exception e)
            {
                NetLog.Warn("net: hello handler failed: " + e.Message);
            }
        }
    }
}
