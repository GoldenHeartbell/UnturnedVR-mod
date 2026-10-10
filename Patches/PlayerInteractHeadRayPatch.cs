using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Pickup/interact targeting. PlayerInteract.Update casts a single ray (4 m, first person) from
    /// MainCamera.instance's position along its forward, and MainCamera is disabled in VR and follows the
    /// mouse-look aim. The prefix puts MainCamera's transform on the right hand (or the VR eye while that
    /// controller is not tracked) for the duration of Update, so the ray, focus, outline and hint all use it;
    /// the postfix puts it back.
    /// </summary>
    [HarmonyPatch(typeof(PlayerInteract), "Update")]
    internal static class PlayerInteractHeadRayPatch
    {
        private struct SavedPose
        {
            public bool Swapped;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static void Prefix(PlayerInteract __instance, out SavedPose __state)
        {
            __state = default;

            Camera eye = VR.VRRig.ActiveCamera;
            Camera main = MainCamera.instance;
            if (!VR.VRState.IsActive || eye == null || main == null
                || __instance.channel == null || !__instance.channel.IsLocalPlayer)
            {
                return;
            }

            Transform t = main.transform;
            __state.Swapped = true;
            __state.Position = t.position;
            __state.Rotation = t.rotation;

            Transform source = eye.transform;
            Transform hand = VR.VRHands.Anchor(VR.VRHands.Right);
            if (hand != null && VR.VRHands.IsTracked(VR.VRHands.Right))
            {
                source = hand;
            }
            t.SetPositionAndRotation(source.position, source.rotation);
        }

        private static void Postfix(SavedPose __state)
        {
            if (!__state.Swapped)
            {
                return;
            }

            if (MainCamera.instance != null)
            {
                MainCamera.instance.transform.SetPositionAndRotation(__state.Position, __state.Rotation);
            }
            VR.VRHaptics.InteractFocus(PlayerInteract.interactable);
        }
    }
}
