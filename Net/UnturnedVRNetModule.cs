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

        public void initialize()
        {
            NetLog.Info("UnturnedVR.Net " + typeof(UnturnedVRNetModule).Assembly.GetName().Version.ToString(3) + " initializing...");

            VRNet.Register();

            // Says hello to joining players when this process is a server; does nothing on a plain client.
            VRNetServerTicker.Create();

            if (VRRole.IsDedicatedServer)
            {
                NetLog.Info("running on a dedicated server: no VR, no client patches.");
                VRRole.AuditDedicatedServer(HarmonyId);
            }
        }

        public void shutdown()
        {
            VRNet.Shutdown();
        }
    }
}
