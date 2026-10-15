using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// The per-eye camera matrices for effects that the game (or the post-processing package) computes from the mono
    /// camera. camera.worldToCameraMatrix / projectionMatrix are the head-centred matrices; in the headset each eye has
    /// its own (GetStereoViewMatrix / GetStereoProjectionMatrix), and a reflection built from the mono ones is seen
    /// shifted and swimming when it is sampled per eye. The helpers fall back to the mono values when the camera is not
    /// rendering a stereo eye (flat camera, scope camera, editor views), so they are safe to call from any patched site.
    /// </summary>
    internal static class StereoMatrices
    {
        public static bool TryGetEye(Camera cam, out Camera.StereoscopicEye eye)
        {
            eye = Camera.StereoscopicEye.Left;
            if (!VR.VRState.IsActive || cam == null || !cam.stereoEnabled)
            {
                return false;
            }

            switch (cam.stereoActiveEye)
            {
                case Camera.MonoOrStereoscopicEye.Left:
                    eye = Camera.StereoscopicEye.Left;
                    return true;
                case Camera.MonoOrStereoscopicEye.Right:
                    eye = Camera.StereoscopicEye.Right;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Replacement for camera.worldToCameraMatrix.</summary>
        public static Matrix4x4 View(Camera cam)
        {
            Camera.StereoscopicEye eye;
            return TryGetEye(cam, out eye) ? cam.GetStereoViewMatrix(eye) : cam.worldToCameraMatrix;
        }

        /// <summary>Replacement for camera.projectionMatrix.</summary>
        public static Matrix4x4 Projection(Camera cam)
        {
            Camera.StereoscopicEye eye;
            return TryGetEye(cam, out eye) ? cam.GetStereoProjectionMatrix(eye) : cam.projectionMatrix;
        }

        /// <summary>
        /// Replacement for camera.CalculateObliqueMatrix(clipPlane): Camera.CalculateObliqueMatrix works on the camera's own
        /// mono projection, so for a stereo eye the same oblique near-plane clip (the standard Lengyel construction that
        /// the Unity docs use) is applied to that eye's projection.
        /// </summary>
        public static Matrix4x4 Oblique(Camera cam, Vector4 clipPlane)
        {
            Camera.StereoscopicEye eye;
            if (!TryGetEye(cam, out eye))
            {
                return cam.CalculateObliqueMatrix(clipPlane);
            }

            Matrix4x4 projection = cam.GetStereoProjectionMatrix(eye);
            Vector4 q = projection.inverse * new Vector4(Sign(clipPlane.x), Sign(clipPlane.y), 1f, 1f);
            Vector4 c = clipPlane * (2f / Vector4.Dot(clipPlane, q));
            projection[2] = c.x - projection[3];
            projection[6] = c.y - projection[7];
            projection[10] = c.z - projection[11];
            projection[14] = c.w - projection[15];
            return projection;
        }

        private static float Sign(float a)
        {
            return a > 0f ? 1f : a < 0f ? -1f : 0f;
        }

        // The eye projection FieldOfView read last, for SetFieldOfView; pendingAspect 0 = none (mono).
        private static float pendingAspect;
        private static Matrix4x4 pendingProjection;

        /// <summary>
        /// Replacement for camera.fieldOfView, for the code that copies it to a helper camera. The VR camera's own
        /// fieldOfView is not what the headset shows (the eye projection decides that), so the vertical field of view and
        /// the aspect are taken from the eye's projection matrix instead. Unity draws a camera's skybox (and the clouds
        /// in it) from fieldOfView and aspect, not from a custom projectionMatrix, so a helper camera that only copies the
        /// head camera's fieldOfView draws its sky at the wrong scale.
        /// </summary>
        public static float FieldOfView(Camera cam)
        {
            Camera.StereoscopicEye eye;
            pendingAspect = 0f;
            eyePoseSource = null;
            if (!TryGetEye(cam, out eye))
            {
                return cam.fieldOfView;
            }

            Matrix4x4 projection = cam.GetStereoProjectionMatrix(eye);
            if (projection.m11 <= 0.0001f || projection.m00 <= 0.0001f)
            {
                return cam.fieldOfView;
            }

            // The eye's own pose (position and orientation, including the tilt of the panels), taken from the view matrix
            // the eye is rendered with, for the transform of the reflection camera (see EulerAngles / Position).
            Matrix4x4 cameraToWorld = cam.GetStereoViewMatrix(eye).inverse;
            Vector3 forward = -(Vector3)cameraToWorld.GetColumn(2);
            Vector3 up = cameraToWorld.GetColumn(1);
            if (forward.sqrMagnitude > 0.5f && up.sqrMagnitude > 0.5f)
            {
                eyePosition = cameraToWorld.GetColumn(3);
                eyeRotation = Quaternion.LookRotation(forward, up);
                eyePoseSource = cam.transform;
            }

            pendingProjection = projection;
            pendingAspect = projection.m11 / projection.m00;
            return 2f * Mathf.Atan(1f / projection.m11) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Replacement for helper.fieldOfView = value. For a stereo eye the helper becomes a physical camera whose own
        /// (automatic) frustum is the eye's, including the lens shift: a headset eye looks through an off-centre frustum,
        /// and a skybox drawn from a symmetric field of view and aspect alone comes out shifted by a few degrees in
        /// opposite directions in the two eyes (clouds that do not fuse). The world is drawn from the explicit matrices
        /// the original code sets afterwards, so this only affects what Unity derives from the camera's own parameters.
        /// The sign convention of the lens shift is checked against the resulting projection matrix, not assumed.
        /// </summary>
        public static void SetFieldOfView(Camera helper, float fieldOfView)
        {
            if (pendingAspect <= 0f)
            {
                helper.usePhysicalProperties = false;
                helper.fieldOfView = fieldOfView;
                helper.ResetAspect();
                return;
            }

            Matrix4x4 target = pendingProjection;
            const float sensorHeight = 24f;

            helper.ResetProjectionMatrix();
            helper.usePhysicalProperties = true;
            helper.gateFit = Camera.GateFitMode.Vertical;
            helper.sensorSize = new Vector2(sensorHeight * pendingAspect, sensorHeight);
            helper.focalLength = sensorHeight * target.m11 * 0.5f;
            helper.aspect = pendingAspect;

            Vector2 shift = new Vector2(target.m02 * 0.5f, target.m12 * 0.5f);
            helper.lensShift = shift;
            Matrix4x4 made = helper.projectionMatrix;
            if (Mathf.Abs(target.m02) > 0.001f && Mathf.Abs(made.m02 - target.m02) > Mathf.Abs(made.m02 + target.m02))
            {
                shift.x = -shift.x;
            }
            if (Mathf.Abs(target.m12) > 0.001f && Mathf.Abs(made.m12 - target.m12) > Mathf.Abs(made.m12 + target.m12))
            {
                shift.y = -shift.y;
            }
            helper.lensShift = shift;

        }

        // The eye pose FieldOfView read last, for the calls that read the source camera's transform; null source = none.
        private static Transform eyePoseSource;
        private static Vector3 eyePosition;
        private static Quaternion eyeRotation;

        /// <summary>
        /// Replacement for transform.eulerAngles in the reflection code: for the head camera's own transform in a stereo
        /// eye it is the eye's orientation (mirrored by the game's code into the reflection camera's rotation, which
        /// Unity may use for the sky), otherwise the transform's.
        /// </summary>
        public static Vector3 EulerAngles(Transform transform)
        {
            return eyePoseSource != null && transform == eyePoseSource ? eyeRotation.eulerAngles : transform.eulerAngles;
        }

        /// <summary>Replacement for transform.position in the reflection code, same rule as <see cref="EulerAngles"/>.</summary>
        public static Vector3 Position(Transform transform)
        {
            return eyePoseSource != null && transform == eyePoseSource ? eyePosition : transform.position;
        }

        /// <summary>
        /// Swaps Transform.eulerAngles and Transform.position reads for the helpers above; returns how many
        /// eulerAngles reads it replaced (the position helper is applied to every position read and decides by transform).
        /// </summary>
        public static int ReplacePose(List<CodeInstruction> code)
        {
            MethodInfo monoEuler = AccessTools.PropertyGetter(typeof(Transform), "eulerAngles");
            MethodInfo monoPosition = AccessTools.PropertyGetter(typeof(Transform), "position");
            MethodInfo euler = AccessTools.Method(typeof(StereoMatrices), "EulerAngles");
            MethodInfo position = AccessTools.Method(typeof(StereoMatrices), "Position");

            int replaced = 0;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.opcode != OpCodes.Callvirt && instruction.opcode != OpCodes.Call)
                {
                    continue;
                }

                MethodInfo target = instruction.operand as MethodInfo;
                if (target == monoEuler)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = euler;
                    replaced++;
                }
                else if (target == monoPosition)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = position;
                }
            }
            return replaced;
        }

        /// <summary>Swaps the field of view copy (get on the source, set on the helper); returns how many it replaced.</summary>
        public static int ReplaceFieldOfView(List<CodeInstruction> code)
        {
            MethodInfo monoGet = AccessTools.PropertyGetter(typeof(Camera), "fieldOfView");
            MethodInfo monoSet = AccessTools.PropertySetter(typeof(Camera), "fieldOfView");
            MethodInfo get = AccessTools.Method(typeof(StereoMatrices), "FieldOfView");
            MethodInfo set = AccessTools.Method(typeof(StereoMatrices), "SetFieldOfView");

            int replaced = 0;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.opcode != OpCodes.Callvirt && instruction.opcode != OpCodes.Call)
                {
                    continue;
                }

                MethodInfo target = instruction.operand as MethodInfo;
                if (target == monoGet || target == monoSet)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = target == monoGet ? get : set;
                    replaced++;
                }
            }
            return replaced;
        }

        /// <summary>Swaps the calls to the given mono camera getters for the per-eye helpers; returns how many it replaced.</summary>
        public static int Replace(List<CodeInstruction> code)
        {
            MethodInfo monoView = AccessTools.PropertyGetter(typeof(Camera), "worldToCameraMatrix");
            MethodInfo monoProjection = AccessTools.PropertyGetter(typeof(Camera), "projectionMatrix");
            MethodInfo monoOblique = AccessTools.Method(typeof(Camera), "CalculateObliqueMatrix", new[] { typeof(Vector4) });
            MethodInfo view = AccessTools.Method(typeof(StereoMatrices), "View");
            MethodInfo projection = AccessTools.Method(typeof(StereoMatrices), "Projection");
            MethodInfo oblique = AccessTools.Method(typeof(StereoMatrices), "Oblique");

            int replaced = 0;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.opcode != OpCodes.Callvirt && instruction.opcode != OpCodes.Call)
                {
                    continue;
                }

                MethodInfo target = instruction.operand as MethodInfo;
                MethodInfo with = target == monoView ? view
                    : target == monoProjection ? projection
                    : target == monoOblique ? oblique
                    : null;
                if (with == null)
                {
                    continue;
                }

                // Same stack shape (the camera, plus the plane for the oblique one), so only the call changes.
                instruction.opcode = OpCodes.Call;
                instruction.operand = with;
                replaced++;
            }
            return replaced;
        }
    }

    /// <summary>
    /// Water reflection. PlanarReflection (the game's Standard Assets water script) renders one mirrored image of the
    /// world per frame from the camera's mono matrices and the water shader samples it with each eye's screen position,
    /// so the reflection was misaligned and moved against the world when you moved your head. The transpiler makes
    /// RenderReflectionFor build the mirrored view from the current eye's view and projection matrices. The
    /// game renders each eye separately (multi pass), so the reflection is rendered once per
    /// eye (see <see cref="PlanarReflectionPerEyePatch"/>).
    /// </summary>
    [HarmonyPatch(typeof(PlanarReflection), "RenderReflectionFor")]
    internal static class PlanarReflectionStereoPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int replaced = StereoMatrices.Replace(code);
            int replacedFov = StereoMatrices.ReplaceFieldOfView(code);
            int replacedPose = StereoMatrices.ReplacePose(code);
            // Expected: worldToCameraMatrix and CalculateObliqueMatrix, one each, the field of view get and set, and the
            // two eulerAngles reads (the head camera's, mirrored into the reflection camera's rotation).
            if (replaced == 2 && replacedFov == 2 && replacedPose == 2)
            {
            }
            else
            {
                VRLog.Warn("water: PlanarReflection.RenderReflectionFor changed, replaced " + replaced + " of 2 camera matrix calls, "
                    + replacedFov + " of 2 field of view calls and " + replacedPose + " of 2 eulerAngles reads.");
            }
            return code;
        }
    }

    /// <summary>
    /// PlanarReflection renders its reflection once per frame (the "helped" flag, cleared in LateUpdate), which was fine
    /// for one camera but gives the right eye the left eye's image. The flag is cleared again when the eye that is being
    /// rendered changes, so each eye gets a reflection from its own matrices. The reflection texture is shared and is
    /// consumed by the eye pass that follows, so one texture is enough. Costs a second reflection render per frame while
    /// water is in view, the same as the second eye costs for the world.
    /// </summary>
    [HarmonyPatch(typeof(PlanarReflection), "WaterTileBeingRendered")]
    internal static class PlanarReflectionPerEyePatch
    {
        private static readonly Dictionary<int, int> lastEye = new Dictionary<int, int>();

        private static bool Prefix(PlanarReflection __instance, Camera currentCam, ref bool ___helped, Material ___sharedMaterial, string ___reflectionSampler)
        {
            if (VR.VRState.IsActive && !VR.VRSettings.WaterReflections)
            {
                // The state the game's own code sets when the reflection is not enabled (the else branch of this method and
                // WaterVolume.SyncPlanarReflectionEnabledPartial): simple water, no reflection texture. Nothing is rendered.
                if (___sharedMaterial != null)
                {
                    ___sharedMaterial.DisableKeyword("WATER_REFLECTIVE");
                    ___sharedMaterial.EnableKeyword("WATER_SIMPLE");
                    ___sharedMaterial.SetTexture(___reflectionSampler, null);
                }
                return false;
            }

            Camera.StereoscopicEye eye;
            if (currentCam == null || !currentCam.CompareTag("MainCamera") || !StereoMatrices.TryGetEye(currentCam, out eye))
            {
                return true;
            }

            int id = __instance.GetInstanceID();
            int last;
            if (!lastEye.TryGetValue(id, out last) || last != (int)eye)
            {
                ___helped = false;
                lastEye[id] = (int)eye;
            }

            return true;
        }
    }

    /// <summary>
    /// Screen-space reflections (the post-processing package's effect, used for smooth surfaces such as puddles, tanks
    /// and silos when Reflections is on in the graphics options) read camera.projectionMatrix and
    /// camera.worldToCameraMatrix, the mono matrices, so the reflected image slid against the view when you moved your
    /// head. The transpiler makes the effect read the current eye's matrices (the
    /// effect is built per eye while the camera is rendering that eye).
    /// </summary>
    [HarmonyPatch]
    internal static class ScreenSpaceReflectionsStereoPatch
    {
        private const string RendererType = "UnityEngine.Rendering.PostProcessing.ScreenSpaceReflectionsRenderer";

        private static MethodBase TargetMethod()
        {
            System.Type type = AccessTools.TypeByName(RendererType);
            return type == null ? null : AccessTools.Method(type, "Render");
        }

        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int replaced = StereoMatrices.Replace(code);
            // Expected: projectionMatrix once and worldToCameraMatrix twice (view and inverse view).
            if (replaced == 3)
            {
            }
            else
            {
                VRLog.Warn("screen-space reflections: ScreenSpaceReflectionsRenderer.Render changed, replaced " + replaced + " of 3 camera matrix calls.");
            }
            return code;
        }
    }
}
