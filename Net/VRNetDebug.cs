using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Server-side diagnostics. Switched on by a start-up argument, not by a compiled-in default, so a server owner can turn them on
    /// (and off) from the server's launch parameters without a different build, and a release never ships with one on.
    /// </summary>
    internal static class VRNetDebug
    {
        /// <summary>
        /// Add <c>-UnturnedVRLogPoses</c> to the server's start parameters to log a summary of the poses it accepts (head, how far the
        /// hands and the muzzle are from the head), at most one line per player every few seconds. Used to check the reach limits in
        /// VRNetGuard against real play.
        /// </summary>
        private static readonly CommandLineFlag logPoses = new CommandLineFlag(false, "-UnturnedVRLogPoses");

        public static bool LogPoses => logPoses;

        public static void LogPose(in VRNetGuard.Sender sender, in VRPoseData pose)
        {
            if (!LogPoses || !VRNetGuard.ShouldLogAccepted(sender))
            {
                return;
            }

            string item = pose.ItemHand == VRPoseData.ItemHandLeft ? "left" : pose.ItemHand == VRPoseData.ItemHandRight ? "right" : "none";
            VRLog.Info("net: pose #" + pose.Sequence + " from " + sender.Id + ": head " + pose.HeadPosition.ToString("0.00")
                + ", item hand " + item
                + ", left hand " + (pose.LeftTracked ? Distance(pose.LeftPosition, pose.HeadPosition) : "untracked")
                + ", right hand " + (pose.RightTracked ? Distance(pose.RightPosition, pose.HeadPosition) : "untracked")
                + ", muzzle " + (pose.HasMuzzle ? Distance(pose.MuzzlePosition, pose.HeadPosition) + " dir " + pose.MuzzleDirection.ToString("0.00") : "none") + ".");
        }

        private static string Distance(Vector3 point, Vector3 head)
        {
            return Vector3.Distance(point, head).ToString("0.00") + " m from the head";
        }
    }
}
