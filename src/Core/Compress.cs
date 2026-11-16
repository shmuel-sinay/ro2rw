using System;
using System.IO;
using System.IO.Compression;

namespace Ro2rwPc
{
    public enum RamdiskFormat { Raw, Gzip, Lz4Legacy, Lz4Frame, Unknown }

    public static class Compress
    {
        public static RamdiskFormat Detect(byte[] d)
        {
            if (d.Length >= 6 && d[0] == '0' && d[1] == '7' && d[2] == '0' && d[3] == '7' && d[4] == '0') return RamdiskFormat.Raw;
            if (d.Length >= 2 && d[0] == 0x1F && (d[1] == 0x8B || d[1] == 0x9E)) return RamdiskFormat.Gzip;
            if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) == 0x184C2102) return RamdiskFormat.Lz4Legacy;
            if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) == 0x184D2204) return RamdiskFormat.Lz4Frame;
            return RamdiskFormat.Unknown;
        }

        public static byte[] Decompress(byte[] d, RamdiskFormat f)
        {
            switch (f)
            {
                case RamdiskFormat.Raw: return d;
                case RamdiskFormat.Gzip:
                    {
                        var ms = new MemoryStream();
                        // concatenated gzip members are possible
                        using (var gz = new GZipStream(new MemoryStream(d), CompressionMode.Decompress)) gz.CopyTo(ms);
                        return ms.ToArray();
                    }
                case RamdiskFormat.Lz4Legacy: return Lz4LegacyDecode(d);
                case RamdiskFormat.Lz4Frame: return Lz4FrameDecode(d);
            }
            throw new NotSupportedException("פורמט דחיסה של ramdisk לא נתמך");
        }

        public static byte[] CompressAs(byte[] d, RamdiskFormat f)
        {
            switch (f)
            {
                case RamdiskFormat.Raw: return d;
                case RamdiskFormat.Gzip:
                    {
                        var ms = new MemoryStream();
                        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(d, 0, d.Length);
                        return ms.ToArray();
                    }
                case RamdiskFormat.Lz4Legacy: return Lz4LegacyEncode(d);
                case RamdiskFormat.Lz4Frame: return Lz4FrameEncode(d);
            }
            throw new NotSupportedException("פורמט דחיסה לא נתמך");
        }

        // ---------------------------------------------------------- LZ4 legacy
        const int LegacyBlock = 8 << 20;

        public static byte[] Lz4LegacyDecode(byte[] d)
        {
            var o = new MemoryStream();
            int p = 4;
            var buf = new byte[LegacyBlock];
            while (p + 4 <= d.Length)
            {
                uint sz = BitConverter.ToUInt32(d, p);
                if (sz == 0x184C2102) { p += 4; continue; }   // concatenated stream
                p += 4;
                if (sz == 0 || p + sz > d.Length) break;
                int n = Lz4.Decode(d, p, p + (int)sz, buf, 0, buf.Length);
                o.Write(buf, 0, n);
                p += (int)sz;
            }
            return o.ToArray();
        }

        public static byte[] Lz4LegacyEncode(byte[] d)
        {
            var o = new MemoryStream();
            o.Write(BitConverter.GetBytes(0x184C2102u), 0, 4);
            for (int p = 0; p < d.Length; p += LegacyBlock)
            {
                int n = Math.Min(LegacyBlock, d.Length - p);
                var c = Lz4Block(d, p, n);
                o.Write(BitConverter.GetBytes((uint)c.Length), 0, 4);
                o.Write(c, 0, c.Length);
            }
            return o.ToArray();
        }

        // ---------------------------------------------------------- LZ4 frame
        public static byte[] Lz4FrameDecode(byte[] d)
        {
            var o = new MemoryStream();
            int p = 0;
            while (p + 7 <= d.Length && BitConverter.ToUInt32(d, p) == 0x184D2204)
            {
                byte flg = d[p + 4], bd = d[p + 5];
                p += 6;
                bool contentSize = (flg & 0x08) != 0, blockChecksum = (flg & 0x10) != 0, contentChecksum = (flg & 0x04) != 0, dictId = (flg & 0x01) != 0;
                if (contentSize) p += 8;
                if (dictId) p += 4;
                p += 1; // header checksum
                int maxBlock = 64 << 10 << (2 * (((bd >> 4) & 7) - 4));
                var buf = new byte[maxBlock];
                while (true)
                {
                    uint bs = BitConverter.ToUInt32(d, p); p += 4;
                    if (bs == 0) break;
                    bool raw = (bs & 0x80000000) != 0;
                    int len = (int)(bs & 0x7FFFFFFF);
                    if (raw) o.Write(d, p, len);
                    else { int n = Lz4.Decode(d, p, p + len, buf, 0, buf.Length); o.Write(buf, 0, n); }
                    p += len;
                    if (blockChecksum) p += 4;
                }
                if (contentChecksum) p += 4;
            }
            return o.ToArray();
        }

        public static byte[] Lz4FrameEncode(byte[] d)
        {
            var o = new MemoryStream();
            o.Write(BitConverter.GetBytes(0x184D2204u), 0, 4);
            byte flg = 0x40 | 0x20; // version 01, block independence
            byte bd = 0x70;         // 4MB blocks
            o.WriteByte(flg); o.WriteByte(bd);
            o.WriteByte((byte)((XxHash32(new[] { flg, bd }, 0, 2) >> 8) & 0xFF));
            const int B = 4 << 20;
            for (int p = 0; p < d.Length; p += B)
            {
                int n = Math.Min(B, d.Length - p);
                var c = Lz4Block(d, p, n);
                if (c.Length >= n)
                {
                    o.Write(BitConverter.GetBytes((uint)n | 0x80000000u), 0, 4);
                    o.Write(d, p, n);
                }
                else
                {
                    o.Write(BitConverter.GetBytes((uint)c.Length), 0, 4);
                    o.Write(c, 0, c.Length);
                }
            }
            o.Write(new byte[4], 0, 4);
            return o.ToArray();
        }

        static uint XxHash32(byte[] b, int off, int len)
        {
            const uint P1 = 2654435761u, P2 = 2246822519u, P3 = 3266489917u, P4 = 668265263u, P5 = 374761393u;
            uint h = P5 + (uint)len;
            int p = off, end = off + len;
            while (p + 4 <= end) { h += BitConverter.ToUInt32(b, p) * P3; h = Rotl(h, 17) * P4; p += 4; }
            while (p < end) { h += b[p] * P5; h = Rotl(h, 11) * P1; p++; }
            h ^= h >> 15; h *= P2; h ^= h >> 13; h *= P3; h ^= h >> 16;
            return h;
        }
        static uint Rotl(uint x, int r) { return (x << r) | (x >> (32 - r)); }

        /// <summary>Greedy LZ4 block compressor (valid LZ4 block format).</summary>
        public static byte[] Lz4Block(byte[] src, int off, int len)
        {
            var o = new MemoryStream(len / 2 + 64);
            const int MinMatch = 4, LastLiterals = 5, MFLimit = 12, HashBits = 16;
            var table = new int[1 << HashBits];
            for (int i = 0; i < table.Length; i++) table[i] = -1;
            int end = off + len;
            int anchor = off, ip = off;
            int matchLimit = end - LastLiterals;
            int ipLimit = end - MFLimit;
            while (ip < ipLimit)
            {
                uint seq = BitConverter.ToUInt32(src, ip);
                int h = (int)((seq * 2654435761u) >> (32 - HashBits));
                int refp = table[h];
                table[h] = ip;
                if (refp < off || ip - refp > 65535 || BitConverter.ToUInt32(src, refp) != seq) { ip++; continue; }
                // extend match
                int ml = MinMatch;
                while (ip + ml < matchLimit && src[refp + ml] == src[ip + ml]) ml++;
                // emit sequence
                int lit = ip - anchor;
                int tokenPos = (int)o.Position;
                o.WriteByte(0);
                int token = 0;
                if (lit >= 15) { token = 0xF0; WriteLen(o, lit - 15); } else token = lit << 4;
                o.Write(src, anchor, lit);
                o.WriteByte((byte)(ip - refp)); o.WriteByte((byte)((ip - refp) >> 8));
                int mlc = ml - MinMatch;
                if (mlc >= 15) { token |= 0x0F; WriteLen(o, mlc - 15); } else token |= mlc;
                var cur = o.Position; o.Position = tokenPos; o.WriteByte((byte)token); o.Position = cur;
                ip += ml;
                anchor = ip;
                if (ip - 2 >= off && ip - 2 + 4 <= end)
                {
                    uint s2 = BitConverter.ToUInt32(src, ip - 2);
                    table[(int)((s2 * 2654435761u) >> (32 - HashBits))] = ip - 2;
                }
            }
            // last literals
            int last = end - anchor;
            if (last >= 15) { o.WriteByte(0xF0); WriteLen(o, last - 15); } else o.WriteByte((byte)(last << 4));
            o.Write(src, anchor, last);
            return o.ToArray();
        }

        static void WriteLen(Stream o, int n)
        {
            while (n >= 255) { o.WriteByte(255); n -= 255; }
            o.WriteByte((byte)n);
        }
    }

    /// <summary>Minimal .xz writer (LZMA2 stored chunks, CRC32 check) - readable by every xz decoder.</summary>
    public static class XzWriter
    {
        static readonly uint[] crcTable = MakeTable();
        static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
            return t;
        }
        public static uint Crc32(byte[] b, int off, int len)
        {
            uint c = 0xFFFFFFFF;
            for (int i = off; i < off + len; i++) c = crcTable[(c ^ b[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
        static void Varint(Stream s, ulong v) { while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; } s.WriteByte((byte)v); }

        public static byte[] Encode(byte[] data)
        {
            var o = new MemoryStream();
            // stream header
            var sh = new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00, 0x01 };
            o.Write(sh, 0, sh.Length);
            o.Write(BitConverter.GetBytes(Crc32(sh, 6, 2)), 0, 4);
            // block header (12 bytes)
            var bh = new byte[12];
            bh[0] = 2; bh[1] = 0x00; bh[2] = 0x21; bh[3] = 0x01; bh[4] = 0x00; // LZMA2, dict 4KiB (unused)
            BitConverter.GetBytes(Crc32(bh, 0, 8)).CopyTo(bh, 8);
            o.Write(bh, 0, 12);
            long start = o.Position;
            for (int p = 0; p < data.Length; p += 65536)
            {
                int n = Math.Min(65536, data.Length - p);
                o.WriteByte((byte)(p == 0 ? 0x01 : 0x02));
                o.WriteByte((byte)((n - 1) >> 8)); o.WriteByte((byte)((n - 1) & 0xFF));
                o.Write(data, p, n);
            }
            o.WriteByte(0x00);
            long comp = o.Position - start;
            while ((o.Position - start) % 4 != 0) o.WriteByte(0);
            o.Write(BitConverter.GetBytes(Crc32(data, 0, data.Length)), 0, 4);
            ulong unpadded = (ulong)(12 + comp + 4);
            // index
            var idx = new MemoryStream();
            idx.WriteByte(0); Varint(idx, 1); Varint(idx, unpadded); Varint(idx, (ulong)data.Length);
            while (idx.Length % 4 != 0) idx.WriteByte(0);
            var ib = idx.ToArray();
            o.Write(ib, 0, ib.Length);
            o.Write(BitConverter.GetBytes(Crc32(ib, 0, ib.Length)), 0, 4);
            // footer
            var ft = new byte[12];
            BitConverter.GetBytes((uint)((ib.Length + 4) / 4 - 1)).CopyTo(ft, 4);
            ft[8] = 0; ft[9] = 1; ft[10] = 0x59; ft[11] = 0x5A;
            BitConverter.GetBytes(Crc32(ft, 4, 6)).CopyTo(ft, 0);
            o.Write(ft, 0, 12);
            return o.ToArray();
        }
    }
}
