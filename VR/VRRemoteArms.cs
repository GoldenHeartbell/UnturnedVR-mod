using SDG.Unturned;
using UnityEngine;
using UnturnedVR.Net;
using Arm = UnturnedVR.VR.VRArmIK.Arm;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Sync of VR hands, milestone 2: the arms of ANOTHER player's third-person rig follow that player's controllers. The server relays
    /// each VR player's pose (Net/VRNetRelay), the game stores the last few (<see cref="VRNetRemotePoses"/>), and this component, on the
    /// "Third" rig of that player's character, moves the arm bones to the hands after the game animated them.
    ///
    /// Vanilla draws other players with the legacy Animation component (clips for the stance and the held item) and the HumanAnimator,
    /// whose LateUpdate bakes the head pitch into the spine and skull. This component runs after that (execution order 500), puts
    /// the bones on the rest pose captured when the rig was created (the same T-pose capture VRArms does for the first-person rig) and
    /// uses the same two-bone IK (<see cref="VRArmIK"/>), at the rig's own scale. When there is no fresh pose, or the player sits, drives,
    /// is dead or is left-handed (the rig is mirrored), it does nothing and the game's animation shows. Milestone 3: the held item is put
    /// on the pose the sender's game gave it (in the left or right hand, aimed and held as the sender holds it), with the other hand on
    /// the grip of a two-handed gun; without a relayed item pose it stays where the game hangs it.
    /// Cost: a handful of transform writes per VR player in view per frame, no allocations.
    /// </summary>
    [DefaultExecutionOrder(500)] // after the Animation component and HumanAnimator.LateUpdate
    internal sealed class VRRemoteArms : MonoBehaviour
    {
        /// <summary>The game draws other players' bodies this far in the past (PlayerMovement's snapshot buffer); the hands use the same so they stay with the body.</summary>
        private const float RenderDelay = 0.1f;

        /// <summary>A pose older than this is not shown. The server relays 20 a second and stops when the sender's pose goes stale.</summary>
        private const float MaxPoseAge = 0.3f;

        /// <summary>
        /// The avatar is much wider than a person (the shoulder joints are 0.37 m from the middle, the arm is 0.375 m thick), so a hand held
        /// in front of a real chest sits inside its body. The torso is treated as a box around the body's middle: half as wide as the
        /// shoulder joints plus this clearance, and <see cref="TorsoHalfDepth"/> plus the clearance deep. A hand inside it is moved out
        /// through the nearest side: a hand beside the body goes outwards, a hand in front of (or behind) the chest goes forward (back).
        /// </summary>
        private const float HandClearance = 0.12f;

        /// <summary>Half the depth of the avatar's torso (its Spine box collider is 0.6 m deep).</summary>
        private const float TorsoHalfDepth = 0.3f;

        /// <summary>The box is only for the body below the neck: a hand above <see cref="TopFadeStart"/> over the neck pivot is pushed less, none above <see cref="TopFadeEnd"/> (a hand raised above the head is not in the torso).</summary>
        private const float TopFadeStart = 0.05f;
        private const float TopFadeEnd = 0.3f;

        /// <summary>Seconds over which the push eases in and out, so a hand crossing the box does not jump.</summary>
        private const float PushSmoothing = 0.04f;

        /// <summary>
        /// Where the elbow goes. The two-bone IK puts the elbow on a circle around the line from the shoulder to the hand, at the point
        /// furthest from the "forearm direction" it is given. The controller's own forward is a bad choice for that: when pointing, the
        /// controller points along the arm, the circle point is then decided by tiny changes and flips (the arm snaps into the body). So
        /// the elbow is steered by the body instead: outwards, a little down and a little back (<see cref="ElbowDown"/>,
        /// <see cref="ElbowBack"/>), which looks like a relaxed arm for any hand position in front of the body.
        /// </summary>
        private const float ElbowDown = 0.35f;
        private const float ElbowBack = 0.3f;

        /// <summary>Hands closer together than <see cref="TogetherNear"/> (m) swing the elbows out further (by <see cref="TogetherOutward"/>), so the arms come in from the sides; the effect fades out by <see cref="TogetherFar"/>.</summary>
        private const float TogetherNear = 0.15f;
        private const float TogetherFar = 0.4f;
        private const float TogetherOutward = 0.5f;

        /// <summary>
        /// The avatar's shoulders are much wider than a person's, so hands held straight out at a real shoulder width made the arms point
        /// inwards. A hand reaching forward is moved outwards by up to <see cref="ReachSpread"/> (m): none within <see cref="ReachNear"/>
        /// in front of the neck pivot, all of it from <see cref="ReachFar"/>. Faded out with the hands together (a two-handed hold, see
        /// <see cref="TogetherNear"/>), so they still meet.
        /// </summary>
        private const float ReachSpread = 0.08f;
        private const float ReachNear = 0.15f;
        private const float ReachFar = 0.5f;

        /// <summary>
        /// The avatar's arm is longer than the distance a person's hand gets from the avatar's (wide) shoulder, so a fully stretched arm
        /// still bent at the elbow. When the sender's hand is far from their own eye (an arm being stretched: <see cref="StraightFrom"/>
        /// to <see cref="StraightAt"/> m), the hand is moved out along the line from the avatar's shoulder until that arm is straight.
        /// </summary>
        private const float StraightFrom = 0.45f;
        private const float StraightAt = 0.62f;

        /// <summary>The held item is drawn this far (m) outwards from the body's middle, on the side of the hand holding it: on the wide avatar it sat inside the hand, towards the middle.</summary>
        private const float ItemOutward = 0.075f;

        private Player player;
        private Transform frame;      // the rig's upright frame ("Third")
        private Transform skeleton;
        private Transform skull;      // the avatar's neck pivot: the hands hang on it
        private Arm left;
        private Arm right;
        private bool resolved;
        private bool unusable;
        private bool swapped;         // the rig's "Left" arm lies on the viewer's right
        private bool driving;
        private string lastSkip = string.Empty;
        private byte channelId;
        private float minLateral;     // half the width of the torso box (m)
        private float halfDepth;      // half the depth of the torso box (m)
        private Vector3 leftPush;     // what the torso box moved the left / right hand by, eased
        private Vector3 rightPush;
        private bool pushesValid;
        private bool shouldersKnown;        // the rig shoulders below, in skull space, measured after last frame's IK
        private Vector3 leftShoulderLocal;  // the arm driven by the sender's left hand
        private Vector3 rightShoulderLocal;
        private Transform itemModel;  // this player's third-person item model as last seen
        private Vector3 itemRestPosition; // its local pose as the game set it, put back when the relayed pose stops
        private Quaternion itemRestRotation;
        private bool itemDriven;
        private Transform itemLoggedFor;
        private bool supportReady;
        private bool hasSupport;
        private Vector3 itemSupportLocal;

        public static void Attach(PlayerAnimator animator)
        {
            if (animator == null || animator.thirdSkeleton == null || animator.player == null)
            {
                return;
            }

            VRRemoteArms arms = animator.thirdSkeleton.parent.GetComponent<VRRemoteArms>();
            if (arms == null)
            {
                arms = animator.thirdSkeleton.parent.gameObject.AddComponent<VRRemoteArms>();
            }
            arms.player = animator.player;
            arms.frame = animator.thirdSkeleton.parent;
            arms.skeleton = animator.thirdSkeleton;
            arms.resolved = false;
            arms.unusable = false;

            // At once, not on the first frame: the rest pose is captured from the rig as the prefab made it, before the Animation
            // component has played a clip on it.
            arms.Resolve();
        }

        private void LateUpdate()
        {
            if (unusable || !resolved || !VRState.ShowsRemoteArms || player == null)
            {
                return;
            }

            if (!CanDrive(out string skip))
            {
                Skip(skip);
                return;
            }

            if (!VRNetRemotePoses.TryGetInterpolated(channelId, RenderDelay, MaxPoseAge, out VRRelayPose relayed))
            {
                Skip("no fresh pose");
                return;
            }

            if (!driving)
            {
                driving = true;
                lastSkip = string.Empty;
                VRLog.Info("remote arms: driving the arms of player channel " + channelId + " from the relayed pose.");
            }

            // The sender's hands are relative to their head, which is not where their body is when they step away from the middle of the
            // play space. They hang on the avatar's own head instead: the neck pivot (the avatar's shoulders hang on it too) plus the
            // offset between the neck and the eye that VRArms uses for the first-person arms.
            Quaternion yaw = Quaternion.Euler(0f, player.look.yaw, 0f);
            Vector3 eye = skull.position - yaw * VRArms.NeckOffset;
            VRRelayPose pose = relayed.ToWorld(player.transform.position, eye, player.look.yaw);
            float scale = Mathf.Abs(left.Pivot.lossyScale.x);

            // The controller rotations are not used for the arms: the elbows are steered by the body (see ElbowDown). With the hands close
            // together the elbows go out further, so the arms come in from the sides in front of the body. Judged on where the hands
            // really are.
            Vector3 bodyForward = yaw * Vector3.forward;
            Vector3 bodyRight = yaw * Vector3.right;
            float together = 0f;
            if (pose.LeftTracked && pose.RightTracked)
            {
                float apart = Vector3.Distance(pose.LeftPosition, pose.RightPosition);
                together = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(TogetherNear, TogetherFar, apart));
            }
            Vector3 leftForward = ElbowForearmDirection(-bodyRight, bodyForward, together);
            Vector3 rightForward = ElbowForearmDirection(bodyRight, bodyForward, together);

            // The avatar is wider than a person: the hands are kept out of its torso (only this player's view of the OTHER player; the
            // local player's own arms are driven by VRArms and are not limited).
            Vector3 center = skull.position;
            Vector3 wantedLeftPush = TorsoPush(pose.LeftPosition, center, yaw, -1f) + ReachPush(pose.LeftPosition, center, yaw, -1f, together);
            Vector3 wantedRightPush = TorsoPush(pose.RightPosition, center, yaw, 1f) + ReachPush(pose.RightPosition, center, yaw, 1f, together);
            Arm leftArm = swapped ? right : left;
            Arm rightArm = swapped ? left : right;
            wantedLeftPush += StraightenPush(leftArm, leftShoulderLocal, pose.LeftPosition + wantedLeftPush, pose.LeftPosition, eye, scale);
            wantedRightPush += StraightenPush(rightArm, rightShoulderLocal, pose.RightPosition + wantedRightPush, pose.RightPosition, eye, scale);
            if (!pushesValid)
            {
                leftPush = wantedLeftPush;
                rightPush = wantedRightPush;
                pushesValid = true;
            }
            else
            {
                float ease = 1f - Mathf.Exp(-Time.deltaTime / PushSmoothing);
                leftPush = Vector3.Lerp(leftPush, wantedLeftPush, ease);
                rightPush = Vector3.Lerp(rightPush, wantedRightPush, ease);
            }

            // swapped: the rig's "Left" arm is on the viewer's right.
            if (pose.LeftTracked)
            {
                VRArmIK.DriveTo(leftArm, pose.LeftPosition + leftPush, leftForward, yaw, swapped ? 1f : -1f, scale, 0f);
            }
            if (pose.RightTracked)
            {
                VRArmIK.DriveTo(rightArm, pose.RightPosition + rightPush, rightForward, yaw, swapped ? -1f : 1f, scale, 0f);
            }
            // DriveTo puts the shoulders at their rest place; kept for next frame's StraightenPush.
            leftShoulderLocal = skull.InverseTransformPoint(leftArm.Upper.position);
            rightShoulderLocal = skull.InverseTransformPoint(rightArm.Upper.position);
            shouldersKnown = true;

            PlaceItem(pose, yaw, leftForward, rightForward, scale);
        }

        // The held item on the pose the sender's own game gave its first-person model (same prefab, so the same origin): in the hand, with
        // the hold, the two-handed aim and the handle slide already in it. Moved with the item hand's torso push, so it stays in that hand,
        // and <see cref="ItemOutward"/> to the outside.
        // A two-handed gun gets the other hand on its grip point, like VRArms does for the local player. Last, because the model hangs on
        // a hook of one of the arms: moving an arm moves it.
        private void PlaceItem(in VRRelayPose pose, Quaternion yaw, Vector3 leftForward, Vector3 rightForward, float scale)
        {
            Transform model = player.equipment != null ? player.equipment.thirdModel : null;
            if (model != itemModel)
            {
                RestoreItem();
                itemModel = model;
                itemLoggedFor = null;
                supportReady = false;
                if (model != null)
                {
                    itemRestPosition = model.localPosition;
                    itemRestRotation = model.localRotation;
                }
            }
            if (model == null || pose.ItemHand == VRPoseData.ItemHandNone)
            {
                RestoreItem();
                return;
            }

            bool inLeft = pose.ItemHand == VRPoseData.ItemHandLeft;
            Vector3 position = pose.ItemPosition + (inLeft ? leftPush : rightPush);
            position += yaw * Vector3.right * (inLeft ? -ItemOutward : ItemOutward);
            Quaternion rotation = pose.ItemRotation;
            model.SetPositionAndRotation(position, rotation);
            itemDriven = true;

            bool supported = false;
            if (pose.TwoHanded && (inLeft ? pose.RightTracked : pose.LeftTracked) && TryGetSupportLocal(model, out Vector3 supportLocal))
            {
                // The off hand: the sender's other hand, on the rig arm that lies on that side.
                bool offIsLeft = !inLeft;
                Arm offArm = offIsLeft ? (swapped ? right : left) : (swapped ? left : right);
                VRArmIK.DriveTo(offArm, model.TransformPoint(supportLocal), offIsLeft ? leftForward : rightForward, yaw, offIsLeft ? -1f : 1f, scale, 0f);
                model.SetPositionAndRotation(position, rotation);
                supported = true;
            }

            if (itemLoggedFor != model)
            {
                itemLoggedFor = model;
                VRLog.Info("remote arms: player channel " + channelId + " holds \"" + model.name + "\" in the " + (inLeft ? "left" : "right") + " hand"
                    + (pose.TwoHanded ? (supported ? ", two-handed (other hand on the grip point)" : ", two-handed (no grip point, other hand on its controller)") : "") + ".");
            }
        }

        // Where the second hand holds this player's gun, in the model's space: the Grip hook, else part of the way to the Barrel hook (the
        // same point VRArms uses for the local player). Worked out once per model.
        private bool TryGetSupportLocal(Transform model, out Vector3 supportLocal)
        {
            if (!supportReady)
            {
                supportReady = true;
                hasSupport = false;
                Attachments attachments = model.GetComponent<Attachments>();
                if (player.equipment.asset is ItemGunAsset gun && attachments != null)
                {
                    if (attachments.gripHook != null)
                    {
                        itemSupportLocal = attachments.gripHook.localPosition;
                        hasSupport = true;
                    }
                    else if (attachments.barrelHook != null)
                    {
                        itemSupportLocal = attachments.barrelHook.localPosition * VRGunTuning.Get(gun, out _).SupportFraction;
                        hasSupport = true;
                    }
                }
            }
            supportLocal = itemSupportLocal;
            return hasSupport;
        }

        // The item back where the game hangs it (on the hook or the spine), for the game's own animation to show.
        private void RestoreItem()
        {
            if (itemDriven && itemModel != null)
            {
                itemModel.SetLocalPositionAndRotation(itemRestPosition, itemRestRotation);
            }
            itemDriven = false;
        }

        // The "forearm direction" handed to the IK for one hand: the opposite of where the elbow should be (outwards, down and back).
        // outward: the body's right or left. See ElbowDown.
        private static Vector3 ElbowForearmDirection(Vector3 outward, Vector3 bodyForward, float together)
        {
            Vector3 elbow = outward * (1f + TogetherOutward * together) - Vector3.up * ElbowDown - bodyForward * ElbowBack;
            return -elbow.normalized;
        }

        // How far, and which way, the torso box moves a hand that is inside it: out through the nearest side. A hand beside the body
        // (small push sideways) goes outwards; a hand in front of the chest (small push forward) goes forward; one behind goes back.
        // Fades out above the neck. side: -1 = the left hand, +1 = the right.
        private Vector3 TorsoPush(Vector3 hand, Vector3 center, Quaternion yaw, float side)
        {
            Vector3 offset = hand - center;
            Vector3 right = yaw * Vector3.right;
            Vector3 forward = yaw * Vector3.forward;
            float lateral = Vector3.Dot(offset, right) * side; // positive: on the hand's own side of the middle line
            float along = Vector3.Dot(offset, forward);
            if (lateral >= minLateral || Mathf.Abs(along) >= halfDepth)
            {
                return Vector3.zero;
            }

            float weight = Mathf.Clamp01((TopFadeEnd - offset.y) / (TopFadeEnd - TopFadeStart));
            if (weight <= 0f)
            {
                return Vector3.zero;
            }

            float sideways = minLateral - lateral;
            float ahead = halfDepth - Mathf.Abs(along);
            Vector3 push = sideways <= ahead ? right * (side * sideways) : forward * ((along < 0f ? -1f : 1f) * ahead);
            return push * weight;
        }

        // Along the arm, so a stretched arm is drawn straight (see StraightFrom). target: the hand with its other pushes; realHand: the
        // relayed hand, judged against the sender's eye.
        private Vector3 StraightenPush(in Arm arm, Vector3 shoulderLocal, Vector3 target, Vector3 realHand, Vector3 eye, float scale)
        {
            if (!shouldersKnown)
            {
                return Vector3.zero;
            }
            float stretch = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(StraightFrom, StraightAt, Vector3.Distance(realHand, eye)));
            if (stretch <= 0f)
            {
                return Vector3.zero;
            }

            Vector3 shoulder = skull.TransformPoint(shoulderLocal);
            float length = (arm.LowerRestPosition.magnitude + arm.HookRestPosition.magnitude) * scale - 0.01f; // DriveTo's reach, elbow stretch 0
            Vector3 toTarget = target - shoulder;
            float distance = toTarget.magnitude;
            if (distance < 0.0001f || distance >= length)
            {
                return Vector3.zero;
            }
            return toTarget * ((length - distance) * stretch / distance);
        }

        // Outwards for a hand reaching forward (see ReachSpread). side: -1 = the left hand, +1 = the right.
        private static Vector3 ReachPush(Vector3 hand, Vector3 center, Quaternion yaw, float side, float together)
        {
            float along = Vector3.Dot(hand - center, yaw * Vector3.forward);
            float weight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ReachNear, ReachFar, along)) * (1f - together);
            return yaw * Vector3.right * (side * ReachSpread * weight);
        }

        private bool CanDrive(out string skip)
        {
            skip = null;
            if (!skeleton.gameObject.activeInHierarchy)
            {
                skip = "rig hidden";
            }
            else if (frame.lossyScale.x < 0f)
            {
                skip = "left-handed (mirrored rig)";
            }
            else if (player.life == null || player.life.isDead)
            {
                skip = "dead";
            }
            else if (player.stance != null && (player.stance.stance == EPlayerStance.DRIVING || player.stance.stance == EPlayerStance.SITTING))
            {
                skip = "driving or sitting";
            }
            return skip == null;
        }

        // Logs a change of the reason once (not every frame) and lets the game's animation show.
        private void Skip(string reason)
        {
            if (driving)
            {
                driving = false;
            }
            pushesValid = false;
            shouldersKnown = false;
            RestoreItem();
            if (reason != lastSkip)
            {
                lastSkip = reason;
                VRLog.Info("remote arms: not driving player channel " + channelId + ": " + reason + ".");
            }
        }

        private void Resolve()
        {
            channelId = (byte) player.channel.id;

            Transform spine = skeleton.Find("Spine");
            skull = spine != null ? spine.Find("Skull") : null;
            if (spine == null || skull == null || !VRArmIK.TryResolve(frame, spine, "Left", out left) || !VRArmIK.TryResolve(frame, spine, "Right", out right))
            {
                VRLog.Warn("remote arms: bone chain not found for player channel " + channelId + ", their arms stay on the game's animation.");
                unusable = true;
                return;
            }

            // Which rig side lies on the viewer's left: the hooks sit far apart sideways in the T-pose. If they do not, the rig is not in
            // its T-pose (a clip already ran) and the rest pose would be wrong, so this player's arms are left alone.
            float leftX = frame.InverseTransformPoint(left.Hook.position).x;
            float rightX = frame.InverseTransformPoint(right.Hook.position).x;
            if (Mathf.Abs(leftX - rightX) < 0.1f)
            {
                VRLog.Warn("remote arms: the rig of player channel " + channelId + " is not in its T-pose (hook x " + leftX.ToString("0.00") + " / "
                    + rightX.ToString("0.00") + "), their arms stay on the game's animation.");
                unusable = true;
                return;
            }

            swapped = leftX > rightX;
            minLateral = Vector3.Distance(left.Upper.position, right.Upper.position) * 0.5f + HandClearance;
            halfDepth = TorsoHalfDepth + HandClearance;
            resolved = true;
            VRLog.Info("remote arms: bones resolved for player channel " + channelId + ". shoulder joint to elbow "
                + Vector3.Distance(left.Upper.position, left.Lower.position).ToString("0.000") + " m, elbow to hook "
                + Vector3.Distance(left.Lower.position, left.Hook.position).ToString("0.000") + " m. hook x: rig-left " + leftX.ToString("0.00")
                + ", rig-right " + rightX.ToString("0.00") + (swapped ? " (sides swapped)" : "") + ", rig scale " + frame.lossyScale.x.ToString("0.00") + ". Hands are kept out of a torso box "
                + (minLateral * 2f).ToString("0.00") + " m wide and " + (halfDepth * 2f).ToString("0.00") + " m deep.");
        }
    }
}
