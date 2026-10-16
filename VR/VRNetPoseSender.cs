using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using UnityEngine;
using UnturnedVR.Net;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Tells a server that runs the mod where the player's head, hands and gun muzzle are, 20 times a second (Net/VRPoseData). It
    /// sends nothing until the server has said hello with the same protocol version (VRNet.ServerAcceptsPoses): a vanilla server
    /// would close the connection on a message it does not know. Nothing is sent in singleplayer or when hosting, where this
    /// process is the server and uses the poses directly. Client only, never touched on a dedicated server.
    /// </summary>
    internal sealed class VRNetPoseSender : MonoBehaviour
    {
        private const float Interval = 0.05f;

        private static ServerStaticMethod sendPose;

        private float nextSend;
        private uint sequence;

        public static void Create()
        {
            if (!Net.VRRole.CanRunClientCode)
            {
                VRLog.Warn("pose sender: not created, this is a dedicated server.");
                return;
            }

            GameObject host = new GameObject("UnturnedVR pose sender");
            DontDestroyOnLoad(host);
            host.AddComponent<VRNetPoseSender>();
        }

        private void Update()
        {
            if (Time.unscaledTime < nextSend || !VRNet.ServerAcceptsPoses)
            {
                return;
            }

            Camera eye = VRRig.ActiveCamera;
            Player player = Player.LocalPlayer;
            if (eye == null || player == null)
            {
                return;
            }

            if (sendPose == null)
            {
                sendPose = ServerStaticMethod.Get(typeof(VRNetRpc), nameof(VRNetRpc.ReceivePose));
                if (sendPose == null)
                {
                    VRLog.Warn("pose sender: the game has no handle for the pose RPC, giving up.");
                    enabled = false;
                    return;
                }
            }

            nextSend = Time.unscaledTime + Interval;
            sendPose.Invoke(ENetReliability.Unreliable, WritePose, Capture(eye, player));
        }

        private VRPoseData Capture(Camera eye, Player player)
        {
            VRPoseData pose = default;
            pose.Sequence = ++sequence;

            pose.HeadPosition = eye.transform.position;
            pose.HeadRotation = eye.transform.rotation;

            Transform left = VRHands.Anchor(VRHands.Left);
            if (left != null && VRHands.IsTracked(VRHands.Left))
            {
                pose.LeftTracked = true;
                pose.LeftPosition = left.position;
                pose.LeftRotation = left.rotation;
            }
            Transform right = VRHands.Anchor(VRHands.Right);
            if (right != null && VRHands.IsTracked(VRHands.Right))
            {
                pose.RightTracked = true;
                pose.RightPosition = right.position;
                pose.RightRotation = right.rotation;
            }

            int itemHand = VRArms.Active ? VRArms.ItemHand : -1;
            pose.ItemHand = itemHand == VRHands.Left ? VRPoseData.ItemHandLeft : itemHand == VRHands.Right ? VRPoseData.ItemHandRight : VRPoseData.ItemHandNone;

            // The muzzle is sent under the same condition the client shoots from it (a gun in a hand, no wall between the head and the
            // muzzle); otherwise the server keeps the vanilla head aim, as the client does.
            if (pose.ItemHand != VRPoseData.ItemHandNone && player.equipment != null && player.equipment.useable is UseableGun
                && VRGun.TryGetMuzzle(player, out Vector3 muzzlePosition, out Quaternion muzzleRotation))
            {
                pose.HasMuzzle = true;
                pose.MuzzlePosition = muzzlePosition;
                pose.MuzzleDirection = muzzleRotation * Vector3.forward;
            }

            return pose;
        }

        private static void WritePose(NetPakWriter writer, VRPoseData pose)
        {
            pose.Write(writer);
        }
    }
}
