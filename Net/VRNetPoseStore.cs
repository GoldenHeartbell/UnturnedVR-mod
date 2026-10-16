using System.Collections.Generic;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Server side: the latest validated pose of each VR player, by Steam id. Later server patches (the launcher's rocket origin,
    /// relaying hands to other players) read from here and never from raw client data. Main thread only. Bounded: at most
    /// <see cref="MaxEntries"/> players, and an entry is removed when its player disconnects.
    /// </summary>
    internal static class VRNetPoseStore
    {
        private struct Entry
        {
            public VRPoseData Pose;
            public float ReceivedAt;
        }

        private const int MaxEntries = 128;

        private static readonly Dictionary<ulong, Entry> entries = new Dictionary<ulong, Entry>();

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
        public static bool TryGetFresh(ulong id, float maxAgeSeconds, out VRPoseData pose)
        {
            if (entries.TryGetValue(id, out Entry entry) && Time.realtimeSinceStartup - entry.ReceivedAt <= maxAgeSeconds)
            {
                pose = entry.Pose;
                return true;
            }

            pose = default;
            return false;
        }

        public static void Forget(ulong id)
        {
            entries.Remove(id);
        }

        public static void Clear()
        {
            entries.Clear();
        }
    }
}
