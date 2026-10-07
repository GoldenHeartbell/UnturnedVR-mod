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
            VRLog.Info("M1 hello-world module initializing...");

            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(UnturnedVRModule).Assembly);

            VRLog.Info("M1 hello-world module initialized.");

            VR.VRManager.InitializeAndStart();

            if (VR.VRState.IsActive)
            {
                VR.VRGui.Create();
            }
        }

        public void shutdown()
        {
            harmony?.UnpatchAll(HarmonyId);
            VRLog.Info("M1 hello-world module shut down.");
        }
    }
}
