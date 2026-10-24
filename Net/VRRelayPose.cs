using SDG.NetPak;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// One VR player's pose as the server relays it to other players. The hands and the muzzle are relative to the sender's HEAD (with the
    /// body's yaw removed), not to the body the server holds: in the sender's play space the head, hands and muzzle move together while the
    /// body (the character's capsule) stays where the game has it, so a position relative to the body drifts off the avatar whenever the
    /// sender steps away from the middle of the play space. The receiver hangs the hands on the neck of the avatar it draws
    /// (<see cref="ToWorld"/>), so the arms stay on the body wherever the sender stands. Only the head itself is relative to the body.
    /// Cosmetic only: nothing in the game decides anything from this. The write and read code is here, in the assembly both sides
    /// load. The size is bounded: 14 + 5 x (36 + 27) bits, at most about 42 bytes.
    /// </summary>
    internal struct VRRelayPose
    {
        /// <summary>Largest number of players in one relay message. The reader refuses a larger count.</summary>
        public const int MaxPerMessage = 16;

        // 5 integer bits and 7 fraction bits per axis: +-16 m in steps of 1/128 m. The server accepts a head 6 m from the player
        // and a hand or muzzle 3.5 m from the head, so everything that passed validation fits.
        private const int IntBits = 5;
        private const int FracBits = 7;

        /// <summary>The sender's channel id, the same byte the game's own player state messages use to name a player.</summary>
        public byte ChannelId;

        /// <summary>See <see cref="VRPoseData.ItemHand"/>.</summary>
        public byte ItemHand;
        public bool LeftTracked;
        public bool RightTracked;
        public bool HasMuzzle;
        public bool TwoHanded;

        // The sender's frame: rotations are relative to Euler(0, yaw, 0). The head position is relative to the player's position; the
        // hand, muzzle and item positions are relative to the head position, and the muzzle direction is only turned by the yaw.
        public Vector3 HeadPosition;
        public Quaternion HeadRotation;
        public Vector3 LeftPosition;
        public Quaternion LeftRotation;
        public Vector3 RightPosition;
        public Quaternion RightRotation;
        public Vector3 MuzzlePosition;
        public Vector3 MuzzleDirection;
        public Vector3 ItemPosition;
        public Quaternion ItemRotation;

        /// <summary>Server side: a validated world pose into the sender's frame. <paramref name="bodyYaw"/> is the server's look yaw of the player, in degrees.</summary>
        public static VRRelayPose FromWorld(byte channelId, in VRPoseData pose, Vector3 bodyPosition, float bodyYaw)
        {
            Quaternion inverse = Quaternion.Euler(0f, -bodyYaw, 0f);

            VRRelayPose relay = default;
            relay.ChannelId = channelId;
            relay.ItemHand = pose.ItemHand;
            relay.LeftTracked = pose.LeftTracked;
            relay.RightTracked = pose.RightTracked;
            relay.HasMuzzle = pose.HasMuzzle;
            relay.TwoHanded = pose.TwoHanded;

            relay.HeadPosition = inverse * (pose.HeadPosition - bodyPosition);
            relay.HeadRotation = inverse * pose.HeadRotation;
            if (pose.LeftTracked)
            {
                relay.LeftPosition = inverse * (pose.LeftPosition - pose.HeadPosition);
                relay.LeftRotation = inverse * pose.LeftRotation;
            }
            if (pose.RightTracked)
            {
                relay.RightPosition = inverse * (pose.RightPosition - pose.HeadPosition);
                relay.RightRotation = inverse * pose.RightRotation;
            }
            if (pose.HasMuzzle)
            {
                relay.MuzzlePosition = inverse * (pose.MuzzlePosition - pose.HeadPosition);
                relay.MuzzleDirection = inverse * pose.MuzzleDirection;
            }
            if (pose.ItemHand != VRPoseData.ItemHandNone)
            {
                relay.ItemPosition = inverse * (pose.ItemPosition - pose.HeadPosition);
                relay.ItemRotation = inverse * pose.ItemRotation;
            }
            return relay;
        }

        /// <summary>
        /// Client side: the pose in world space for a remote body at <paramref name="bodyPosition"/> facing <paramref name="bodyYaw"/>
        /// (degrees). <paramref name="eyePosition"/> is where the avatar's head is: the hands and the muzzle hang on it, so they stay on the
        /// avatar however far the sender's head is from the middle of their play space. The head position is placed on the body.
        /// </summary>
        public VRRelayPose ToWorld(Vector3 bodyPosition, Vector3 eyePosition, float bodyYaw)
        {
            Quaternion yaw = Quaternion.Euler(0f, bodyYaw, 0f);

            VRRelayPose world = this;
            world.HeadPosition = bodyPosition + yaw * HeadPosition;
            world.HeadRotation = yaw * HeadRotation;
            world.LeftPosition = eyePosition + yaw * LeftPosition;
            world.LeftRotation = yaw * LeftRotation;
            world.RightPosition = eyePosition + yaw * RightPosition;
            world.RightRotation = yaw * RightRotation;
            world.MuzzlePosition = eyePosition + yaw * MuzzlePosition;
            world.MuzzleDirection = yaw * MuzzleDirection;
            world.ItemPosition = eyePosition + yaw * ItemPosition;
            world.ItemRotation = yaw * ItemRotation;
            return world;
        }

        /// <summary>
        /// Between two samples of the same player: positions and directions blend, rotations slerp, and what a sample has or does not
        /// have (a tracked hand, the muzzle, the item hand) comes from <paramref name="b"/>, the newer one, unless both have it.
        /// </summary>
        public static VRRelayPose Lerp(in VRRelayPose a, in VRRelayPose b, float t)
        {
            VRRelayPose result = b;
            result.HeadPosition = Vector3.Lerp(a.HeadPosition, b.HeadPosition, t);
            result.HeadRotation = Quaternion.Slerp(a.HeadRotation, b.HeadRotation, t);
            if (a.LeftTracked && b.LeftTracked)
            {
                result.LeftPosition = Vector3.Lerp(a.LeftPosition, b.LeftPosition, t);
                result.LeftRotation = Quaternion.Slerp(a.LeftRotation, b.LeftRotation, t);
            }
            if (a.RightTracked && b.RightTracked)
            {
                result.RightPosition = Vector3.Lerp(a.RightPosition, b.RightPosition, t);
                result.RightRotation = Quaternion.Slerp(a.RightRotation, b.RightRotation, t);
            }
            if (a.HasMuzzle && b.HasMuzzle)
            {
                result.MuzzlePosition = Vector3.Lerp(a.MuzzlePosition, b.MuzzlePosition, t);
                result.MuzzleDirection = Vector3.Slerp(a.MuzzleDirection, b.MuzzleDirection, t);
            }
            if (a.ItemHand != VRPoseData.ItemHandNone && a.ItemHand == b.ItemHand)
            {
                result.ItemPosition = Vector3.Lerp(a.ItemPosition, b.ItemPosition, t);
                result.ItemRotation = Quaternion.Slerp(a.ItemRotation, b.ItemRotation, t);
            }
            return result;
        }

        public void Write(NetPakWriter writer)
        {
            writer.WriteUInt8(ChannelId);
            writer.WriteBits(ItemHand, 2);
            writer.WriteBit(LeftTracked);
            writer.WriteBit(RightTracked);
            writer.WriteBit(HasMuzzle);
            writer.WriteBit(TwoHanded);

            writer.WriteClampedVector3(HeadPosition, IntBits, FracBits);
            writer.WriteQuaternion(HeadRotation);
            if (LeftTracked)
            {
                writer.WriteClampedVector3(LeftPosition, IntBits, FracBits);
                writer.WriteQuaternion(LeftRotation);
            }
            if (RightTracked)
            {
                writer.WriteClampedVector3(RightPosition, IntBits, FracBits);
                writer.WriteQuaternion(RightRotation);
            }
            if (HasMuzzle)
            {
                writer.WriteClampedVector3(MuzzlePosition, IntBits, FracBits);
                writer.WriteNormalVector3(MuzzleDirection);
            }
            if (ItemHand != VRPoseData.ItemHandNone)
            {
                writer.WriteClampedVector3(ItemPosition, IntBits, FracBits);
                writer.WriteQuaternion(ItemRotation);
            }
        }

        /// <summary>Reads exactly what <see cref="Write"/> wrote. False if the data ends early or a field is out of range.</summary>
        public static bool TryRead(NetPakReader reader, out VRRelayPose pose)
        {
            pose = default;

            if (!reader.ReadUInt8(out pose.ChannelId))
            {
                return false;
            }
            if (!reader.ReadBits(2, out uint itemHand) || itemHand > VRPoseData.ItemHandRight)
            {
                return false;
            }
            pose.ItemHand = (byte) itemHand;
            if (!reader.ReadBit(out pose.LeftTracked) || !reader.ReadBit(out pose.RightTracked) || !reader.ReadBit(out pose.HasMuzzle)
                || !reader.ReadBit(out pose.TwoHanded))
            {
                return false;
            }

            if (!reader.ReadClampedVector3(out pose.HeadPosition, IntBits, FracBits) || !reader.ReadQuaternion(out pose.HeadRotation))
            {
                return false;
            }
            if (pose.LeftTracked && (!reader.ReadClampedVector3(out pose.LeftPosition, IntBits, FracBits) || !reader.ReadQuaternion(out pose.LeftRotation)))
            {
                return false;
            }
            if (pose.RightTracked && (!reader.ReadClampedVector3(out pose.RightPosition, IntBits, FracBits) || !reader.ReadQuaternion(out pose.RightRotation)))
            {
                return false;
            }
            if (pose.HasMuzzle && (!reader.ReadClampedVector3(out pose.MuzzlePosition, IntBits, FracBits) || !reader.ReadNormalVector3(out pose.MuzzleDirection)))
            {
                return false;
            }
            if (pose.ItemHand != VRPoseData.ItemHandNone && (!reader.ReadClampedVector3(out pose.ItemPosition, IntBits, FracBits) || !reader.ReadQuaternion(out pose.ItemRotation)))
            {
                return false;
            }
            return true;
        }
    }
}
