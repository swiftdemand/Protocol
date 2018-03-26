using Neo.Cryptography;
using Neo.IO;
using Neo.IO.Json;
using Neo.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Neo.Core
{
    public class Block : BlockBase, IInventory, IEquatable<Block>
    {
        public Transaction[] Transactions;

        private Header _header = null;

        public Header Header
        {
            get
            {
                if (_header == null)
                {
                    _header = new Header
                    {
                        PrevHash = PrevHash,
                        MerkleRoot = MerkleRoot,
                        Timestamp = Timestamp,
                        Index = Index,
                        ConsensusData = ConsensusData,
                        NextConsensus = NextConsensus,
                        Script = Script
                    };
                }
                return _header;
            }
        }

        InventoryType IInventory.InventoryType => InventoryType.Block;

        public override int Size => base.Size + Transactions.GetVarSize();

        /// <summary>
        /// Calculate the fee (exluding system fee) that is left over for all transactions
        /// </summary>
        public static Fixed8 CalculateNetFee(IEnumerable<Transaction> transactions)
        {
            Transaction[] ts = transactions.Where(p => p.Type != TransactionType.MinerTransaction && p.Type != TransactionType.ClaimTransaction).ToArray();
            Fixed8 amount_in = ts.SelectMany(p => p.References.Values.Where(o => o.AssetId == Blockchain.UtilityToken.Hash)).Sum(p => p.Value);
            Fixed8 amount_out = ts.SelectMany(p => p.Outputs.Where(o => o.AssetId == Blockchain.UtilityToken.Hash)).Sum(p => p.Value);
            Fixed8 amount_sysfee = ts.Sum(p => p.SystemFee);
            return amount_in - amount_out - amount_sysfee;
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Transactions = new Transaction[reader.ReadVarInt(0x10000)];
            if (Transactions.Length == 0) throw new FormatException();
            for (int i = 0; i < Transactions.Length; i++)
            {
                Transactions[i] = Transaction.DeserializeFrom(reader);
            }
            if (MerkleTree.ComputeRoot(Transactions.Select(p => p.Hash).ToArray()) != MerkleRoot)
                throw new FormatException();
        }

        public bool Equals(Block other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (ReferenceEquals(null, other)) return false;
            return Hash.Equals(other.Hash);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as Block);
        }

        /// <summary>
        /// Create block from a limited amount of bytes
        /// </summary>
        public static Block FromTrimmedData(byte[] data, int index, Func<UInt256, Transaction> txSelector)
        {
            Block block = new Block();
            using (MemoryStream ms = new MemoryStream(data, index, data.Length - index, false))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                ((IVerifiable)block).DeserializeUnsigned(reader);
                reader.ReadByte(); block.Script = reader.ReadSerializable<Witness>();
                block.Transactions = new Transaction[reader.ReadVarInt(0x10000000)];
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    block.Transactions[i] = txSelector(reader.ReadSerializable<UInt256>());
                }
            }
            return block;
        }

        public override int GetHashCode()
        {
            return Hash.GetHashCode();
        }

        /// <summary>
        /// Generate the Merkle root based on the hashes for all transactions in the block
        /// </summary>
        public void RebuildMerkleRoot()
        {
            MerkleRoot = MerkleTree.ComputeRoot(Transactions.Select(p => p.Hash).ToArray());
        }

        public override void Serialize(BinaryWriter writer)
        {
            base.Serialize(writer);
            writer.Write(Transactions);
        }

        /// <summary>
        /// Convert block to a JSON object containing a transaction list `tx`
        /// </summary>
        public override JObject ToJson()
        {
            JObject json = base.ToJson();
            json["tx"] = Transactions.Select(p => p.ToJson()).ToArray();
            return json;
        }

        /// <summary>
        /// Convert the block object to a byte array that contains only the block header and the transaction hashes (with no transaction data)
        /// </summary>
        public byte[] Trim()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                ((IVerifiable)this).SerializeUnsigned(writer);
                writer.Write((byte)1); writer.Write(Script);
                writer.Write(Transactions.Select(p => p.Hash).ToArray());
                writer.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Verify that the block is valid
        /// </summary>
        /// <param name="completely">Whether to verify all transactions in the block completely</param>
        /// <returns>`true` if valid</returns>
        public bool Verify(bool completely)
        {
            if (!Verify()) return false;
            if (Transactions[0].Type != TransactionType.MinerTransaction || Transactions.Skip(1).Any(p => p.Type == TransactionType.MinerTransaction))
                return false;
            if (completely)
            {
                if (NextConsensus != Blockchain.GetConsensusAddress(Blockchain.Default.GetValidators(Transactions).ToArray()))
                    return false;
                foreach (Transaction tx in Transactions)
                    if (!tx.Verify(Transactions.Where(p => !p.Hash.Equals(tx.Hash)))) return false;
                Transaction tx_gen = Transactions.FirstOrDefault(p => p.Type == TransactionType.MinerTransaction);
                if (tx_gen?.Outputs.Sum(p => p.Value) != CalculateNetFee(Transactions)) return false;
            }
            return true;
        }
    }
}
