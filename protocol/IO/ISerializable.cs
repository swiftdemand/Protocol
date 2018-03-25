using System.IO;

namespace Neo.IO
{
    /// <summary>
    /// An interface for serialization
    /// </summary>
    public interface ISerializable
    {
        int Size { get; }

        void Serialize(BinaryWriter writer);

        void Deserialize(BinaryReader reader);
    }
}
