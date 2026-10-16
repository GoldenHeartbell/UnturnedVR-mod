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
        private static ServerStaticMethod sendShotPose;
        private static VRNetPoseSender instance;

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
            instance = host.AddComponent<VRNetPoseSender>();
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }

        /// <summary>
        /// Called from UseableGun.fire (Patches/UseableGunMuzzlePatch) for the local player's shot, with the muzzle pose the client
        /// fires its own rocket from. For a launcher, on a server that runs the mod, the pose goes to the server at once and reliably, ahead
        /// of the input packet that carries the shot (the client sends that after the tick that ran fire), and the server starts its
        /// damaging rocket from this exact muzzle instead of from a pose that arrived later. Nothing happens in singleplayer or on a
        /// server without the mod, or for guns that are not launchers.
        /// </summary>
        public static void SendLauncherShot(Player player, Vector3 muzzlePosition, Quaternion muzzleRotation)
        {
            if (instance == null || !VRNet.ServerAcceptsPoses)
            {
                return;
            }

            ItemGunAsset asset = player != null && player.equipment != null ? player.equipment.asset as ItemGunAsset : null;
            Camera eye = VRRig.ActiveCamera;
            if (asset == null || asset.projectile == null || eye == null)
            {
                return;
            }

            instance.SendShot(eye, player, muzzlePosition, muzzleRotation);
        }

        private void SendShot(Camera eye, Player player, Vector3 muzzlePosition, Quaternion muzzleRotation)
        {
            if (sendShotPose == null)
            {
                sendShotPose = ServerStaticMethod.Get(typeof(VRNetRpc), nameof(VRNetRpc.ReceiveShotPose));
                if (sendShotPose == null)
                {
                    VRLog.Warn("pose sender: the game has no handle for the shot pose RPC, shots start from the latest pose.");
                    return;
                }
            }

            VRPoseData pose = Capture(eye, player, false);
            pose.HasMuzzle = true;
            pose.MuzzlePosition = muzzlePosition;
            pose.MuzzleDirection = muzzleRotation * Vector3.forward;
            sendShotPose.Invoke(ENetReliability.Reliable, WritePose, pose);
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

            // Add the interval to the last due time, not to now, so the frame's rounding does not add up (20 a second, not 17); after a
            // stall, start over instead of sending a burst.
            nextSend += Interval;
            if (nextSend <= Time.unscaledTime)
            {
                nextSend = Time.unscaledTime + Interval;
            }
            sendPose.Invoke(ENetReliability.Unreliable, WritePose, Capture(eye, player, true));
        }

        private VRPoseData Capture(Camera eye, Player player, bool includeMuzzle)
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
            if (includeMuzzle && pose.ItemHand != VRPoseData.ItemHandNone && player.equipment != null && player.equipment.useable is UseableGun
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
