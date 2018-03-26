using Neo.IO;
using Neo.IO.Json;
using Neo.VM;
using System;
using System.IO;

namespace Neo.Core
{
    /// <summary>
    /// Transaction input
    /// </summary>
    public class CoinReference : IEquatable<CoinReference>, IInteropInterface, ISerializable
    {
        /// <summary>
        /// Hash of the transaction output
        /// </summary>
        public UInt256 PrevHash;
        /// <summary>
        /// Index of the transaction output
        /// </summary>
        public ushort PrevIndex;

        public int Size => PrevHash.Size + sizeof(ushort);

        void ISerializable.Deserialize(BinaryReader reader)
        {
            PrevHash = reader.ReadSerializable<UInt256>();
            PrevIndex = reader.ReadUInt16();
        }

        public bool Equals(CoinReference other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (ReferenceEquals(null, other)) return false;
            return PrevHash.Equals(other.PrevHash) && PrevIndex.Equals(other.PrevIndex);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj)) return true;
            if (ReferenceEquals(null, obj)) return false;
            if (!(obj is CoinReference)) return false;
            return Equals((CoinReference)obj);
        }

        public override int GetHashCode()
        {
            return PrevHash.GetHashCode() + PrevIndex.GetHashCode();
        }

        void ISerializable.Serialize(BinaryWriter writer)
        {
            writer.Write(PrevHash);
            writer.Write(PrevIndex);
        }

        /// <summary>
        /// Convert transaction input to json object containing `txid` and `vout`
        /// </summary>
        public JObject ToJson()
        {
            JObject json = new JObject();
            json["txid"] = PrevHash.ToString();
            json["vout"] = PrevIndex;
            return json;
        }
    }
}
