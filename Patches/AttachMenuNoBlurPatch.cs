using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// No world blur behind the attachment menu. Attach mode sets PlayerUI.isLocked, which counts as a cursor UI and so
    /// PlayerUI.tickMenuBlur turns the main depth-of-field on, exactly like the inventory does. The attach menu is a
    /// small panel on the gun that you need to see the world around, so the blur is switched off while the local
    /// player's gun is in attach mode. isLocked / showCursor are left alone (the laser and input handling use them);
    /// the inventory, pause menu and the other cursor UIs keep their blur.
    /// </summary>
    [HarmonyPatch(typeof(UnturnedPostProcess), nameof(UnturnedPostProcess.SetIsMainBlurEnabled))]
    internal static class AttachMenuNoBlurPatch
    {
        private static void Prefix(ref bool enabled)
        {
            if (enabled && VR.VRState.IsActive && VR.VRAttachMenu.IsAttaching(VR.VRAttachMenu.EquippedGun()))
            {
                enabled = false;
            }
        }
    }
}
