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
            VRLog.Info("UnturnedVR " + typeof(UnturnedVRModule).Assembly.GetName().Version.ToString(3) + " initializing...");

            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(UnturnedVRModule).Assembly);
            Patches.VRInputPatches.Apply(harmony);

            VRLog.Info("UnturnedVR initialized.");

            VR.VRManager.InitializeAndStart();

            if (VR.VRState.IsActive)
            {
                VR.VRGui.Create();
                VR.VRPointer.Create();
            }
        }

        public void shutdown()
        {
            harmony?.UnpatchAll(HarmonyId);
            VRLog.Info("UnturnedVR shut down.");
        }
    }
}
