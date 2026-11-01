using HarmonyLib;
using SDG.Unturned;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Physical grenade throw: a throwable (grenade, flash, smoke, flare...) is armed with the trigger of the hand that holds it and
    /// thrown by letting go of that trigger during a throwing motion, along the hand's path at that moment.
    ///
    /// The game (SDK UseableThrowable): startPrimary starts the STRONG throw (far), startSecondary the WEAK one (short); 60% into
    /// the Use anim (isThrowable) tick spawns the throwable 1 m along player.look.aim with that mode's force, on the owning client
    /// and on the server alike; the server's one does the damage. One use per equip (hasUsed).
    ///
    /// The gesture, per throw:
    ///   - Armed: the item hand's trigger goes down (it must have been up since the throwable was in the hand). The throwable's
    ///     use sound is one clip: a pin pull, a pause, the throw. Its pin part plays now (stopped at the pause), with a rumble;
    ///     when the game plays the clip at the throw it starts after the pause (Patches/UseableThrowablePatch, Player.playSound),
    ///     so the throw part comes at the throw. The pause is found in the clip's samples (PinEnd). Nothing is pressed yet.
    ///   - Throw: the trigger is let go. The hand's velocity over the last ReleaseFrames frames is the throw: slower than
    ///     MinThrowSpeed = no throw (disarmed, nothing happens, arm again). The hand at or above OverhandBelowHead under the head
    ///     = overhand (primary, the strong throw), lower = underhand (secondary, the weak lob).
    ///   - The throwable leaves from the hand along that velocity, its pitch within PitchMin..PitchMax. The held model is then
    ///     hidden: the game keeps the item until its Use anim has played out (then useStepB takes the next one of the same kind).
    /// Where the server does the same (HandAimed: singleplayer, or a protocol 7 server, which gets the release as a shot pose:
    /// Net/UseableThrowableServerPatch) the throwable leaves the hand at once (Patches/UseableThrowablePatch). Elsewhere the
    /// release only presses the key, and the game throws as usual from the view.
    /// All numbers are GUESSES, to be tuned in the headset. Called from VRArms once the item is in its final pose.
    /// </summary>
    internal static class VRThrow
    {
        private const float MinThrowSpeed = 2.0f; // m/s, hand, averaged over the last frames: slower at the release = no throw
        private const int ReleaseFrames = 3; // the hand's path over this many frames before the release is the throw
        private const float OverhandBelowHead = 0.5f; // metres: the hand higher than this under the head at the release = overhand
        private const float PitchMin = -30f;
        private const float PitchMax = 70f;
        private const float PendingMaxSeconds = 0.5f;

        private enum Phase { Waiting, Ready, Armed }

        private static Phase phase = Phase.Waiting;
        private static Vector3 lastHand; // rig space
        private static bool hasLastHand;
        private static readonly Vector3[] recent = new Vector3[ReleaseFrames]; // world velocities of the last frames
        private static int recentNext;
        private static int recentCount;

        private static bool pending;
        private static float pendingSince;
        private static Vector3 pendingOrigin;
        private static Quaternion pendingRotation;
        private static int pressFrame = -1;
        private static KeyCode pressKey = KeyCode.None;
        // The use sound split at the pause after the pin pull: the pin part at the arming, the rest at the throw.
        private const float PinEndFallback = 0.4f; // seconds: GUESS, used when the clip's samples cannot be read or show no pause
        private const float PinWindow = 0.01f; // seconds per loudness window when looking for the pause
        private const float PinQuiet = 0.05f; // a window quieter than this part of the clip's loudest one is quiet
        private const float PinMinPause = 0.04f; // seconds of quiet that make the pause
        private static readonly Dictionary<AudioClip, float> pinEnds = new Dictionary<AudioClip, float>();
        private static readonly FieldInfo handleSourceField = AccessTools.Field(typeof(OneShotAudioHandle), "audioSource");
        private static FieldInfo pooledComponentField;
        private static OneShotAudioHandle pinHandle;
        private static AudioClip pinClip;
        private static float pinStopAt; // PinEnd(pinClip), kept so the per-frame check skips the dictionary
        private static AudioClip throwSound; // the game's play of it at the throw starts after the pin part
        private static float throwSoundUntil;

        /// <summary>The throw replaces the triggers this frame: VRKeyMap must not press primary or secondary from them.</summary>
        public static bool TakesTriggers { get; private set; }

        /// <summary>The throwable leaves the hand, at once, on the client and the server alike (else: the game's own timing and aim).</summary>
        public static bool HandAimed => VRState.IsActive && (Provider.isServer || Net.VRNet.ServerAcceptsPoses);

        /// <summary>The attack key pressed by a release, held for one frame.</summary>
        public static bool KeyHeld(KeyCode key)
        {
            // Called for every InputEx.GetKey query: the plain compares first, the frame count (an engine call) last.
            return key == pressKey && key != KeyCode.None && pressFrame == Time.frameCount;
        }

        /// <summary>A release waits for the game to play the throw's use sound (cheap gate for the Player.playSound patch).</summary>
        public static bool ExpectsSound => (object)throwSound != null;

        /// <summary>A released throw waits for the game to spawn the throwable (UseableThrowablePatch spawns it now, from the hand).</summary>
        public static bool HasPendingThrow(UseableThrowable throwable)
        {
            return pending && throwable != null && throwable.channel != null && throwable.channel.IsLocalPlayer;
        }

        /// <summary>Where the throwable leaves from and the way it flies (for player.look.aim during the spawn).</summary>
        public static void GetThrowRay(out Vector3 origin, out Quaternion rotation)
        {
            origin = pendingOrigin;
            rotation = pendingRotation;
        }

        /// <summary>The game spawned the throwable of the pending throw.</summary>
        public static void ThrowSpawned()
        {
            if (pending)
            {
                pending = false;
                VRLog.Info("throw: thrown " + (Time.time - pendingSince).ToString("0.00") + " s after the release.");
            }
            // The throwable is out of the hand; the game keeps the item (and its model) until the Use anim has played out.
            Player player = Player.LocalPlayer;
            Transform model = player != null && player.equipment != null ? player.equipment.firstModel : null;
            if (model != null)
            {
                foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.enabled = false;
                }
            }
        }

        /// <summary>
        /// The game just played <paramref name="clip"/> for the local player: for the throw's use sound (once per release) the
        /// play jumps past the pin part, which already played at the arming.
        /// </summary>
        public static void SoundPlayed(AudioClip clip, OneShotAudioHandle handle)
        {
            if ((object)throwSound == null)
            {
                return;
            }
            if (Time.time > throwSoundUntil)
            {
                throwSound = null; // the game never played it: stop gating every playSound
                return;
            }
            if (!ReferenceEquals(clip, throwSound))
            {
                return;
            }
            throwSound = null;
            AudioSource source = GetSource(handle);
            float pinEnd = PinEnd(clip);
            if (source != null && pinEnd < clip.length)
            {
                source.time = pinEnd;
            }
        }

        private static AudioSource GetSource(OneShotAudioHandle handle)
        {
            if (!handle.IsValid || handleSourceField == null)
            {
                return null;
            }
            object pooled = handleSourceField.GetValue(handle);
            if (pooled == null)
            {
                return null;
            }
            if (pooledComponentField == null)
            {
                pooledComponentField = AccessTools.Field(pooled.GetType(), "component");
            }
            return pooledComponentField != null ? pooledComponentField.GetValue(pooled) as AudioSource : null;
        }

        /// <summary>
        /// Seconds into the use sound where the pin part ends: the first pause (PinMinPause of windows under PinQuiet of the
        /// loudest one) after the clip got loud, cut where the sound starts again. Read once per clip; the fallback if the
        /// samples cannot be read (a compressed clip that is not decompressed on load) or there is no pause.
        /// </summary>
        private static float PinEnd(AudioClip clip)
        {
            if (pinEnds.TryGetValue(clip, out float cached))
            {
                return cached;
            }
            float result = PinEndFallback;
            string how = "fallback, samples not readable";
            try
            {
                int channels = Mathf.Max(1, clip.channels);
                float[] data = new float[clip.samples * channels];
                if (clip.samples > 0 && clip.GetData(data, 0))
                {
                    int window = Mathf.Max(1, Mathf.RoundToInt(clip.frequency * PinWindow)) * channels;
                    int count = data.Length / window;
                    float[] level = new float[count];
                    float loudest = 0f;
                    for (int w = 0; w < count; w++)
                    {
                        float sum = 0f;
                        for (int i = w * window; i < (w + 1) * window; i++)
                        {
                            sum += data[i] * data[i];
                        }
                        level[w] = Mathf.Sqrt(sum / window);
                        loudest = Mathf.Max(loudest, level[w]);
                    }
                    how = "fallback, no pause found";
                    int minQuiet = Mathf.Max(1, Mathf.RoundToInt(PinMinPause / PinWindow));
                    bool heard = false;
                    int quietRun = 0;
                    for (int w = 0; w < count && loudest > 0f; w++)
                    {
                        bool quiet = level[w] < loudest * PinQuiet;
                        if (!heard)
                        {
                            heard = !quiet;
                        }
                        else if (quiet)
                        {
                            quietRun++;
                        }
                        else if (quietRun >= minQuiet)
                        {
                            result = w * PinWindow;
                            how = "pause found";
                            break;
                        }
                        else
                        {
                            quietRun = 0;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                how = "fallback, " + e.Message;
            }
            pinEnds[clip] = result;
            VRLog.Info("throw: sound '" + clip.name + "' " + clip.length.ToString("0.00") + " s, pin part ends at "
                + result.ToString("0.00") + " s (" + how + ").");
            return result;
        }

        /// <summary>Once per frame from VRArms, after the held item has been posed.</summary>
        public static void Update()
        {
            StopPinAtPause(); // a quick throw lets the pin part finish on its own; returns at once while no pin sound plays
            DropStalePending();

            // Guards, cheapest first. Each failing one stops the gesture and ends the frame here.
            Player player = Player.LocalPlayer;
            PlayerEquipment equipment = player != null ? player.equipment : null;
            if (!VRState.IsActive || equipment == null || !(equipment.useable is UseableThrowable))
            {
                StopGesture(); // no throwable in hand: nearly every frame ends here
                return;
            }
            Transform rig = VRRig.RigTransform;
            Camera eye = VRRig.ActiveCamera;
            int hand = VRArms.ItemHand;
            if (rig == null || eye == null || hand < 0)
            {
                StopGesture();
                return;
            }
            Transform anchor = VRHands.Anchor(hand);
            if (anchor == null || !VRHands.IsTracked(hand) || player.movement.getVehicle() != null || IsUiOpen())
            {
                StopGesture();
                return;
            }

            TakesTriggers = true;
            TrackHand(rig, anchor);
            bool trigger = ((hand == VRHands.Right ? VRControllerInput.RightButtons : VRControllerInput.LeftButtons) & VRButton.Trigger) != 0;
            StepGesture(player, equipment, hand, anchor, eye, trigger);
        }

        private static bool IsUiOpen()
        {
            return VRPointer.Active || (PlayerUI.window != null && PlayerUI.window.showCursor);
        }

        /// <summary>The game never threw for the last release: give up on it.</summary>
        private static void DropStalePending()
        {
            if (pending && Time.time - pendingSince > PendingMaxSeconds)
            {
                pending = false;
                VRLog.Info("throw: the game did not throw for the release, dropped.");
            }
        }

        /// <summary>The throw cannot run this frame (no throwable, hand not tracked, menu open...): hand the triggers back, disarm.</summary>
        private static void StopGesture()
        {
            TakesTriggers = false;
            if (phase == Phase.Armed)
            {
                VRLog.Info("throw: disarmed (hand not tracked, menu open or the throwable left the hand).");
            }
            phase = Phase.Waiting;
            hasLastHand = false;
            recentCount = 0;
        }

        /// <summary>Records the hand's world velocity this frame (from its rig-space motion, so walking and vehicles do not count).</summary>
        private static void TrackHand(Transform rig, Transform anchor)
        {
            Vector3 handPosition = rig.InverseTransformPoint(anchor.position);
            if (hasLastHand && Time.deltaTime > 1e-5f)
            {
                recent[recentNext] = rig.TransformVector(handPosition - lastHand) / Time.deltaTime;
                recentNext = (recentNext + 1) % ReleaseFrames;
                recentCount = Mathf.Min(recentCount + 1, ReleaseFrames);
            }
            lastHand = handPosition;
            hasLastHand = true;
        }

        /// <summary>The gesture's state machine: Waiting (trigger up first) → Ready → Armed (trigger down) → release (trigger up).</summary>
        private static void StepGesture(Player player, PlayerEquipment equipment, int hand, Transform anchor, Camera eye, bool trigger)
        {
            switch (phase)
            {
                case Phase.Waiting:
                    // A trigger already held when the throwable came into the hand (say, from the last throw) does not arm it.
                    if (!trigger)
                    {
                        phase = Phase.Ready;
                    }
                    break;
                case Phase.Ready:
                    if (trigger && !equipment.isBusy && !pending)
                    {
                        phase = Phase.Armed;
                        AudioClip sound = equipment.asset is ItemThrowableAsset asset ? asset.use : null;
                        if (sound != null)
                        {
                            pinStopAt = PinEnd(sound); // read the clip before it plays
                            pinHandle = player.playSound(sound, 1f, 0f); // no pitch change: the pin part ends where PinEnd says
                            pinClip = sound;
                        }
                        VRHaptics.Pulse(hand, 0.4f, 0.05f, 120f);
                        VRLog.Info("throw: armed" + (sound != null ? ", sound '" + sound.name + "'" : ", the throwable has no use sound") + ".");
                    }
                    break;
                case Phase.Armed:
                    if (!trigger)
                    {
                        phase = Phase.Ready;
                        Release(player, equipment, hand, anchor.position, eye.transform);
                    }
                    break;
            }
        }

        /// <summary>Stops the arming's pin sound once it reaches the pause (the throw part comes at the throw).</summary>
        private static void StopPinAtPause()
        {
            if ((object)pinClip == null)
            {
                return;
            }
            AudioSource source = GetSource(pinHandle);
            if (source == null)
            {
                pinClip = null; // finished or recycled
                return;
            }
            if (source.time >= pinStopAt)
            {
                pinHandle.Stop();
                pinClip = null;
            }
        }

        private static void Release(Player player, PlayerEquipment equipment, int hand, Vector3 handPosition, Transform head)
        {
            Vector3 velocity = Vector3.zero;
            for (int i = 0; i < recentCount; i++)
            {
                velocity += recent[i];
            }
            velocity = recentCount > 0 ? velocity / recentCount : Vector3.zero;
            float speed = velocity.magnitude;
            if (speed < MinThrowSpeed)
            {
                VRLog.Info("throw: let go at " + speed.ToString("0.0") + " m/s (a throw from " + MinThrowSpeed.ToString("0.0") + " m/s), disarmed, not thrown.");
                return;
            }
            if (equipment.isBusy)
            {
                VRLog.Info("throw: let go while the game was busy, not thrown.");
                return;
            }

            bool overhand = handPosition.y >= head.position.y - OverhandBelowHead;
            Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
            Vector3 flatView = new Vector3(head.forward.x, 0f, head.forward.z);
            if (flat.sqrMagnitude < 1e-4f)
            {
                flat = flatView.sqrMagnitude > 1e-4f ? flatView : Vector3.forward; // straight up or down: the way the head faces
            }
            flat.Normalize();
            float pitch = Mathf.Clamp(Mathf.Atan2(velocity.y, new Vector2(velocity.x, velocity.z).magnitude) * Mathf.Rad2Deg, PitchMin, PitchMax);
            Vector3 direction = Quaternion.AngleAxis(-pitch, Vector3.Cross(Vector3.up, flat)) * flat;

            throwSound = equipment.asset is ItemThrowableAsset thrown ? thrown.use : null;
            throwSoundUntil = Time.time + PendingMaxSeconds;
            pressKey = overhand ? ControlsSettings.primary : ControlsSettings.secondary;
            pressFrame = Time.frameCount + 1; // read by the game's Update next frame (this runs in LateUpdate)
            if (HandAimed)
            {
                pending = true;
                pendingSince = Time.time;
                pendingOrigin = handPosition;
                pendingRotation = Quaternion.LookRotation(direction);
                VRNetPoseSender.SendThrow(player, pendingOrigin, pendingRotation);
            }
            VRHaptics.Pulse(hand, overhand ? 0.8f : 0.5f, 0.06f, 160f);
            VRLog.Info("throw: " + (overhand ? "overhand (strong)" : "underhand (weak)") + ", hand " + speed.ToString("0.0")
                + " m/s, " + (head.position.y - handPosition.y).ToString("0.00") + " m under the head, pitch " + pitch.ToString("0")
                + " degrees, heading " + Vector3.SignedAngle(flatView, flat, Vector3.up).ToString("0") + " degrees from the view"
                + (HandAimed ? "" : " (this server throws from the view)") + ".");
        }
    }
}
