#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ValheimAdmin.Shared
{
    /// <summary>
    /// What a cartography table holds: the explored map and the pins players wrote to it. This is the
    /// uncompressed <c>Minimap.GetSharedMapData</c> ZPackage (BinaryWriter layout): int version,
    /// int cell count, one bool per cell (row by row, z first, like the game's minimap grid), then for
    /// version 2+ int pin count and per pin long owner, string name, 3 floats position, int type,
    /// bool checked and, for version 3+, string author.
    /// </summary>
    public sealed class SharedMapData
    {
        public sealed class Pin
        {
            public long OwnerId;
            public string Name;
            public float X;
            public float Y;
            public float Z;
            public int Type;
            public bool Checked;
            public string Author;
        }

        public int Version;
        /// <summary>Side of the explored grid in cells (the game's minimap texture size).</summary>
        public int Size;
        /// <summary>Explored cells, index row * Size + column; null when the grid is not square.</summary>
        public BitArray Explored;
        public readonly List<Pin> Pins = new List<Pin>();

        public static SharedMapData Parse(byte[] data)
        {
            using (var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
            {
                var result = new SharedMapData { Version = reader.ReadInt32() };
                int cells = reader.ReadInt32();
                if (cells < 0 || cells > data.Length) throw new InvalidDataException("Bad explored size " + cells);
                byte[] explored = reader.ReadBytes(cells);
                if (explored.Length != cells) throw new EndOfStreamException();
                int size = (int)Math.Round(Math.Sqrt(cells));
                if (size * size == cells)
                {
                    result.Size = size;
                    result.Explored = new BitArray(cells);
                    for (int i = 0; i < cells; i++)
                        if (explored[i] != 0) result.Explored[i] = true;
                }
                if (result.Version < 2) return result;

                int count = reader.ReadInt32();
                if (count < 0) throw new InvalidDataException("Bad pin count " + count);
                for (int i = 0; i < count; i++)
                {
                    var pin = new Pin
                    {
                        OwnerId = reader.ReadInt64(),
                        Name = reader.ReadString(),
                        X = reader.ReadSingle(),
                        Y = reader.ReadSingle(),
                        Z = reader.ReadSingle(),
                        Type = reader.ReadInt32(),
                        Checked = reader.ReadBoolean(),
                    };
                    if (result.Version >= 3) pin.Author = reader.ReadString();
                    result.Pins.Add(pin);
                }
                return result;
            }
        }
    }
}
