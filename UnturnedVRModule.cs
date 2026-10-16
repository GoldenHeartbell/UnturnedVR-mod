using System.Runtime.CompilerServices;
using HarmonyLib;
using SDG.Framework.Modules;

namespace UnturnedVR
{
    /// <summary>
    /// Entry point of UnturnedVR.dll, the client assembly (module role "Client": the game never loads it on a dedicated
    /// server). The part that also runs on a server is UnturnedVR.Net.dll, listed before this one in UnturnedVR.module.
    /// </summary>
    public class UnturnedVRModule : IModuleNexus
    {
        public const string HarmonyId = Net.UnturnedVRNetModule.HarmonyId;

        private Harmony harmony;

        public void initialize()
        {
            VRLog.Info("UnturnedVR " + typeof(UnturnedVRModule).Assembly.GetName().Version.ToString(3) + " initializing...");

            // The role "Client" keeps this assembly off a dedicated server; this is the second line of defence.
            if (!Net.VRRole.CanRunClientCode)
            {
                VRLog.Error("the client module was loaded on a dedicated server, refusing.");
                return;
            }

            // UnturnedVR.Net registered the RPCs already; this is a no-op then.
            Net.VRNet.Register();

            InitializeClient();
        }

        // Separate method so the VR types it touches load only after the checks above.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InitializeClient()
        {
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(UnturnedVRModule).Assembly);
            Patches.VRInputPatches.Apply(harmony);

            VRLog.Info("UnturnedVR initialized.");

            VR.VRManager.InitializeAndStart();

            if (VR.VRState.IsActive)
            {
                VR.VRGui.Create();
                VR.VRPointer.Create();
                VR.VRKeyboard.Create();
                VR.VRNetPoseSender.Create();
            }
        }

        public void shutdown()
        {
            harmony?.UnpatchAll(HarmonyId);
            VRLog.Info("UnturnedVR shut down.");
        }
    }
}
