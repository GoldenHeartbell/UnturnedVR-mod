using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Holographic sights (red / holo dots): the game does not leave the reticle where the sight model has it. Every
    /// frame UseableGun.UpdateHolographicReticulePosition moves the sight's "Reticule" transform to where a ray from
    /// the animator's viewmodel camera (plus recoil) crosses the reticle plane, so the dot follows "the true aiming
    /// direction regardless of viewmodel animation". In VR that viewmodel camera is neither the headset nor the gun
    /// (the gun sits on the controller), so the dot slid out of the sight window. In VR the reticle stays at the
    /// position the sight model was authored with, the middle of the sight window, and the gun and the head decide
    /// what it looks at, like a normal (parallax) dot sight. The game's own numbers are used: the same start position
    /// it restores at the top of that method. Cost: one transform write per frame while a holographic sight is on.
    /// </summary>
    [HarmonyPatch(typeof(UseableGun), "UpdateHolographicReticulePosition")]
    internal static class UseableGunHolographicReticulePatch
    {
        private static bool logged;

        // Field injection (triple underscore): firstAttachments and originalReticuleHookLocalPosition are not public.
        private static bool Prefix(UseableGun __instance, Attachments ___firstAttachments, Vector3 ___originalReticuleHookLocalPosition)
        {
            if (!VR.VRState.IsActive || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return true; // the game's own behaviour
            }

            Transform hook = ___firstAttachments != null ? ___firstAttachments.reticuleHook : null;
            if (hook == null)
            {
                return true;
            }

            hook.localPosition = ___originalReticuleHookLocalPosition;
            if (!logged)
            {
                logged = true;
                VRLog.Info("holographic sight: reticle kept in the middle of the sight (the game's viewmodel-camera ray is not used in VR).");
            }
            return false;
        }
    }
}
