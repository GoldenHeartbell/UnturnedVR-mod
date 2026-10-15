using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Aiming down the sights with your hands: the game's aim (UseableGun.isAiming, which tightens the spread, slows
    /// walking, hides the crosshair and tells other players) turns on when the gun's sight is at your eye, and off when
    /// you lower it. VRKeyMap feeds <see cref="Active"/> in as the "secondary" key for guns that have a sight reference,
    /// so the game runs its normal aim path; the other hand's trigger no longer aims.
    /// The test is geometric: the eye is behind the sight (along the barrel line), close to that line, and the head
    /// looks roughly along it. Enter and exit differ (hysteresis), entering needs a short dwell and leaving needs the
    /// sight to stay out of the zone for a moment, so a glance sideways or the edge of the zone does not flicker the
    /// aim animation and sound. All numbers come from a real sight picture; tune by feel.
    /// Evaluated from VRArms.Apply right after the item is placed (the one point in the frame where the gun and the
    /// eye are both in their final pose), not from the input code at the start of the frame.
    /// A scoped gun also gets its scope camera moved onto the sight line (<see cref="PlaceScopeCamera"/>) so the lens
    /// shows what the barrel points at instead of what the head looks at.
    /// Cost: a few vector operations per frame.
    /// </summary>
    internal static class VRAds
    {
        // "Behind" is how far the eye is behind the sight point along the barrel. Negative = the sight point is already
        // past the eye plane, which happens when a two-handed gun is pulled right into the face. Each eye is measured
        // (the head pose is the middle of the head, eyes sit HalfEyeSeparation to either side of it).
        // The head-direction limit is wide on purpose: the eyes can look sideways inside the head and a one-handed gun
        // held across the body is not parallel to where the head faces.
        private const float HalfEyeSeparation = 0.032f; // metres
        private const float EnterAlongMin = -0.15f; // metres
        private const float EnterAlongMax = 0.50f;
        private const float EnterLateral = 0.09f;   // metres off the sight line (per eye)
        private const float EnterAngle = 50f;       // degrees between head direction and barrel
        private const float ExitAlongMin = -0.22f;
        private const float ExitAlongMax = 0.60f;
        private const float ExitLateral = 0.14f;
        private const float ExitAngle = 65f;
        private const float EnterDwell = 0.10f;     // seconds the entry zone must hold
        private const float ExitDwell = 0.25f;      // seconds the sight must stay out of the zone before aim ends
        private const float MinOnSeconds = 0.20f;   // does not drop out right after entering
        private const float NearLogInterval = 0.5f; // diagnostics: how often the "why not" line may appear

        private static bool applies;
        private static bool active;
        private static bool changed;

        /// <summary>The sight is at the eye (the game should aim).</summary>
        public static bool Active => active && applies && VRArms.Active;

        /// <summary>The equipped item is a gun with a sight reference: eye aiming decides, not the trigger.</summary>
        public static bool Applies => applies && VRArms.Active;

        /// <summary>Active changed since the last <see cref="AcknowledgeChange"/> (for the toggle-aim setting).</summary>
        public static bool Changed => changed;

        public static void AcknowledgeChange()
        {
            changed = false;
        }

        private static float enterSince = -1f;
        private static float outSince = -1f;
        private static float activeSince;
        private static float lastNearLog;
        private static bool loggedScopeQuality;
        private static Transform scopeWarnedModel;

        /// <summary>Once per frame, from VRArms.Apply (LateUpdate) after the item has been placed.</summary>
        public static void Update()
        {
            bool wasActive = active;
            bool isActive = wasActive;
            applies = false;

            Camera eye = VRRig.ActiveCamera;
            Player player = Player.LocalPlayer;
            bool uiOpen = VRPointer.Active || (PlayerUI.window != null && PlayerUI.window.showCursor);
            if (!uiOpen && eye != null && player != null
                && player.equipment != null && player.equipment.useable is UseableGun
                && VRGun.TryGetSightLine(player, out Vector3 point, out Vector3 direction, out _))
            {
                applies = true;
                // Either eye may be the aiming eye (a pistol across the body is aimed with the near eye): measure
                // both and use the one that is closer to the sight line.
                Transform head = eye.transform;
                Vector3 halfEyes = head.right * HalfEyeSeparation;
                Vector3 toLeft = head.position - halfEyes - point;
                Vector3 toRight = head.position + halfEyes - point;
                float behindLeft = -Vector3.Dot(toLeft, direction);
                float behindRight = -Vector3.Dot(toRight, direction);
                float lateralLeft = (toLeft + direction * behindLeft).magnitude; // distance from the sight line
                float lateralRight = (toRight + direction * behindRight).magnitude;
                bool leftEye = lateralLeft <= lateralRight;
                float behind = leftEye ? behindLeft : behindRight;
                float lateral = leftEye ? lateralLeft : lateralRight;
                float angle = Vector3.Angle(head.forward, direction);
                float now = Time.unscaledTime;

                if (!wasActive)
                {
                    bool inside = behind >= EnterAlongMin && behind <= EnterAlongMax && lateral <= EnterLateral && angle <= EnterAngle;
                    if (!inside)
                    {
                        enterSince = -1f;
                        if (VRDebugFlags.LogAds && behind > -0.30f && behind < 0.70f && now - lastNearLog >= NearLogInterval)
                        {
                            // The gun is near your face but aim is not on: say which of the entry conditions fails.
                            lastNearLog = now;
                            VRLog.Info("ads: waiting (behind " + behind.ToString("0.00") + " m" + (behind < EnterAlongMin ? " TOO CLOSE/BEHIND" : behind > EnterAlongMax ? " TOO FAR" : "")
                                + ", off-line " + lateral.ToString("0.00") + " m" + (lateral > EnterLateral ? " TOO FAR OFF" : "")
                                + ", angle " + angle.ToString("0") + (angle > EnterAngle ? " TOO MUCH" : "") + ").");
                        }
                    }
                    else if (enterSince < 0f)
                    {
                        enterSince = now;
                    }
                    else if (now - enterSince >= EnterDwell)
                    {
                        isActive = true;
                        activeSince = now;
                        outSince = -1f;
                        if (VRDebugFlags.LogAds)
                        {
                            VRLog.Info("ads: on (behind " + behind.ToString("0.00") + " m, off-line " + lateral.ToString("0.00")
                                + " m, angle " + angle.ToString("0") + ").");
                        }
                    }
                }
                else
                {
                    bool outside = behind < ExitAlongMin || behind > ExitAlongMax || lateral > ExitLateral || angle > ExitAngle;
                    if (!outside)
                    {
                        outSince = -1f;
                    }
                    else if (outSince < 0f)
                    {
                        outSince = now;
                    }
                    else if (now - outSince >= ExitDwell && now - activeSince >= MinOnSeconds)
                    {
                        isActive = false;
                        enterSince = -1f;
                        if (VRDebugFlags.LogAds)
                        {
                            VRLog.Info("ads: off (behind " + behind.ToString("0.00") + " m" + (behind < ExitAlongMin || behind > ExitAlongMax ? " OUT" : "")
                                + ", off-line " + lateral.ToString("0.00") + " m" + (lateral > ExitLateral ? " OUT" : "")
                                + ", angle " + angle.ToString("0") + (angle > ExitAngle ? " OUT" : "") + ").");
                        }
                    }
                }
            }
            else
            {
                isActive = false;
                enterSince = -1f;
                outSince = -1f;
            }

            active = isActive;
            VRGun.UpdateAimFilter(isActive && applies); // calms the barrel direction used by the scope picture and the shots
            if (isActive != wasActive)
            {
                changed = true;
                if (isActive)
                {
                    VRHaptics.Pulse(VRArms.ItemHand, 0.25f, 0.03f, 200f);
                }
            }
        }

        /// <summary>
        /// Puts the game's scope camera (the render source of the scope lens on the gun) on the sight line, looking along
        /// the barrel. Without this it sits at the character's head and the lens shows the head view. Called after the
        /// item is placed, in LateUpdate and again before render.
        /// </summary>
        public static void PlaceScopeCamera()
        {
            Player player = Player.LocalPlayer;
            PlayerLook look = player != null ? player.look : null;
            if (look == null || !look.isScopeActive || look.scopeCamera == null
                || !VRGun.TryGetSightLine(player, out Vector3 point, out Vector3 direction, out Vector3 up))
            {
                return;
            }

            look.scopeCamera.transform.SetPositionAndRotation(point, Quaternion.LookRotation(VRGun.SmoothDirection(direction), up));

            Transform model = player.equipment.firstModel;
            if (model != scopeWarnedModel)
            {
                scopeWarnedModel = model;
                VRLog.Info("ads: scope camera follows the sight line of \"" + (model != null ? model.name : "?") + "\".");
                if (!loggedScopeQuality && SDG.Unturned.GraphicsSettings.scopeQuality == EGraphicQuality.OFF)
                {
                    loggedScopeQuality = true;
                    VRLog.Warn("ads: Scope quality is Off in the graphics settings, so scopes zoom the flat screen instead of drawing the lens. Set it above Off for scopes to work in VR.");
                }
            }
        }
    }
}
