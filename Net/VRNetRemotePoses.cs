using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Client side: the last few relayed poses of each other VR player (<see cref="VRRelayPose"/>), by channel id, with the time each
    /// arrived. The relay RPC (<see cref="VRNetRpc.ReceiveRelayPoses"/>) writes here and the client assembly's drawing code reads;
    /// this assembly knows nothing about VR, so there is no event between the two. Main thread only. Bounded: a small ring per possible
    /// channel id, so nothing a server sends can make it grow.
    /// </summary>
    internal static class VRNetRemotePoses
    {
        private struct Sample
        {
            public VRRelayPose Pose;
            public float ReceivedAt;
        }

        private const int Slots = 256;

        /// <summary>Samples kept per player: a few relay messages (50 ms apart), enough to span the render delay with some jitter.</summary>
        private const int Depth = 4;

        // samples[channel * Depth + k]: a ring, the newest at (next - 1). counts[channel] is how many are filled (at most Depth).
        private static readonly Sample[] samples = new Sample[Slots * Depth];
        private static readonly byte[] counts = new byte[Slots];
        private static readonly byte[] nexts = new byte[Slots];

        public static void Store(in VRRelayPose pose)
        {
            Store(pose, Time.realtimeSinceStartup);
        }

        // The clock is a parameter so the ring can be tested outside the game.
        internal static void Store(in VRRelayPose pose, float now)
        {
            int channel = pose.ChannelId;
            int next = nexts[channel];
            samples[channel * Depth + next] = new Sample { Pose = pose, ReceivedAt = now };
            nexts[channel] = (byte) ((next + 1) % Depth);
            if (counts[channel] < Depth)
            {
                ++counts[channel];
            }
        }

        /// <summary>The latest pose of that player if it arrived at most <paramref name="maxAgeSeconds"/> ago.</summary>
        public static bool TryGetFresh(byte channelId, float maxAgeSeconds, out VRRelayPose pose, out float ageSeconds)
        {
            return TryGetFresh(channelId, Time.realtimeSinceStartup, maxAgeSeconds, out pose, out ageSeconds);
        }

        /// <summary>
        /// The pose of that player as it was <paramref name="delaySeconds"/> ago, blended between the two samples around that moment, so
        /// the hands move smoothly although the messages come in steps. The game draws other players' bodies about 0.1 s in the past for
        /// the same reason; use that delay and the hands stay with the body. Before the oldest kept sample the oldest is used, after the
        /// newest the newest. False when the newest sample is older than <paramref name="maxAgeSeconds"/>.
        /// </summary>
        public static bool TryGetInterpolated(byte channelId, float delaySeconds, float maxAgeSeconds, out VRRelayPose pose)
        {
            return TryGetInterpolated(channelId, Time.realtimeSinceStartup, delaySeconds, maxAgeSeconds, out pose);
        }

        internal static bool TryGetFresh(byte channelId, float now, float maxAgeSeconds, out VRRelayPose pose, out float ageSeconds)
        {
            pose = default;
            ageSeconds = 0f;
            if (counts[channelId] == 0)
            {
                return false;
            }

            Sample newest = Back(channelId, 0);
            ageSeconds = now - newest.ReceivedAt;
            if (ageSeconds > maxAgeSeconds)
            {
                return false;
            }

            pose = newest.Pose;
            return true;
        }

        internal static bool TryGetInterpolated(byte channelId, float now, float delaySeconds, float maxAgeSeconds, out VRRelayPose pose)
        {
            pose = default;
            int count = counts[channelId];
            if (count == 0)
            {
                return false;
            }

            Sample newest = Back(channelId, 0);
            if (now - newest.ReceivedAt > maxAgeSeconds)
            {
                return false;
            }

            float at = now - delaySeconds;
            if (count == 1 || at >= newest.ReceivedAt)
            {
                pose = newest.Pose;
                return true;
            }

            // Walk back from the newest until the sample at or before the wanted time; the one after it is the other end of the blend.
            Sample later = newest;
            for (int back = 1; back < count; ++back)
            {
                Sample earlier = Back(channelId, back);
                if (earlier.ReceivedAt <= at)
                {
                    float span = later.ReceivedAt - earlier.ReceivedAt;
                    float t = span > 0.0001f ? Mathf.Clamp01((at - earlier.ReceivedAt) / span) : 1f;
                    pose = VRRelayPose.Lerp(earlier.Pose, later.Pose, t);
                    return true;
                }
                later = earlier;
            }

            pose = later.Pose; // older than everything kept
            return true;
        }

        // The sample that arrived <back> messages before the newest one.
        private static Sample Back(byte channelId, int back)
        {
            int index = (nexts[channelId] - 1 - back + 2 * Depth) % Depth;
            return samples[channelId * Depth + index];
        }

        /// <summary>The connection ended: nothing relayed belongs to the next server.</summary>
        public static void Clear()
        {
            System.Array.Clear(samples, 0, samples.Length);
            System.Array.Clear(counts, 0, Slots);
            System.Array.Clear(nexts, 0, Slots);
        }
    }
}
