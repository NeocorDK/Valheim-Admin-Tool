using System;
using System.IO;
using System.IO.Compression;

namespace ValheimAdmin
{
    /// <summary>
    /// Minimal PNG writer (8-bit RGB or grey+alpha, filter "none"). Unity's EncodeToPNG needs a
    /// Texture2D and the main thread; this runs anywhere, including the map generator thread on a
    /// -nographics server.
    /// </summary>
    public static class Png
    {
        /// <summary>colorType 2 = RGB (3 bytes per pixel), 4 = grey + alpha (2 bytes per pixel). Rows top to bottom.</summary>
        public static byte[] Encode(byte[] pixels, int width, int height, int colorType)
        {
            int bpp = colorType == 2 ? 3 : colorType == 4 ? 2 : throw new ArgumentException("colorType");
            if (pixels.Length != width * height * bpp) throw new ArgumentException("pixel buffer size");

            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var ihdr = new byte[13];
                WriteInt(ihdr, 0, width);
                WriteInt(ihdr, 4, height);
                ihdr[8] = 8;
                ihdr[9] = (byte)colorType;
                Chunk(png, "IHDR", ihdr);

                Chunk(png, "IDAT", Zlib(pixels, width * bpp, height));
                Chunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static byte[] Zlib(byte[] pixels, int stride, int height)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
                uint a = 1, b = 0;
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    var filter = new byte[1];
                    for (int y = 0; y < height; y++)
                    {
                        deflate.Write(filter, 0, 1);
                        deflate.Write(pixels, y * stride, stride);
                        Adler(filter, 0, 1, ref a, ref b);
                        Adler(pixels, y * stride, stride, ref a, ref b);
                    }
                }
                var adler = new byte[4];
                WriteInt(adler, 0, (int)((b << 16) | a));
                output.Write(adler, 0, 4);
                return output.ToArray();
            }
        }

        private static void Adler(byte[] data, int offset, int count, ref uint a, ref uint b)
        {
            for (int i = offset; i < offset + count; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
        }

        private static void Chunk(Stream png, string type, byte[] data)
        {
            var header = new byte[8];
            WriteInt(header, 0, data.Length);
            for (int i = 0; i < 4; i++) header[4 + i] = (byte)type[i];
            png.Write(header, 0, 8);
            png.Write(data, 0, data.Length);
            uint crc = Crc(header, 4, 4, 0xFFFFFFFF);
            crc = Crc(data, 0, data.Length, crc) ^ 0xFFFFFFFF;
            var tail = new byte[4];
            WriteInt(tail, 0, (int)crc);
            png.Write(tail, 0, 4);
        }

        private static uint[] crcTable;

        private static uint Crc(byte[] data, int offset, int count, uint crc)
        {
            if (crcTable == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                crcTable = table;
            }
            for (int i = offset; i < offset + count; i++)
                crc = crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}
