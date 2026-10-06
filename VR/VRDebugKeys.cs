#if UNITY_EDITOR
using System.Text;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Editor-only: toggles for tracking down rendering differences between the game's MainCamera
    /// and the VR camera while looking at the scene in the headset.
    ///   F6 toggle PostProcessLayer   F7 toggle HDR   F8 cycle rendering path   F9 dump both cameras
    /// </summary>
    internal class VRDebugKeys : MonoBehaviour
    {
        private Camera VrCamera => GetComponentInChildren<Camera>(true);

        private void Update()
        {
            Camera cam = VrCamera;
            if (cam == null)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.F6))
            {
                PostProcessLayer layer = cam.GetComponent<PostProcessLayer>();
                if (layer != null)
                {
                    layer.enabled = !layer.enabled;
                    UnturnedLog.info("[UnturnedVR] debug: PostProcessLayer enabled = " + layer.enabled);
                }
                else
                {
                    UnturnedLog.info("[UnturnedVR] debug: VR camera has no PostProcessLayer.");
                }
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                cam.allowHDR = !cam.allowHDR;
                UnturnedLog.info("[UnturnedVR] debug: allowHDR = " + cam.allowHDR);
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                cam.renderingPath = cam.renderingPath == RenderingPath.DeferredShading
                    ? RenderingPath.Forward
                    : RenderingPath.DeferredShading;
                UnturnedLog.info("[UnturnedVR] debug: renderingPath = " + cam.renderingPath);
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                var sb = new StringBuilder("[UnturnedVR] debug camera dump:\n");
                Describe(sb, "MainCamera", MainCamera.instance);
                Describe(sb, "VR camera ", cam);
                UnturnedLog.info(sb.ToString());
            }
        }

        private static void Describe(StringBuilder sb, string label, Camera c)
        {
            if (c == null)
            {
                sb.Append(label).Append(": null\n");
                return;
            }

            PostProcessLayer layer = c.GetComponent<PostProcessLayer>();
            sb.Append(label).Append(": path=").Append(c.actualRenderingPath)
              .Append(" hdr=").Append(c.allowHDR).Append(" msaa=").Append(c.allowMSAA)
              .Append(" clear=").Append(c.clearFlags).Append(" near=").Append(c.nearClipPlane)
              .Append(" far=").Append(c.farClipPlane).Append(" mask=").Append(c.cullingMask)
              .Append(" depthTex=").Append(c.depthTextureMode).Append(" stereo=").Append(c.stereoTargetEye)
              .Append(" ppl=").Append(layer == null ? "none" : layer.enabled.ToString())
              .Append(" components=");
            foreach (Component comp in c.GetComponents<Component>())
            {
                sb.Append(comp == null ? "<missing>" : comp.GetType().Name).Append(',');
            }
            sb.Append('\n');
        }
    }
}
#endif
