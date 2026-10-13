using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Small UI icons the mod draws itself (the game has no VR headset glyph in its UI icon bundles: its icons are
    /// white shapes on a transparent background that the buttons tint, so these are made the same way, in code, once).
    /// </summary>
    internal static class VRIcons
    {
        private const int Size = 80;       // pixels; shown at 40 so it stays sharp
        private const float Canvas = 64f;  // the shapes below are laid out on a 64 x 64 canvas, origin bottom left

        private static Texture2D hmd;

        /// <summary>A VR headset seen from the front: visor with two lenses, a nose dip and the head straps.</summary>
        public static Texture2D Hmd
        {
            get
            {
                if (hmd == null)
                {
                    hmd = BuildHmd();
                }
                return hmd;
            }
        }

        private static Texture2D BuildHmd()
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            texture.name = "UnturnedVR HMD Icon";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave; // survives the game's unused-asset cleanup

            float pixelsPerUnit = Size / Canvas;
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float px = (x + 0.5f) / pixelsPerUnit;
                    float py = (y + 0.5f) / pixelsPerUnit;

                    // Solid parts: the visor, the strap over the top, the two side straps.
                    float d = RoundedBox(px, py, 32f, 30f, 26f, 15f, 8f);
                    d = Mathf.Min(d, RoundedBox(px, py, 32f, 48.5f, 11f, 4f, 3f));
                    d = Mathf.Min(d, RoundedBox(px, py, 4f, 30f, 3.5f, 7f, 2.5f));
                    d = Mathf.Min(d, RoundedBox(px, py, 60f, 30f, 3.5f, 7f, 2.5f));

                    // Cut-outs: the two lenses and the dip for the nose on the bottom edge.
                    d = Mathf.Max(d, -Circle(px, py, 21f, 30f, 8.5f));
                    d = Mathf.Max(d, -Circle(px, py, 43f, 30f, 8.5f));
                    d = Mathf.Max(d, -Circle(px, py, 32f, 15f, 5f));

                    // Signed distance (negative inside) to a one pixel soft edge.
                    float alpha = Mathf.Clamp01(0.5f - d * pixelsPerUnit);
                    pixels[y * Size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static float RoundedBox(float px, float py, float cx, float cy, float halfX, float halfY, float radius)
        {
            float qx = Mathf.Abs(px - cx) - (halfX - radius);
            float qy = Mathf.Abs(py - cy) - (halfY - radius);
            float outsideX = Mathf.Max(qx, 0f);
            float outsideY = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        }

        private static float Circle(float px, float py, float cx, float cy, float radius)
        {
            float dx = px - cx;
            float dy = py - cy;
            return Mathf.Sqrt(dx * dx + dy * dy) - radius;
        }
    }
}
