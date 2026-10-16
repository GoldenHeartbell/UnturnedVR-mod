using System.Collections.Generic;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Server side: the poses of each VR player, by Steam id, after validation. Later server patches (the launcher's rocket origin,
    /// relaying hands to other players) read from here and never from raw client data. Main thread only. Bounded: at most
    /// <see cref="MaxEntries"/> players, a few queued shot poses each, and everything of a player is removed when it disconnects.
    ///
    /// Two kinds: the latest ordinary pose (20 a second, newest wins), and shot poses: the pose at the moment of a launcher shot,
    /// which the server uses for that shot's rocket. The server runs a shot a little after the client fired it, and the newest
    /// ordinary pose is then from later, when the gun has moved; the shot pose is the one from the trigger pull.
    /// </summary>
    internal static class VRNetPoseStore
    {
        private struct Entry
        {
            public VRPoseData Pose;
            public float ReceivedAt;
        }

        private const int MaxEntries = 128;

        /// <summary>Shots waiting for the server to run them, per player. A launcher fires every few seconds, so a few is plenty.</summary>
        private const int MaxShotsPerPlayer = 4;

        private static readonly Dictionary<ulong, Entry> entries = new Dictionary<ulong, Entry>();
        private static readonly Dictionary<ulong, Queue<Entry>> shots = new Dictionary<ulong, Queue<Entry>>();

        /// <summary>False (and nothing stored) when the pose is not newer than the stored one, or the table is full.</summary>
        public static bool TryStore(ulong id, in VRPoseData pose)
        {
            if (entries.TryGetValue(id, out Entry old))
            {
                // Unreliable messages can arrive out of order; the difference as a signed number also survives the counter wrapping.
                if ((int) (pose.Sequence - old.Pose.Sequence) <= 0)
                {
                    return false;
                }
            }
            else if (entries.Count >= MaxEntries)
            {
                return false;
            }

            entries[id] = new Entry { Pose = pose, ReceivedAt = Time.realtimeSinceStartup };
            return true;
        }

        public static bool Has(ulong id)
        {
            return entries.ContainsKey(id);
        }

        /// <summary>The player's latest pose if it arrived at most <paramref name="maxAgeSeconds"/> ago.</summary>
        public static bool TryGetFresh(ulong id, float maxAgeSeconds, out VRPoseData pose, out float ageSeconds)
        {
            if (entries.TryGetValue(id, out Entry entry))
            {
                ageSeconds = Time.realtimeSinceStartup - entry.ReceivedAt;
                if (ageSeconds <= maxAgeSeconds)
                {
                    pose = entry.Pose;
                    return true;
                }
            }
            else
            {
                ageSeconds = 0f;
            }

            pose = default;
            return false;
        }

        /// <summary>Queues the pose of a launcher shot. The oldest waiting one is dropped when the player already has the maximum.</summary>
        public static bool StoreShot(ulong id, in VRPoseData pose)
        {
            return StoreShot(id, pose, Time.realtimeSinceStartup);
        }

        /// <summary>The oldest queued shot pose that arrived at most <paramref name="maxAgeSeconds"/> ago, removed from the queue (one pose per shot).</summary>
        public static bool TryTakeShot(ulong id, float maxAgeSeconds, out VRPoseData pose, out float ageSeconds)
        {
            return TryTakeShot(id, maxAgeSeconds, Time.realtimeSinceStartup, out pose, out ageSeconds);
        }

        // The clock is a parameter so the queue can be tested outside the game.
        internal static bool StoreShot(ulong id, in VRPoseData pose, float now)
        {
            if (!shots.TryGetValue(id, out Queue<Entry> queue))
            {
                if (shots.Count >= MaxEntries)
                {
                    return false;
                }
                queue = new Queue<Entry>(MaxShotsPerPlayer);
                shots.Add(id, queue);
            }

            if (queue.Count >= MaxShotsPerPlayer)
            {
                queue.Dequeue();
            }
            queue.Enqueue(new Entry { Pose = pose, ReceivedAt = now });
            return true;
        }

        internal static bool TryTakeShot(ulong id, float maxAgeSeconds, float now, out VRPoseData pose, out float ageSeconds)
        {
            pose = default;
            ageSeconds = 0f;

            if (!shots.TryGetValue(id, out Queue<Entry> queue))
            {
                return false;
            }

            while (queue.Count > 0)
            {
                Entry entry = queue.Dequeue();
                float age = now - entry.ReceivedAt;
                if (age <= maxAgeSeconds)
                {
                    pose = entry.Pose;
                    ageSeconds = age;
                    return true;
                }
                // Too old to belong to a shot still waiting: dropped, and the next one is looked at.
            }

            return false;
        }

        public static void Forget(ulong id)
        {
            entries.Remove(id);
            shots.Remove(id);
        }

        public static void Clear()
        {
            entries.Clear();
            shots.Clear();
        }
    }
}
