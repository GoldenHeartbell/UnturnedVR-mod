using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// M5: pickup/interact targeting follows the head. PlayerInteract.Update casts a single ray (4 m,
    /// first person) from MainCamera.instance's position along its forward, and MainCamera is disabled
    /// in VR and follows the mouse-look aim. The prefix puts MainCamera's transform on the VR eye for the
    /// duration of Update (so the ray, focus, outline and hint all use the head), the postfix puts it back.
    /// Later, with motion controls, the pose below is the one to swap for the hand.
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
            t.SetPositionAndRotation(eye.transform.position, eye.transform.rotation);
        }

        private static void Postfix(SavedPose __state)
        {
            if (!__state.Swapped || MainCamera.instance == null)
            {
                return;
            }

            MainCamera.instance.transform.SetPositionAndRotation(__state.Position, __state.Rotation);
        }
    }
}
