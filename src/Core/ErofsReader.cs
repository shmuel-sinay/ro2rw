using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Ro2rwPc
{
    /// <summary>Read-only EROFS parser (plain, inline, chunked, LZ4/LZ4HC/DEFLATE compressed, ztailpacking, fragments, dedupe).</summary>
    public sealed class ErofsReader : IFsReader
    {
        public const uint Magic = 0xE0F5E1E2;
        const int SbOff = 1024;
        const uint NullAddr = 0xFFFFFFFF;

        // feature bits
        const uint INCOMPAT_ZERO_PADDING = 0x1, INCOMPAT_BIG_PCLUSTER = 0x2, INCOMPAT_CHUNKED = 0x4, INCOMPAT_DEVICE_TABLE = 0x8,
            INCOMPAT_ZTAILPACKING = 0x10, INCOMPAT_FRAGMENTS = 0x20, INCOMPAT_XATTR_PREFIXES = 0x40, INCOMPAT_48BIT = 0x80, INCOMPAT_METABOX = 0x100;

        const int LAYOUT_FLAT_PLAIN = 0, LAYOUT_COMPRESSED_FULL = 1, LAYOUT_FLAT_INLINE = 2, LAYOUT_COMPRESSED_COMPACT = 3, LAYOUT_CHUNK = 4;
        const int ADV_COMPACTED_2B = 0x1, ADV_BIG_PCLUSTER_1 = 0x2, ADV_BIG_PCLUSTER_2 = 0x4, ADV_INLINE_PCLUSTER = 0x8, ADV_INTERLACED = 0x10, ADV_FRAGMENT_PCLUSTER = 0x20;
        const int LC_PLAIN = 0, LC_HEAD1 = 1, LC_NONHEAD = 2, LC_HEAD2 = 3;
        const int D0_CBLKCNT = 1 << 11;

        readonly CachedReader r;
        readonly IByteSource src;
        int blkbits; uint blksz;
        long metaBase, xattrBase;
        uint featIncompat;
        ulong rootNid, packedNid;
        bool hasPacked;
        long buildTime;
        List<string> longPrefixes = new List<string>();
        List<int> longPrefixBase = new List<int>();
        Inode packed;

        public string FsName { get { return "EROFS"; } }

        public static bool Detect(IByteSource s)
        {
            if (s.Length < SbOff + 128) return false;
            return BitConverter.ToUInt32(s.ReadBytes(SbOff, 4), 0) == Magic;
        }

        public ErofsReader(IByteSource s)
        {
            src = s; r = new CachedReader(s);
            var sb = r.Bytes(SbOff, 128);
            if (BitConverter.ToUInt32(sb, 0) != Magic) throw new InvalidDataException("Not EROFS");
            blkbits = sb[12];
            if (blkbits < 9 || blkbits > 16) throw new InvalidDataException("EROFS: bad block size");
            blksz = 1u << blkbits;
            rootNid = BitConverter.ToUInt16(sb, 14);
            buildTime = (long)BitConverter.ToUInt64(sb, 24);
            metaBase = (long)BitConverter.ToUInt32(sb, 40) << blkbits;
            xattrBase = (long)BitConverter.ToUInt32(sb, 44) << blkbits;
            featIncompat = BitConverter.ToUInt32(sb, 80);
            ushort extraDevices = BitConverter.ToUInt16(sb, 86);
            int xattrPrefixCount = sb[91];
            uint xattrPrefixStart = BitConverter.ToUInt32(sb, 92);
            packedNid = BitConverter.ToUInt64(sb, 96);
            if ((featIncompat & INCOMPAT_48BIT) != 0)
                throw new NotSupportedException("EROFS עם תכונת 48bit עדיין לא נתמך");
            if ((featIncompat & INCOMPAT_METABOX) != 0)
                throw new NotSupportedException("EROFS עם metabox עדיין לא נתמך");
            if ((featIncompat & INCOMPAT_DEVICE_TABLE) != 0 && extraDevices > 0)
                throw new NotSupportedException("EROFS עם התקנים נוספים לא נתמך");
            hasPacked = (featIncompat & INCOMPAT_FRAGMENTS) != 0 && packedNid != 0;
            if (hasPacked) packed = LoadInode(packedNid);

            if ((featIncompat & INCOMPAT_XATTR_PREFIXES) != 0 && xattrPrefixCount > 0)
            {
                long pos = (long)xattrPrefixStart << 2;
                for (int i = 0; i < xattrPrefixCount; i++)
                {
                    byte[] lenb = hasPacked ? ReadRange(packed, pos, 2) : r.Bytes(pos, 2);
                    int len = BitConverter.ToUInt16(lenb, 0);
                    byte[] body = hasPacked ? ReadRange(packed, pos + 2, len) : r.Bytes(pos + 2, len);
                    longPrefixBase.Add(body[0]);
                    longPrefixes.Add(Encoding.UTF8.GetString(body, 1, len - 1));
                    pos += (2 + len + 3) & ~3;
                }
            }
        }

        sealed class Inode
        {
            public ulong Nid; public long Pos; public bool Extended; public int Layout;
            public uint Mode; public long Size; public uint Uid, Gid; public long Mtime; public uint Nlink;
            public uint IU; public int XattrIcount; public int InodeIsize; public int XattrIsize;
            // compressed info
            public List<ZExt> Exts;
            public bool WholeFragment; public long FragmentOff;
        }

        struct ZExt { public long La; public long Llen; public long Pa; public int Plen; public int Kind; public int Alg; public long FragOff; }
        const int K_COMP = 0, K_PLAIN = 1, K_INTERLACED = 2, K_FRAGMENT = 3, K_HOLE = 4;

        Inode LoadInode(ulong nid)
        {
            var ino = new Inode { Nid = nid, Pos = metaBase + (long)nid * 32 };
            var b = r.Bytes(ino.Pos, 64);
            ushort fmt = BitConverter.ToUInt16(b, 0);
            ino.Extended = (fmt & 1) != 0;
            ino.Layout = (fmt >> 1) & 7;
            ino.XattrIcount = BitConverter.ToUInt16(b, 2);
            ino.Mode = BitConverter.ToUInt16(b, 4);
            if (ino.Extended)
            {
                ino.InodeIsize = 64;
                ino.Size = (long)BitConverter.ToUInt64(b, 8);
                ino.IU = BitConverter.ToUInt32(b, 16);
                ino.Uid = BitConverter.ToUInt32(b, 24);
                ino.Gid = BitConverter.ToUInt32(b, 28);
                ino.Mtime = (long)BitConverter.ToUInt64(b, 32);
                ino.Nlink = BitConverter.ToUInt32(b, 44);
            }
            else
            {
                ino.InodeIsize = 32;
                ino.Nlink = BitConverter.ToUInt16(b, 6);
                ino.Size = BitConverter.ToUInt32(b, 8);
                ino.IU = BitConverter.ToUInt32(b, 16);
                ino.Uid = BitConverter.ToUInt16(b, 24);
                ino.Gid = BitConverter.ToUInt16(b, 26);
                ino.Mtime = buildTime;
            }
            ino.XattrIsize = ino.XattrIcount == 0 ? 0 : 12 + (ino.XattrIcount - 1) * 4;
            return ino;
        }

        long InlinePos(Inode i) { return i.Pos + i.InodeIsize + i.XattrIsize; }

        // ------------------------------------------------------------ xattrs
        List<Xattr> ReadXattrs(Inode ino)
        {
            var list = new List<Xattr>();
            if (ino.XattrIcount == 0) return list;
            long p = ino.Pos + ino.InodeIsize;
            int shared = r.U8(p + 4);
            long end = p + ino.XattrIsize;
            long q = p + 12;
            for (int i = 0; i < shared; i++)
            {
                uint id = r.U32(q); q += 4;
                long dummy;
                list.Add(ReadEntry(xattrBase + (long)id * 4, out dummy));
            }
            while (q + 4 <= end)
            {
                long next;
                var x = ReadEntry(q, out next);
                if (x == null) break;
                list.Add(x);
                q = next;
            }
            return list;
        }

        Xattr ReadEntry(long pos, out long next)
        {
            int nameLen = r.U8(pos), idx = r.U8(pos + 1);
            int valSize = r.U16(pos + 2);
            next = pos + ((4 + nameLen + valSize + 3) & ~3);
            if (nameLen == 0 && idx == 0 && valSize == 0) return null;
            string name = Encoding.UTF8.GetString(r.Bytes(pos + 4, nameLen));
            string prefix;
            if ((idx & 0x80) != 0)
            {
                int li = idx & 0x7F;
                if (li >= longPrefixes.Count) throw new InvalidDataException("EROFS: bad long xattr prefix");
                prefix = FsUtil.PrefixFor(longPrefixBase[li]) + longPrefixes[li];
            }
            else prefix = FsUtil.PrefixFor(idx);
            return new Xattr(prefix + name, r.Bytes(pos + 4 + nameLen, valSize));
        }

        // ------------------------------------------------------------ data access
        /// <summary>Reads a byte range of an inode's content.</summary>
        byte[] ReadRange(Inode ino, long off, int count)
        {
            var res = new byte[count];
            int got = 0;
            ReadInto(ino, off, count, (b, o, n) => { Buffer.BlockCopy(b, o, res, got, n); got += n; });
            return res;
        }

        void ReadInto(Inode ino, long off, long count, Action<byte[], int, int> sink)
        {
            if (count <= 0) return;
            if (off + count > ino.Size) throw new InvalidDataException("EROFS: read beyond EOF");
            switch (ino.Layout)
            {
                case LAYOUT_FLAT_PLAIN:
                case LAYOUT_FLAT_INLINE:
                    ReadFlat(ino, off, count, sink); break;
                case LAYOUT_CHUNK:
                    ReadChunked(ino, off, count, sink); break;
                case LAYOUT_COMPRESSED_FULL:
                case LAYOUT_COMPRESSED_COMPACT:
                    ReadCompressed(ino, off, count, sink); break;
                default:
                    throw new NotSupportedException("EROFS: unknown data layout " + ino.Layout);
            }
        }

        void ReadFlat(Inode ino, long off, long count, Action<byte[], int, int> sink)
        {
            long blocksPart = ino.Size;
            if (ino.Layout == LAYOUT_FLAT_INLINE) blocksPart = ino.Size - (ino.Size % blksz);
            long baseAddr = (long)ino.IU << blkbits;
            var buf = new byte[1 << 20];
            while (count > 0)
            {
                int n;
                if (off < blocksPart)
                {
                    n = (int)Math.Min(Math.Min(count, buf.Length), blocksPart - off);
                    src.Read(baseAddr + off, buf, 0, n);
                }
                else
                {
                    n = (int)Math.Min(count, buf.Length);
                    r.Read(InlinePos(ino) + (off - blocksPart), buf, 0, n);
                }
                sink(buf, 0, n);
                off += n; count -= n;
            }
        }

        void ReadChunked(Inode ino, long off, long count, Action<byte[], int, int> sink)
        {
            int chunkbits = blkbits + (int)(ino.IU & 0x1F);
            bool indexes = (ino.IU & 0x20) != 0;
            int unit = indexes ? 8 : 4;
            long tbl = (InlinePos(ino) + unit - 1) / unit * unit;
            long csz = 1L << chunkbits;
            var buf = new byte[1 << 20];
            while (count > 0)
            {
                long ci = off >> chunkbits;
                long within = off & (csz - 1);
                int n = (int)Math.Min(Math.Min(count, buf.Length), csz - within);
                uint blk = indexes ? r.U32(tbl + ci * 8 + 4) : r.U32(tbl + ci * 4);
                if (indexes && r.U16(tbl + ci * 8 + 2) != 0) throw new NotSupportedException("EROFS: multi-device chunks");
                if (blk == NullAddr) Array.Clear(buf, 0, n);
                else src.Read(((long)blk << blkbits) + within, buf, 0, n);
                sink(buf, 0, n);
                off += n; count -= n;
            }
        }

        // --------------------------------------------------------- compressed
        struct LCluster { public int Type; public int ClusterOfs; public long Pblk; public int CBlks; public int Delta0; public long NextPackOff; }

        void InitCompressed(Inode ino)
        {
            if (ino.Exts != null) return;
            long hpos = (InlinePos(ino) + 7) & ~7L;
            var h = r.Bytes(hpos, 8);
            uint fragOff = BitConverter.ToUInt32(h, 0);
            int idataSize = BitConverter.ToUInt16(h, 2);
            int advise = BitConverter.ToUInt16(h, 4);
            int algs = h[6];
            int cbits = h[7];
            ino.Exts = new List<ZExt>();
            if ((cbits & 0x80) != 0)
            {
                ino.WholeFragment = true;
                ino.FragmentOff = fragOff;
                ino.Exts.Add(new ZExt { La = 0, Llen = ino.Size, Kind = K_FRAGMENT, FragOff = fragOff });
                return;
            }
            int lbits = blkbits + (cbits & 7);
            long lsz = 1L << lbits;
            long total = (ino.Size + lsz - 1) >> lbits;
            bool compact = ino.Layout == LAYOUT_COMPRESSED_COMPACT;
            bool big1 = (advise & ADV_BIG_PCLUSTER_1) != 0, big2 = (advise & ADV_BIG_PCLUSTER_2) != 0;
            bool ztail = (advise & ADV_INLINE_PCLUSTER) != 0, frag = (advise & ADV_FRAGMENT_PCLUSTER) != 0;
            bool interlaced = (advise & ADV_INTERLACED) != 0;

            long ebase = hpos + 8;
            int c4init = 0, c2b = 0;
            if (compact)
            {
                c4init = (int)((32 - ebase % 32) / 4);
                if (c4init == 8) c4init = 0;
                if ((advise & ADV_COMPACTED_2B) != 0 && c4init < total) c2b = (int)((total - c4init) / 16 * 16);
            }

            Func<long, LCluster> load = lcn =>
            {
                if (!compact) return LoadFull(ebase + 8 + lcn * 8, lbits);
                long pos = ebase; long l = lcn; int ash;
                if (l < c4init) ash = 2;
                else
                {
                    pos += c4init * 4; l -= c4init;
                    if (l < c2b) ash = 1;
                    else { pos += (long)c2b * 2; l -= c2b; ash = 2; }
                }
                pos += l << ash;
                return LoadCompact(pos, ash, lbits, big1);
            };

            // collect heads
            var heads = new List<long>(); var types = new List<int>(); var pblks = new List<long>(); var lcns = new List<long>();
            LCluster lastLc = default(LCluster);
            for (long lcn = 0; lcn < total; lcn++)
            {
                var lc = load(lcn);
                if (lcn == total - 1) lastLc = lc;
                if (lc.Type == LC_NONHEAD) continue;
                heads.Add((lcn << lbits) + lc.ClusterOfs);
                types.Add(lc.Type); pblks.Add(lc.Pblk); lcns.Add(lcn);
            }
            if (heads.Count == 0 || heads[0] != 0) throw new InvalidDataException("EROFS: bad compressed index");
            long idataOff = lastLc.NextPackOff;
            long fragFull = fragOff;
            for (int i = 0; i < heads.Count; i++)
            {
                long la = heads[i];
                long end = i + 1 < heads.Count ? heads[i + 1] : ino.Size;
                if (la >= ino.Size) break;
                end = Math.Min(end, ino.Size);
                var e = new ZExt { La = la, Llen = end - la };
                int t = types[i];
                bool isTail = i == heads.Count - 1 || heads[i + 1] >= ino.Size;
                if (isTail && frag)
                {
                    if (!compact) fragFull |= pblks[i] << 32;
                    e.Kind = K_FRAGMENT; e.FragOff = fragFull;
                }
                else
                {
                    int cblks = 1;
                    long lcn = lcns[i];
                    bool big = (t == LC_HEAD1 && big1) || ((t == LC_HEAD2 || t == LC_PLAIN) && big2);
                    if (big && ((lcn + 1) << lbits) < ino.Size)
                    {
                        var n = load(lcn + 1);
                        if (n.Type == LC_NONHEAD)
                        {
                            if (n.CBlks <= 0) throw new InvalidDataException("EROFS: missing CBLKCNT");
                            cblks = n.CBlks;
                        }
                    }
                    if (isTail && ztail) { e.Pa = idataOff; e.Plen = idataSize; e.Kind = -1; }
                    else { e.Pa = pblks[i] << blkbits; e.Plen = cblks << blkbits; }
                    if (t == LC_PLAIN) e.Kind = (e.Kind == -1 ? 100 : 0) + (interlaced ? K_INTERLACED : K_PLAIN);
                    else
                    {
                        e.Alg = t == LC_HEAD1 ? (algs & 0xF) : (algs >> 4);
                        e.Kind = (e.Kind == -1 ? 100 : 0) + K_COMP;
                    }
                }
                ino.Exts.Add(e);
            }
        }

        LCluster LoadFull(long pos, int lbits)
        {
            var b = r.Bytes(pos, 8);
            int adv = BitConverter.ToUInt16(b, 0);
            var lc = new LCluster { Type = adv & 3, NextPackOff = pos + 8 };
            if (lc.Type == LC_NONHEAD)
            {
                lc.ClusterOfs = 1 << lbits;
                int d0 = BitConverter.ToUInt16(b, 4);
                if ((d0 & D0_CBLKCNT) != 0) { lc.CBlks = d0 & ~D0_CBLKCNT; lc.Delta0 = 1; }
                else lc.Delta0 = d0;
            }
            else
            {
                lc.ClusterOfs = BitConverter.ToUInt16(b, 2);
                lc.Pblk = BitConverter.ToUInt32(b, 4);
            }
            return lc;
        }

        static uint DecodeBits(byte[] pack, int lobits, int bitpos, out int type)
        {
            int byteOff = bitpos / 8;
            uint v = 0;
            for (int k = 0; k < 4 && byteOff + k < pack.Length; k++) v |= (uint)pack[byteOff + k] << (8 * k);
            v >>= (bitpos & 7);
            type = (int)((v >> lobits) & 3);
            return v & ((1u << lobits) - 1);
        }

        LCluster LoadCompact(long pos, int ash, int lbits, bool big)
        {
            int vcnt;
            if (ash == 2 && lbits <= 14) vcnt = 2;
            else if (ash == 1 && lbits <= 12) vcnt = 16;
            else throw new NotSupportedException("EROFS: unsupported compact index");
            int packSize = vcnt << ash;
            long baseAbs = pos / packSize * packSize;
            var lc = new LCluster { NextPackOff = baseAbs + packSize };
            int lobits = Math.Max(lbits, 12);
            int encodebits = (packSize - 4) * 8 / vcnt;
            var pack = r.Bytes(baseAbs, packSize);
            int i = (int)((pos - baseAbs) >> ash);
            int type;
            uint lo = DecodeBits(pack, lobits, encodebits * i, out type);
            lc.Type = type;
            if (type == LC_NONHEAD)
            {
                lc.ClusterOfs = 1 << lbits;
                if ((lo & D0_CBLKCNT) != 0)
                {
                    lc.CBlks = (int)(lo & ~(uint)D0_CBLKCNT); lc.Delta0 = 1;
                    return lc;
                }
                if (i + 1 != vcnt) { lc.Delta0 = (int)lo; return lc; }
                int t2;
                uint lo2 = DecodeBits(pack, lobits, encodebits * (i - 1), out t2);
                if (t2 != LC_NONHEAD) lo2 = 0; else if ((lo2 & D0_CBLKCNT) != 0) lo2 = 1;
                lc.Delta0 = (int)lo2 + 1;
                return lc;
            }
            lc.ClusterOfs = (int)lo;
            int nblk;
            if (!big)
            {
                nblk = 1;
                while (i > 0)
                {
                    --i;
                    int t; uint l = DecodeBits(pack, lobits, encodebits * i, out t);
                    if (t == LC_NONHEAD) i -= (int)l;
                    if (i >= 0) ++nblk;
                }
            }
            else
            {
                nblk = 0;
                while (i > 0)
                {
                    --i;
                    int t; uint l = DecodeBits(pack, lobits, encodebits * i, out t);
                    if (t == LC_NONHEAD)
                    {
                        if ((l & D0_CBLKCNT) != 0) { --i; nblk += (int)(l & ~(uint)D0_CBLKCNT); continue; }
                        if (l <= 1) throw new InvalidDataException("EROFS: bad compact index");
                        i -= (int)l - 2;
                        continue;
                    }
                    ++nblk;
                }
            }
            lc.Pblk = BitConverter.ToUInt32(pack, packSize - 4) + (long)nblk;
            return lc;
        }

        // cache of last decoded extent
        Inode cacheIno; int cacheIdx = -1; byte[] cacheData;

        byte[] DecodeExtent(Inode ino, int idx)
        {
            if (cacheIno == ino && cacheIdx == idx) return cacheData;
            var e = ino.Exts[idx];
            var outb = new byte[e.Llen];
            int kind = e.Kind % 100;
            bool inl = e.Kind >= 100;
            if (kind == K_FRAGMENT)
            {
                if (!hasPacked) throw new InvalidDataException("EROFS: fragment without packed inode");
                int got = 0;
                ReadInto(packed, e.FragOff, e.Llen, (b, o, n) => { Buffer.BlockCopy(b, o, outb, got, n); got += n; });
            }
            else
            {
                byte[] inb = inl ? r.Bytes(e.Pa, e.Plen) : src.ReadBytes(e.Pa, e.Plen);
                if (kind == K_PLAIN)
                {
                    Buffer.BlockCopy(inb, 0, outb, 0, (int)Math.Min(e.Llen, inb.Length));
                }
                else if (kind == K_INTERLACED)
                {
                    int count = (int)e.Llen;
                    int skip = (int)(e.La & (blksz - 1));
                    if (inl) skip = 0;
                    int right = Math.Min((int)blksz - skip, count);
                    right = Math.Min(right, inb.Length - skip);
                    Buffer.BlockCopy(inb, skip, outb, 0, right);
                    Buffer.BlockCopy(inb, 0, outb, right, count - right);
                }
                else
                {
                    int start = 0;
                    if ((featIncompat & INCOMPAT_ZERO_PADDING) != 0 || e.Alg == 2)
                        while (start < inb.Length && inb[start] == 0) start++;
                    switch (e.Alg)
                    {
                        case 0: // lz4
                            {
                                int n = Lz4.Decode(inb, start, inb.Length, outb, 0, outb.Length);
                                if (n != outb.Length) throw new InvalidDataException("EROFS: LZ4 short output");
                                break;
                            }
                        case 2: // deflate
                            using (var ds = new DeflateStream(new MemoryStream(inb, start, inb.Length - start), CompressionMode.Decompress))
                            {
                                int got = 0;
                                while (got < outb.Length)
                                {
                                    int n = ds.Read(outb, got, outb.Length - got);
                                    if (n <= 0) throw new InvalidDataException("EROFS: deflate short output");
                                    got += n;
                                }
                            }
                            break;
                        case 1: throw new NotSupportedException("EROFS עם דחיסת LZMA עדיין לא נתמך");
                        case 3: throw new NotSupportedException("EROFS עם דחיסת ZSTD עדיין לא נתמך");
                        default: throw new NotSupportedException("EROFS: unknown compression " + e.Alg);
                    }
                }
            }
            cacheIno = ino; cacheIdx = idx; cacheData = outb;
            return outb;
        }

        void ReadCompressed(Inode ino, long off, long count, Action<byte[], int, int> sink)
        {
            InitCompressed(ino);
            // find first extent
            int lo = 0, hi = ino.Exts.Count - 1, idx = 0;
            while (lo <= hi) { int m = (lo + hi) >> 1; if (ino.Exts[m].La <= off) { idx = m; lo = m + 1; } else hi = m - 1; }
            while (count > 0)
            {
                var e = ino.Exts[idx];
                var data = DecodeExtent(ino, idx);
                int within = (int)(off - e.La);
                int n = (int)Math.Min(count, e.Llen - within);
                sink(data, within, n);
                off += n; count -= n; idx++;
            }
        }

        // ------------------------------------------------------------ tree
        byte[] ReadAll(Inode ino)
        {
            if (ino.Size > int.MaxValue) throw new InvalidDataException("too big");
            return ReadRange(ino, 0, (int)ino.Size);
        }

        public FsEntry ReadTree()
        {
            var inodes = new Dictionary<ulong, FsInode>();
            var root = new FsEntry { Name = "", Inode = Convert(LoadInode(rootNid)), Children = new List<FsEntry>() };
            inodes[rootNid] = root.Inode;
            var stack = new Stack<KeyValuePair<FsEntry, ulong>>();
            stack.Push(new KeyValuePair<FsEntry, ulong>(root, rootNid));
            while (stack.Count > 0)
            {
                var kv = stack.Pop();
                var dirIno = (Inode)kv.Key.Inode.SourceRef;
                foreach (var de in ReadDir(dirIno))
                {
                    if (de.Key == "." || de.Key == "..") continue;
                    FsInode fi;
                    bool isNewDir = false;
                    if (!inodes.TryGetValue(de.Value, out fi))
                    {
                        fi = Convert(LoadInode(de.Value));
                        inodes[de.Value] = fi;
                        isNewDir = fi.Type == NodeType.Dir;
                    }
                    else if (fi.Type == NodeType.Dir) throw new InvalidDataException("EROFS: directory hard link loop");
                    var ent = new FsEntry { Name = de.Key, Inode = fi, Parent = kv.Key };
                    if (fi.Type == NodeType.Dir) ent.Children = new List<FsEntry>();
                    kv.Key.Children.Add(ent);
                    if (isNewDir) stack.Push(new KeyValuePair<FsEntry, ulong>(ent, de.Value));
                }
            }
            FsUtil.CountLinks(root);
            return root;
        }

        FsInode Convert(Inode i)
        {
            var f = new FsInode
            {
                Mode = i.Mode, Uid = i.Uid, Gid = i.Gid, Mtime = i.Mtime, Size = i.Size,
                Type = FsUtil.TypeFromMode(i.Mode), SourceRef = i
            };
            f.Xattrs = ReadXattrs(i);
            if (f.Type == NodeType.Symlink)
            {
                f.LinkTarget = Encoding.UTF8.GetString(ReadAll(i));
            }
            else if (f.Type == NodeType.CharDev || f.Type == NodeType.BlockDev)
            {
                uint dev = i.IU; // new_encode_dev
                f.RdevMajor = (dev & 0xfff00) >> 8;
                f.RdevMinor = (dev & 0xff) | ((dev >> 12) & 0xfff00);
                f.Size = 0;
            }
            if (f.Type != NodeType.File) { if (f.Type != NodeType.Symlink) f.Size = 0; }
            return f;
        }

        List<KeyValuePair<string, ulong>> ReadDir(Inode d)
        {
            var res = new List<KeyValuePair<string, ulong>>();
            var data = ReadAll(d);
            long size = data.Length;
            for (long blk = 0; blk < size; blk += blksz)
            {
                int bl = (int)Math.Min(blksz, size - blk);
                int b0 = (int)blk;
                int first = BitConverter.ToUInt16(data, b0 + 8);
                int n = first / 12;
                if (n <= 0 || first > bl) throw new InvalidDataException("EROFS: bad directory block");
                for (int k = 0; k < n; k++)
                {
                    int eo = b0 + k * 12;
                    ulong nid = BitConverter.ToUInt64(data, eo);
                    int no = BitConverter.ToUInt16(data, eo + 8);
                    int ne = k + 1 < n ? BitConverter.ToUInt16(data, eo + 12 + 8) : bl;
                    int len = ne - no;
                    if (k + 1 == n) { int z = 0; while (z < len && data[b0 + no + z] != 0) z++; len = z; }
                    res.Add(new KeyValuePair<string, ulong>(Encoding.UTF8.GetString(data, b0 + no, len), nid));
                }
            }
            return res;
        }

        public void ReadData(FsInode inode, Action<byte[], int, int> sink)
        {
            if (inode.OverrideData != null) { sink(inode.OverrideData, 0, inode.OverrideData.Length); return; }
            var i = (Inode)inode.SourceRef;
            ReadInto(i, 0, i.Size, sink);
        }
    }
}
