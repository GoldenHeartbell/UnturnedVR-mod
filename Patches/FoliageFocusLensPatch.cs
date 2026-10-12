using HarmonyLib;
using SDG.Framework.Foliage;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// "Scope Focus Foliage" (graphics setting): while zoomed, PlayerLook sets FoliageSystem.focusCamera to MainCamera
    /// (binoculars) or to the scope camera (scoped guns), and the focus pass draws its foliage with
    /// Graphics.DrawMeshInstanced restricted to that camera. In VR MainCamera is disabled, so the binoculars' focus
    /// foliage was drawn for no camera at all. While the lens is up, the lens camera takes that place; the tiles are
    /// also frustum-tested against it, so only what the lens can see is queued. Scoped guns keep the game's scope
    /// camera (it works already). Runs right before FoliageSystem uses the field, so it does not matter whether
    /// PlayerLook.Update ran earlier or later that frame. Cost: a few comparisons per frame.
    /// </summary>
    [HarmonyPatch(typeof(FoliageSystem), "Update")]
    internal static class FoliageFocusLensPatch
    {
        private static bool logged;

        private static void Prefix()
        {
            if (!VR.VRState.IsActive || !FoliageSystem.isFocused)
            {
                return;
            }

            UnityEngine.Camera lens = VR.VRBinoculars.LensCamera;
            UnityEngine.Camera current = FoliageSystem.focusCamera;
            if (lens != null)
            {
                if (current == null || current == MainCamera.instance)
                {
                    FoliageSystem.focusCamera = lens;
                    if (!logged)
                    {
                        logged = true;
                        VRLog.Info("foliage focus: drawn for the binoculars lens camera instead of the disabled MainCamera.");
                    }
                }
            }
            else if (VR.VRBinoculars.IsLensCamera(current))
            {
                // The lens went down but the focus camera still points at it (PlayerLook only rewrites it on a raycast hit).
                FoliageSystem.focusCamera = MainCamera.instance;
            }
        }
    }
}
