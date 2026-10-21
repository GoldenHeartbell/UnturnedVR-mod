using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The two-bone arm IK of the game's character rigs, shared by the first-person arms (<see cref="VRArms"/>) and the arms of other
    /// players (<see cref="VRRemoteArms"/>). Both rigs have the same bone chain: *_Shoulder (no skin weight), *_Arm (the visible
    /// shoulder joint), *_Hand (the elbow) and *_Hook (the palm end). Every bone is posed from its rest pose, captured when the rig is
    /// resolved, so the pose depends only on where the hand must be; nothing depends on the game's own arm animation.
    /// </summary>
    internal static class VRArmIK
    {
        internal struct Arm
        {
            public Transform Pivot;  // *_Shoulder: no skin weight, pinned at rest
            public Transform Upper;  // *_Arm: the visible shoulder joint
            public Transform Lower;  // *_Hand: the visible elbow
            public Transform Hook;   // palm end
            public Vector3 PivotRestPosition;
            public Quaternion PivotRestRotation;
            public Vector3 UpperRestPosition;    // under Pivot
            public Quaternion UpperRestRotation;
            public Vector3 LowerRestPosition;    // under Upper
            public Quaternion LowerRestRotation;
            public Vector3 HookRestPosition;     // under Lower
            public Quaternion HookRestRotation;
            public Vector3 UpperAxis;            // direction (in Upper's local space) from Upper to Lower
            public Vector3 LowerAxis;            // direction (in Lower's local space) from Lower to Hook
            public Vector3 TopLocal;             // the forearm block's top face (in Lower's local space): the rig's up at resolve
            public Vector3 InnerLocal;           // the face that looks at the body when the arm is held forward (Lower's local space)
        }

        /// <summary>
        /// Finds the bones of one arm under <paramref name="spine"/> and captures their rest pose. Call it while the rig is still in its
        /// T-pose (arms out to the sides): the rest rotations and the forearm's faces are taken from it. <paramref name="root"/> is the
        /// rig's upright frame (its up and right).
        /// </summary>
        internal static bool TryResolve(Transform root, Transform spine, string side, out Arm arm)
        {
            arm = default;
            Transform pivot = spine.Find(side + "_Shoulder");
            Transform upper = pivot != null ? pivot.Find(side + "_Arm") : null;
            Transform lower = upper != null ? upper.Find(side + "_Hand") : null;
            Transform hook = lower != null ? lower.Find(side + "_Hook") : null;
            if (hook == null)
            {
                return false;
            }

            arm.Pivot = pivot;
            arm.Upper = upper;
            arm.Lower = lower;
            arm.Hook = hook;
            arm.PivotRestPosition = pivot.localPosition;
            arm.PivotRestRotation = pivot.localRotation;
            arm.UpperRestPosition = upper.localPosition;
            arm.UpperRestRotation = upper.localRotation;
            arm.LowerRestPosition = lower.localPosition;
            arm.LowerRestRotation = lower.localRotation;
            arm.HookRestPosition = hook.localPosition;
            arm.HookRestRotation = hook.localRotation;
            arm.UpperAxis = lower.localPosition.normalized;
            arm.LowerAxis = hook.localPosition.normalized;

            // Which face of the forearm block is the top: the one that points up in the rest pose (T-pose, arms out to the
            // sides, so up is square to the arm). The block's roll later comes only from the smallest swing, so this face
            // keeps pointing up while the arm points forward. Assumes the rest pose is upright; logged in Resolve.
            Vector3 top = Vector3.ProjectOnPlane(Quaternion.Inverse(lower.rotation) * root.up, arm.LowerAxis);
            arm.TopLocal = top.sqrMagnitude > 0.0001f ? top.normalized : Vector3.up;

            // Inner face: from the T-pose (arm out to its side, top up), turning the arm forward carries the face with
            // normal cross(top, axis) towards the body on the arm that lies out to the viewer's left, and the opposite
            // face on the one out to the right. Logged in Resolve.
            Vector3 axisWorld = lower.rotation * arm.LowerAxis;
            float inwardSign = Vector3.Dot(axisWorld, root.right) < 0f ? 1f : -1f;
            arm.InnerLocal = (inwardSign * Vector3.Cross(arm.TopLocal, arm.LowerAxis)).normalized;
            return true;
        }

        /// <summary>
        /// The hand (hook) goes to <paramref name="target"/>; <paramref name="forward"/> is the way the forearm should roughly point (elbow
        /// side). <paramref name="scale"/> is the world scale of the rig's bone offsets (the first-person rig is scaled down), and
        /// <paramref name="elbowStretch"/> lengthens the upper arm in rig units (0 = vanilla proportions).
        /// </summary>
        // roll: extra twist of the forearm about its own axis in degrees (0 for a hand on a controller). With levelWeight > 0
        // the roll is measured from "top face up" instead of from the smallest swing, blended in by levelWeight.
        internal static void DriveTo(Arm arm, Vector3 target, Vector3 forward, Quaternion yaw, float side, float scale, float elbowStretch, float roll = 0f, float levelWeight = 0f)
        {
            // The shoulder joint (*_Arm) stays where the skin expects it: pivot at rest, *_Arm at its rest offset.
            arm.Pivot.SetLocalPositionAndRotation(arm.PivotRestPosition, arm.PivotRestRotation);
            arm.Upper.localPosition = arm.UpperRestPosition;
            Vector3 lowerPosition = arm.LowerRestPosition + arm.UpperAxis * elbowStretch;
            arm.Lower.localPosition = lowerPosition;
            arm.Hook.SetLocalPositionAndRotation(arm.HookRestPosition, arm.HookRestRotation);

            float upperLength = lowerPosition.magnitude * scale; // shoulder joint -> elbow
            float lowerLength = arm.HookRestPosition.magnitude * scale;  // elbow -> hook (forearm + hand block)

            // Hook target: the controller, as far as the arm reaches (it never stretches).
            Vector3 shoulder = arm.Upper.position;
            Vector3 toTarget = target - shoulder;
            float distance = toTarget.magnitude;
            if (distance < 0.0001f)
            {
                return;
            }
            Vector3 direction = toTarget / distance;
            distance = Mathf.Clamp(distance, Mathf.Abs(upperLength - lowerLength) + 0.01f, upperLength + lowerLength - 0.005f);
            Vector3 hook = shoulder + direction * distance;

            // Elbow circle of the two-bone IK; take the point nearest to where the controller's forward would put it.
            float along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance) / (2f * distance);
            float radius = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
            Vector3 center = shoulder + direction * along;
            Vector3 wanted = hook - forward * lowerLength - center;
            wanted -= Vector3.Dot(wanted, direction) * direction;
            if (wanted.sqrMagnitude < 0.0001f)
            {
                wanted = yaw * new Vector3(side * 0.35f, -1f, -0.25f); // controller points along the arm: bend down and out
                wanted -= Vector3.Dot(wanted, direction) * direction;
            }
            Vector3 elbow = center + wanted.normalized * radius;

            // Smallest swing from the rest pose, so the result depends only on where the bones must point.
            Quaternion upperRest = arm.Pivot.rotation * arm.UpperRestRotation;
            Quaternion upperRotation = Quaternion.FromToRotation(upperRest * arm.UpperAxis, elbow - shoulder) * upperRest;
            arm.Upper.rotation = upperRotation;

            Quaternion lowerRest = upperRotation * arm.LowerRestRotation;
            Quaternion lowerRotation = Quaternion.FromToRotation(lowerRest * arm.LowerAxis, hook - elbow) * lowerRest;
            Vector3 forearmAxis = (hook - elbow).normalized;
            Vector3 upAcross = Vector3.ProjectOnPlane(Vector3.up, forearmAxis);
            if (levelWeight > 0f && upAcross.sqrMagnitude > 0.04f)
            {
                // The top face is put straight towards the world's up (then turned by roll), whatever twist the smallest
                // swing left in the arm; the swing's own twist is what made the arm swirl when it points across the body.
                Vector3 desiredTop = Quaternion.AngleAxis(roll, forearmAxis) * upAcross.normalized;
                Vector3 currentTop = lowerRotation * arm.TopLocal;
                float angle = Vector3.SignedAngle(currentTop, desiredTop, forearmAxis);
                Quaternion leveled = Quaternion.AngleAxis(angle, forearmAxis) * lowerRotation;
                lowerRotation = Quaternion.Slerp(lowerRotation, leveled, levelWeight);
            }
            else if (roll != 0f)
            {
                lowerRotation = Quaternion.AngleAxis(roll, forearmAxis) * lowerRotation;
            }
            arm.Lower.rotation = lowerRotation;
        }
    }
}
