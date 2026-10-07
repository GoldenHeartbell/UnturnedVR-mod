using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// M4: the "pick up X" label under the outlined item. PlayerUI.hint places it by projecting the
    /// target through MainCamera.instance, which is disabled in VR and follows the mouse-look aim, not
    /// the head, so the label lands somewhere that does not match what the headset sees (or off the
    /// HUD panel entirely). A postfix moves it to where the line from the VR eye to the target crosses
    /// the HUD panel, so it sits over the item as seen in the headset. hint runs every frame while
    /// the target is focused, so the label tracks head movement.
    /// </summary>
    [HarmonyPatch(typeof(PlayerUI), nameof(PlayerUI.hint), new[] { typeof(Transform), typeof(EPlayerMessage), typeof(string), typeof(Color), typeof(object[]) })]
    internal static class PlayerUIHintPatch
    {
        private const float EdgeMargin = 0.05f;

        private static void Postfix(Transform transform)
        {
            Camera eye = VR.VRRig.ActiveCamera;
            Transform panel = VR.VRGui.PanelTransform;
            if (!VR.VRState.IsActive || transform == null || eye == null || panel == null
                || PlayerUI.messageBox == null || PlayerUI.container == null)
            {
                return;
            }

            // Ray from the eye through the target, intersected with the panel plane (panel +Z points away from the viewer).
            Vector3 direction = transform.position - eye.transform.position;
            Vector3 normal = panel.forward;
            float facing = Vector3.Dot(direction, normal);
            Vector2 viewport = new Vector2(0.5f, 0.5f);
            if (facing > 0.0001f)
            {
                float t = Vector3.Dot(panel.position - eye.transform.position, normal) / facing;
                Vector3 local = panel.InverseTransformPoint(eye.transform.position + direction * t);
                viewport = new Vector2(
                    Mathf.Clamp(local.x + 0.5f, EdgeMargin, 1f - EdgeMargin),
                    Mathf.Clamp(local.y + 0.5f, EdgeMargin, 1f - EdgeMargin));
            }

            Vector2 normalized = PlayerUI.container.ViewportToNormalizedPosition(viewport);
            PlayerUI.messageBox.PositionScale_X = normalized.x;
            PlayerUI.messageBox.PositionScale_Y = normalized.y;
        }
    }
}
