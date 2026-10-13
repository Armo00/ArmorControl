using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Managed-only worker. Unity's RGB24 ReadPixels buffer is bottom-up; PNG is top-down.
    internal static class MapPngEncoder
    {
        private static readonly uint[] CrcTable = CreateCrcTable();

        internal static byte[] Encode(byte[] rgb, int width, int height)
        {
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || rgb == null || rgb.Length != checked(width * height * 3))
                throw new ArgumentException("Invalid map pixels.");
            byte[] compressed;
            using (var data = new MemoryStream())
            {
                data.WriteByte(0x78); data.WriteByte(0x01); // RFC 1950 wrapper, fastest DEFLATE.
                uint a = 1, b = 0;
                using (var deflate = new DeflateStream(data, CompressionLevel.Fastest, true))
                {
                    for (int y = height - 1; y >= 0; y--)
                    {
                        deflate.WriteByte(0); b = (b + a) % 65521; // PNG row filter: none.
                        int start = y * width * 3, count = width * 3;
                        deflate.Write(rgb, start, count);
                        for (int i = start; i < start + count; i++) { a = (a + rgb[i]) % 65521; b = (b + a) % 65521; }
                    }
                }
                WriteUInt(data, (b << 16) | a); compressed = data.ToArray();
            }
            using (var png = new MemoryStream())
            {
                byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 }; png.Write(signature, 0, signature.Length);
                using (var header = new MemoryStream())
                {
                    WriteUInt(header, (uint)width); WriteUInt(header, (uint)height);
                    header.WriteByte(8); header.WriteByte(2); header.WriteByte(0); header.WriteByte(0); header.WriteByte(0);
                    Chunk(png, "IHDR", header.ToArray());
                }
                Chunk(png, "IDAT", compressed); Chunk(png, "IEND", new byte[0]); return png.ToArray();
            }
        }

        private static void Chunk(Stream stream, string type, byte[] data)
        {
            WriteUInt(stream, (uint)data.Length); byte[] name = Encoding.ASCII.GetBytes(type); stream.Write(name, 0, 4); stream.Write(data, 0, data.Length);
            uint crc = 0xffffffff;
            foreach (byte value in name) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
            foreach (byte value in data) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
            WriteUInt(stream, crc ^ 0xffffffff);
        }

        private static void WriteUInt(Stream stream, uint value)
        { stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) { uint value = i; for (int j = 0; j < 8; j++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1; table[i] = value; }
            return table;
        }
    }
}
