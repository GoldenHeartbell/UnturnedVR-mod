using System.Collections.Generic;
using System.Reflection;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The player's status widgets (PlayerLifeUI: the vitals box with health / food / water / virus / stamina / oxygen, and
    /// the row of status icons: bleeding, broken leg, hunger, full moon, radiation, safezone...) taken off the flat HUD
    /// panel and shown in two places, like VRWorldLabels / the hint panel do with their widgets (the game keeps drawing
    /// them; this class crops their rectangle out of the UI texture and VRGui cuts the same rectangle out of the HUD panel):
    ///   1. WRIST: a panel at the left wrist, only while that hand is free (not holding or supporting an item), raised,
    ///      and the head looks at it. Billboarded upright towards the eye, so it does not depend on how the controller
    ///      is rotated. Parented to the hand anchor, so it follows the hand without a frame of lag.
    ///   2. GLANCE REVEAL: the game's own status icon boxes (the ones it shows above the vitals box), cropped and hung as
    ///      small head-locked pieces at the lower left of the view, for as long as the game shows them (bleeding, broken,
    ///      food / water / virus / oxygen at 0, radiation, full moon, safezone...). Nothing of its own is invented: a vital
    ///      that is merely low has no game icon and shows nothing. Hidden while the wrist panel is up (the wrist shows
    ///      everything).
    /// The wrist panel also carries the compass tape (PlayerLifeUI.compassBox) at its top, with its own setting
    /// (VRSettings.WristCompass), independent of the status setting.
    /// The game decides what is shown (icon visibility, vitals visible by level config, compass availability); this class only reads it.
    /// </summary>
    internal sealed class VRWristStatus
    {
        // Wrist panel
        private const float WristWidth = 0.20f;            // width of the vitals box in metres (icon strip uses the same scale)
        // The visible arm is scaled (VRArms.ArmScale 0.65) and its hook, the controller position, sits about 0.33 m beyond
        // the wrist bone, so the lower forearm is about 0.3 m behind the grip point.
        private const float WristBack = 0.28f;             // from the controller's grip point towards the elbow
        private const float WristLift = 0.09f;             // towards the eye, so it floats above the (thick) arm
        private const float WristGap = 0.006f;             // between the icon strip and the vitals box
        private const float CompassWidth = 0.22f;          // the compass tape's width in metres, above the icon strip
        // On the arm (VRArms.TryGetForearm): flat on the inner face of the forearm block (the side that looks at the body),
        // text running along the arm, metres in world size.
        private const float ForearmHalfThickness = 0.1875f * 0.65f; // the arm mesh is 0.375 thick unscaled x ArmScale
        private const float ForearmAlong = 0.17f;          // from the elbow bone towards the hand (the block is about 0.33 m)
        private const float ForearmLift = 0.003f;          // off the surface
        private const float PanelShiftBack = 0.03f;       // panel moved this far to its left (towards the elbow on the left arm)...
        private const float PanelShiftUp = 0.06f;         // ...and this far up (towards the top edge of the arm), metres
        private const float FacingOn = 0.25f;              // the panel's face must point at the eye: dot with the eye direction...
        private const float FacingOff = 0.05f;             // ...and may fall to this before it hides again
        // The wrist is a "look down at the arm held low in front of you" gesture. Raised, reaching or pointing arms (picking
        // something up, aiming at things) must not show it, so the arm has to be low and close, the head has to look down at it,
        // the hand has to be still and nothing may be targeted for pickup.
        private const float GazeOnDegrees = 34f;           // head must look this close to the wrist to show it...
        private const float GazeOffDegrees = 46f;          // ...and may drift this far before it hides again
        private const float MinPitchDownOn = 20f;          // head pitched down at least this far to show it...
        private const float MinPitchDownOff = 10f;         // ...and may come up this far before it hides again
        private const float MinBelowEye = 0.22f;           // the wrist is at least this far below the eyes...
        private const float MaxBelowEye = 0.80f;           // ...and not lower than this (hanging at the hip)
        private const float MinAhead = -0.15f;             // roughly in front of the head (a hand pulled to the belly is under it)
        private const float MaxAhead = 0.60f;
        private const float MaxSide = 0.50f;
        private const float MaxWristDistance = 0.65f;      // a reaching arm is further than this
        private const float MaxHandSpeed = 0.8f;           // m/s; pointing and grabbing move faster than checking a watch
        private const float Hysteresis = 0.08f;            // metres of extra room for the position limits once it is shown
        private const float DwellSeconds = 0.25f;
        private const float WristFadeIn = 0.12f;
        private const float WristFadeOut = 0.15f;

        // Vehicle dashboard (the game's vehicleBox: fuel, speed, health, battery, gear / RPM, lock state).
        // Placed once per drive relative to the driver's eyes, in the vehicle's frame: right of the steering wheel, a little
        // below the eyes. Offsets are in metres, in the vehicle's axes (x right, y up, z forward). Tuned by feel.
        private const float DashRight = 0.30f;
        private const float DashDown = 0.32f;
        private const float DashAhead = 0.65f;
        private const float DashScale = 1.4f;             // apparent size relative to the same widget on the HUD panel
        private const float DashSettleSeconds = 0.6f;     // after sitting down, before the eye point is taken
        private const float DashFadeIn = 0.25f;
        private const float DashLabelAbove = 25f;         // the game's lock label hangs this far above the box (canvas units)

        // Gun display (the game's UseableGun infoBox: ammo in the magazine and fire mode, as one box with the game's own look):
        // the whole game box (ammo, fire mode, magazine) hangs over the gun's inner side (the left flank for the right hand,
        // the right flank for the left hand) and is only there while the gun is twisted so that this side faces up, or while
        // that side is turned straight at the eyes. It reads from both sides of its plane (turned half a turn about its own
        // upright axis when the eye is behind it), so raising the gun above the eyes does not make it vanish. Tuned by feel.
        private const float GunSideBoxUnits = 420f;       // the box is this wide in canvas units
        private const float GunSideMetersPerUnit = 0.0005f; // 420 units = 21 cm
        private const float GunSideOut = 0.01f;           // off the gun's measured outer side (it floats a little)
        private const float GunSideWidthFallback = 0.03f; // half width used until the model has been measured
        private const float GunMeasureSeconds = 1f;       // the model is measured again this often (attachments come and go)
        private const float GunSideUp = 0.07f;            // above the model origin
        private const float GunSideAlong = 0.02f;         // along the barrel from the model origin, only used when the hand cannot be read
        private const float GunSideHandGap = -0.09f;      // the panel's rear edge this far in front of the hand (metres; negative = back over the grip)

        private const float GunSideTwistOn = 0.4f;        // sine of the twist (roll about the barrel from level): about 24 degrees shows it...
        private const float GunSideTwistOff = 0.22f;      // ...and it may fall back to about 13 degrees before it hides
        private const float GunLookOn = 0.6f;             // the flank must face the eye: dot with the eye direction, shown from here...
        private const float GunLookOff = 0.45f;           // ...and hidden again below this (about 53 degrees off the eye)
        private const float GunFlipDot = 0.05f;           // turned to the other face once the eye is this far behind its plane (hysteresis)
        private const float GunSideFadeIn = 0.12f;
        private const float GunSideFadeOut = 0.1f;
        private const float GunMinEyeDistance = 0.22f;    // closer to the eye than this (sights up to the eye) it is hidden

        // Glance reveal
        private const float RevealDistance = 0.9f;
        private const float RevealYaw = -20f;              // degrees, negative = left of the view centre
        private const float RevealPitch = 16f;             // degrees below the view centre
        private const float RevealScale = 1.3f;            // apparent size relative to the same widget on the HUD panel
        private const float RevealGap = 0.008f;
        private const float GasmaskScale = 0.6f;           // the gas mask box is 400 units wide: shown smaller than the icons
        private const float RevealFadeIn = 0.2f;
        private const float RevealFadeOut = 0.5f;

        private const float RevealTopRightPad = 4f;       // canvas units added above and right of an icon in the glance reveal
        private const float RevealBottomPad = 0f;          // canvas units kept below an icon in the glance reveal
        private const float Pad = 4f;                    // canvas units around a widget (border / shadow)
        private const float HoleExtra = 8f;                // more around the HUD panel's hole than the crop (12 in total)

        private const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class Piece
        {
            public GameObject Object;
            public Transform Transform;
            public Mesh Mesh;
            public readonly Vector2[] Uv = new Vector2[4];
            public float Alpha = 1f;
        }

        private sealed class Alert
        {
            public string Name;
            public ISleekElement Element;      // the game's icon box in the status icon strip, or a widget of its own (Own)
            public bool Own;                   // not in the icon strip: the rect is the element's own, shown even with the wrist panel up
            public bool SecondRow;             // wide boxes (gas mask) go on a row of their own below the icons
            public float Scale = 1f;           // size relative to the other reveal pieces
            public bool Active;
            public Rect Rect;                  // canvas rect, valid while shown
        }

        // PlayerLifeUI's private statics (resolved once).
        private static readonly FieldInfo lifeBoxField = typeof(PlayerLifeUI).GetField("lifeBox", AnyMember);
        private static readonly FieldInfo iconsField = typeof(PlayerLifeUI).GetField("statusIconsContainer", AnyMember);
        private static readonly FieldInfo vehicleBoxField = typeof(PlayerLifeUI).GetField("vehicleBox", AnyMember);
        private static readonly FieldInfo vehicleLockedField = typeof(PlayerLifeUI).GetField("vehicleLockedLabel", AnyMember);
        private static bool loggedMissing;

        private readonly Transform parent;
        private readonly Material material;
        private readonly int layer;

        // Wrist (the root hangs on the hand anchor, which is destroyed with the rig: rebuilt when that happens)
        private Transform wristRoot;
        private Piece vitalsPiece;
        private Piece iconsPiece;
        private Piece compassPiece;
        private bool wristWanted;
        private float wristSince;
        private float wristAlpha;
        private bool loggedWrist;
        private float lastGateLog;

        // The gate must read the forearm after VRArms posed it (its LateUpdate, order 500): the HUD update runs earlier in the
        // frame and would see the game's own animation pose. VRArms calls ArmsPosed(); the HUD update uses that result.
        private static VRWristStatus instance;
        private Transform lastEye;
        private int lastUpdateFrame = -100;
        private int gateFrame = -100;
        private bool gateResult;

        // Vehicle dashboard: a child of the vehicle (rigid with it, no lag), fixed in the vehicle's frame
        private Transform dashRoot;
        private Piece dashPiece;
        private InteractableVehicle dashVehicle;
        private float dashSince;
        private bool dashCaptured;
        private Vector3 dashLocalPosition;
        private Quaternion dashLocalRotation;
        private float dashAlpha;
        private bool recaptureDash;

        // Gun display: hangs on the gun's pose, which VRArms hands over every frame (GunPosed) right after it placed the model
        private Transform gunRoot;
        private Piece gunPiece;
        private int gunPosedFrame = -100;
        private float gunAlpha;
        private float gunTwist;          // side mode: how far the inner side faces up (1 = straight up)
        private float gunHandZ;          // the holding hand along the barrel from the model origin, metres (forward positive)
        private bool gunSideShown;       // the twist gate (with hysteresis)
        private bool gunLookShown;       // the "flank faces the eye" gate (with hysteresis)
        private bool gunFlipped;         // the eye is behind the flank's plane: the panel is turned to its other face
        private float gunEyeDot;         // the flank's outward normal against the direction to the eye (1 = looking straight at it)
        private Transform measuredModel;                // the gun model the extents below belong to
        private float measuredAt = -100f;
        private bool measured;
        private float gunMinX, gunMaxX, gunMinZ, gunMaxZ; // extents of the model's meshes in the gun's frame (x right, z forward), metres
        private readonly List<Renderer> measureRenderers = new List<Renderer>();
        private bool loggedGun;
        private static readonly FieldInfo gunInfoBoxField = typeof(UseableGun).GetField("infoBox", AnyMember);

        // The game's gun box as it was before it was made fixed width, so it can be put back (setting turned off).
        private sealed class GunBoxLayout
        {
            public ISleekElement Box;
            public float BoxScaleX;
            public float BoxOffsetX;
            public float PosScaleX;
            public float PosOffsetX;
        }

        private GunBoxLayout gunLayout;

        // Reveal
        private Transform revealRoot; // hangs on the eye (rigid: no lag), rebuilt when the rig that holds it is destroyed
        private readonly List<Piece> revealPieces = new List<Piece>();
        private readonly List<Alert> alerts = new List<Alert>();
        private readonly List<Alert> shown = new List<Alert>();
        private ISleekElement alertsFor;
        private float revealAlpha;

        // Per update
        private Vector2 canvasSize;
        private List<Rect> holes;

        public VRWristStatus(Transform parent, Material material, int layer)
        {
            this.parent = parent;
            this.material = material;
            this.layer = layer;
            instance = this;

            EnsureWristObjects();

            EnsureRevealRoot();
        }

        private void EnsureRevealRoot()
        {
            if (revealRoot != null)
            {
                return;
            }

            revealPieces.Clear(); // destroyed with the old root
            revealRoot = new GameObject("UnturnedVR Glance Reveal").transform;
            revealRoot.gameObject.layer = layer;
            revealRoot.SetParent(parent, worldPositionStays: false);
            revealRoot.gameObject.SetActive(false);
        }

        private void EnsureWristObjects()
        {
            if (wristRoot != null)
            {
                return;
            }

            wristRoot = new GameObject("UnturnedVR Wrist Status").transform;
            wristRoot.gameObject.layer = layer;
            wristRoot.SetParent(parent, worldPositionStays: false);
            vitalsPiece = CreatePiece(wristRoot, "UnturnedVR Wrist Vitals");
            iconsPiece = CreatePiece(wristRoot, "UnturnedVR Wrist Icons");
            compassPiece = CreatePiece(wristRoot, "UnturnedVR Wrist Compass");
            wristRoot.gameObject.SetActive(false);
        }

        /// <summary>Hides everything (menu, binoculars lens, HUD closed, setting off).</summary>
        public void Hide()
        {
            HideDash();
            HideGun();
            HideWristAndReveal();
        }

        private void HideWristAndReveal()
        {
            EnsureWristObjects();
            if (wristRoot.gameObject.activeSelf)
            {
                wristRoot.gameObject.SetActive(false);
            }
            HideReveal();
            wristWanted = false;
            wristAlpha = 0f;
        }

        private void HideReveal()
        {
            EnsureRevealRoot();
            if (revealRoot.gameObject.activeSelf)
            {
                revealRoot.gameObject.SetActive(false);
            }
            revealAlpha = 0f;
        }

        /// <summary>
        /// Once per frame, after the game's HUD update. Adds the canvas rectangles (origin top left) it took over to
        /// <paramref name="holeRects"/>, so the HUD panel does not show them a second time.
        /// </summary>
        /// <param name="widthPerMeter">HUD panel width per metre of distance (the panel's width / its distance).</param>
        public void Update(Transform eye, Vector2 canvas, float widthPerMeter, List<Rect> holeRects)
        {
            canvasSize = canvas;
            holes = holeRects;
            EnsureWristObjects();

            UpdateDash(eye, widthPerMeter);
            UpdateGun(eye);
            // The wrist panel carries two independent things: the status (vitals box + icon strip, with the glance reveal) and
            // the compass tape. Each has its own setting; the panel and its look-down gesture are shared.
            bool statusOn = VRSettings.WristStatus;
            bool compassOn = VRSettings.WristCompass;
            if (!statusOn && !compassOn)
            {
                HideWristAndReveal();
                return;
            }

            ISleekElement lifeBox = lifeBoxField != null ? lifeBoxField.GetValue(null) as ISleekElement : null;
            ISleekElement icons = iconsField != null ? iconsField.GetValue(null) as ISleekElement : null;
            Player player = Player.LocalPlayer;
            if (eye == null || player == null || player.life == null || (statusOn && (lifeBox == null || icons == null)))
            {
                if (statusOn && !loggedMissing && (lifeBoxField == null || iconsField == null))
                {
                    loggedMissing = true;
                    VRLog.Warn("wrist status: PlayerLifeUI.lifeBox / statusIconsContainer not found, the status stays on the HUD panel.");
                }
                HideWristAndReveal();
                return;
            }

            Rect? vitalsShown = null;
            Rect? iconsShown = null;
            if (statusOn)
            {
                // PlayerLifeUI builds new widgets for every spawn: the cached references are only good for this lifeBox.
                if (!ReferenceEquals(lifeBox, alertsFor))
                {
                    alertsFor = lifeBox;
                    alerts.Clear();
                    BuildAlerts();
                }

                // The game's own boxes stay on the HUD panel only if they are not visible (nothing to cut then).
                // The hole is larger than the crop: the boxes' border and drop shadow reach further than the crop's margin
                // (the top and right edges showed on the HUD panel with the same margin as the crop).
                if (lifeBox.IsVisible)
                {
                    vitalsShown = RectOf(lifeBox);
                    holes.Add(Inflate(vitalsShown.Value, HoleExtra));
                }
                if (icons.IsVisible)
                {
                    iconsShown = RectOf(icons);
                    holes.Add(Inflate(iconsShown.Value, HoleExtra));
                }

                // Voice chat indicators and the gas mask box, shown in the glance reveal: cut out of the HUD panel the same way.
                for (int i = 0; i < alerts.Count; i++)
                {
                    Alert alert = alerts[i];
                    if (alert.Own && alert.Element.IsVisible)
                    {
                        holes.Add(Inflate(RectOf(alert.Element), HoleExtra));
                    }
                }
            }

            // The compass tape: shown by the game only with the server's Compass option, in arena, or with a compass item.
            // The game keeps updating the widget on the canvas; this crops it. Not visible = nothing to cut.
            Rect? compassShown = null;
            if (compassOn)
            {
                ISleekElement compass = PlayerLifeUI.compassBox;
                if (compass != null && compass.IsVisible)
                {
                    compassShown = RectOf(compass);
                    holes.Add(Inflate(compassShown.Value, HoleExtra));
                }
            }

            lastEye = eye;
            lastUpdateFrame = Time.frameCount;
            // The result from VRArms' pass of this frame's LateUpdate (one frame old); evaluated here only without arms.
            bool wrist = Time.frameCount - gateFrame <= 2 ? gateResult : UpdateWristGate(eye);
            UpdateWrist(eye, wrist, vitalsShown, iconsShown, compassShown);
            if (statusOn)
            {
                UpdateReveal(eye, wrist, iconsShown, widthPerMeter);
            }
            else
            {
                HideReveal();
            }
        }

        /// <summary>Called by VRArms once per frame, right after it posed the arms: evaluates the wrist gate on the final arm pose.</summary>
        internal static void ArmsPosed()
        {
            VRWristStatus self = instance;
            if (self == null || self.lastEye == null || Time.frameCount - self.lastUpdateFrame > 2)
            {
                return;
            }

            self.gateResult = self.UpdateWristGate(self.lastEye);
            self.gateFrame = Time.frameCount;
        }

        // ----------------------------------------------------------------------------------------------- wrist

        // The left hand is free, held low and close in front of the body, still, and the head is pitched down looking at
        // it, with nothing targeted for pickup (with hysteresis and a short dwell).
        private bool UpdateWristGate(Transform eye)
        {
            Transform anchor = VRHands.Anchor(VRHands.Left);
            bool free = anchor != null && VRHands.IsTracked(VRHands.Left)
                && VRArms.ItemHand != VRHands.Left && !VRHold.Both
                && !VRAttachMenu.Open && !VRBinoculars.LensVisible
                && UnturnedVR.Patches.PlayerUIHintPatch.CurrentTarget == null;
            bool want = false;
            if (free)
            {
                float m = wristWanted ? Hysteresis : 0f;
                // Measured at the spot you look at (where the panel hangs), not at the controller: with the hand pulled to
                // the body or across it the two are far apart. On the arm that is the middle of the forearm block's top
                // face, and the face must point at the eyes (it is not seen from underneath or from the side).
                Vector3 panelPoint;
                bool facing = true;
                bool forearmFound = VRArms.TryGetForearm(VRHands.Left, out Transform forearm, out Vector3 axisLocal, out Vector3 topLocal, out Vector3 innerLocal);
                if (forearmFound)
                {
                    // World metres, independent of the bone's scale (it is scaled by the arm scale and may be mirrored).
                    Vector3 top = forearm.TransformDirection(innerLocal).normalized;
                    Vector3 along = forearm.TransformDirection(axisLocal).normalized;
                    panelPoint = forearm.position + along * ForearmAlong + top * ForearmHalfThickness;
                    Vector3 toEyeDir = (eye.position - panelPoint).normalized;
                    facing = Vector3.Dot(top, toEyeDir) > (wristWanted ? FacingOff : FacingOn);
                }
                else
                {
                    panelPoint = anchor.TransformPoint(new Vector3(0f, 0f, -WristBack));
                }
                Vector3 toWrist = panelPoint - eye.position;
                Vector3 toHand = anchor.position - eye.position;

                // Head heading on the ground plane; looking steeply down the top of the head points the way you face.
                Vector3 forward = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
                if (forward.sqrMagnitude < 0.04f)
                {
                    forward = Vector3.ProjectOnPlane(eye.up, Vector3.up);
                }
                forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
                Vector3 side = Vector3.Cross(Vector3.up, forward);

                float below = -toWrist.y;
                float ahead = Vector3.Dot(toWrist, forward);
                float lateral = Mathf.Abs(Vector3.Dot(toWrist, side));
                float pitchDown = Mathf.Asin(Mathf.Clamp(-eye.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
                // Looking at either the forearm spot or the hand counts.
                float angle = Mathf.Min(Vector3.Angle(eye.forward, toWrist), Vector3.Angle(eye.forward, toHand));
                bool still = VRHands.WorldVelocity(VRHands.Left).sqrMagnitude < MaxHandSpeed * MaxHandSpeed;

                if (pitchDown >= MinPitchDownOff && Time.unscaledTime - lastGateLog > 1.5f)
                {
                    // Diagnostic (temporary): why the panel does or does not show while looking down at the arm.
                    lastGateLog = Time.unscaledTime;
                    VRLog.Info("wrist gate: still " + still + ", facing " + facing + ", pitchDown " + pitchDown.ToString("0")
                        + ", below " + below.ToString("0.00") + ", ahead " + ahead.ToString("0.00") + ", lateral " + lateral.ToString("0.00")
                        + ", dist " + toWrist.magnitude.ToString("0.00") + ", gaze " + angle.ToString("0")
                        + ", hand speed " + VRHands.WorldVelocity(VRHands.Left).magnitude.ToString("0.00")
                        + ", onArm " + (forearmFound ? "yes" : "no (hand anchor)") + ".");
                }

                want = still && facing
                    && pitchDown >= (wristWanted ? MinPitchDownOff : MinPitchDownOn)
                    && below >= MinBelowEye - m && below <= MaxBelowEye + m
                    && ahead >= MinAhead - m && ahead <= MaxAhead + m
                    && lateral <= MaxSide + m
                    && toWrist.magnitude <= MaxWristDistance + m
                    && angle < (wristWanted ? GazeOffDegrees : GazeOnDegrees);
            }

            if (want && !wristWanted)
            {
                wristSince = Time.unscaledTime;
            }
            wristWanted = want;
            return want && Time.unscaledTime - wristSince >= DwellSeconds;
        }

        private void UpdateWrist(Transform eye, bool show, Rect? vitals, Rect? icons, Rect? compass)
        {
            float dt = Time.unscaledDeltaTime;
            float target = show ? 1f : 0f;
            wristAlpha = Mathf.MoveTowards(wristAlpha, target, dt / (show ? WristFadeIn : WristFadeOut));

            bool visible = wristAlpha > 0.001f && (vitals.HasValue || icons.HasValue || compass.HasValue);
            if (wristRoot.gameObject.activeSelf != visible)
            {
                wristRoot.gameObject.SetActive(visible);
            }
            if (!visible)
            {
                return;
            }

            if (VRArms.TryGetForearm(VRHands.Left, out Transform forearm, out Vector3 axisLocal, out Vector3 topLocal, out Vector3 innerLocal))
            {
                // Stuck on the inner face of the forearm block: a fixed pose in the elbow bone's space, so it rides the arm
                // whenever the arm is posed (both of VRArms' passes) without a frame of lag. Text up = towards the hand;
                // the quad's visible side (-Z) looks out along the top face's normal.
                if (wristRoot.parent != forearm)
                {
                    wristRoot.SetParent(forearm, worldPositionStays: false);
                }
                // Inner face, text upright along the arm (up = the forearm's top face, so it reads left to right along the arm).
                Vector3 topWorld = forearm.TransformDirection(topLocal).normalized;
                Vector3 innerWorld = forearm.TransformDirection(innerLocal).normalized;
                Vector3 alongWorld = forearm.TransformDirection(axisLocal).normalized;
                wristRoot.position = forearm.position + alongWorld * ForearmAlong + innerWorld * (ForearmHalfThickness + ForearmLift);
                wristRoot.rotation = Quaternion.LookRotation(-innerWorld, topWorld);
                wristRoot.position += wristRoot.right * -PanelShiftBack + wristRoot.up * PanelShiftUp;
                wristRoot.localScale = InverseScale(forearm.lossyScale);
            }
            else
            {
                Transform anchor = VRHands.Anchor(VRHands.Left);
                if (anchor == null)
                {
                    return;
                }

                // No arm to stick it on (arms not driven): behind the grip point on the arm's line, lifted towards the
                // eye. Parenting to the anchor keeps it on the hand when the pose is re-applied just before rendering;
                // the rotation is re-set every frame.
                if (wristRoot.parent != anchor)
                {
                    wristRoot.SetParent(anchor, worldPositionStays: true);
                }
                Vector3 position = anchor.TransformPoint(new Vector3(0f, 0f, -WristBack));
                Vector3 toEye = eye.position - position;
                if (toEye.sqrMagnitude < 0.0001f)
                {
                    return;
                }
                Vector3 lift = toEye.normalized * WristLift;
                wristRoot.position = position + lift;
                wristRoot.rotation = Quaternion.LookRotation(-toEye.normalized, Vector3.up);
                wristRoot.localScale = InverseScale(anchor.lossyScale);
            }

            // Content, top to bottom: the compass tape (centred, its own scale: it is much wider than the vitals box), the
            // icon strip (left aligned) and the vitals box.
            float mpu = WristWidth / Mathf.Max(vitals.HasValue ? vitals.Value.width : canvasSize.x * 0.2f, 1f);
            float vitalsHeight = 0f;
            float iconsHeight = 0f;
            float vitalsWidth = 0f;
            float compassHeight = 0f;
            float compassMpu = 0f;
            if (compass.HasValue)
            {
                compassMpu = CompassWidth / Mathf.Max(compass.Value.width, 1f);
                compassHeight = compass.Value.height * compassMpu;
            }
            if (vitals.HasValue)
            {
                vitalsWidth = vitals.Value.width * mpu;
                vitalsHeight = vitals.Value.height * mpu;
            }
            if (icons.HasValue)
            {
                iconsHeight = icons.Value.height * mpu;
            }

            // The status block is centred on the panel origin on its own, exactly as it was before the compass existed; the
            // compass hangs above it, so adding it never moves the status. (Compass only: it is centred.)
            float statusTotal = vitalsHeight + iconsHeight + (vitals.HasValue && icons.HasValue ? WristGap : 0f);
            float top = statusTotal * 0.5f;
            float left = -vitalsWidth * 0.5f;
            if (compass.HasValue)
            {
                float compassCentre = statusTotal > 0f ? top + WristGap + compassHeight * 0.5f : 0f;
                ShowPiece(compassPiece, compass.Value, compassMpu, new Vector3(0f, compassCentre, 0f), wristAlpha);
            }
            else
            {
                HidePiece(compassPiece);
            }

            if (icons.HasValue)
            {
                float width = icons.Value.width * mpu;
                ShowPiece(iconsPiece, icons.Value, mpu, new Vector3(left + width * 0.5f, top - iconsHeight * 0.5f, 0f), wristAlpha);
                top -= iconsHeight + WristGap;
            }
            else
            {
                HidePiece(iconsPiece);
            }

            if (vitals.HasValue)
            {
                ShowPiece(vitalsPiece, vitals.Value, mpu, new Vector3(0f, top - vitalsHeight * 0.5f, 0f), wristAlpha);
            }
            else
            {
                HidePiece(vitalsPiece);
            }

            if (!loggedWrist)
            {
                loggedWrist = true;
                VRLog.Info("wrist status: panel shown at the left wrist (vitals " + (vitals.HasValue ? vitals.Value.size.ToString() : "hidden")
                    + ", icons " + (icons.HasValue ? icons.Value.size.ToString() : "hidden") + ", " + WristWidth.ToString("0.00") + " m wide).");
            }
        }

        // ----------------------------------------------------------------------------------------------- reveal

        private void BuildAlerts()
        {
            AddIcon("bleeding", "bleedingBox");
            AddIcon("broken", "brokenBox");
            AddIcon("starved", "starvedBox");
            AddIcon("dehydrated", "dehydratedBox");
            AddIcon("infected", "infectedBox");
            AddIcon("drowning", "drownedBox");
            AddIcon("asphyxiating", "asphyxiatingBox");
            AddIcon("radiation", "radiationBox");
            AddIcon("temperature", "temperatureBox");
            AddIcon("full moon", "moonBox");
            AddIcon("safezone", "safeBox");
            AddIcon("arrest", "arrestBox");

            // Widgets that are not in the icon strip, with a place of their own on the HUD: the voice chat indicator (you are
            // talking), the "outbound voice chat is off" hint, and the gas mask's durability box (radiated with a mask on).
            AddOwn("voice", "voiceBox", 1f, false);
            AddOwn("voice off", "voiceOutboundOffIcon", 1f, false);
            AddOwn("gas mask", "gasmaskBox", GasmaskScale, true);
        }

        private void AddIcon(string name, string field)
        {
            ISleekElement element = Field(field);
            if (element != null)
            {
                alerts.Add(new Alert { Name = name, Element = element });
            }
        }

        private void AddOwn(string name, string field, float scale, bool secondRow)
        {
            ISleekElement element = Field(field);
            if (element != null)
            {
                alerts.Add(new Alert { Name = name, Element = element, Own = true, Scale = scale, SecondRow = secondRow });
            }
        }

        private static ISleekElement Field(string name)
        {
            FieldInfo field = typeof(PlayerLifeUI).GetField(name, AnyMember);
            if (field == null)
            {
                VRLog.Warn("wrist status: PlayerLifeUI." + name + " not found, it is left out of the glance reveal.");
                return null;
            }
            return field.GetValue(null) as ISleekElement;
        }

        private void UpdateReveal(Transform eye, bool wristShown, Rect? icons, float widthPerMeter)
        {
            // Every status icon the game has turned on (PlayerLifeUI.updateIcons: bleeding, broken, temperature, food /
            // water / virus / oxygen at 0, asphyxiating, full moon, radiation, safezone, arrest) is shown for as long as
            // the game shows it. No timers: the game decides when an icon comes and goes.
            EnsureRevealRoot();
            shown.Clear();
            for (int i = 0; i < alerts.Count; i++)
            {
                Alert alert = alerts[i];
                bool active = alert.Element.IsVisible;

                if (active && !alert.Active && VRDebugFlags.LogGui)
                {
                    VRLog.Info("wrist status: " + alert.Name + " icon on.");
                }
                alert.Active = active;

                // The wrist panel carries the status icons, so they are left out of the reveal while it is up; the voice and
                // gas mask widgets are not on it and stay in the reveal.
                if (active && (!wristShown || alert.Own) && ResolveRect(alert, icons))
                {
                    shown.Add(alert);
                }
            }

            float target = shown.Count > 0 ? 1f : 0f;
            revealAlpha = Mathf.MoveTowards(revealAlpha, target, Time.unscaledDeltaTime / (target > revealAlpha ? RevealFadeIn : RevealFadeOut));
            bool visible = revealAlpha > 0.001f && shown.Count > 0;
            if (revealRoot.gameObject.activeSelf != visible)
            {
                revealRoot.gameObject.SetActive(visible);
            }
            if (!visible)
            {
                return;
            }

            // Head-locked block at the lower left: a child of the eye, so it is part of the view itself (no smoothing, no
            // lag, no trail while the head moves; the camera's pose at render time carries it).
            if (revealRoot.parent != eye)
            {
                revealRoot.SetParent(eye, worldPositionStays: false);
            }
            Quaternion offset = Quaternion.Euler(RevealPitch, RevealYaw, 0f);
            Vector3 eyeScale = eye.lossyScale;
            revealRoot.localScale = InverseScale(eyeScale);
            revealRoot.localPosition = offset * Vector3.forward * (RevealDistance / Mathf.Max(Mathf.Abs(eyeScale.z), 0.0001f));
            revealRoot.localRotation = Quaternion.identity;

            // Size: the same apparent size as on the HUD panel, a bit larger.
            float mpu = RevealDistance * widthPerMeter * RevealScale / Mathf.Max(canvasSize.x, 1f);

            // One row of the game's own icon boxes (and the voice indicators), left aligned (the block's top left is the root),
            // in the game's order; the wide gas mask box gets a second row below it.
            int piece = 0;
            float rowTop = 0f;
            for (int row = 0; row < 2; row++)
            {
                float x = 0f;
                float rowHeight = 0f;
                for (int i = 0; i < shown.Count; i++)
                {
                    Alert alert = shown[i];
                    if (alert.SecondRow != (row == 1))
                    {
                        continue;
                    }

                    float scaled = mpu * alert.Scale;
                    float w = alert.Rect.width * scaled;
                    float h = alert.Rect.height * scaled;
                    ShowPiece(RevealPiece(piece++), alert.Rect, scaled, new Vector3(x + w * 0.5f, rowTop - h * 0.5f, 0f), revealAlpha);
                    x += w + RevealGap;
                    rowHeight = Mathf.Max(rowHeight, h);
                }
                if (rowHeight > 0f)
                {
                    rowTop -= rowHeight + RevealGap;
                }
            }

            for (int i = piece; i < revealPieces.Count; i++)
            {
                HidePiece(revealPieces[i]);
            }
        }

        // Canvas rectangle of the alert's icon box inside the game's icon strip (50 x 50 at PositionOffset_X).
        private bool ResolveRect(Alert alert, Rect? icons)
        {
            if (alert.Own)
            {
                alert.Rect = RectOf(alert.Element); // the widget's own box, with the usual margin
                return true;
            }
            if (!icons.HasValue)
            {
                return false;
            }
            Rect strip = icons.Value;
            float left = strip.xMin + Pad + alert.Element.PositionOffset_X;
            float height = alert.Element.SizeOffset_Y;
            // Less room below than above: the vitals box sits right under the strip and its dark border / shadow would show.
            // Extra room at the top and right: the icon's own edge was cut there with only Pad.
            alert.Rect = Rect.MinMaxRect(left - Pad, strip.yMin - RevealTopRightPad,
                left + alert.Element.SizeOffset_X + Pad + RevealTopRightPad, strip.yMin + Pad + height + RevealBottomPad);
            return true;
        }

        private Piece RevealPiece(int index)
        {
            while (revealPieces.Count <= index)
            {
                revealPieces.Add(CreatePiece(revealRoot, "UnturnedVR Glance Piece"));
            }
            return revealPieces[index];
        }

        // ----------------------------------------------------------------------------------------------- dashboard

        /// <summary>Takes the dashboard's place again from where the head is now (recenter key).</summary>
        internal static void RecaptureDash()
        {
            if (instance != null)
            {
                instance.recaptureDash = true;
            }
        }

        private void HideDash()
        {
            if (dashRoot != null && dashRoot.gameObject.activeSelf)
            {
                dashRoot.gameObject.SetActive(false);
            }
            dashAlpha = 0f;
        }

        private void EnsureDashObjects()
        {
            if (dashRoot != null)
            {
                return;
            }

            dashRoot = new GameObject("UnturnedVR Vehicle Dashboard").transform;
            dashRoot.gameObject.layer = layer;
            dashRoot.SetParent(parent, worldPositionStays: false);
            dashPiece = CreatePiece(dashRoot, "UnturnedVR Dashboard");
            dashRoot.gameObject.SetActive(false);
            dashVehicle = null;
            dashCaptured = false;
        }

        // The vehicle box of the game, always there while driving: fixed in the vehicle's frame, to the right of the wheel,
        // not following the head.
        private void UpdateDash(Transform eye, float widthPerMeter)
        {
            ISleekElement box = vehicleBoxField != null ? vehicleBoxField.GetValue(null) as ISleekElement : null;
            ISleekElement locked = vehicleLockedField != null ? vehicleLockedField.GetValue(null) as ISleekElement : null;
            Player player = Player.LocalPlayer;
            InteractableVehicle vehicle = player != null && player.movement != null ? player.movement.getVehicle() : null;
            if (!VRSettings.VehicleDash || eye == null || box == null || vehicle == null || !box.IsVisible)
            {
                if (vehicle == null)
                {
                    dashVehicle = null;
                    dashCaptured = false;
                }
                HideDash();
                return;
            }

            EnsureDashObjects();
            if (vehicle != dashVehicle)
            {
                dashVehicle = vehicle;
                dashCaptured = false;
                dashSince = Time.unscaledTime;
                dashAlpha = 0f;
            }
            if (recaptureDash)
            {
                recaptureDash = false;
                dashCaptured = false;
                dashSince = Time.unscaledTime - DashSettleSeconds; // the head is where it should be already
            }

            // Crop: the vehicle box plus the lock label that hangs above it. Its hole in the HUD panel is cut from the first
            // frame, also while the dashboard is still settling, so the box is never seen out on the far HUD panel.
            Rect crop = RectOf(box);
            if (locked != null && locked.IsVisible)
            {
                crop = Rect.MinMaxRect(crop.xMin, crop.yMin - DashLabelAbove, crop.xMax, crop.yMax);
            }
            holes.Add(Inflate(crop, HoleExtra));

            Transform frame = vehicle.transform;
            if (!dashCaptured)
            {
                if (Time.unscaledTime - dashSince < DashSettleSeconds)
                {
                    HideDash();
                    return; // nothing is shown until the dashboard has its place
                }

                // The driver's eyes in the vehicle's frame; the dashboard hangs ahead / right / below them, tilted square to
                // the line of sight so it reads straight on.
                Vector3 eyeLocal = frame.InverseTransformPoint(eye.position);
                Vector3 offset = new Vector3(DashRight, -DashDown, DashAhead);
                dashLocalPosition = eyeLocal + Vector3.Scale(offset, InverseScale(frame.lossyScale));
                dashLocalRotation = Quaternion.LookRotation(offset.normalized, Vector3.up);
                dashCaptured = true;
                VRLog.Info("vehicle dashboard: placed " + offset.ToString("0.00") + " m from the driver's eyes (vehicle frame).");
            }

            if (dashRoot.parent != frame)
            {
                dashRoot.SetParent(frame, worldPositionStays: false);
            }
            dashRoot.localPosition = dashLocalPosition;
            dashRoot.localRotation = dashLocalRotation;
            dashRoot.localScale = InverseScale(frame.lossyScale);

            dashAlpha = Mathf.MoveTowards(dashAlpha, 1f, Time.unscaledDeltaTime / DashFadeIn);
            if (!dashRoot.gameObject.activeSelf)
            {
                dashRoot.gameObject.SetActive(true);
            }

            float distance = new Vector3(DashRight, DashDown, DashAhead).magnitude;
            float mpu = distance * widthPerMeter * DashScale / Mathf.Max(canvasSize.x, 1f);
            ShowPiece(dashPiece, crop, mpu, Vector3.zero, dashAlpha);
        }

        // ----------------------------------------------------------------------------------------------- gun display

        /// <summary>
        /// Called by VRArms every frame right after it placed the equipped gun: its model origin and barrel frame (barrel along
        /// forward, up is up) in world space. The panel is put on it here, in the same frame, so it does not lag the gun.
        /// The frame is taken from the model's own barrel (and sight) when they can be read, so the panel is straight with the
        /// model whatever hold rotation the animation gave the gun; the given frame is the fallback.
        /// </summary>
        internal static void GunPosed(Vector3 position, Quaternion frame)
        {
            VRWristStatus self = instance;
            if (self == null)
            {
                return;
            }

            self.gunPosedFrame = Time.frameCount;
            if (self.gunRoot == null)
            {
                return;
            }

            VRGun.TryGetGunFrame(Player.LocalPlayer, frame, out frame);

            // Over the inner side of the gun: the left flank in the right hand, the right flank in the left hand. The
            // panel is parallel to the flank, upright along the gun, text running towards the muzzle for a viewer there.
            float side = VRArms.ItemHand == VRHands.Left ? 1f : -1f;
            Vector3 outward = frame * new Vector3(side, 0f, 0f);
            // The twist is the roll about the barrel measured from "level" (world up made square to the barrel), so a gun
            // pitched up or down at the sky / ground reads the same as one pointed along the horizon. Barrel straight up
            // or down has no level: the last value is kept.
            Vector3 level = Vector3.ProjectOnPlane(Vector3.up, frame * Vector3.forward);
            if (level.sqrMagnitude > 0.01f)
            {
                self.gunTwist = Vector3.Dot(outward, level.normalized);
            }
            Transform handAnchor = VRArms.ItemHand >= 0 ? VRHands.Anchor(VRArms.ItemHand) : null;
            if (handAnchor != null)
            {
                self.gunHandZ = Vector3.Dot(handAnchor.position - position, frame * Vector3.forward);
            }
            self.MeasureGun(Player.LocalPlayer, position, frame);
            // Hangs on the gun's measured outer side, so a slim pistol and a wide rifle both get the panel next to them, and
            // inside the length of the gun, so a short gun gets it over its middle.
            float outer = self.measured ? (side > 0f ? self.gunMaxX : -self.gunMinX) : GunSideWidthFallback;
            float along = GunSideAlong;
            float half = GunSideBoxUnits * GunSideMetersPerUnit * 0.5f;
            if (handAnchor != null)
            {
                // Measured from the hand, not from the model origin or the gun's ends: the panel's rear edge sits at the
                // hand whatever the model's origin and length are (they differ per gun).
                along = self.gunHandZ + half + GunSideHandGap;
            }
            else if (self.measured)
            {
                along = self.gunMaxZ - self.gunMinZ > half * 2f
                    ? Mathf.Clamp(GunSideAlong, self.gunMinZ + half, self.gunMaxZ - half)
                    : (self.gunMinZ + self.gunMaxZ) * 0.5f;
            }
            self.gunRoot.position = position + frame * new Vector3(side * (Mathf.Max(outer, 0.01f) + GunSideOut), GunSideUp, along);
            Quaternion flat = Quaternion.LookRotation(-outward, frame * Vector3.up); // flat on the flank, seen from outside (-Z)

            // The quad is drawn from one side only. With the eye behind the flank's plane (gun raised above the eyes, or the gun
            // held across the body) it is turned half a turn about its own upright axis, so its front faces the eye and the text
            // reads the right way round from either side. Same place, still flat on the gun; hysteresis around edge-on.
            Transform eye = self.lastEye;
            if (eye != null)
            {
                Vector3 toEye = eye.position - self.gunRoot.position;
                float dot = toEye.sqrMagnitude > 0.0001f ? Vector3.Dot(outward, toEye.normalized) : 1f;
                self.gunEyeDot = dot;
                self.gunFlipped = self.gunFlipped ? dot < GunFlipDot : dot < -GunFlipDot;
            }
            else
            {
                self.gunEyeDot = 0f;
                self.gunFlipped = false;
            }
            self.gunRoot.rotation = self.gunFlipped ? flat * Quaternion.Euler(0f, 180f, 0f) : flat;
            self.gunRoot.localScale = InverseScale(self.parent.lossyScale);
        }

        // The extents of the held model's meshes along the gun's right and forward axes (origin = the model origin, frame = the
        // barrel frame), so the panel can sit on the outside of any gun. Redone when the model changes and every second
        // (a sight, magazine or laser changes the width). Only mesh renderers: no particles, trails or lines.
        private void MeasureGun(Player player, Vector3 origin, Quaternion frame)
        {
            Transform model = player != null && player.equipment != null ? player.equipment.firstModel : null;
            if (model == null)
            {
                measured = false;
                return;
            }
            if (model == measuredModel && measured && Time.unscaledTime - measuredAt < GunMeasureSeconds)
            {
                return;
            }

            measuredModel = model;
            measuredAt = Time.unscaledTime;
            Quaternion inverse = Quaternion.Inverse(frame);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            bool any = false;
            model.GetComponentsInChildren(false, measureRenderers);
            for (int i = 0; i < measureRenderers.Count; i++)
            {
                Renderer renderer = measureRenderers[i];
                Bounds local;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    local = skinned.localBounds;
                }
                else if (renderer is MeshRenderer)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null)
                    {
                        continue;
                    }
                    local = filter.sharedMesh.bounds;
                }
                else
                {
                    continue;
                }
                if (!renderer.enabled)
                {
                    continue;
                }

                Transform t = renderer.transform;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents,
                        new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                    Vector3 inFrame = inverse * (t.TransformPoint(corner) - origin);
                    minX = Mathf.Min(minX, inFrame.x);
                    maxX = Mathf.Max(maxX, inFrame.x);
                    minZ = Mathf.Min(minZ, inFrame.z);
                    maxZ = Mathf.Max(maxZ, inFrame.z);
                    any = true;
                }
            }
            measureRenderers.Clear();

            measured = any;
            if (!any)
            {
                return;
            }

            gunMinX = minX;
            gunMaxX = maxX;
            gunMinZ = minZ;
            gunMaxZ = maxZ;
            if (loggedGunModel != model)
            {
                loggedGunModel = model;
                VRLog.Info("gun display: \"" + model.name + "\" measured from the model origin: left " + (-minX).ToString("0.000")
                    + " m, right " + maxX.ToString("0.000") + " m, back " + (-minZ).ToString("0.00") + " m, front " + maxZ.ToString("0.00") + " m, hand at " + gunHandZ.ToString("0.00") + " m.");
            }
        }

        private Transform loggedGunModel;

        private void HideGun()
        {
            if (gunRoot != null && gunRoot.gameObject.activeSelf)
            {
                gunRoot.gameObject.SetActive(false);
            }
            gunAlpha = 0f;
            gunSideShown = false;
            gunLookShown = false;
            gunFlipped = false;
        }

        private void EnsureGunObjects()
        {
            if (gunRoot != null)
            {
                return;
            }

            gunRoot = new GameObject("UnturnedVR Gun Display").transform;
            gunRoot.gameObject.layer = layer;
            gunRoot.SetParent(parent, worldPositionStays: false);
            gunPiece = CreatePiece(gunRoot, "UnturnedVR Gun Box");
            gunRoot.gameObject.SetActive(false);
        }

        // Puts the game's gun box back the way it laid it out (the setting was changed while the gun is held).
        private void RestoreGunBox()
        {
            GunBoxLayout l = gunLayout;
            gunLayout = null;
            if (l == null)
            {
                return;
            }

            try
            {
                l.Box.SizeScale_X = l.BoxScaleX;
                l.Box.SizeOffset_X = l.BoxOffsetX;
                l.Box.PositionScale_X = l.PosScaleX;
                l.Box.PositionOffset_X = l.PosOffsetX;
            }
            catch (System.Exception)
            {
                // the widgets are gone already (the gun was unequipped)
            }
        }

        // The game's gun box on the gun: made a fixed width and moved to the right screen edge (so a narrow window cannot cut
        // it). The game keeps its whole layout (ammo, fire mode, magazine) and keeps writing the texts; only the box is ours.
        private void TuneGunBox(ISleekElement box)
        {
            if (gunLayout != null && gunLayout.Box == box)
            {
                return;
            }
            if (gunLayout != null)
            {
                RestoreGunBox(); // another box: back to the game's layout first
            }

            gunLayout = new GunBoxLayout
            {
                Box = box,
                BoxScaleX = box.SizeScale_X,
                BoxOffsetX = box.SizeOffset_X,
                PosScaleX = box.PositionScale_X,
                PosOffsetX = box.PositionOffset_X,
            };
            box.SizeScale_X = 0f;
            box.SizeOffset_X = GunSideBoxUnits;
            box.PositionScale_X = 1f;
            box.PositionOffset_X = -GunSideBoxUnits - 10f;
        }

        // The equipped gun's info box (ammo in the magazine / fire mode / magazine) off the flat HUD, on the gun, as the game's
        // own box, whole (so it keeps its look and corners). Over the gun's inner side, only visible while the gun is twisted
        // to show that side to the eyes. Never turned towards the head.
        private void UpdateGun(Transform eye)
        {
            bool enabled = VRSettings.GunDisplay;
            Player player = Player.LocalPlayer;
            UseableGun gun = player != null && player.equipment != null ? player.equipment.useable as UseableGun : null;
            ISleekElement infoBox = gun != null && gunInfoBoxField != null ? gunInfoBoxField.GetValue(gun) as ISleekElement : null;
            if (gunLayout != null && (!enabled || infoBox != gunLayout.Box))
            {
                RestoreGunBox();
            }
            if (!enabled || eye == null || infoBox == null || !infoBox.IsVisible || Time.frameCount - gunPosedFrame > 2)
            {
                if (gun != null && infoBox == null && !loggedGun && gunInfoBoxField == null)
                {
                    loggedGun = true;
                    VRLog.Warn("gun display: UseableGun.infoBox not found, the gun status stays on the HUD panel.");
                }
                HideGun();
                return;
            }

            EnsureGunObjects();
            TuneGunBox(infoBox);

            // The box (with its border and shadow) is cut out of the HUD panel, whether or not the panel is faded in right now.
            float left = infoBox.PositionScale_X * canvasSize.x + infoBox.PositionOffset_X;
            float top = infoBox.PositionScale_Y * canvasSize.y + infoBox.PositionOffset_Y;
            float width = infoBox.SizeScale_X * canvasSize.x + infoBox.SizeOffset_X;
            float height = infoBox.SizeScale_Y * canvasSize.y + infoBox.SizeOffset_Y;
            Rect box = Rect.MinMaxRect(left - Pad, top - Pad, left + width + Pad, top + height + Pad);
            holes.Add(Inflate(box, HoleExtra));

            // Two ways to show it, both with hysteresis, and not right at the eye (sight up at the eye):
            //   twist: the inner side is rolled up. Only the roll decides (not where the gun is relative to the eyes), so it
            //          works with the gun straight out at eye height too;
            //   look:  the inner side is turned straight at the eyes (from the eye's side of its plane), for a gun held
            //          raised or low where the roll alone does not say whether you are looking at that side.
            // The panel stays flat on the flank and in place; GunPosed turns it to the face the eye can see.
            float distance = (eye.position - gunRoot.position).magnitude;
            gunSideShown = gunTwist > (gunSideShown ? GunSideTwistOff : GunSideTwistOn);
            gunLookShown = gunEyeDot > (gunLookShown ? GunLookOff : GunLookOn);
            float target = (gunSideShown || gunLookShown) && distance >= GunMinEyeDistance ? 1f : 0f;
            float fadeSeconds = target > gunAlpha ? GunSideFadeIn : GunSideFadeOut;
            gunAlpha = Mathf.MoveTowards(gunAlpha, target, Time.unscaledDeltaTime / fadeSeconds);
            if (gunAlpha <= 0.001f)
            {
                if (gunRoot.gameObject.activeSelf)
                {
                    gunRoot.gameObject.SetActive(false);
                }
                return;
            }

            if (!gunRoot.gameObject.activeSelf)
            {
                gunRoot.gameObject.SetActive(true);
            }
            ShowPiece(gunPiece, box, GunSideMetersPerUnit, Vector3.zero, gunAlpha);
        }

        // ----------------------------------------------------------------------------------------------- pieces

        private static Rect Inflate(Rect rect, float amount)
        {
            return Rect.MinMaxRect(rect.xMin - amount, rect.yMin - amount, rect.xMax + amount, rect.yMax + amount);
        }

        private Rect RectOf(ISleekElement element)
        {
            float left = element.PositionScale_X * canvasSize.x + element.PositionOffset_X;
            float top = element.PositionScale_Y * canvasSize.y + element.PositionOffset_Y;
            float width = element.SizeScale_X * canvasSize.x + element.SizeOffset_X;
            float height = element.SizeScale_Y * canvasSize.y + element.SizeOffset_Y;
            return Rect.MinMaxRect(left - Pad, top - Pad, left + width + Pad, top + height + Pad);
        }

        private Piece CreatePiece(Transform root, string name)
        {
            GameObject go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(root, worldPositionStays: false);

            // Unit quad visible from -Z, like VRGui's panels.
            Mesh mesh = new Mesh { name = name + " Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0.1f));
            // Vertex colour carries the fade (the UI shader multiplies it in).
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return new Piece { Object = go, Transform = go.transform, Mesh = mesh };
        }

        // Crops the canvas rectangle onto the piece, scales it by metres per canvas unit and places it (root space).
        private void ShowPiece(Piece piece, Rect canvasRect, float metersPerUnit, Vector3 localPosition, float alpha)
        {
            float u0 = Mathf.Clamp01(canvasRect.xMin / canvasSize.x);
            float u1 = Mathf.Clamp01(canvasRect.xMax / canvasSize.x);
            float v0 = Mathf.Clamp01(1f - canvasRect.yMax / canvasSize.y);
            float v1 = Mathf.Clamp01(1f - canvasRect.yMin / canvasSize.y);
            if (u1 <= u0 || v1 <= v0)
            {
                HidePiece(piece);
                return;
            }

            // Re-uploaded only when the cropped rectangle moved (a fresh piece holds zeros, which a rectangle never equals).
            Vector2 uvMin = new Vector2(u0, v0);
            Vector2 uvMax = new Vector2(u1, v1);
            if (piece.Uv[0] != uvMin || piece.Uv[3] != uvMax)
            {
                piece.Uv[0] = uvMin;
                piece.Uv[1] = new Vector2(u1, v0);
                piece.Uv[2] = new Vector2(u0, v1);
                piece.Uv[3] = uvMax;
                piece.Mesh.uv = piece.Uv;
            }

            if (Mathf.Abs(piece.Alpha - alpha) > 0.01f)
            {
                piece.Alpha = alpha;
                Color c = new Color(1f, 1f, 1f, alpha);
                piece.Mesh.colors = new[] { c, c, c, c };
            }

            piece.Transform.localPosition = localPosition;
            piece.Transform.localRotation = Quaternion.identity;
            piece.Transform.localScale = new Vector3((u1 - u0) * canvasSize.x * metersPerUnit, (v1 - v0) * canvasSize.y * metersPerUnit, 1f);
            if (!piece.Object.activeSelf)
            {
                piece.Object.SetActive(true);
            }
        }

        private static void HidePiece(Piece piece)
        {
            if (piece != null && piece.Object.activeSelf)
            {
                piece.Object.SetActive(false);
            }
        }

        private static Vector3 InverseScale(Vector3 scale)
        {
            return new Vector3(
                Mathf.Abs(scale.x) > 0.0001f ? 1f / scale.x : 1f,
                Mathf.Abs(scale.y) > 0.0001f ? 1f / scale.y : 1f,
                Mathf.Abs(scale.z) > 0.0001f ? 1f / scale.z : 1f);
        }
    }
}
