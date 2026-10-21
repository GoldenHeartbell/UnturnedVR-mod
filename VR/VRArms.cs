using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using Arm = UnturnedVR.VR.VRArmIK.Arm;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Hands, phase 2: the game's first-person arm rig ("View" root -> Viewmodel -> Skeleton) follows the
    /// controllers. Findings (SDK, Idle/Animation.fbx, Viewmodel/Model_0.fbx skin weights, PlayerAnimator, log):
    /// vanilla puts the rig at the world origin and renders it with a separate ViewmodelCamera on the Skull bone,
    /// so in VR the arms stuck to the head.
    /// The arm mesh (Model_0, 82 vertices, 5 rings per arm at 0.374/0.562/0.749/0.936/1.123 from the neck) is
    /// skinned like this: ring 0.374 100% Spine (fixed to the body), 0.562 half Spine / half *_Arm, 0.749 half
    /// *_Arm / half *_Hand, 0.936 and 1.123 100% *_Hand. *_Shoulder has NO skin weight. So the visible joints are:
    ///   *_Arm  = the shoulder joint (origin 0.370 from the neck pivot, where the body ring is),
    ///   *_Hand = the elbow (0.372 further), *_Hook = the palm end (0.508 further).
    /// Earlier versions treated *_Shoulder/*_Arm/*_Hand as shoulder/elbow/wrist, which bent the arm at the invisible
    /// neck pivot and stretched the soft zone next to the fixed body ring (the "fumbled" upper arm).
    /// Now: the root is moved under the headset (yaw only) and scaled by <see cref="ArmScale"/>; the VR camera
    /// renders the viewmodel layer and the ViewmodelCamera is off. Spine, Skull and *_Shoulder are pinned to their
    /// rest pose so the *_Arm origin (the shoulder) stays where the skin expects it; only *_Arm and *_Hand rotate
    /// (two-bone IK with the hook on the controller, as far as the arm reaches: it never stretches; the elbow is the
    /// point of the IK circle nearest to where the controller's forward would put it). Nothing depends on the game's
    /// arm animation: every bone is posed from its rest pose, captured when the rig is resolved (T-pose), by the
    /// smallest swing that makes it point where it must. Bone directions come from the rest offsets (left arm: -x,
    /// right: +x); the bones below *_Hand are pinned to their rest offsets and rotations every frame.
    /// The equipped item is counter-scaled to its normal size and turned with the controller; the animation's
    /// item orientation relative to the root (sampled once the equip animation is done, never from the T-pose)
    /// tells which way the item faces.
    /// Cost: a handful of transform writes per frame, no allocations, no Find after init.
    /// </summary>
    [DefaultExecutionOrder(500)] // after the Animation component and PlayerAnimator.LateUpdate
    internal class VRArms : MonoBehaviour
    {
        /// <summary>Neck pivot relative to the eye, in the yaw frame (right, up, forward). Tune in headset.</summary>
        internal static readonly Vector3 NeckOffset = new Vector3(0f, -0.28f, -0.05f);
        /// <summary>
        /// Uniform scale of the arm rig. Shoulder joint to hook is 0.880 unscaled (0.372 + 0.508), so reach = 0.88 x scale;
        /// a human arm is about 0.6 and the arm mesh is 0.375 thick unscaled. Tune (reach vs thickness).
        /// </summary>
        private const float ArmScale = 0.65f;
        /// <summary>
        /// Extra upper-arm length in rig units (x ArmScale metres): the elbow joint is moved out along the arm. Only the
        /// soft skin zone between the shoulder ring and the hand block lengthens (pure translation, so the cross
        /// sections stay square and the hand block is untouched); the clothing texture stretches slightly there.
        /// 0 = vanilla proportions.
        /// </summary>
        /// <summary>Seconds the item glides to its new place when it is grabbed or passed.</summary>
        private const float ItemBlendSeconds = 0.12f;

        private const float ElbowStretch = 0.15f;
        private const float MinCalibrateSeconds = 0.6f;
        private const float GiveUpSeconds = 4f;
        /// <summary>Hooks further than this sideways from the root (unscaled units) mean the rig is still in its T-pose.</summary>
        private const float MaxSaneHookX = 0.8f;

        private static VRArms instance;

        /// <summary>True while the arms are driven by VR; the VR camera must then draw the viewmodel layer.</summary>
        public static bool Active => instance != null && instance.running;

        /// <summary>Hand (VRHands.Left/Right) the equipped item is posed on this frame, -1 when there is none.</summary>
        public static int ItemHand { get; private set; } = -1;

        /// <summary>World position where the second hand holds the equipped gun (VRHold grabs there), if it has one.</summary>
        public static Vector3 SupportPoint { get; private set; }
        public static bool SupportValid { get; private set; }

        /// <summary>The equipped gun's aim frame this frame (the controller turned by the gun's class pose): forward along the barrel, up as held.</summary>
        public static Quaternion AimFrame { get; private set; } = Quaternion.identity;

        private PlayerAnimator animator;
        private Transform root;
        private Transform viewmodel;
        private Transform skull;
        private Vector3 skullRestLocal; // neck pivot in root space (rig units) in the rest pose
        private Transform spine;
        private Vector3 spineRestPosition;
        private Quaternion spineRestRotation;
        private Vector3 skullRestPosition;
        private Quaternion skullRestRotation;
        private Camera viewmodelCamera;
        private Arm left;
        private Arm right;
        private bool swapped;
        private bool resolved;
        private bool itemCalibrated;
        private const float SupportHandSideShift = 0f; // second hand sits this far to its own side of the grip point (m)
        private const float SupportHandRoll = 30f;          // forearm twist of the second hand away from top-up (degrees, sign by side)
        private const float TwoHandMinSpan = 0.08f;  // hands closer than this: no two-hand aim (m)
        private const float TwoHandFullSpan = 0.18f; // from here on the barrel points fully at the other hand
        private bool itemIsGun;
        private bool itemIsOptic;      // binoculars: moved off the hand by opticPose
        private VRGunTuning.Pose opticPose;
        private bool itemSlides;       // fishing rod / axe: slid along its own length so the hand holds it somewhere else on the handle
        private Vector3 slideTipLocal; // model-local unit vector from the model origin towards the rod's tip / the axe's head
        private float slideLength;     // metres the hand sits up that axis from the model origin (negative = towards the butt; VRGunTuning.GetRodGrip / GetAxeGrip)
        // The item is posed on its hand before the calibration wait is over, from the vanilla hold as it is animated
        // right now, so it appears in its place (the drawing hand, the offset of binoculars or a gun) and does not jump
        // there once the equip animation ends. The model's own local pose is remembered because posing it changes it
        // (the hold must be sampled from the untouched pose).
        private bool provisionalPending;   // a new model is equipped and calibration has not finished
        private bool provisionalReady;     // itemToRoot holds a usable provisional value
        private bool provisionalDriven;    // the model has been posed with it (its local pose was changed)
        private Vector3 modelLocalPosition;
        private Quaternion modelLocalRotation;
        private bool aimBiasValid;     // two-handed aim: barrel direction relative to the hand-to-hand line, taken on grab
        private Vector3 aimBias;
        private bool hasSupport;       // the gun has a point for the second hand
        private Vector3 supportLocal;  // that point in the model's local space
        private VRGunTuning.Pose gunPose; // offset/pitch of the equipped gun relative to the controller (gun-offsets.txt)
        private Quaternion itemToRoot = Quaternion.identity;
        private Quaternion activeHold = Quaternion.identity; // itemToRoot, or what DriveItem replaced it with for a bow / a gun swung straight
        private Vector3 itemBaseScale = Vector3.one;
        private bool loggedGiveUp;
        private bool running;
        private Transform lastModel;
        private float modelChangedAt;
        private Vector3 lastFlat = Vector3.forward;
        private bool loggedFirstApply;
        private int seenHoldVersion;
        private float blendStart = -1f;
        private Vector3 blendFromPosition;
        private Quaternion blendFromRotation;
        private Vector3 lastItemPosition;
        private Quaternion lastItemRotation;

        public static void Attach(PlayerAnimator playerAnimator)
        {
            if (playerAnimator == null || playerAnimator.viewmodelParentTransform == null)
            {
                return;
            }

            VRArms arms = playerAnimator.viewmodelParentTransform.GetComponent<VRArms>();
            if (arms == null)
            {
                arms = playerAnimator.viewmodelParentTransform.gameObject.AddComponent<VRArms>();
            }
            arms.animator = playerAnimator;
            arms.resolved = false;
            instance = arms;
            VRPerf.Ensure();
        }

        private void OnEnable()
        {
            instance = this;
            Application.onBeforeRender += OnBeforeRender;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRender;
            running = false;
            if (instance == this)
            {
                instance = null;
            }
        }

        private void LateUpdate()
        {
            long t = VRPerf.Begin();
            Apply(allowCalibrate: true);
            VRPerf.End(VRPerf.Arms, t);
        }

        private void OnBeforeRender()
        {
            long t = VRPerf.Begin();
            Apply(allowCalibrate: false);
            VRPerf.End(VRPerf.Arms, t);
        }

        private bool Resolve()
        {
            if (animator == null || animator.firstSkeleton == null)
            {
                return false;
            }

            root = animator.viewmodelParentTransform;
            Transform skeleton = animator.firstSkeleton;
            viewmodel = skeleton.parent;
            spine = skeleton.Find("Spine");
            if (root == null || viewmodel == null || spine == null)
            {
                return false;
            }

            skull = spine.Find("Skull");
            viewmodelCamera = animator.viewmodelCamera;
            if (!ResolveArm(root, spine, "Left", out left) || !ResolveArm(root, spine, "Right", out right) || skull == null)
            {
                VRLog.Warn("arms: bone chain not found, arms stay on the vanilla rig.");
                return false;
            }

            skullRestLocal = root.InverseTransformPoint(skull.position);
            spineRestPosition = spine.localPosition;
            spineRestRotation = spine.localRotation;
            skullRestPosition = skull.localPosition;
            skullRestRotation = skull.localRotation;
            lastModel = null;
            itemCalibrated = false;
            modelChangedAt = Time.unscaledTime;
            resolved = true;

            // Which rig side is on the viewer's left (the hooks sit far apart sideways in any pose, even the T-pose).
            float leftX = root.InverseTransformPoint(left.Hook.position).x;
            float rightX = root.InverseTransformPoint(right.Hook.position).x;
            if (Mathf.Abs(leftX - rightX) > 0.1f)
            {
                swapped = leftX > rightX;
            }
            VRLog.Info("arms: bones resolved. shoulder joint to elbow " + Vector3.Distance(left.Upper.position, left.Lower.position).ToString("0.000")
                + " m, elbow to hook " + Vector3.Distance(left.Lower.position, left.Hook.position).ToString("0.000")
                + " m (unscaled). hook x: rig-left " + leftX.ToString("0.00") + ", rig-right " + rightX.ToString("0.00")
                + (swapped ? " (sides swapped)" : "")
                + ". rest offsets (arm/hand/hook) left: " + left.UpperRestPosition + " " + left.LowerRestPosition + " " + left.HookRestPosition
                + ", right: " + right.UpperRestPosition + " " + right.LowerRestPosition + " " + right.HookRestPosition + ".");
            VRLog.Info("arms: forearm top face (elbow bone local) left " + left.TopLocal.ToString("0.00") + ", right " + right.TopLocal.ToString("0.00")
                + "; forearm axis left " + left.LowerAxis.ToString("0.00") + ", right " + right.LowerAxis.ToString("0.00")
                + "; inner face left " + left.InnerLocal.ToString("0.00") + ", right " + right.InnerLocal.ToString("0.00") + ".");
            return true;
        }

        private static bool ResolveArm(Transform root, Transform spine, string side, out Arm arm)
        {
            return VRArmIK.TryResolve(root, spine, side, out arm);
        }

        /// <summary>
        /// The forearm block (elbow to hook) of the arm that follows this hand, for hanging something flat on it: its
        /// transform (follows the arm whenever the arm is posed), the direction towards the hand and the top face, both in
        /// that transform's local space. False while the arms are not driven by VR.
        /// </summary>
        internal static bool TryGetForearm(int hand, out Transform forearm, out Vector3 axisLocal, out Vector3 topLocal, out Vector3 innerLocal)
        {
            forearm = null;
            axisLocal = Vector3.forward;
            topLocal = Vector3.up;
            innerLocal = Vector3.forward;
            VRArms self = instance;
            if (self == null || !self.running)
            {
                return false;
            }

            // The rig's "Left" arm is on the viewer's right when swapped (same pairing as Apply).
            Arm arm = (hand == VRHands.Left) != self.swapped ? self.left : self.right;
            if (arm.Lower == null)
            {
                return false;
            }

            forearm = arm.Lower;
            axisLocal = arm.LowerAxis;
            topLocal = arm.TopLocal;
            innerLocal = arm.InnerLocal;
            return true;
        }

        /// <summary>
        /// Poses the whole first-person rig for this frame: the arm root under the headset, both arms on the controllers,
        /// and the equipped item on its hand. Everything that has to follow the final gun pose is also kicked off from
        /// here (sight/scope, laser dot, attachment menu panel), because the gun is only in its place after this runs.
        /// Called twice per frame, see <paramref name="allowCalibrate"/>:
        /// LateUpdate (after the game's animation and PlayerLook have written their poses) and OnBeforeRender (again with the
        /// freshest controller poses, so the hands do not lag a frame behind the headset).
        /// Order of work: bail out when VR is not running / the rig is not resolved (<c>running</c> stays false), turn the
        /// game's ViewmodelCamera off (the VR camera draws the viewmodel layer), put the root under the neck, pin Spine and
        /// Skull to rest, (calibrate), drive both arms by IK, place the item, then the item-dependent systems.
        /// </summary>
        /// <param name="allowCalibrate">
        /// True only for the LateUpdate call, false for OnBeforeRender. It gates the work that must run exactly once per
        /// frame and may depend on the game's own animated state:
        /// <see cref="TryCalibrate"/> (detects an item change, resets <see cref="VRHold"/>, and samples the item's
        /// orientation relative to the root once the equip animation is over and the rig has left its T-pose) and
        /// <see cref="VRAds.Update"/> (aim-down-sights decision, which has side effects such as the aim key and a rumble tick).
        /// The before-render call skips both: it only re-poses arms and item from the state the LateUpdate call left, using
        /// the already calibrated orientation.
        /// </param>
        private void Apply(bool allowCalibrate)
        {
            running = false;
            Camera eye = VRRig.ActiveCamera;
            if (!VRState.IsActive || eye == null || animator == null)
            {
                return;
            }
            if ((!resolved && !Resolve()) || animator.firstSkeleton == null || !animator.firstSkeleton.gameObject.activeInHierarchy)
            {
                return;
            }
            running = true;

            if (viewmodelCamera != null && viewmodelCamera.enabled)
            {
                viewmodelCamera.enabled = false; // the VR camera draws the viewmodel layer instead
            }
            if (viewmodel.localScale.x < 0f)
            {
                viewmodel.localScale = Vector3.one; // left-handed players mirror the rig; hands follow controllers here
            }

            Transform head = eye.transform;
            Vector3 flat = head.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f)
            {
                flat = lastFlat; // looking straight up/down: keep the last heading
            }
            flat.Normalize();
            lastFlat = flat;
            Quaternion yaw = Quaternion.LookRotation(flat, Vector3.up);

            // Root: skull (neck pivot) under the eye, facing the head's heading, scaled to a human arm length.
            if (root.localScale.x != ArmScale)
            {
                root.localScale = new Vector3(ArmScale, ArmScale, ArmScale);
            }
            // Spine and Skull carry skin weight of the upper arm: keep them at rest whatever the animation does.
            spine.SetLocalPositionAndRotation(spineRestPosition, spineRestRotation);
            skull.SetLocalPositionAndRotation(skullRestPosition, skullRestRotation);

            // The neck pivot comes from the rest pose, not from the (animated) skull bone, so punches etc. do not shake the rig.
            Vector3 neck = head.position + yaw * NeckOffset;
            root.SetPositionAndRotation(neck - yaw * (skullRestLocal * ArmScale), yaw);

            if (allowCalibrate)
            {
                TryCalibrate();
            }

            // swapped: the rig's "Left" arm is on the viewer's right.
            DriveArm(left, swapped ? VRHands.Right : VRHands.Left, yaw, swapped ? 1f : -1f);
            DriveArm(right, swapped ? VRHands.Left : VRHands.Right, yaw, swapped ? -1f : 1f);
            DriveItem(yaw);
            if (allowCalibrate)
            {
                VRAds.Update(); // once per frame, with the gun and the eye in their final pose
                VRBinoculars.Update(); // same for binoculars: raised to the eyes or not
            }
            VRAds.PlaceScopeCamera();
            VRBinoculars.PlaceCamera();
            VRLaser.Update(); // the laser dot follows the muzzle; the game placed it from the head earlier this frame
            // The beam of a switched-on flashlight starts at the light, not at the eyes, and goes the way the hand frame
            // points (the item's final pose without its hold rotation, as the game aims along the root's forward).
            VRFlashlight.Update(lastModel != null ? lastModel.rotation * Quaternion.Inverse(activeHold) : (Quaternion?)null);
            VRAttachMenu.CachePanelPose(head.position); // the gun is in its final place now
            if (allowCalibrate)
            {
                VRWristStatus.ArmsPosed(); // the wrist panel's gate reads the forearm in this final pose
            }

            if (!loggedFirstApply)
            {
                loggedFirstApply = true;
                VRLog.Info("arms: driven by VR (root moved under the headset, ViewmodelCamera off).");
            }
        }

        // Orientation of the equipped item relative to the root in the animation's pose (the vanilla hold), taken
        // once the equip animation is done and the rig is out of its T-pose. Redone when the item changes.
        private void TryCalibrate()
        {
            Player player = animator.player;
            Transform model = player != null && player.equipment != null ? player.equipment.firstModel : null;
            if (model != lastModel)
            {
                RestoreItemScale();
                VRHold.Reset(model != null);
                seenHoldVersion = VRHold.Version;
                blendStart = -1f;
                lastModel = model;
                modelChangedAt = Time.unscaledTime;
                itemCalibrated = false;
                loggedGiveUp = false;
                itemIsGun = false; // the pose of the previous item must not apply while this one is not calibrated
                itemIsOptic = false;
                itemSlides = false;
                hasSupport = false;
                provisionalReady = false;
                provisionalDriven = false;
                provisionalPending = false;
                if (model != null)
                {
                    itemBaseScale = model.localScale;
                    modelLocalPosition = model.localPosition;
                    modelLocalRotation = model.localRotation;
                    provisionalPending = true;
                }
            }
            if (itemCalibrated || model == null)
            {
                return;
            }

            float waited = Time.unscaledTime - modelChangedAt;
            bool inTPose = Mathf.Abs(root.InverseTransformPoint(left.Hook.position).x) > MaxSaneHookX
                || Mathf.Abs(root.InverseTransformPoint(right.Hook.position).x) > MaxSaneHookX;

            if (provisionalPending && !inTPose)
            {
                // Pose the item on its hand from now on, not only after the wait below: until then it would hang on the
                // game's own (right) hand and jump to the other hand or to its offset when the wait ends. The hold is
                // read from the model's own untouched pose, and the animation keeps changing it, so it is sampled every
                // frame until the calibration below takes over.
                if (provisionalDriven)
                {
                    model.SetLocalPositionAndRotation(modelLocalPosition, modelLocalRotation);
                }
                itemToRoot = Quaternion.Inverse(root.rotation) * model.rotation;
                if (!provisionalReady)
                {
                    // The item's class pose is known from its asset; the second-hand point of a gun waits for calibration.
                    if (player.equipment.asset is ItemOpticAsset provisionalOptic)
                    {
                        opticPose = VRGunTuning.GetOptic(provisionalOptic, out _);
                        itemIsOptic = true;
                    }
                    else if (player.equipment.asset is ItemGunAsset provisionalGun)
                    {
                        gunPose = VRGunTuning.Get(provisionalGun, out _);
                        itemIsGun = true;
                    }
                    else
                    {
                        itemSlides = TryPrepareSlide(model, player.equipment.asset, out slideTipLocal, out slideLength, out _, out _, out _);
                    }
                    provisionalReady = true;
                }
            }

            if (waited < MinCalibrateSeconds || player.equipment.isBusy)
            {
                return;
            }

            if (inTPose)
            {
                if (waited > GiveUpSeconds && !loggedGiveUp)
                {
                    loggedGiveUp = true;
                    VRLog.Warn("arms: the rig never left its T-pose, the item keeps the arm's orientation.");
                }
                return;
            }

            if (provisionalDriven)
            {
                model.SetLocalPositionAndRotation(modelLocalPosition, modelLocalRotation); // sample the untouched pose
                provisionalDriven = false;
            }
            itemToRoot = Quaternion.Inverse(root.rotation) * model.rotation;
            provisionalReady = false;
            provisionalPending = false;
            itemIsGun = false;
            itemIsOptic = false;
            itemSlides = TryPrepareSlide(model, player.equipment.asset, out slideTipLocal, out slideLength, out string slideKind,
                out float towardsTip, out float towardsButt);
            VRPerf.LogModel(model);
            if (slideKind != null)
            {
                ItemAsset slideAsset = player.equipment.asset;
                VRLog.Info(slideKind + ": \"" + model.name + "\" id " + slideAsset.id + " guid " + slideAsset.GUID.ToString("N") + " \"" + slideAsset.name + "\", "
                    + (itemSlides ? (slideKind == "axe" ? "head" : "tip") + " along model " + slideTipLocal.ToString("0.00") + " (" + towardsTip.ToString("0.00")
                        + " m beyond the origin, " + towardsButt.ToString("0.00") + " m behind it), hand " + slideLength.ToString("0.00") + " m up the handle"
                        : "no mesh found, it stays as the animation holds it") + ".");
            }
            else if (player.equipment.asset is ItemOpticAsset opticAsset)
            {
                itemIsOptic = true;
                opticPose = VRGunTuning.GetOptic(opticAsset, out string opticClass);
                Vector3 euler = opticPose.Rotation.eulerAngles;
                VRLog.Info("optic: \"" + model.name + "\" id " + opticAsset.id + " guid " + opticAsset.GUID.ToString("N") + ", class " + opticClass
                    + ", offset " + opticPose.Offset + ", pitch/yaw/roll " + euler.x.ToString("0.#") + "/" + euler.y.ToString("0.#") + "/" + euler.z.ToString("0.#") + ".");
            }
            else if (player.equipment.asset is ItemGunAsset gunAsset)
            {
                itemIsGun = true;
                gunPose = VRGunTuning.Get(gunAsset, out string gunClass);
                VRLog.Info("gun: \"" + model.name + "\" id " + gunAsset.id + " guid " + gunAsset.GUID.ToString("N") + ", class " + gunClass + ", offset " + gunPose.Offset
                    + ", pitch " + gunPose.Rotation.eulerAngles.x.ToString("0.#") + ".");

                // Second-hand point: the Grip hook (where a foregrip attaches), else part of the way to the muzzle.
                hasSupport = false;
                Attachments attachments = model.GetComponent<Attachments>();
                if (attachments != null && attachments.gripHook != null)
                {
                    supportLocal = attachments.gripHook.localPosition;
                    hasSupport = true;
                }
                else if (attachments != null && attachments.barrelHook != null)
                {
                    supportLocal = attachments.barrelHook.localPosition * gunPose.SupportFraction;
                    hasSupport = true;
                }
                VRLog.Info("gun: second-hand point " + (hasSupport ? (attachments.gripHook != null ? "Grip hook " : "barrel x " + gunPose.SupportFraction.ToString("0.00") + " ")
                    + supportLocal + ", moved by " + gunPose.Support + ", hand " + gunPose.SupportInward.ToString("0.00") + " m in towards the gun" :"none (no Grip or Barrel hook), the hand stays on the controller") + ".");
            }
            itemCalibrated = true;
            VRLog.Info("arms: calibrated item orientation for \"" + model.name + "\".");
        }

        // side: -1 = the viewer's left, +1 = right (only used to pick a bend direction in degenerate poses).
        private void DriveArm(Arm arm, int hand, Quaternion yaw, float side)
        {
            Transform anchor = VRHands.Anchor(hand);
            if (anchor == null || !VRHands.IsTracked(hand))
            {
                return; // untracked: keep the vanilla pose
            }
            DriveArmTo(arm, anchor.position, anchor.forward, yaw, side);
        }

        // The hand (hook) goes to target; forward is the way the forearm should roughly point (elbow side). See VRArmIK.DriveTo.
        private void DriveArmTo(Arm arm, Vector3 target, Vector3 forward, Quaternion yaw, float side, float roll = 0f, float levelWeight = 0f)
        {
            VRArmIK.DriveTo(arm, target, forward, yaw, side, ArmScale, ElbowStretch, roll, levelWeight);
        }

        // The equipped item sits on the controller, turned like it, at its normal size (the rig is scaled down).
        private void DriveItem(Quaternion yaw)
        {
            ItemHand = -1;
            SupportValid = false;
            if ((!itemCalibrated && !provisionalReady) || lastModel == null || VRDebugFlags.PerfNoItemPose)
            {
                return;
            }

            int hand;
            if (lastModel.parent == right.Hook)
            {
                hand = swapped ? VRHands.Left : VRHands.Right;
            }
            else if (lastModel.parent == left.Hook)
            {
                hand = swapped ? VRHands.Right : VRHands.Left;
            }
            else if (itemIsGun)
            {
                // A gun whose asset hangs it on the spine instead of a hand (EquipableModelParent Spine / SpineHook, e.g. the
                // Palmov id 7098 and the Javelin id 6908): it went to the head with the vanilla view. It goes in the trigger
                // hand, like a gun on the right hook.
                hand = swapped ? VRHands.Left : VRHands.Right;
            }
            else
            {
                return;
            }
            if (VRHold.HandOverride >= 0)
            {
                hand = VRHold.HandOverride; // passed to the other hand (VRHold)
            }

            Transform anchor = VRHands.Anchor(hand);
            if (anchor == null || !VRHands.IsTracked(hand))
            {
                return;
            }
            ItemHand = hand;

            // Both hands: between them. Otherwise on the holding hand.
            int offHand = -1;
            float twoHandWeight = 0f;
            Vector3 position = anchor.position;
            Quaternion rotation = anchor.rotation;
            if (!(VRHold.Both && itemIsGun))
            {
                aimBiasValid = false;
            }
            if (VRHold.Both)
            {
                int otherHand = hand == VRHands.Left ? VRHands.Right : VRHands.Left;
                Transform other = VRHands.Anchor(otherHand);
                if (other != null && VRHands.IsTracked(otherHand))
                {
                    if (itemIsGun)
                    {
                        // Two-handed gun: stays on the trigger hand, the barrel points at the other hand (the roll
                        // stays the trigger controller's). Hands close together fade back to the one-hand pose,
                        // because a short line between them is too noisy to aim with.
                        Vector3 toOther = other.position - position;
                        float span = toOther.magnitude;
                        float weight = Mathf.Clamp01((span - TwoHandMinSpan) / (TwoHandFullSpan - TwoHandMinSpan));
                        offHand = otherHand;
                        twoHandWeight = weight;
                        if (weight > 0f)
                        {
                            // The gun is not on the line between the two controllers (it sits above the trigger hand
                            // and the second hand holds it below the barrel), so the line is read relative to how
                            // the barrel stood when the second hand took hold: no jump on grab or release, only
                            // changes of the line aim the gun.
                            Vector3 line = toOther / span;
                            Quaternion lineFrame = Mathf.Abs(line.y) > 0.98f ? Quaternion.LookRotation(line, yaw * Vector3.forward)
                                : Quaternion.LookRotation(line, Vector3.up);
                            Vector3 barrelNow = (rotation * gunPose.Rotation) * Vector3.forward; // barrel runs along its forward axis
                            if (!aimBiasValid)
                            {
                                aimBias = Quaternion.Inverse(lineFrame) * barrelNow;
                                aimBiasValid = true;
                            }
                            Quaternion swing = Quaternion.FromToRotation(barrelNow, lineFrame * aimBias);
                            rotation = Quaternion.Slerp(rotation, swing * rotation, weight);
                        }
                    }
                    else
                    {
                        position = Vector3.Lerp(position, other.position, 0.5f);
                        rotation = Quaternion.Slerp(rotation, other.rotation, 0.5f);
                    }
                }
            }
            Quaternion aimFrame = rotation;
            if (itemIsGun)
            {
                position += rotation * gunPose.Offset;
                rotation *= gunPose.Rotation;
                aimFrame = rotation; // barrel along its forward axis
                AimFrame = aimFrame;
            }
            else if (itemIsOptic)
            {
                position += rotation * opticPose.Offset; // off the hand, so the hand holds them from below
                rotation *= opticPose.Rotation;
            }
            // The animation's hold, except for a bow: the vanilla bow is slanted in the view, so it is stood up from its own axes (VRGun.TryGetBowHold).
            Quaternion hold = itemToRoot;
            if (itemIsGun && VRGun.TryGetBowHold(lastModel, out Quaternion bowHold))
            {
                hold = bowHold;
            }
            else if (itemIsGun && VRGun.TryStraightenHold(lastModel, itemToRoot, itemCalibrated, out Quaternion straightHold))
            {
                hold = straightHold; // barrel pointing well off the controller forward in the animation (shotguns): swung straight
            }
            activeHold = hold;
            rotation *= hold;
            if (itemSlides)
            {
                position -= rotation * slideTipLocal * slideLength; // slid through the hand: the hand ends up slideLength up the handle (negative = lower)
            }

            if (itemIsGun)
            {
                LogGunPose(anchor, position, rotation, aimFrame, twoHandWeight > 0f);
            }

            // Someone grabbed or let go: glide from where the item was to its new place instead of jumping.
            if (VRHold.Version != seenHoldVersion)
            {
                seenHoldVersion = VRHold.Version;
                blendStart = Time.unscaledTime;
                blendFromPosition = lastItemPosition;
                blendFromRotation = lastItemRotation;
            }
            if (blendStart >= 0f)
            {
                float t = (Time.unscaledTime - blendStart) / ItemBlendSeconds;
                if (t >= 1f)
                {
                    blendStart = -1f;
                }
                else
                {
                    t = t * t * (3f - 2f * t);
                    position = Vector3.Lerp(blendFromPosition, position, t);
                    rotation = Quaternion.Slerp(blendFromRotation, rotation, t);
                }
            }
            lastItemPosition = position;
            lastItemRotation = rotation;
            lastModel.SetPositionAndRotation(position, rotation);
            if (!itemCalibrated)
            {
                provisionalDriven = true; // TryCalibrate must put the local pose back before it samples the hold
            }
            Vector3 scale = itemBaseScale / ArmScale;
            if (lastModel.localScale != scale)
            {
                lastModel.localScale = scale;
            }

            // Two-handed gun: the other hand holds the gun at its grip point (not wherever the controller is). The
            // controller still decides where the barrel points. Moving that arm can move the model (it hangs on a hook
            // of one of the arms), so the model is placed again afterwards.
            if (itemIsGun && hasSupport)
            {
                SupportPoint = lastModel.TransformPoint(supportLocal) + aimFrame * gunPose.Support;
                SupportValid = true;
            }
            if (offHand >= 0 && SupportValid && twoHandWeight > 0f)
            {
                Transform offAnchor = VRHands.Anchor(offHand);
                if (offAnchor != null)
                {
                    // The hand wraps the gun from its own side: the point moves away from the barrel towards that
                    // arm's side, the elbow hangs down and out (not across the view), the forearm is turned a quarter
                    // so the inside of the arm faces the body.
                    bool onRigLeft = offHand == (swapped ? VRHands.Right : VRHands.Left);
                    float side = offHand == VRHands.Left ? -1f : 1f; // viewer's left / right
                    Vector3 outward = side * (yaw * Vector3.right);
                    Vector3 grip = SupportPoint + outward * (SupportHandSideShift - gunPose.SupportInward);
                    Vector3 target = Vector3.Lerp(offAnchor.position, grip, twoHandWeight);
                    Vector3 elbowDirection = (-(aimFrame * Vector3.forward) * 0.4f + outward * 0.9f + Vector3.down * 0.5f).normalized;
                    DriveArmTo(onRigLeft ? left : right, target, -elbowDirection, yaw, side, -side * SupportHandRoll, twoHandWeight);
                    lastModel.SetPositionAndRotation(position, rotation);
                }
            }

            if (itemIsGun)
            {
                // The gun's ammo / fire mode panel hangs on the model's origin in the barrel frame (blend included).
                VRWristStatus.GunPosed(position, rotation * Quaternion.Inverse(hold));
            }
        }

        // Diagnostic for "the gun points more to the right in the left hand": logs, once per change of hand / grip / model,
        // where the gun sits relative to the controller anchor it is posed on (the same numbers for both hands mean the
        // mod adds nothing hand specific). Euler angles are in the anchor's frame: x = pitch (positive = down),
        // y = yaw (positive = right), z = roll.
        private int loggedPoseKey = -1;
        private Transform loggedPoseModel;

        private void LogGunPose(Transform anchor, Vector3 position, Quaternion rotation, Quaternion aimFrame, bool twoHanded)
        {
            int key = ItemHand * 2 + (twoHanded ? 1 : 0);
            if (key == loggedPoseKey && lastModel == loggedPoseModel)
            {
                return;
            }
            loggedPoseKey = key;
            loggedPoseModel = lastModel;

            Quaternion inverse = Quaternion.Inverse(anchor.rotation);
            Vector3 modelEuler = Wrap((inverse * rotation).eulerAngles);
            Vector3 barrelEuler = Wrap((inverse * aimFrame).eulerAngles);
            Vector3 offset = inverse * (position - anchor.position);
            VRLog.Info("gun pose: " + lastModel.name + " in the " + (ItemHand == VRHands.Left ? "LEFT" : "RIGHT") + " hand, "
                + (twoHanded ? "two-handed (barrel aimed at the other hand)" : "one hand")
                + ". vs controller (pitch/yaw/roll deg): model " + modelEuler.ToString("0.0")
                + ", barrel axis " + barrelEuler.ToString("0.0")
                + "; class offset " + gunPose.Offset.ToString("0.000") + " rot " + Wrap(gunPose.Rotation.eulerAngles).ToString("0.0")
                + "; hold rotation " + Wrap(itemToRoot.eulerAngles).ToString("0.0")
                + "; model position vs controller (right/up/fwd m) " + offset.ToString("0.000"));
        }

        private static Vector3 Wrap(Vector3 euler)
        {
            return new Vector3(Mathf.DeltaAngle(0f, euler.x), Mathf.DeltaAngle(0f, euler.y), Mathf.DeltaAngle(0f, euler.z));
        }

        // A hand-held tool whose hold is slid along its handle: a fishing rod ("rod") or an axe ("axe"; the game has no axe flag, so the
        // asset's file name or item name contains "axe", pickaxes included). Finds the handle's long axis in the model's local space and
        // which way the tip / head is: the model's longest mesh extent; the tip of a rod is the end further from the origin (GUESS: the origin
        // is at the handle), the head of an axe the end with the thicker cross-section (else also the end further from the origin).
        // length = the "rodgrip" / "axegrip" value of gun-offsets.txt. Distances in metres. Returns false (kind set) when the model has no mesh.
        private static bool TryPrepareSlide(Transform model, ItemAsset asset, out Vector3 tip, out float length, out string kind, out float towardsTip, out float towardsButt)
        {
            tip = Vector3.forward;
            length = 0f;
            towardsTip = 0f;
            towardsButt = 0f;
            kind = null;
            if (asset is ItemFisherAsset)
            {
                kind = "rod";
            }
            else if (asset is ItemMeleeAsset && (IsAxeName(asset.name) || IsAxeName(asset.itemName)))
            {
                kind = "axe";
            }
            if (kind == null)
            {
                return false;
            }
            length = kind == "rod" ? VRGunTuning.GetRodGrip() : VRGunTuning.GetAxeGrip();

            List<Vector3> points = new List<Vector3>();
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(false))
            {
                Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                    : renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }
                if (kind == "axe" && mesh.isReadable)
                {
                    foreach (Vector3 vertex in mesh.vertices)
                    {
                        points.Add(model.InverseTransformPoint(renderer.transform.TransformPoint(vertex)));
                    }
                    continue;
                }
                Bounds bounds = mesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                    points.Add(model.InverseTransformPoint(renderer.transform.TransformPoint(local)));
                }
            }
            if (points.Count == 0)
            {
                return false;
            }

            Vector3 min = points[0];
            Vector3 max = points[0];
            foreach (Vector3 point in points)
            {
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            Vector3 size = max - min;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            bool positive = Mathf.Abs(max[axis]) >= Mathf.Abs(min[axis]);

            if (kind == "axe")
            {
                // Cross-section (widest range over the two other axes) in the outer quarter at each end of the handle.
                float quarter = size[axis] * 0.25f;
                int a = (axis + 1) % 3;
                int b = (axis + 2) % 3;
                Vector2 lowMin = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 lowMax = new Vector2(float.MinValue, float.MinValue);
                Vector2 highMin = lowMin;
                Vector2 highMax = lowMax;
                foreach (Vector3 point in points)
                {
                    Vector2 across = new Vector2(point[a], point[b]);
                    if (point[axis] <= min[axis] + quarter)
                    {
                        lowMin = Vector2.Min(lowMin, across);
                        lowMax = Vector2.Max(lowMax, across);
                    }
                    if (point[axis] >= max[axis] - quarter)
                    {
                        highMin = Vector2.Min(highMin, across);
                        highMax = Vector2.Max(highMax, across);
                    }
                }
                float lowWidth = Mathf.Max(lowMax.x - lowMin.x, lowMax.y - lowMin.y);
                float highWidth = Mathf.Max(highMax.x - highMin.x, highMax.y - highMin.y);
                if (highWidth > lowWidth * 1.2f)
                {
                    positive = true;
                }
                else if (lowWidth > highWidth * 1.2f)
                {
                    positive = false;
                }
            }

            float scale = model.lossyScale.x;
            tip = Vector3.zero;
            tip[axis] = positive ? 1f : -1f;
            towardsTip = (positive ? max[axis] : -min[axis]) * scale;
            towardsButt = (positive ? -min[axis] : max[axis]) * scale;
            return true;
        }

        private static bool IsAxeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return name.ToLowerInvariant().Contains("axe"); // also catches pickaxes
        }

        private void RestoreItemScale()
        {
            if (lastModel != null)
            {
                lastModel.localScale = itemBaseScale;
            }
        }
    }
}
