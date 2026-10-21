using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Where this copy of the module is running, and the checks that keep server code on the server and client code on the
    /// game client. The module is two assemblies: UnturnedVR.Net.dll (this one, role "Both_Optional", runs on a server too) and
    /// UnturnedVR.dll (all VR and client code, role "Client", which the game never loads on a dedicated server). On top of that
    /// the code checks itself:
    ///   - every server RPC handler goes through <see cref="VRNetGuard.TryAccept"/>, which refuses unless this process is the server;
    ///   - the client assembly's entry points check <see cref="CanRunClientCode"/>;
    ///   - a dedicated server audits itself at startup (<see cref="AuditDedicatedServer"/>) and logs what it found.
    /// Singleplayer and a hosted game are both: the one process is the client and the server.
    /// </summary>
    internal static class VRRole
    {
        // The mod's client assembly (module role "Client", so the game never loads it on a server) and the VR assemblies it
        // ships (the game's own server never loads them); none may be loaded on a dedicated server.
        private static readonly string[] ClientOnlyAssemblies =
        {
            "UnturnedVR",
            "Unity.XR.Management",
            "Unity.XR.OpenVR",
            "UnityEngine.SpatialTracking",
        };

        /// <summary>The standalone dedicated-server app (no screen, no player of its own).</summary>
        public static bool IsDedicatedServer
        {
            get
            {
                // Dedicator.IsDedicatedServer is a const in the server build, so a mod built against the client would fail to
                // find it there. The obsolete isDedicated property exists in both.
#pragma warning disable CS0618
                return Dedicator.isDedicated;
#pragma warning restore CS0618
            }
        }

        /// <summary>This process has a screen and a local player: client code (VR, patches, senders) may run.</summary>
        public static bool CanRunClientCode => !IsDedicatedServer;

        /// <summary>This process is the authority for the game (dedicated server, host, or singleplayer): server handlers may act.</summary>
        public static bool CanRunServerCode => Provider.isServer;

        /// <summary>
        /// Dedicated server only: checks that no client-side code got in, and logs the result either way, so a server owner can
        /// see it in the console. Never throws.
        /// </summary>
        public static void AuditDedicatedServer(string harmonyId)
        {
            try
            {
                int patchedMethods = 0;
                foreach (MethodBase method in Harmony.GetAllPatchedMethods())
                {
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
                    if (info != null && info.Owners.Contains(harmonyId))
                    {
                        ++patchedMethods;
                    }
                }

                List<string> clientAssemblies = new List<string>();
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = loaded.GetName().Name;
                    if (Array.IndexOf(ClientOnlyAssemblies, name) >= 0)
                    {
                        clientAssemblies.Add(name);
                    }
                }

                string summary = "role check: dedicated server, " + patchedMethods + " client patch(es) applied (expected 0), client-only assemblies loaded: "
                    + (clientAssemblies.Count == 0 ? "none" : string.Join(", ", clientAssemblies.ToArray())) + ".";
                if (patchedMethods == 0 && clientAssemblies.Count == 0)
                {
                    VRLog.Info(summary);
                }
                else
                {
                    VRLog.Error(summary + " Client code is running on a server; this is a bug.");
                }
            }
            catch (Exception e)
            {
                VRLog.Warn("role check failed: " + e.Message);
            }
        }
    }
}
