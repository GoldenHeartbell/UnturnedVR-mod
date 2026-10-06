using HarmonyLib;
using SDG.Framework.Modules;
using SDG.Unturned;

namespace UnturnedVR
{
    public class UnturnedVRModule : IModuleNexus
    {
        public const string HarmonyId = "digital.unturnedvr.module";

        private Harmony harmony;

        public void initialize()
        {
            UnturnedLog.info("[UnturnedVR] M1 hello-world module initializing...");

            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(UnturnedVRModule).Assembly);

            UnturnedLog.info("[UnturnedVR] M1 hello-world module initialized.");

            VR.VRManager.InitializeAndStart();

            if (VR.VRState.IsActive)
            {
                VR.VRGui.Create();
            }
        }

        public void shutdown()
        {
            harmony?.UnpatchAll(HarmonyId);
            UnturnedLog.info("[UnturnedVR] M1 hello-world module shut down.");
        }
    }
}
