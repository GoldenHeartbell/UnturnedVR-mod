using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Phase 3: which hand(s) hold the equipped item. The game only knows one equipped item (its model hangs on the
    /// right hook), so this is visual only: VRArms.DriveItem poses the item on the holding hand(s).
    /// A hand that grips next to the item grabs it:
    ///   - the holding hand is gripping too: both hands hold it (item between the hands); letting go of one hand
    ///     leaves it in the other one;
    ///   - the holding hand is not gripping: the item is passed to the grabbing hand and stays there.
    /// A hand does not have to keep gripping to hold an item it holds alone. The grip presses used for grabbing are
    /// swallowed (VRKeyMap) until released, so they do not also fire "interact" or "firemode".
    /// Cost: two distance checks on a grip press, nothing otherwise.
    /// </summary>
    internal static class VRHold
    {
        /// <summary>The free hand must be this close (metres) to the holding hand to grab the item.</summary>
        private const float GrabRadius = 0.25f;

        /// <summary>Hand (VRHands.Left/Right) holding the item after a pass, -1 = the item's own hand.</summary>
        public static int HandOverride { get; private set; } = -1;

        /// <summary>Both hands hold the item right now (both grips down).</summary>
        public static bool Both { get; private set; }

        /// <summary>Changes whenever who holds the item changes; VRArms blends the item over to the new pose.</summary>
        public static int Version { get; private set; }

        /// <summary>The free hand must be this close (metres) to a gun's second-hand point to hold it there.</summary>
        private const float SupportGrabRadius = 0.15f;

        // Both hands hold a gun because the free hand gripped its second-hand point: the trigger hand does not have
        // to grip, and only letting go of this hand ends the two-handed hold.
        private static bool supportHold;
        private static int supportHand;

        private static bool previousLeft;
        private static bool previousRight;
        private static bool consumedLeft;
        private static bool consumedRight;

        /// <summary>A new item was equipped (or none): it starts in its own hand again.</summary>
        public static void Reset()
        {
            HandOverride = -1;
            Both = false;
            supportHold = false;
            Version++;
        }

        /// <summary>True while this hand's grip press was used to grab the item and must not reach the game.</summary>
        public static bool Consumed(bool right)
        {
            return right ? consumedRight : consumedLeft;
        }

        /// <summary>Once per frame from VRKeyMap with the raw grip states.</summary>
        public static void Update(bool leftGrip, bool rightGrip, bool allowGrab)
        {
            bool leftDown = leftGrip && !previousLeft;
            bool rightDown = rightGrip && !previousRight;
            previousLeft = leftGrip;
            previousRight = rightGrip;
            if (!leftGrip)
            {
                consumedLeft = false;
            }
            if (!rightGrip)
            {
                consumedRight = false;
            }

            int holder = VRArms.ItemHand;
            if (holder < 0)
            {
                Both = false;
                supportHold = false;
                return;
            }

            if (supportHold)
            {
                if ((supportHand == VRHands.Left ? leftGrip : rightGrip) && allowGrab)
                {
                    return;
                }
                supportHold = false;
                Both = false;
                Version++;
                VRLog.Info("hold: second hand let go, gun in the " + (holder == VRHands.Left ? "left" : "right") + " hand only.");
                return;
            }

            if (Both)
            {
                if (leftGrip && rightGrip && allowGrab)
                {
                    return;
                }
                // One hand let go: the item stays in the hand that still grips (or in the primary one).
                Both = false;
                HandOverride = leftGrip != rightGrip ? (leftGrip ? VRHands.Left : VRHands.Right) : holder;
                Version++;
                VRLog.Info("hold: item in the " + (HandOverride == VRHands.Left ? "left" : "right") + " hand only.");
                return;
            }

            int free = holder == VRHands.Left ? VRHands.Right : VRHands.Left;
            if (!allowGrab || !(free == VRHands.Left ? leftDown : rightDown))
            {
                return;
            }

            Transform holding = VRHands.Anchor(holder);
            Transform other = VRHands.Anchor(free);
            if (holding == null || other == null || !VRHands.IsTracked(holder) || !VRHands.IsTracked(free))
            {
                return;
            }
            // A gun: the free hand at its second-hand point holds it there (nearer to that point than to the trigger hand).
            float toHolder = (holding.position - other.position).sqrMagnitude;
            if (VRArms.SupportValid)
            {
                float toSupport = (VRArms.SupportPoint - other.position).sqrMagnitude;
                if (toSupport <= SupportGrabRadius * SupportGrabRadius && toSupport < toHolder)
                {
                    supportHold = true;
                    supportHand = free;
                    Both = true;
                    if (free == VRHands.Left)
                    {
                        consumedLeft = true;
                    }
                    else
                    {
                        consumedRight = true;
                    }
                    Version++;
                    VRLog.Info("hold: gun held by the second hand at its grip point.");
                    return;
                }
            }
            if (toHolder > GrabRadius * GrabRadius)
            {
                return;
            }

            if (holder == VRHands.Left ? leftGrip : rightGrip)
            {
                Both = true;
                consumedLeft = true;
                consumedRight = true;
                VRLog.Info("hold: item in both hands.");
            }
            else
            {
                HandOverride = free;
                if (free == VRHands.Left)
                {
                    consumedLeft = true;
                }
                else
                {
                    consumedRight = true;
                }
                VRLog.Info("hold: item passed to the " + (free == VRHands.Left ? "left" : "right") + " hand.");
            }
            Version++;
        }
    }
}
