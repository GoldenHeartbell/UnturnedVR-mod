using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Hands, phase 1: one anchor transform per controller, children of the rig root, so a tracking-space
    /// pose is just the anchor's local pose (the rig carries body yaw and eye height, same as the HMD).
    /// Poses are re-read right before rendering (Application.onBeforeRender) so hands are not a frame behind
    /// the head. Everything else (arm IK, held items, gun aim) reads <see cref="Anchor"/> and never samples
    /// the controllers itself.
    /// </summary>
    internal class VRHands : MonoBehaviour
    {
        public const int Left = 0;
        public const int Right = 1;

        /// <summary>
        /// Controller -> hand pose. Euler X is the tilt in degrees, positive = forward axis pitched DOWN. The raw
        /// controller forward points about 25 degrees above where the hand points when the arm is held out, so
        /// the arms, the held item, the pickup ray and the menu/inventory laser (VRPointer) all use this tilt.
        /// Position offset is zero. Tune in headset.
        /// </summary>
        public static Vector3 GripOffsetPosition = Vector3.zero;
        public static Vector3 GripOffsetEuler = new Vector3(12f, 0f, 0f);

        private static VRHands instance;
        private readonly Transform[] anchors = new Transform[2];
        private readonly LineRenderer[] markers = new LineRenderer[2];
        private Quaternion gripOffsetRotation = Quaternion.identity;

        /// <summary>Anchor of a hand (<see cref="Left"/> / <see cref="Right"/>), or null before the rig exists.</summary>
        public static Transform Anchor(int hand)
        {
            return instance != null ? instance.anchors[hand] : null;
        }

        /// <summary>True while that controller has a valid pose; the anchor then holds it.</summary>
        public static bool IsTracked(int hand)
        {
            return hand == Left ? VRControllerInput.LeftPoseValid : VRControllerInput.RightPoseValid;
        }

        /// <summary>Velocity of that hand in world space (m/s).</summary>
        public static Vector3 WorldVelocity(int hand)
        {
            Transform rig = VRRig.RigTransform;
            Vector3 v = hand == Left ? VRControllerInput.LeftVelocity : VRControllerInput.RightVelocity;
            return rig != null ? rig.rotation * v : v;
        }

        public static void Create(Transform rigRoot)
        {
            if (instance != null || rigRoot == null)
            {
                return;
            }
            rigRoot.gameObject.AddComponent<VRHands>();
        }

        private void Awake()
        {
            BuildAnchors();
        }

        private void BuildAnchors()
        {
            instance = this;
            for (int i = 0; i < 2; i++)
            {
                if (anchors[i] != null)
                {
                    continue;
                }
                GameObject go = new GameObject(i == Left ? "UnturnedVR Left Hand" : "UnturnedVR Right Hand");
                go.transform.SetParent(transform, worldPositionStays: false);
                anchors[i] = go.transform;
                if (VRDebugFlags.HandMarkers)
                {
                    markers[i] = CreateMarker(go, i == Left ? new Color(0.2f, 0.9f, 1f) : new Color(1f, 0.6f, 0.1f));
                }
            }
            gripOffsetRotation = Quaternion.Euler(GripOffsetEuler);
        }

        private void OnEnable()
        {
            instance = this;
            Application.onBeforeRender += OnBeforeRender;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRender;
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }

        // The frame-start sample (VRControllerInput.Refresh, early Update) is already applied by LateUpdate.
        private void LateUpdate()
        {
            long t = VRPerf.Begin();
            VRControllerInput.Refresh();
            Apply();
            VRPerf.End(VRPerf.Hands, t);
        }

        private void OnBeforeRender()
        {
            long t = VRPerf.Begin();
            VRControllerInput.RefreshPoses();
            Apply();
            VRPerf.End(VRPerf.Hands, t);
        }

        private void Apply()
        {
            // A script reload in the editor while playing keeps this component but wipes its fields (and a
            // throwing onBeforeRender callback would stop the other callbacks, incl. the head pose).
            if (anchors[Left] == null || anchors[Right] == null)
            {
                BuildAnchors();
            }
            Place(Left, VRControllerInput.LeftPoseValid, VRControllerInput.LeftPosition, VRControllerInput.LeftRotation);
            Place(Right, VRControllerInput.RightPoseValid, VRControllerInput.RightPosition, VRControllerInput.RightRotation);
        }

        private void Place(int hand, bool valid, Vector3 position, Quaternion rotation)
        {
            Transform anchor = anchors[hand];
            if (markers[hand] != null && markers[hand].enabled != valid)
            {
                markers[hand].enabled = valid;
            }
            if (!valid)
            {
                return;
            }
            anchor.localRotation = rotation * gripOffsetRotation;
            anchor.localPosition = position + rotation * GripOffsetPosition;
        }

        // Debug cross: long line forward, short ticks right and up. Local space, so it rides the anchor.
        private static LineRenderer CreateMarker(GameObject parent, Color color)
        {
            LineRenderer line = parent.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.material = new Material(Canvas.GetDefaultCanvasMaterial());
            line.startColor = color;
            line.endColor = color;
            line.startWidth = 0.006f;
            line.endWidth = 0.006f;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.positionCount = 6;
            line.SetPositions(new[]
            {
                new Vector3(0f, 0f, 0.15f), Vector3.zero, new Vector3(0.04f, 0f, 0f),
                new Vector3(-0.04f, 0f, 0f), Vector3.zero, new Vector3(0f, 0.04f, 0f),
            });
            return line;
        }
    }
}
