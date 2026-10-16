#if !UNITY_EDITOR // PlanarReflection (Standard Assets water) is in the shipped Assembly-CSharp, not in the SDK/editor build
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Makes the water reflection render cheaper in VR without changing what it shows in practice. A reflection is a whole
    /// extra camera render, now once per eye, and the log showed it costing about 2 ms of main-thread time per render
    /// (4 ms per frame) with the game's settings (shadows to 400 m in 4 cascades, 8 pixel lights, LOD bias 5), which was
    /// enough to push the frame over the 90 Hz budget and halve the frame rate. A mirrored, rippling image does not show
    /// shadows or fine LODs, so for the duration of the reflection render only: shadows are off, the pixel light count
    /// is capped and the LOD bias is lowered, then the values are put back (finalizer, so also if the render throws).
    /// The values are global quality settings, which is why they are restored right after the render; the eye's own
    /// render (culling and shadows) is not affected because it does not read them again until after the restore.
    /// </summary>
    [HarmonyPatch(typeof(PlanarReflection), "RenderReflectionFor")]
    internal static class PlanarReflectionBudgetPatch
    {
        private const int ReflectionPixelLights = 1;
        private const float ReflectionLodBias = 1.5f;

        // The game's culling masks per planar reflection quality step (static fields of PlanarReflection), indexed by
        // EGraphicQuality value minus 1: Low, Medium, High, Ultra.
        private static readonly string[] MaskFieldNames = { "CULLING_MASK_LOW", "CULLING_MASK_MEDIUM", "CULLING_MASK_HIGH", "CULLING_MASK_ULTRA" };
        private static FieldInfo[] maskFields;

        private static bool applied;
        private static float savedShadowDistance;
        private static int savedPixelLights;
        private static float savedLodBias;

        private static void Prefix(Camera cam, Camera reflectCamera)
        {
            if (!VR.VRState.IsActive || applied)
            {
                return;
            }

            CapReflectionQuality(reflectCamera);
            ApplySky(cam, reflectCamera);

            applied = true;
            savedShadowDistance = QualitySettings.shadowDistance;
            savedPixelLights = QualitySettings.pixelLightCount;
            savedLodBias = QualitySettings.lodBias;

            QualitySettings.shadowDistance = 0f;
            if (savedPixelLights > ReflectionPixelLights)
            {
                QualitySettings.pixelLightCount = ReflectionPixelLights;
            }
            if (savedLodBias > ReflectionLodBias)
            {
                QualitySettings.lodBias = ReflectionLodBias;
            }
        }

        /// <summary>
        /// The VR setting's quality step caps the game's planar reflection quality: when it is lower, the reflection
        /// camera uses the game's culling mask for that step. The game sets its own mask only when its graphics settings
        /// change, so for one render after such a change this cap is overwritten, then applied again. Off never gets
        /// here (the render is skipped in PlanarReflectionPerEyePatch).
        /// </summary>
        private static void CapReflectionQuality(Camera reflectCamera)
        {
            if (reflectCamera == null)
            {
                return;
            }

            int level = VR.VRSettings.WaterReflectionLevel;
            int game = (int)GraphicsSettings.planarReflectionQuality;
            if (level <= 0 || level >= game)
            {
                return; // at or above the game's own step: nothing to cap
            }

            if (maskFields == null)
            {
                maskFields = new FieldInfo[MaskFieldNames.Length];
                for (int index = 0; index < MaskFieldNames.Length; index++)
                {
                    maskFields[index] = AccessTools.Field(typeof(PlanarReflection), MaskFieldNames[index]);
                }
            }

            FieldInfo field = level - 1 < maskFields.Length ? maskFields[level - 1] : null;
            if (field == null)
            {
                return;
            }

            int mask = (int)field.GetValue(null);
            reflectCamera.cullingMask = mask;
        }

        // The clear mode each reflection camera had when the game made it, by instance id, to put back at Ultra.
        private static readonly System.Collections.Generic.Dictionary<int, CameraClearFlags> originalClearFlags =
            new System.Collections.Generic.Dictionary<int, CameraClearFlags>();

        /// <summary>
        /// The sky (skybox and the clouds in it) is only in the reflection at Ultra. Below that the reflection camera
        /// clears to one flat colour taken from the level's own sky colours instead of drawing the skybox: no sky pass, and
        /// no cloud reflection to get wrong. At Ultra the game's clear mode is put back.
        /// </summary>
        private static void ApplySky(Camera cam, Camera reflectCamera)
        {
            if (reflectCamera == null)
            {
                return;
            }

            int id = reflectCamera.GetInstanceID();
            int level = VR.VRSettings.WaterReflectionLevel;
            if (level >= VR.VRSettings.WaterReflectionLevelMax && VR.VRSettings.WaterSkyReflection)
            {
                // Ultra: the real sky, drawn as a cube object (see DrawSkyCube); the engine's own sky pass is replaced by
                // a flat clear, because it draws the sky of the unmirrored view.
                if (DrawSkyCube(cam, reflectCamera))
                {
                    if (!originalClearFlags.ContainsKey(id))
                    {
                        originalClearFlags[id] = reflectCamera.clearFlags;
                    }

                    reflectCamera.clearFlags = CameraClearFlags.SolidColor;
                    reflectCamera.backgroundColor = Color.Lerp(LevelLighting.skyboxEquator, LevelLighting.skyboxSky, 0.35f);
                }
                else
                {
                    CameraClearFlags original;
                    if (originalClearFlags.TryGetValue(id, out original))
                    {
                        reflectCamera.clearFlags = original;
                        originalClearFlags.Remove(id);
                    }
                }
            }
            else
            {
                if (!originalClearFlags.ContainsKey(id))
                {
                    originalClearFlags[id] = reflectCamera.clearFlags;
                }

                reflectCamera.clearFlags = CameraClearFlags.SolidColor;
                reflectCamera.backgroundColor = Color.Lerp(LevelLighting.skyboxEquator, LevelLighting.skyboxSky, 0.35f);
            }
        }

        // Half-size of the sky cube in metres: inside the reflection camera's far plane, outside everything near.
        private const float SkyCubeHalfSize = 100f;
        private static Mesh skyCube;

        /// <summary>
        /// The sky of the reflection. The engine draws a camera's sky from the camera's matrices and, for the mirrored
        /// (improper) view matrix of a water reflection, draws the sky of the unmirrored view (sky probe), so the clouds
        /// followed the head. The sky shader takes its direction straight from the mesh vertex, so the same sky material is
        /// drawn here as an ordinary cube centred on the real eye position, queued for the reflection camera only
        /// (Graphics.DrawMesh with the camera). The mirrored camera then shows it exactly like the rest of the world, which
        /// is the correct reflected sky: the world part of the reflection is not touched at all.
        /// </summary>
        private static bool DrawSkyCube(Camera cam, Camera reflectCamera)
        {
            Material sky = RenderSettings.skybox;
            int mask = reflectCamera.cullingMask;
            if (sky == null || cam == null || mask == 0)
            {
                return false;
            }

            // A layer the reflection camera renders.
            int layer = 0;
            while (((mask >> layer) & 1) == 0)
            {
                layer++;
            }

            Camera.StereoscopicEye eye;
            Vector3 eyePosition = cam.transform.position;
            if (StereoMatrices.TryGetEye(cam, out eye))
            {
                eyePosition = cam.GetStereoViewMatrix(eye).inverse.GetColumn(3);
            }

            if (skyCube == null)
            {
                GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                skyCube = primitive.GetComponent<MeshFilter>().sharedMesh;
                Object.Destroy(primitive);
            }

            Matrix4x4 matrix = Matrix4x4.TRS(eyePosition, Quaternion.identity, Vector3.one * (SkyCubeHalfSize * 2f));
            UnityEngine.Graphics.DrawMesh(skyCube, matrix, sky, layer, reflectCamera, 0, null, false, false, false);
            return true;
        }

        private static void Finalizer()
        {
            if (!applied)
            {
                return;
            }

            applied = false;
            QualitySettings.shadowDistance = savedShadowDistance;
            QualitySettings.pixelLightCount = savedPixelLights;
            QualitySettings.lodBias = savedLodBias;
        }
    }
}
#endif
