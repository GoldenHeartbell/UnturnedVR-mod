using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Hand-shake smoothing for a direction (the "one euro" filter): a low-pass whose cut-off rises with how fast the
    /// direction moves, so a hand that is held still is calm and a deliberate sweep follows with little lag.
    /// Per step: speed (degrees/s, itself low-passed) sets the cut-off <c>MinCutoff + Beta * speed</c>, and the
    /// filtered direction turns the fraction <c>1 - exp(-2 pi cutoff dt)</c> of the way to the raw one.
    /// Used by the binoculars lens (VRBinoculars). Meant to be reused for gun aiming: step it once per frame with
    /// the raw sight/barrel direction and read <see cref="Value"/>; only the picture (scope camera) or also the shot
    /// direction can then use the calm direction, which is a design choice of the caller (lag vs bullets).
    /// The numbers are GUESSES: <see cref="MinCutoff"/> is the calm-hand cut-off in Hz (lower = steadier but more
    /// lag), <see cref="Beta"/> is how fast it opens up when moving (higher = less lag in sweeps, less calm).
    /// Cost: a handful of vector operations per step.
    /// </summary>
    internal sealed class VRAimFilter
    {
        public float MinCutoff = 1f;       // Hz
        public float Beta = 0.05f;         // Hz per degree/s
        public float DerivativeCutoff = 1f; // Hz, smoothing of the speed estimate

        private Vector3 value;
        private float speed;
        private bool valid;

        /// <summary>The filtered direction (unit length) after the last <see cref="Step"/>.</summary>
        public Vector3 Value => value;

        /// <summary>The next step starts from the raw direction (no lag from a previous aim).</summary>
        public void Reset()
        {
            valid = false;
            speed = 0f;
        }

        /// <param name="raw">Unit direction measured this frame.</param>
        /// <param name="dt">Seconds since the last step.</param>
        /// <param name="cutoffScale">Multiplies the calm cut-off: below 1 is steadier, for example 1/sqrt(zoom).</param>
        public Vector3 Step(Vector3 raw, float dt, float cutoffScale = 1f)
        {
            if (!valid || dt <= 0f)
            {
                value = raw;
                valid = true;
                return value;
            }

            float angle = Vector3.Angle(value, raw);
            float instantSpeed = angle / dt;
            speed = Mathf.Lerp(speed, instantSpeed, Alpha(DerivativeCutoff, dt));

            float cutoff = MinCutoff * cutoffScale + Beta * speed;
            value = Vector3.Slerp(value, raw, Alpha(cutoff, dt));
            return value;
        }

        private static float Alpha(float cutoff, float dt)
        {
            return 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(cutoff, 0.01f) * dt);
        }
    }
}
