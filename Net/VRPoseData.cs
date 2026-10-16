using SDG.NetPak;
using UnityEngine;

namespace UnturnedVR.Net
{
    /// <summary>
    /// Version of the mod's network messages. The server announces it with its hello; a client only sends poses to a server whose
    /// version equals its own, so a changed layout can never be read by an older or newer peer.
    /// </summary>
    internal static class VRNetProtocol
    {
        public const byte Version = 1;
    }

    /// <summary>
    /// What a VR client tells the server about the player: head, hands, which hand holds the item, and the gun's muzzle. World space.
    /// The write and read code is here, in the assembly both sides load, so the two can never drift apart. The size is bounded:
    /// at most 32 + 5 + 3 x (60 + 27) + 60 + 27 bits, about 48 bytes.
    /// </summary>
    internal struct VRPoseData
    {
        public const byte ItemHandNone = 0;
        public const byte ItemHandLeft = 1;
        public const byte ItemHandRight = 2;

        /// <summary>Counts up per message; the server drops anything not newer than the last one it accepted.</summary>
        public uint Sequence;

        /// <summary><see cref="ItemHandNone"/>, <see cref="ItemHandLeft"/> or <see cref="ItemHandRight"/>.</summary>
        public byte ItemHand;
        public bool LeftTracked;
        public bool RightTracked;

        /// <summary>The gun can fire from its muzzle (the same condition the client uses to shoot from it); else the vanilla head aim applies.</summary>
        public bool HasMuzzle;

        public Vector3 HeadPosition;
        public Quaternion HeadRotation;
        public Vector3 LeftPosition;
        public Quaternion LeftRotation;
        public Vector3 RightPosition;
        public Quaternion RightRotation;
        public Vector3 MuzzlePosition;
        public Vector3 MuzzleDirection;

        public void Write(NetPakWriter writer)
        {
            writer.WriteUInt32(Sequence);
            writer.WriteBits(ItemHand, 2);
            writer.WriteBit(LeftTracked);
            writer.WriteBit(RightTracked);
            writer.WriteBit(HasMuzzle);

            writer.WriteClampedVector3(HeadPosition);
            writer.WriteQuaternion(HeadRotation);
            if (LeftTracked)
            {
                writer.WriteClampedVector3(LeftPosition);
                writer.WriteQuaternion(LeftRotation);
            }
            if (RightTracked)
            {
                writer.WriteClampedVector3(RightPosition);
                writer.WriteQuaternion(RightRotation);
            }
            if (HasMuzzle)
            {
                writer.WriteClampedVector3(MuzzlePosition);
                writer.WriteNormalVector3(MuzzleDirection);
            }
        }

        /// <summary>Reads exactly what <see cref="Write"/> wrote. False if the data ends early or a field is out of range.</summary>
        public static bool TryRead(NetPakReader reader, out VRPoseData pose)
        {
            pose = default;

            if (!reader.ReadUInt32(out pose.Sequence))
            {
                return false;
            }
            if (!reader.ReadBits(2, out uint itemHand) || itemHand > ItemHandRight)
            {
                return false;
            }
            pose.ItemHand = (byte) itemHand;
            if (!reader.ReadBit(out pose.LeftTracked) || !reader.ReadBit(out pose.RightTracked) || !reader.ReadBit(out pose.HasMuzzle))
            {
                return false;
            }

            if (!reader.ReadClampedVector3(out pose.HeadPosition) || !reader.ReadQuaternion(out pose.HeadRotation))
            {
                return false;
            }
            if (pose.LeftTracked && (!reader.ReadClampedVector3(out pose.LeftPosition) || !reader.ReadQuaternion(out pose.LeftRotation)))
            {
                return false;
            }
            if (pose.RightTracked && (!reader.ReadClampedVector3(out pose.RightPosition) || !reader.ReadQuaternion(out pose.RightRotation)))
            {
                return false;
            }
            if (pose.HasMuzzle && (!reader.ReadClampedVector3(out pose.MuzzlePosition) || !reader.ReadNormalVector3(out pose.MuzzleDirection)))
            {
                return false;
            }
            return true;
        }
    }
}
