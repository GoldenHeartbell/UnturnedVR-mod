using System;
using HarmonyLib;
using SDG.Framework.Modules;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Entry point of UnturnedVR.Net.dll, the part of the mod that also runs on a dedicated server (Linux or Windows). It is
    /// listed first in UnturnedVR.module, so its RPCs are registered before the client assembly initialises. The client
    /// assembly (UnturnedVR.dll, module role "Client") is never loaded on a server.
    /// </summary>
    public class UnturnedVRNetModule : IModuleNexus
    {
        /// <summary>Id of the client assembly's Harmony instance; the server audit checks that nothing is patched under it.</summary>
        public const string HarmonyId = "digital.unturnedvr.module";

        /// <summary>Id of the Harmony instance for the patches in this assembly (the server-side ones).</summary>
        public const string NetHarmonyId = "digital.unturnedvr.net";

        private Harmony harmony;

        public void initialize()
        {
            VRLog.Info("UnturnedVR.Net " + typeof(UnturnedVRNetModule).Assembly.GetName().Version.ToString(3) + " initializing...");

            VRNet.Register();

            // Says hello to joining players when this process is a server; does nothing on a plain client.
            VRNetServerTicker.Create();

            // Relays VR players' poses to the players who opted in; does nothing on a plain client.
            VRNetRelay.Create();

            ApplyServerPatches();

            if (VRRole.IsDedicatedServer)
            {
                VRLog.Info("running on a dedicated server: no VR, no client patches.");
                VRRole.AuditDedicatedServer(HarmonyId);
            }
        }

        public void shutdown()
        {
            harmony?.UnpatchAll(NetHarmonyId);
            VRNet.Shutdown();
        }

        // The patches in this assembly act only in a process that is the server (each checks it), so they are safe to apply everywhere.
        private void ApplyServerPatches()
        {
            try
            {
                harmony = new Harmony(NetHarmonyId);
                harmony.PatchAll(typeof(UnturnedVRNetModule).Assembly);
                int patched = 0;
                foreach (var method in harmony.GetPatchedMethods())
                {
                    ++patched;
                }
                VRLog.Info("net: server patches applied: " + patched + " method(s) (rocket launcher muzzle).");
            }
            catch (Exception e)
            {
                VRLog.Warn("net: server patches failed, the game keeps its own behaviour: " + e.Message);
            }
        }
    }
}
