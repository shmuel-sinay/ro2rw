using System;
using System.Collections.Generic;
using System.IO;

namespace Ro2rwPc
{
    /// <summary>Random-access read-only byte source.</summary>
    public interface IByteSource : IDisposable
    {
        long Length { get; }
        void Read(long offset, byte[] buffer, int index, int count);
    }

    public static class ByteSourceExt
    {
        public static byte[] ReadBytes(this IByteSource s, long offset, int count)
        {
            var b = new byte[count];
            s.Read(offset, b, 0, count);
            return b;
        }
    }

    public sealed class RawFileSource : IByteSource
    {
        readonly FileStream fs;
        public RawFileSource(string path)
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        }
        public long Length { get { return fs.Length; } }
        public void Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0) throw new IOException("Negative read offset");
            int done = 0;
            if (offset >= fs.Length) { Array.Clear(buffer, index, count); return; }
            fs.Position = offset;
            while (done < count)
            {
                int n = fs.Read(buffer, index + done, count - done);
                if (n <= 0) break;
                done += n;
            }
            if (done < count) Array.Clear(buffer, index + done, count - done);
        }
        public void Dispose() { fs.Dispose(); }
    }

    /// <summary>
    /// Android sparse image(s) exposed as a raw image without unpacking to disk.
    /// Several files (e.g. Xiaomi super.img.0, super.img.1 ...) are merged.
    /// </summary>
    public sealed class SparseSource : IByteSource
    {
        public const uint Magic = 0xED26FF3A;

        struct Chunk
        {
            public long OutStart;   // byte offset in raw image
            public long Length;     // bytes
            public int FileIndex;   // -1 = fill
            public long FileOffset;
            public uint Fill;
        }

        readonly List<FileStream> files = new List<FileStream>();
        readonly List<Chunk> chunks = new List<Chunk>();
        long length;

        public static bool IsSparse(string path)
        {
            using (var f = File.OpenRead(path))
            {
                if (f.Length < 28) return false;
                var b = new byte[4];
                f.Read(b, 0, 4);
                return BitConverter.ToUInt32(b, 0) == Magic;
            }
        }

        public SparseSource(IEnumerable<string> paths)
        {
            foreach (var p in paths) AddFile(p);
            chunks.Sort((a, b) => a.OutStart.CompareTo(b.OutStart));
            for (int i = 1; i < chunks.Count; i++)
                if (chunks[i].OutStart < chunks[i - 1].OutStart + chunks[i - 1].Length)
                    throw new InvalidDataException("Sparse files overlap - mixed images?");
        }

        void AddFile(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            int fi = files.Count;
            files.Add(fs);
            var h = new byte[28];
            if (fs.Read(h, 0, 28) != 28 || BitConverter.ToUInt32(h, 0) != Magic)
                throw new InvalidDataException("Not a sparse image: " + path);
            int fileHdr = BitConverter.ToUInt16(h, 8);
            int chunkHdr = BitConverter.ToUInt16(h, 10);
            uint blk = BitConverter.ToUInt32(h, 12);
            uint totalBlks = BitConverter.ToUInt32(h, 16);
            uint totalChunks = BitConverter.ToUInt32(h, 20);
            long total = (long)totalBlks * blk;
            if (total > length) length = total;
            long pos = fileHdr;
            long outBlk = 0;
            var ch = new byte[12];
            for (uint c = 0; c < totalChunks; c++)
            {
                fs.Position = pos;
                if (fs.Read(ch, 0, 12) != 12) throw new InvalidDataException("Truncated sparse image: " + path);
                ushort type = BitConverter.ToUInt16(ch, 0);
                uint sz = BitConverter.ToUInt32(ch, 4);
                uint totalSz = BitConverter.ToUInt32(ch, 8);
                long dataPos = pos + chunkHdr;
                long len = (long)sz * blk;
                switch (type)
                {
                    case 0xCAC1: // raw
                        chunks.Add(new Chunk { OutStart = outBlk * blk, Length = len, FileIndex = fi, FileOffset = dataPos });
                        break;
                    case 0xCAC2: // fill
                        {
                            var fb = new byte[4];
                            fs.Position = dataPos; fs.Read(fb, 0, 4);
                            uint fill = BitConverter.ToUInt32(fb, 0);
                            if (fill != 0)
                                chunks.Add(new Chunk { OutStart = outBlk * blk, Length = len, FileIndex = -1, Fill = fill });
                            break;
                        }
                    case 0xCAC3: // don't care
                    case 0xCAC4: // crc
                        break;
                    default:
                        throw new InvalidDataException("Unknown sparse chunk type 0x" + type.ToString("X4"));
                }
                if (type != 0xCAC4) outBlk += sz;
                pos += totalSz;
            }
        }

        public long Length { get { return length; } }

        int FindChunk(long off)
        {
            int lo = 0, hi = chunks.Count - 1, res = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (chunks[mid].OutStart <= off) { res = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return res;
        }

        public void Read(long offset, byte[] buffer, int index, int count)
        {
            Array.Clear(buffer, index, count);
            long end = offset + count;
            int ci = FindChunk(offset);
            if (ci < 0) ci = 0;
            for (; ci < chunks.Count; ci++)
            {
                var c = chunks[ci];
                if (c.OutStart >= end) break;
                long cEnd = c.OutStart + c.Length;
                if (cEnd <= offset) continue;
                long s = Math.Max(offset, c.OutStart);
                long e = Math.Min(end, cEnd);
                int dst = index + (int)(s - offset);
                int n = (int)(e - s);
                if (c.FileIndex < 0)
                {
                    var fb = BitConverter.GetBytes(c.Fill);
                    for (int i = 0; i < n; i++) buffer[dst + i] = fb[(int)((s - c.OutStart + i) & 3)];
                }
                else
                {
                    var fs = files[c.FileIndex];
                    fs.Position = c.FileOffset + (s - c.OutStart);
                    int got = 0;
                    while (got < n)
                    {
                        int r = fs.Read(buffer, dst + got, n - got);
                        if (r <= 0) throw new EndOfStreamException("Truncated sparse data");
                        got += r;
                    }
                }
            }
        }

        public void Dispose() { foreach (var f in files) f.Dispose(); }
    }

    /// <summary>A view over a set of linear extents of another source (an LP partition).</summary>
    public sealed class ExtentSource : IByteSource
    {
        public struct Ext { public long Start; public long Length; public long Target; public bool Zero; }
        readonly IByteSource baseSrc;
        readonly List<Ext> exts;
        readonly long length;

        public ExtentSource(IByteSource baseSrc, List<Ext> exts)
        {
            this.baseSrc = baseSrc; this.exts = exts;
            foreach (var e in exts) length = Math.Max(length, e.Start + e.Length);
        }
        public long Length { get { return length; } }
        public void Read(long offset, byte[] buffer, int index, int count)
        {
            long end = offset + count;
            if (offset >= length || end > length) Array.Clear(buffer, index, count);
            foreach (var e in exts)
            {
                long eEnd = e.Start + e.Length;
                if (eEnd <= offset || e.Start >= end) continue;
                long s = Math.Max(offset, e.Start), en = Math.Min(end, eEnd);
                int dst = index + (int)(s - offset), n = (int)(en - s);
                if (e.Zero) Array.Clear(buffer, dst, n);
                else baseSrc.Read(e.Target + (s - e.Start), buffer, dst, n);
            }
        }
        public void Dispose() { }
    }

    /// <summary>Small block cache for many tiny metadata reads.</summary>
    public sealed class CachedReader
    {
        readonly IByteSource src;
        readonly Dictionary<long, byte[]> cache = new Dictionary<long, byte[]>();
        const int B = 4096;
        public CachedReader(IByteSource s) { src = s; }
        public IByteSource Source { get { return src; } }

        byte[] Block(long blk)
        {
            byte[] b;
            if (cache.TryGetValue(blk, out b)) return b;
            if (cache.Count > 4096) cache.Clear();
            b = new byte[B];
            src.Read(blk * B, b, 0, B);
            cache[blk] = b;
            return b;
        }

        public void Read(long off, byte[] dst, int idx, int count)
        {
            while (count > 0)
            {
                long blk = off / B; int o = (int)(off % B);
                int n = Math.Min(count, B - o);
                Buffer.BlockCopy(Block(blk), o, dst, idx, n);
                off += n; idx += n; count -= n;
            }
        }
        public byte[] Bytes(long off, int count) { var b = new byte[count]; Read(off, b, 0, count); return b; }
        public ushort U16(long off) { return BitConverter.ToUInt16(Bytes(off, 2), 0); }
        public uint U32(long off) { return BitConverter.ToUInt32(Bytes(off, 4), 0); }
        public ulong U64(long off) { return BitConverter.ToUInt64(Bytes(off, 8), 0); }
        public byte U8(long off) { return Bytes(off, 1)[0]; }
    }
}
