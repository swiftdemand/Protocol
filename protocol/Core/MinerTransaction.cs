using Neo.IO.Json;
using System;
using System.IO;
using System.Linq;

namespace Neo.Core
{
    public class MinerTransaction : Transaction
    {
        public uint Nonce;

        public override Fixed8 NetworkFee => Fixed8.Zero;

        public override int Size => base.Size + sizeof(uint);

        public MinerTransaction()
            : base(TransactionType.MinerTransaction)
        {
        }

        protected override void DeserializeExclusiveData(BinaryReader reader)
        {
            if (Version != 0) throw new FormatException();
            this.Nonce = reader.ReadUInt32();
        }

        /// <summary>
        /// Triggered when deserialization is completed
        /// </summary>
        protected override void OnDeserialized()
        {
            base.OnDeserialized();
            if (Inputs.Length != 0)
                throw new FormatException();
            if (Outputs.Any(p => p.AssetId != Blockchain.UtilityToken.Hash))
                throw new FormatException();
        }

        protected override void SerializeExclusiveData(BinaryWriter writer)
        {
            writer.Write(Nonce);
        }

        public override JObject ToJson()
        {
            JObject json = base.ToJson();
            json["nonce"] = Nonce;
            return json;
        }
    }
}
