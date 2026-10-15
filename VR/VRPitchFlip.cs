using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Hallucination can flip the game's look input (PlayerLook.pitchInputMultiplier = -1): mouse up looks down. The VR view
    /// pitch is the physical head pitch, so this mirrors it on the VR camera when the "Hallucination pitch flip" setting
    /// is on: looking up shows the view going down. Only the camera's pitch is changed (yaw, roll and the position of the
    /// head stay, so nothing swings around the head).
    ///
    /// Sits on the HMD camera object, added after the pose sources (TrackedPoseDriver, VROpenVRPoseFallback), which write
    /// the raw pose in Update/LateUpdate and again in Application.onBeforeRender. This runs after each of those writes
    /// (execution order, and the onBeforeRender subscription order), so the camera never renders a raw pose. The state
    /// (<see cref="VRRig.PitchFlipActive"/>) is set each frame from PlayerLookHeadYawPatch.
    /// </summary>
    [DefaultExecutionOrder(100)]
    internal sealed class VRPitchFlip : MonoBehaviour
    {
        private Quaternion lastWritten;
        private bool hasWritten;

        /// <summary>The camera currently holds the pose this component wrote (not a fresh raw pose from the tracker).</summary>
        public bool HoldsFlippedPose => hasWritten && transform.localRotation == lastWritten;

        private void OnEnable()
        {
            hasWritten = false;
            Application.onBeforeRender += Apply;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= Apply;
        }

        private void LateUpdate()
        {
            Apply();
        }

        private void Apply()
        {
            if (!VRRig.PitchFlipActive)
            {
                hasWritten = false;
                return;
            }

            // Nothing has written a new raw pose since our last flip: flipping again would undo it.
            if (HoldsFlippedPose)
            {
                return;
            }

            Vector3 euler = transform.localRotation.eulerAngles;
            float pitch = Mathf.DeltaAngle(0f, euler.x);
            lastWritten = Quaternion.Euler(-pitch, euler.y, euler.z);
            hasWritten = true;
            transform.localRotation = lastWritten;
        }
    }
}
