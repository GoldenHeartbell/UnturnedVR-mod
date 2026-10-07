#if UNITY_EDITOR
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Editor-only stand-in. In the Unity editor XR is brought up by XR Plugin Management
    /// (Open VR loader, enabled in Project Settings > XR Plug-in Management), not by the
    /// xrmanager AssetBundle + OpenVR path used in the shipped build below.
    /// </summary>
    internal static class VRManager
    {
        public static void InitializeAndStart()
        {
            // Without this the editor pauses when the Game view loses focus (always the case with the
            // headset on), the XR session drops out of its focused state and no devices are reported.
            Application.runInBackground = true;

            var manager = UnityEngine.XR.Management.XRGeneralSettings.Instance?.Manager;
            if (manager == null)
            {
                VRLog.Error("(editor) no XRGeneralSettings/Manager. Enable Open VR Loader under Project Settings > XR Plug-in Management (PC tab).");
                return;
            }

            // XR Plugin Management normally starts the loader itself ("Initialize XR on Startup");
            // if that is off or it failed early, start it here.
            if (manager.activeLoader == null)
            {
                manager.InitializeLoaderSync();
                if (manager.activeLoader != null)
                {
                    manager.StartSubsystems();
                }
            }

            if (manager.activeLoader == null)
            {
                VRLog.Error("(editor) no active XR loader. Is SteamVR running with the headset connected?");
                return;
            }

            VRLog.Info("(editor) active XR loader \"" + manager.activeLoader.name + "\".");
            VRState.IsActive = true;
        }
    }
}
#else
using System;
using System.IO;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.XR.Management;

namespace UnturnedVR.VR
{
    /// <summary>
    /// M2: brings up Unity's XR Management loader (OpenVR) using VHVR's "xrmanager" AssetBundle.
    /// Adapted from ValheimVRMod's VRCore/VRManager.cs, trimmed to the display path only - no
    /// SteamVR_Input/Actions here, that belongs to M5 (controller input) and pulls in a much
    /// larger dependency (SteamVR.dll, SteamVR_Actions.dll, ...) we don't need yet.
    /// See docs/unturned-internals.md and docs/progress.md for the M0/M2 recon this relies on.
    /// </summary>
    internal static class VRManager
    {
        // Referencing these types forces the CLR to load Unity.XR.OpenVR.dll before the bundle
        // is read below; otherwise Unity can't reattach OpenVRLoader/OpenVRSettings to the
        // deserialized Behaviours and logs them as "missing script".
        private static readonly Type[] ForceLoadProviderAssemblies =
        {
            typeof(Unity.XR.OpenVR.OpenVRLoader),
            typeof(Unity.XR.OpenVR.OpenVRSettings),
        };

        private const int MaxLoaderInitTries = 5;

        public static void InitializeAndStart()
        {
            XRGeneralSettings settings = LoadXRGeneralSettingsFromBundle();
            if (settings == null || settings.Manager == null)
            {
                VRLog.Error("no usable XRGeneralSettings/Manager, aborting.");
                return;
            }

            XRManagerSettings manager = settings.Manager;

            if (!TryInitializeLoader(manager))
            {
                VRLog.Error("failed to get an active XR loader. Is SteamVR running with the headset connected?");
                return;
            }

            VRLog.Info($"active loader is \"{manager.activeLoader.name}\" ({manager.activeLoader.GetType()}).");

            try
            {
                manager.StartSubsystems();
            }
            catch (Exception ex)
            {
                VRLog.Error("exception from StartSubsystems():");
                VRLog.Exception(ex);
                return;
            }

            VRLog.Info("StartSubsystems() called. If a headset is connected, it should now be showing the game.");

            VRState.IsActive = true;
        }

        private static XRGeneralSettings LoadXRGeneralSettingsFromBundle()
        {
            string bundlePath = Path.Combine(Application.streamingAssetsPath, "xrmanager");
            VRLog.Info($"loading AssetBundle at \"{bundlePath}\"");

            AssetBundle bundle;
            try
            {
                bundle = AssetBundle.LoadFromFile(bundlePath);
            }
            catch (Exception ex)
            {
                VRLog.Error("exception while loading xrmanager AssetBundle:");
                VRLog.Exception(ex);
                return null;
            }

            if (bundle == null)
            {
                VRLog.Error("AssetBundle.LoadFromFile returned null.");
                return null;
            }

            // Loading all assets triggers XRGeneralSettings.OnEnable(), which self-registers as
            // the static Instance below - the AssetBundle itself is not otherwise referenced again.
            bundle.LoadAllAssets();

            XRGeneralSettings instance = XRGeneralSettings.Instance;
            if (instance == null)
            {
                VRLog.Error("XRGeneralSettings.Instance is still null after loading the bundle.");
                return null;
            }

            VRLog.Info("XRGeneralSettings.Instance resolved.");
            return instance;
        }

        private static bool TryInitializeLoader(XRManagerSettings manager)
        {
            int tries = 0;
            do
            {
                manager.InitializeLoaderSync();
                tries++;
            }
            while (manager.activeLoader == null && tries < MaxLoaderInitTries);

            if (manager.activeLoader == null)
            {
                VRLog.Error($"activeLoader still null after {tries} attempt(s).");
                return false;
            }

            VRLog.Info($"activeLoader resolved after {tries} attempt(s).");
            return true;
        }
    }
}
#endif
