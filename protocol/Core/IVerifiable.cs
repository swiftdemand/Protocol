using Neo.IO;
using Neo.VM;
using System.IO;

namespace Neo.Core
{
    /// <summary>
    /// An interface for data that needs to be signed
    /// </summary>
    public interface IVerifiable : ISerializable, IScriptContainer
    {
        /// <summary>
        /// Scripts used to validate the object
        /// </summary>
        Witness[] Scripts { get; set; }

        /// <summary>
        /// Deserialize unsigned data
        /// </summary>
        void DeserializeUnsigned(BinaryReader reader);

        /// <summary>
        /// Return script hashes that need verification
        /// </summary>
        UInt160[] GetScriptHashesForVerifying();

        /// <summary>
        /// Serialize unsigned data
        /// </summary>
        void SerializeUnsigned(BinaryWriter writer);
    }
}
