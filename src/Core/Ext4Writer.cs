using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ro2rwPc
{
    /// <summary>
    /// Builds a fresh, writable ext4 image (4K blocks, extents, no journal, no shared blocks) from a file tree.
    /// </summary>
    public sealed class Ext4Writer
    {
        public const int B = 4096;
        const int IS = 256;           // inode size
        const int BPG = 32768;        // blocks per group
        const int ExtraIsize = 32;
        const int MaxExtentLen = 32768;
        const int LeafCap = (B - 12) / 12;   // 340

        readonly FsEntry root;
        readonly string volName;
        readonly List<FsInode> inodes = new List<FsInode>();         // index = ino - 12 (user inodes)
        readonly Dictionary<FsInode, DirInfo> dirs = new Dictionary<FsInode, DirInfo>();
        readonly Dictionary<FsInode, XInfo> xinfo = new Dictionary<FsInode, XInfo>();
        readonly List<XBlock> xblocks = new List<XBlock>();
        FsInode lostFound;
        long dataBlocks;     // all data blocks incl. margin
        int neededInodes;
        long minBlocks;

        sealed class DirInfo { public List<KeyValuePair<string, FsInode>> Entries = new List<KeyValuePair<string, FsInode>>(); public FsInode Parent; public int Blocks; public int SubDirs; }
        sealed class XInfo { public byte[] InInode; public XBlock Block; }
        sealed class XBlock { public byte[] Content; public int Refs; public long Blk; public string Key; }

        public long MinimalBytes { get { return minBlocks * B; } }
        public int FileCount { get { return inodes.Count; } }

        public Ext4Writer(FsEntry root, string volName)
        {
            this.root = root; this.volName = volName ?? "";
            Prepare();
        }

        // ------------------------------------------------------------ preparation
        void Prepare()
        {
            FsUtil.CountLinks(root);
            root.Inode.NewIno = 2;
            lostFound = new FsInode { Type = NodeType.Dir, Mode = FsUtil.S_IFDIR | 0x1C0, Mtime = root.Inode.Mtime, NewIno = 11 };
            // copy selinux label from root for lost+found
            foreach (var x in root.Inode.Xattrs) if (x.Name == "security.selinux") lostFound.Xattrs.Add(x);

            uint next = 12;
            var queue = new Queue<FsEntry>();
            queue.Enqueue(root);
            dirs[root.Inode] = new DirInfo { Parent = root.Inode };
            dirs[lostFound] = new DirInfo { Parent = root.Inode };
            while (queue.Count > 0)
            {
                var d = queue.Dequeue();
                var di = dirs[d.Inode];
                if (d == root) di.Entries.Add(new KeyValuePair<string, FsInode>("lost+found", lostFound));
                foreach (var c in d.Children)
                {
                    if (d == root && c.Name == "lost+found") continue;
                    if (Encoding.UTF8.GetByteCount(c.Name) > 255) throw new InvalidDataException("שם קובץ ארוך מדי: " + c.Path);
                    if (c.Inode.NewIno == 0)
                    {
                        c.Inode.NewIno = next++;
                        inodes.Add(c.Inode);
                    }
                    di.Entries.Add(new KeyValuePair<string, FsInode>(c.Name, c.Inode));
                    if (c.Inode.Type == NodeType.Dir)
                    {
                        dirs[c.Inode] = new DirInfo { Parent = d.Inode };
                        queue.Enqueue(c);
                    }
                }
            }
            di_count(dirs[root.Inode]);
            neededInodes = (int)next;

            long blocks = 0;
            var all = new List<FsInode> { root.Inode, lostFound };
            all.AddRange(inodes);
            var xmap = new Dictionary<string, XBlock>();
            foreach (var ino in all)
            {
                if (ino.Type == NodeType.Dir)
                {
                    var di = dirs[ino];
                    di.Blocks = ino == lostFound ? 1 : Math.Max(1, DirBlockCount(di));
                    di.SubDirs = 0;
                    foreach (var e in di.Entries) if (e.Value.Type == NodeType.Dir) di.SubDirs++;
                    blocks += di.Blocks;
                }
                else if (ino.Type == NodeType.File)
                {
                    long sz = ino.OverrideData != null ? ino.OverrideData.Length : ino.Size;
                    long nb = (sz + B - 1) / B;
                    blocks += nb;
                    long estExt = nb / (BPG - 2048) + 2;
                    if (estExt > 4) blocks += (estExt + LeafCap - 1) / LeafCap;
                }
                else if (ino.Type == NodeType.Symlink)
                {
                    int len = Encoding.UTF8.GetByteCount(ino.LinkTarget ?? "");
                    if (len >= 60) blocks += (len + B - 1) / B;
                }
                // xattrs
                if (ino.Xattrs.Count > 0)
                {
                    var xi = new XInfo();
                    xi.InInode = BuildInInode(ino.Xattrs);
                    if (xi.InInode == null)
                    {
                        var content = BuildXBlock(ino.Xattrs);
                        string key = Convert.ToBase64String(content);
                        XBlock xb;
                        if (!xmap.TryGetValue(key, out xb) || xb.Refs >= 1000)
                        {
                            xb = new XBlock { Content = content, Key = key };
                            xmap[key] = xb;
                            xblocks.Add(xb);
                            blocks++;
                        }
                        xb.Refs++;
                        xi.Block = xb;
                    }
                    xinfo[ino] = xi;
                }
            }
            dataBlocks = blocks + 64 + blocks / 200;
            minBlocks = ComputeMinBlocks();
        }

        static void di_count(DirInfo d) { }

        static int RecLen(int nameLen) { return (8 + nameLen + 3) & ~3; }

        int DirBlockCount(DirInfo di)
        {
            int blocks = 1, used = RecLen(1) + RecLen(2);
            foreach (var e in di.Entries)
            {
                int rl = RecLen(Encoding.UTF8.GetByteCount(e.Key));
                if (used + rl > B) { blocks++; used = 0; }
                used += rl;
            }
            return blocks;
        }

        // ------------------------------------------------------------ xattrs
        static List<KeyValuePair<Xattr, KeyValuePair<int, byte[]>>> Normalize(List<Xattr> xs)
        {
            var l = new List<KeyValuePair<Xattr, KeyValuePair<int, byte[]>>>();
            foreach (var x in xs)
            {
                int idx; string suf;
                FsUtil.SplitName(x.Name, out idx, out suf);
                var nb = Encoding.UTF8.GetBytes(suf);
                if (nb.Length > 255) throw new InvalidDataException("xattr name too long");
                l.Add(new KeyValuePair<Xattr, KeyValuePair<int, byte[]>>(x, new KeyValuePair<int, byte[]>(idx, nb)));
            }
            l.Sort((a, b) =>
            {
                int c = a.Value.Key.CompareTo(b.Value.Key); if (c != 0) return c;
                c = a.Value.Value.Length.CompareTo(b.Value.Value.Length); if (c != 0) return c;
                return string.CompareOrdinal(Encoding.UTF8.GetString(a.Value.Value), Encoding.UTF8.GetString(b.Value.Value));
            });
            return l;
        }

        static int Pad4(int n) { return (n + 3) & ~3; }

        /// <summary>Returns the in-inode xattr area (from offset 160 to 256) or null if it doesn't fit.</summary>
        static byte[] BuildInInode(List<Xattr> xs)
        {
            const int region = IS - 128 - ExtraIsize; // 96
            var l = Normalize(xs);
            int need = 4 + 4;
            foreach (var e in l) need += 16 + Pad4(e.Value.Value.Length) + Pad4(e.Key.Value.Length);
            if (need > region) return null;
            var b = new byte[region];
            BitConverter.GetBytes(0xEA020000u).CopyTo(b, 0);
            int ep = 4, vend = region;
            foreach (var e in l)
            {
                var name = e.Value.Value; var val = e.Key.Value;
                vend -= Pad4(val.Length);
                b[ep] = (byte)name.Length; b[ep + 1] = (byte)e.Value.Key;
                BitConverter.GetBytes((ushort)(vend - 4)).CopyTo(b, ep + 2);   // relative to first entry
                BitConverter.GetBytes((uint)val.Length).CopyTo(b, ep + 8);
                // e_hash 0 for in-inode entries
                Buffer.BlockCopy(name, 0, b, ep + 16, name.Length);
                Buffer.BlockCopy(val, 0, b, vend, val.Length);
                ep += Pad4(16 + name.Length);
            }
            return b;
        }

        static uint EntryHash(byte[] name, byte[] val)
        {
            uint h = 0;
            foreach (byte c in name) h = (h << 5) ^ (h >> 27) ^ (uint)(sbyte)c;
            if (val.Length > 0)
            {
                var pv = new byte[Pad4(val.Length)];
                Buffer.BlockCopy(val, 0, pv, 0, val.Length);
                for (int i = 0; i < pv.Length; i += 4) h = (h << 16) ^ (h >> 16) ^ BitConverter.ToUInt32(pv, i);
            }
            return h;
        }

        static byte[] BuildXBlock(List<Xattr> xs)
        {
            var l = Normalize(xs);
            var b = new byte[B];
            BitConverter.GetBytes(0xEA020000u).CopyTo(b, 0);
            BitConverter.GetBytes(1u).CopyTo(b, 8);   // h_blocks
            int ep = 32, vend = B;
            uint bh = 0;
            foreach (var e in l)
            {
                var name = e.Value.Value; var val = e.Key.Value;
                vend -= Pad4(val.Length);
                if (vend < ep + Pad4(16 + name.Length) + 4) throw new InvalidDataException("xattrs too large");
                b[ep] = (byte)name.Length; b[ep + 1] = (byte)e.Value.Key;
                BitConverter.GetBytes((ushort)vend).CopyTo(b, ep + 2);
                BitConverter.GetBytes((uint)val.Length).CopyTo(b, ep + 8);
                uint eh = EntryHash(name, val);
                BitConverter.GetBytes(eh).CopyTo(b, ep + 12);
                Buffer.BlockCopy(name, 0, b, ep + 16, name.Length);
                Buffer.BlockCopy(val, 0, b, vend, val.Length);
                ep += Pad4(16 + name.Length);
                bh = (bh << 16) ^ (bh >> 16) ^ eh;
            }
            BitConverter.GetBytes(bh).CopyTo(b, 16);
            return b;
        }

        // ------------------------------------------------------------ geometry
        sealed class Geo
        {
            public long N; public int G; public int Ipg; public int Itb; public int GdtBlocks; public long Overhead;
            public long[] DataStart, GroupEnd, BB, IB, IT;
        }

        static bool HasSuper(int g)
        {
            if (g <= 1) return true;
            foreach (int p in new[] { 3, 5, 7 })
            {
                long x = p; while (x < g) x *= p;
                if (x == g) return true;
            }
            return false;
        }

        int WantedInodes(long n)
        {
            long w = neededInodes + Math.Max(neededInodes / 4, 1024) + (n * (long)B) / (2L << 20);
            return (int)Math.Min(w, int.MaxValue / 2);
        }

        Geo MakeGeo(long n)
        {
            var g = new Geo { N = n };
            g.G = (int)((n + BPG - 1) / BPG);
            long ipg = (WantedInodes(n) + g.G - 1) / g.G;
            ipg = (ipg + 15) / 16 * 16;
            if (ipg < 16) ipg = 16;
            if (ipg > 8 * B) ipg = 8 * B;
            if (ipg * g.G < neededInodes) throw new InvalidDataException("יותר מדי קבצים למחיצה בגודל הזה");
            g.Ipg = (int)ipg;
            g.Itb = g.Ipg * IS / B;
            g.GdtBlocks = (g.G * 32 + B - 1) / B;
            g.DataStart = new long[g.G]; g.GroupEnd = new long[g.G]; g.BB = new long[g.G]; g.IB = new long[g.G]; g.IT = new long[g.G];
            for (int i = 0; i < g.G; i++)
            {
                long s = (long)i * BPG;
                long p = s;
                if (HasSuper(i)) p += 1 + g.GdtBlocks;
                g.BB[i] = p; g.IB[i] = p + 1; g.IT[i] = p + 2;
                p += 2 + g.Itb;
                g.DataStart[i] = p;
                g.GroupEnd[i] = Math.Min(s + BPG, n);
                g.Overhead += p - s;
            }
            return g;
        }

        long ComputeMinBlocks()
        {
            long n = dataBlocks + 512;
            for (int iter = 0; iter < 50; iter++)
            {
                var g = MakeGeo(n);
                long lastSize = g.GroupEnd[g.G - 1] - (long)(g.G - 1) * BPG;
                long lastOver = g.DataStart[g.G - 1] - (long)(g.G - 1) * BPG;
                if (lastSize < lastOver + 64) { n = (long)(g.G - 1) * BPG + lastOver + 64 + 256; continue; }
                long need = dataBlocks + g.Overhead;
                if (need <= n) return n;
                n = need + 128;
            }
            throw new InvalidDataException("ext4 sizing did not converge");
        }

        // ------------------------------------------------------------ allocation
        sealed class Alloc
        {
            readonly Geo g; int grp; long pos;
            public readonly byte[][] Bitmap;
            public Alloc(Geo g)
            {
                this.g = g; grp = 0; pos = g.DataStart[0];
                Bitmap = new byte[g.G][];
                for (int i = 0; i < g.G; i++)
                {
                    Bitmap[i] = new byte[B];
                    long s = (long)i * BPG;
                    for (long b = s; b < g.DataStart[i]; b++) Set(b);
                    long gsize = g.GroupEnd[i] - s;
                    for (long k = gsize; k < BPG; k++) Bitmap[i][k >> 3] |= (byte)(1 << (int)(k & 7));
                }
            }
            void Set(long blk) { int gi = (int)(blk / BPG); long k = blk % BPG; Bitmap[gi][k >> 3] |= (byte)(1 << (int)(k & 7)); }
            public List<long[]> Take(long count)
            {
                var runs = new List<long[]>();
                while (count > 0)
                {
                    if (grp >= g.G) throw new InvalidDataException("אין מספיק מקום במחיצה (ext4)");
                    if (pos >= g.GroupEnd[grp]) { grp++; if (grp < g.G) pos = g.DataStart[grp]; continue; }
                    long take = Math.Min(count, Math.Min(g.GroupEnd[grp] - pos, MaxExtentLen));
                    if (runs.Count > 0)
                    {
                        var l = runs[runs.Count - 1];
                        if (l[0] + l[1] == pos && l[1] + take <= MaxExtentLen) { l[1] += take; }
                        else runs.Add(new long[] { pos, take });
                    }
                    else runs.Add(new long[] { pos, take });
                    for (long b = pos; b < pos + take; b++) Set(b);
                    pos += take; count -= take;
                }
                return runs;
            }
        }

        // ------------------------------------------------------------ write
        public long ChooseBlocks(long targetBytes)
        {
            long n = targetBytes / B;
            if (n < minBlocks) throw new InvalidDataException("הגודל שנבחר קטן מהנדרש");
            var g = MakeGeo(n);
            long lastSize = g.GroupEnd[g.G - 1] - (long)(g.G - 1) * BPG;
            long lastOver = g.DataStart[g.G - 1] - (long)(g.G - 1) * BPG;
            if (g.G > 1 && lastSize < lastOver + 64) n = (long)(g.G - 1) * BPG;
            if (n < minBlocks) n = minBlocks;
            return n;
        }

        /// <summary>Writes the filesystem of <paramref name="targetBytes"/> (rounded) at <paramref name="baseOff"/>.</summary>
        public long Write(Stream o, long baseOff, long targetBytes, IFsReader reader, Action<long> progress, Func<bool> cancelled)
        {
            long n = ChooseBlocks(targetBytes);
            var g = MakeGeo(n);
            var al = new Alloc(g);
            var rnd = new Random();
            uint now = (uint)Math.Min(uint.MaxValue, (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);

            var all = new List<FsInode> { root.Inode, lostFound };
            all.AddRange(inodes);

            // allocate directories, xattr blocks
            var runsOf = new Dictionary<FsInode, List<long[]>>();
            foreach (var ino in all) if (ino.Type == NodeType.Dir) runsOf[ino] = al.Take(dirs[ino].Blocks);
            foreach (var xb in xblocks) xb.Blk = al.Take(1)[0][0];
            // symlink + files
            var symData = new Dictionary<FsInode, byte[]>();
            foreach (var ino in all)
            {
                if (ino.Type == NodeType.File)
                {
                    long sz = ino.OverrideData != null ? ino.OverrideData.Length : ino.Size;
                    runsOf[ino] = al.Take((sz + B - 1) / B);
                }
                else if (ino.Type == NodeType.Symlink)
                {
                    var t = Encoding.UTF8.GetBytes(ino.LinkTarget ?? "");
                    if (t.Length >= 60) { symData[ino] = t; runsOf[ino] = al.Take((t.Length + B - 1) / B); }
                }
            }
            // extent leaves
            var leaves = new Dictionary<FsInode, List<long>>();
            foreach (var kv in runsOf)
            {
                if (kv.Value.Count <= 4) continue;
                int nleaf = (kv.Value.Count + LeafCap - 1) / LeafCap;
                if (nleaf > 4) throw new InvalidDataException("קובץ מפוצל מדי");
                var l = new List<long>();
                for (int i = 0; i < nleaf; i++) l.Add(al.Take(1)[0][0]);
                leaves[kv.Key] = l;
            }

            // inode table
            int totalInodes = g.Ipg * g.G;
            var itable = new byte[(long)totalInodes * IS];
            var usedDirs = new int[g.G];
            Action<FsInode> putInode = ino =>
            {
                long off = (long)(ino.NewIno - 1) * IS;
                var raw = EncodeInode(ino, runsOf, leaves, now);
                Buffer.BlockCopy(raw, 0, itable, (int)off, IS);
                if (ino.Type == NodeType.Dir) usedDirs[(ino.NewIno - 1) / g.Ipg]++;
            };
            foreach (var ino in all) putInode(ino);

            // inode bitmaps
            var ibm = new byte[g.G][];
            int lastIno = (int)(12 + inodes.Count) - 1;
            for (int i = 0; i < g.G; i++)
            {
                ibm[i] = new byte[B];
                for (int k = 0; k < 8 * B; k++)
                {
                    long ino = (long)i * g.Ipg + k + 1;
                    bool used = k >= g.Ipg || ino <= lastIno;
                    if (used) ibm[i][k >> 3] |= (byte)(1 << (k & 7));
                }
            }

            // counts
            long freeBlocks = 0; long freeInodes = 0;
            var gfreeB = new int[g.G]; var gfreeI = new int[g.G];
            for (int i = 0; i < g.G; i++)
            {
                long gsize = g.GroupEnd[i] - (long)i * BPG;
                int used = 0;
                for (int k = 0; k < gsize; k++) if ((al.Bitmap[i][k >> 3] & (1 << (k & 7))) != 0) used++;
                gfreeB[i] = (int)(gsize - used);
                freeBlocks += gfreeB[i];
                int ui = 0;
                for (int k = 0; k < g.Ipg; k++) if ((ibm[i][k >> 3] & (1 << (k & 7))) != 0) ui++;
                gfreeI[i] = g.Ipg - ui;
                freeInodes += gfreeI[i];
            }

            // group descriptors
            var gdt = new byte[g.GdtBlocks * B];
            for (int i = 0; i < g.G; i++)
            {
                int p = i * 32;
                BitConverter.GetBytes((uint)g.BB[i]).CopyTo(gdt, p);
                BitConverter.GetBytes((uint)g.IB[i]).CopyTo(gdt, p + 4);
                BitConverter.GetBytes((uint)g.IT[i]).CopyTo(gdt, p + 8);
                BitConverter.GetBytes((ushort)gfreeB[i]).CopyTo(gdt, p + 12);
                BitConverter.GetBytes((ushort)gfreeI[i]).CopyTo(gdt, p + 14);
                BitConverter.GetBytes((ushort)usedDirs[i]).CopyTo(gdt, p + 16);
            }

            // superblock
            var uuid = Guid.NewGuid().ToByteArray();
            var sb = new byte[1024];
            Action<int, uint> u32 = (p, v) => BitConverter.GetBytes(v).CopyTo(sb, p);
            Action<int, ushort> u16 = (p, v) => BitConverter.GetBytes(v).CopyTo(sb, p);
            u32(0, (uint)totalInodes); u32(4, (uint)n); u32(8, 0); u32(12, (uint)freeBlocks); u32(16, (uint)freeInodes);
            u32(20, 0); u32(24, 2); u32(28, 2); u32(32, BPG); u32(36, BPG); u32(40, (uint)g.Ipg);
            u32(44, 0); u32(48, now); u16(52, 0); u16(54, 0xFFFF); u16(56, 0xEF53); u16(58, 1); u16(60, 1); u16(62, 0);
            u32(64, now); u32(68, 0); u32(72, 0); u32(76, 1); u16(80, 0); u16(82, 0); u32(84, 11); u16(88, IS);
            u32(92, 0x8);                  // ext_attr
            u32(96, 0x2 | 0x40);           // filetype, extents
            u32(100, 0x1 | 0x2 | 0x20 | 0x40); // sparse_super, large_file, dir_nlink, extra_isize
            Buffer.BlockCopy(uuid, 0, sb, 104, 16);
            var vn = Encoding.ASCII.GetBytes(volName.Length > 16 ? volName.Substring(0, 16) : volName);
            Buffer.BlockCopy(vn, 0, sb, 120, vn.Length);
            var lm = Encoding.ASCII.GetBytes("/" + volName.TrimStart('/'));
            Buffer.BlockCopy(lm, 0, sb, 136, Math.Min(63, lm.Length));
            for (int k = 0; k < 4; k++) u32(236 + k * 4, (uint)rnd.Next());
            sb[252] = 1;                   // half_md4
            u32(264, now);
            u16(348, ExtraIsize); u16(350, ExtraIsize);
            u32(352, 2);                   // unsigned dir hash

            long total = 0, written = 0;
            foreach (var ino in all) if (ino.Type == NodeType.File) total += ino.OverrideData != null ? ino.OverrideData.Length : ino.Size;
            total += itable.Length;

            // ---- write metadata
            for (int i = 0; i < g.G; i++)
            {
                long gs = (long)i * BPG;
                if (HasSuper(i))
                {
                    BitConverter.GetBytes((ushort)i).CopyTo(sb, 90);
                    o.Position = baseOff + gs * B + (i == 0 ? 1024 : 0);
                    o.Write(sb, 0, 1024);
                    o.Position = baseOff + (gs + 1) * B;
                    o.Write(gdt, 0, gdt.Length);
                }
                o.Position = baseOff + g.BB[i] * B; o.Write(al.Bitmap[i], 0, B);
                o.Position = baseOff + g.IB[i] * B; o.Write(ibm[i], 0, B);
                o.Position = baseOff + g.IT[i] * B;
                o.Write(itable, (int)((long)i * g.Ipg * IS), g.Ipg * IS);
            }
            written += itable.Length;
            progress(written * 1000 / Math.Max(1, total));

            // ---- directories
            foreach (var ino in all)
            {
                if (ino.Type != NodeType.Dir) continue;
                var data = BuildDirBlocks(ino);
                WriteRuns(o, baseOff, runsOf[ino], data, data.Length);
            }
            // ---- xattr blocks
            foreach (var xb in xblocks)
            {
                BitConverter.GetBytes((uint)xb.Refs).CopyTo(xb.Content, 4);
                o.Position = baseOff + xb.Blk * B; o.Write(xb.Content, 0, B);
            }
            // ---- extent leaves
            foreach (var kv in leaves)
            {
                var runs = runsOf[kv.Key];
                for (int li = 0; li < kv.Value.Count; li++)
                {
                    var blk = new byte[B];
                    int first = li * LeafCap, cnt = Math.Min(LeafCap, runs.Count - first);
                    WriteExtHeader(blk, 0, cnt, LeafCap, 0);
                    long lb = 0; for (int k = 0; k < first; k++) lb += runs[k][1];
                    for (int k = 0; k < cnt; k++)
                    {
                        WriteExtent(blk, 12 + k * 12, (uint)lb, runs[first + k]);
                        lb += runs[first + k][1];
                    }
                    o.Position = baseOff + kv.Value[li] * B; o.Write(blk, 0, B);
                }
            }
            // ---- long symlinks
            foreach (var kv in symData) WriteRuns(o, baseOff, runsOf[kv.Key], kv.Value, kv.Value.Length);

            // ---- file data
            foreach (var ino in all)
            {
                if (ino.Type != NodeType.File) continue;
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                long sz = ino.OverrideData != null ? ino.OverrideData.Length : ino.Size;
                if (sz == 0) continue;
                var runs = runsOf[ino];
                int ri = 0; long inRun = 0; long got = 0;
                o.Position = baseOff + runs[0][0] * B;
                reader.ReadData(ino, (buf, off, cnt) =>
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    while (cnt > 0)
                    {
                        if (got >= sz) throw new InvalidDataException("file larger than expected");
                        long runBytes = runs[ri][1] * B;
                        if (inRun >= runBytes) { ri++; inRun = 0; o.Position = baseOff + runs[ri][0] * B; continue; }
                        int nn = (int)Math.Min(cnt, Math.Min(runBytes - inRun, sz - got));
                        o.Write(buf, off, nn);
                        off += nn; cnt -= nn; inRun += nn; got += nn;
                    }
                });
                if (got != sz) throw new InvalidDataException("קובץ נקרא חלקית");
                written += sz;
                progress(written * 1000 / Math.Max(1, total));
            }
            // make sure the image has its full length
            long endPos = baseOff + n * B;
            if (o.Length < endPos) o.SetLength(endPos);
            return n * B;
        }

        static void WriteRuns(Stream o, long baseOff, List<long[]> runs, byte[] data, int len)
        {
            int p = 0;
            foreach (var r in runs)
            {
                int n = (int)Math.Min(len - p, r[1] * B);
                if (n <= 0) break;
                o.Position = baseOff + r[0] * B;
                o.Write(data, p, n);
                p += n;
            }
        }

        byte[] BuildDirBlocks(FsInode ino)
        {
            var di = dirs[ino];
            var data = new byte[di.Blocks * B];
            int blk = 0, p = 0, lastEntry = -1;
            Action<string, uint, int> add = (name, inum, ftype) =>
            {
                var nb = Encoding.UTF8.GetBytes(name);
                int rl = RecLen(nb.Length);
                if (p + rl > B)
                {
                    // extend last entry to end of block
                    BitConverter.GetBytes((ushort)(blk * B + B - lastEntry)).CopyTo(data, lastEntry + 4);
                    blk++; p = 0;
                }
                int at = blk * B + p;
                BitConverter.GetBytes(inum).CopyTo(data, at);
                BitConverter.GetBytes((ushort)rl).CopyTo(data, at + 4);
                data[at + 6] = (byte)nb.Length; data[at + 7] = (byte)ftype;
                Buffer.BlockCopy(nb, 0, data, at + 8, nb.Length);
                lastEntry = at; p += rl;
            };
            add(".", ino.NewIno, 2);
            add("..", di.Parent.NewIno, 2);
            foreach (var e in di.Entries) add(e.Key, e.Value.NewIno, FileType(e.Value.Type));
            BitConverter.GetBytes((ushort)(blk * B + B - lastEntry)).CopyTo(data, lastEntry + 4);
            for (int k = blk + 1; k < di.Blocks; k++)
            {
                // empty trailing block (shouldn't happen) - single empty entry
                BitConverter.GetBytes((ushort)B).CopyTo(data, k * B + 4);
            }
            return data;
        }

        static int FileType(NodeType t)
        {
            switch (t)
            {
                case NodeType.File: return 1;
                case NodeType.Dir: return 2;
                case NodeType.CharDev: return 3;
                case NodeType.BlockDev: return 4;
                case NodeType.Fifo: return 5;
                case NodeType.Socket: return 6;
                case NodeType.Symlink: return 7;
            }
            return 0;
        }

        static void WriteExtHeader(byte[] b, int off, int entries, int max, int depth)
        {
            BitConverter.GetBytes((ushort)0xF30A).CopyTo(b, off);
            BitConverter.GetBytes((ushort)entries).CopyTo(b, off + 2);
            BitConverter.GetBytes((ushort)max).CopyTo(b, off + 4);
            BitConverter.GetBytes((ushort)depth).CopyTo(b, off + 6);
        }

        static void WriteExtent(byte[] b, int off, uint lblk, long[] run)
        {
            BitConverter.GetBytes(lblk).CopyTo(b, off);
            BitConverter.GetBytes((ushort)run[1]).CopyTo(b, off + 4);
            BitConverter.GetBytes((ushort)(run[0] >> 32)).CopyTo(b, off + 6);
            BitConverter.GetBytes((uint)run[0]).CopyTo(b, off + 8);
        }

        byte[] EncodeInode(FsInode ino, Dictionary<FsInode, List<long[]>> runsOf, Dictionary<FsInode, List<long>> leaves, uint now)
        {
            var raw = new byte[IS];
            long size; uint flags = 0; long blocks = 0;
            List<long[]> runs; runsOf.TryGetValue(ino, out runs);
            uint links;
            switch (ino.Type)
            {
                case NodeType.Dir:
                    size = (long)dirs[ino].Blocks * B;
                    links = (uint)(2 + dirs[ino].SubDirs);
                    if (links >= 65000) links = 1;
                    break;
                case NodeType.File:
                    size = ino.OverrideData != null ? ino.OverrideData.Length : ino.Size;
                    links = (uint)Math.Max(1, ino.LinkCount);
                    break;
                case NodeType.Symlink:
                    size = Encoding.UTF8.GetByteCount(ino.LinkTarget ?? "");
                    links = (uint)Math.Max(1, ino.LinkCount);
                    break;
                default:
                    size = 0;
                    links = (uint)Math.Max(1, ino.LinkCount);
                    break;
            }
            if (links > 65000) links = 65000;
            uint mode = ino.Mode;
            BitConverter.GetBytes((ushort)mode).CopyTo(raw, 0);
            BitConverter.GetBytes((ushort)(ino.Uid & 0xFFFF)).CopyTo(raw, 2);
            BitConverter.GetBytes((uint)size).CopyTo(raw, 4);
            uint t = ino.Mtime <= 0 ? now : (uint)Math.Min(ino.Mtime, uint.MaxValue);
            BitConverter.GetBytes(t).CopyTo(raw, 8);
            BitConverter.GetBytes(t).CopyTo(raw, 12);
            BitConverter.GetBytes(t).CopyTo(raw, 16);
            BitConverter.GetBytes((ushort)(ino.Gid & 0xFFFF)).CopyTo(raw, 24);
            BitConverter.GetBytes((ushort)links).CopyTo(raw, 26);
            BitConverter.GetBytes((uint)(size >> 32)).CopyTo(raw, 108);
            BitConverter.GetBytes((ushort)(ino.Uid >> 16)).CopyTo(raw, 120);
            BitConverter.GetBytes((ushort)(ino.Gid >> 16)).CopyTo(raw, 122);
            BitConverter.GetBytes((ushort)ExtraIsize).CopyTo(raw, 128);

            bool useExtents = ino.Type == NodeType.Dir || ino.Type == NodeType.File || (ino.Type == NodeType.Symlink && runs != null);
            if (useExtents)
            {
                flags |= 0x80000;
                runs = runs ?? new List<long[]>();
                foreach (var r in runs) blocks += r[1];
                List<long> lv;
                if (runs.Count <= 4)
                {
                    WriteExtHeader(raw, 40, runs.Count, 4, 0);
                    long lb = 0;
                    for (int k = 0; k < runs.Count; k++) { WriteExtent(raw, 52 + k * 12, (uint)lb, runs[k]); lb += runs[k][1]; }
                }
                else if (leaves.TryGetValue(ino, out lv))
                {
                    WriteExtHeader(raw, 40, lv.Count, 4, 1);
                    long lb = 0;
                    for (int li = 0; li < lv.Count; li++)
                    {
                        int e = 52 + li * 12;
                        BitConverter.GetBytes((uint)lb).CopyTo(raw, e);
                        BitConverter.GetBytes((uint)lv[li]).CopyTo(raw, e + 4);
                        BitConverter.GetBytes((ushort)(lv[li] >> 32)).CopyTo(raw, e + 8);
                        int first = li * LeafCap, cnt = Math.Min(LeafCap, runs.Count - first);
                        for (int k = 0; k < cnt; k++) lb += runs[first + k][1];
                    }
                    blocks += lv.Count;
                }
            }
            else if (ino.Type == NodeType.Symlink)
            {
                var tb = Encoding.UTF8.GetBytes(ino.LinkTarget ?? "");
                Buffer.BlockCopy(tb, 0, raw, 40, tb.Length);
            }
            else if (ino.Type == NodeType.CharDev || ino.Type == NodeType.BlockDev)
            {
                uint maj = ino.RdevMajor, min = ino.RdevMinor;
                if (maj < 256 && min < 256) BitConverter.GetBytes((maj << 8) | min).CopyTo(raw, 40);
                else BitConverter.GetBytes((min & 0xff) | (maj << 8) | ((min & ~0xffu) << 12)).CopyTo(raw, 44);
            }

            XInfo xi;
            if (xinfo.TryGetValue(ino, out xi))
            {
                if (xi.InInode != null) Buffer.BlockCopy(xi.InInode, 0, raw, 128 + ExtraIsize, xi.InInode.Length);
                else
                {
                    BitConverter.GetBytes((uint)xi.Block.Blk).CopyTo(raw, 104);
                    BitConverter.GetBytes((ushort)(xi.Block.Blk >> 32)).CopyTo(raw, 118);
                    blocks += 1;
                }
            }
            long sectors = blocks * (B / 512);
            BitConverter.GetBytes((uint)sectors).CopyTo(raw, 28);
            BitConverter.GetBytes((ushort)(sectors >> 32)).CopyTo(raw, 116);
            BitConverter.GetBytes(flags).CopyTo(raw, 32);
            return raw;
        }
    }
}
