using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ro2rwPc
{
    /// <summary>Read-only ext2/3/4 parser (extents, block maps, inline data, xattrs, shared_blocks).</summary>
    public sealed class Ext4Reader : IFsReader
    {
        readonly IByteSource src;
        readonly CachedReader r;
        int bs; long inodesPerGroup; int inodeSize; uint descSize; bool is64;
        long gdtPos; uint incompat;
        public string FsName { get { return "EXT4"; } }

        public static bool Detect(IByteSource s)
        {
            if (s.Length < 2048) return false;
            return BitConverter.ToUInt16(s.ReadBytes(1024 + 56, 2), 0) == 0xEF53;
        }

        public Ext4Reader(IByteSource s)
        {
            src = s; r = new CachedReader(s);
            var sb = r.Bytes(1024, 1024);
            if (BitConverter.ToUInt16(sb, 56) != 0xEF53) throw new InvalidDataException("Not ext4");
            bs = 1024 << (int)BitConverter.ToUInt32(sb, 24);
            inodesPerGroup = BitConverter.ToUInt32(sb, 40);
            uint rev = BitConverter.ToUInt32(sb, 76);
            inodeSize = rev == 0 ? 128 : BitConverter.ToUInt16(sb, 88);
            incompat = BitConverter.ToUInt32(sb, 96);
            is64 = (incompat & 0x80) != 0;
            descSize = is64 ? BitConverter.ToUInt16(sb, 254) : 32u;
            if (descSize < 32) descSize = 32;
            uint firstData = BitConverter.ToUInt32(sb, 20);
            gdtPos = (long)(firstData + 1) * bs;
            if ((incompat & 0x200) != 0) { } // flex_bg fine
            if ((incompat & 0x10000) != 0) throw new NotSupportedException("ext4 מוצפן לא נתמך");
        }

        long InodePos(uint ino)
        {
            long g = (ino - 1) / inodesPerGroup, idx = (ino - 1) % inodesPerGroup;
            long d = gdtPos + g * descSize;
            long tbl = r.U32(d + 8);
            if (is64 && descSize >= 64) tbl |= (long)r.U32(d + 0x28) << 32;
            return tbl * bs + idx * inodeSize;
        }

        sealed class Ino { public uint Num; public byte[] Raw; public uint Mode; public long Size; public uint Flags; }

        Ino Load(uint num)
        {
            var raw = r.Bytes(InodePos(num), inodeSize);
            var i = new Ino { Num = num, Raw = raw, Mode = BitConverter.ToUInt16(raw, 0), Flags = BitConverter.ToUInt32(raw, 32) };
            i.Size = BitConverter.ToUInt32(raw, 4) | ((long)BitConverter.ToUInt32(raw, 108) << 32);
            return i;
        }

        // physical runs: (logicalBlock, physBlock, count); phys<0 => hole
        List<long[]> Map(Ino i)
        {
            var runs = new List<long[]>();
            if ((i.Flags & 0x80000) != 0) WalkExtents(i.Raw, 40, runs, 0);
            else
            {
                long nblocks = (i.Size + bs - 1) / bs;
                long lb = 0;
                for (int k = 0; k < 12 && lb < nblocks; k++, lb++) AddRun(runs, lb, BitConverter.ToUInt32(i.Raw, 40 + k * 4));
                uint[] ind = { BitConverter.ToUInt32(i.Raw, 88), BitConverter.ToUInt32(i.Raw, 92), BitConverter.ToUInt32(i.Raw, 96) };
                for (int lvl = 0; lvl < 3 && lb < nblocks; lvl++) lb = Indirect(ind[lvl], lvl, lb, nblocks, runs);
            }
            return runs;
        }

        long Indirect(uint blk, int lvl, long lb, long nblocks, List<long[]> runs)
        {
            long per = bs / 4;
            long span = 1; for (int k = 0; k <= lvl; k++) span *= per;
            if (blk == 0) return lb + span;
            var b = r.Bytes((long)blk * bs, bs);
            for (int k = 0; k < per && lb < nblocks; k++)
            {
                uint p = BitConverter.ToUInt32(b, k * 4);
                if (lvl == 0) { AddRun(runs, lb, p); lb++; }
                else lb = Indirect(p, lvl - 1, lb, nblocks, runs);
            }
            return lb;
        }

        static void AddRun(List<long[]> runs, long lb, uint pb)
        {
            if (pb == 0) return;
            if (runs.Count > 0)
            {
                var l = runs[runs.Count - 1];
                if (l[0] + l[2] == lb && l[1] + l[2] == pb) { l[2]++; return; }
            }
            runs.Add(new long[] { lb, pb, 1 });
        }

        void WalkExtents(byte[] b, int off, List<long[]> runs, int depthGuard)
        {
            if (depthGuard > 8) throw new InvalidDataException("ext4: extent tree too deep");
            if (BitConverter.ToUInt16(b, off) != 0xF30A) throw new InvalidDataException("ext4: bad extent header");
            int n = BitConverter.ToUInt16(b, off + 2);
            int depth = BitConverter.ToUInt16(b, off + 6);
            for (int k = 0; k < n; k++)
            {
                int e = off + 12 + k * 12;
                uint lblk = BitConverter.ToUInt32(b, e);
                if (depth == 0)
                {
                    int len = BitConverter.ToUInt16(b, e + 4);
                    bool uninit = len > 32768;
                    if (uninit) len -= 32768;
                    long start = ((long)BitConverter.ToUInt16(b, e + 6) << 32) | BitConverter.ToUInt32(b, e + 8);
                    runs.Add(new long[] { lblk, uninit ? -1 : start, len });
                }
                else
                {
                    long leaf = ((long)BitConverter.ToUInt16(b, e + 8) << 32) | BitConverter.ToUInt32(b, e + 4);
                    var child = r.Bytes(leaf * bs, bs);
                    WalkExtents(child, 0, runs, depthGuard + 1);
                }
            }
        }

        void Stream(Ino i, Action<byte[], int, int> sink)
        {
            if ((i.Flags & 0x10000000) != 0) { var d = InlineData(i); sink(d, 0, d.Length); return; }
            long size = i.Size;
            var runs = Map(i);
            runs.Sort((a, b) => a[0].CompareTo(b[0]));
            long pos = 0;
            var buf = new byte[1 << 20];
            var zero = new byte[1 << 16];
            foreach (var run in runs)
            {
                long lstart = run[0] * bs;
                if (lstart >= size) break;
                while (pos < lstart) { int z = (int)Math.Min(zero.Length, lstart - pos); sink(zero, 0, z); pos += z; }
                long rlen = Math.Min(run[2] * (long)bs, size - lstart);
                long done = 0;
                while (done < rlen)
                {
                    int n = (int)Math.Min(buf.Length, rlen - done);
                    if (run[1] < 0) Array.Clear(buf, 0, n);
                    else src.Read(run[1] * bs + done, buf, 0, n);
                    sink(buf, 0, n);
                    done += n;
                }
                pos = lstart + rlen;
            }
            while (pos < size) { int z = (int)Math.Min(zero.Length, size - pos); sink(zero, 0, z); pos += z; }
        }

        byte[] InlineData(Ino i)
        {
            var res = new byte[i.Size];
            int a = (int)Math.Min(60, i.Size);
            Buffer.BlockCopy(i.Raw, 40, res, 0, a);
            if (i.Size > 60)
            {
                foreach (var x in RawXattrs(i, true))
                    if (x.Name == "system.data") Buffer.BlockCopy(x.Value, 0, res, 60, (int)Math.Min(x.Value.Length, i.Size - 60));
            }
            return res;
        }

        byte[] ReadAllBytes(Ino i)
        {
            var ms = new MemoryStream();
            Stream(i, (b, o, n) => ms.Write(b, o, n));
            return ms.ToArray();
        }

        List<Xattr> RawXattrs(Ino i, bool includeSystemData)
        {
            var list = new List<Xattr>();
            if (inodeSize > 128)
            {
                int extra = BitConverter.ToUInt16(i.Raw, 128);
                int p = 128 + extra;
                if (p + 4 <= inodeSize && BitConverter.ToUInt32(i.Raw, p) == 0xEA020000)
                    ParseEntries(i.Raw, p + 4, inodeSize, p + 4, list);
            }
            long acl = BitConverter.ToUInt32(i.Raw, 104) | ((long)BitConverter.ToUInt16(i.Raw, 118) << 32);
            if (acl != 0)
            {
                var b = r.Bytes(acl * bs, bs);
                if (BitConverter.ToUInt32(b, 0) == 0xEA020000) ParseEntries(b, 32, bs, 0, list);
            }
            if (!includeSystemData) list.RemoveAll(x => x.Name == "system.data");
            return list;
        }

        static void ParseEntries(byte[] b, int start, int end, int valueBase, List<Xattr> list)
        {
            int p = start;
            while (p + 16 <= end)
            {
                int nl = b[p], idx = b[p + 1];
                if (BitConverter.ToUInt32(b, p) == 0) break;
                int voff = BitConverter.ToUInt16(b, p + 2);
                uint vinum = BitConverter.ToUInt32(b, p + 4);
                int vsize = (int)BitConverter.ToUInt32(b, p + 8);
                if (vinum != 0) throw new NotSupportedException("ext4: ea_inode xattrs לא נתמכים");
                string name = FsUtil.PrefixFor(idx) + Encoding.UTF8.GetString(b, p + 16, nl);
                var v = new byte[vsize];
                if (valueBase + voff + vsize <= b.Length) Buffer.BlockCopy(b, valueBase + voff, v, 0, vsize);
                list.Add(new Xattr(name, v));
                p += (16 + nl + 3) & ~3;
            }
        }

        public FsEntry ReadTree()
        {
            var inodes = new Dictionary<uint, FsInode>();
            var root = new FsEntry { Name = "", Inode = Convert(Load(2)), Children = new List<FsEntry>() };
            inodes[2] = root.Inode;
            var stack = new Stack<FsEntry>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                var di = (Ino)d.Inode.SourceRef;
                foreach (var de in ReadDir(di))
                {
                    if (de.Key == "." || de.Key == "..") continue;
                    if (d.Parent == null && de.Key == "lost+found") continue;
                    FsInode fi;
                    bool newDir = false;
                    if (!inodes.TryGetValue(de.Value, out fi))
                    {
                        fi = Convert(Load(de.Value));
                        inodes[de.Value] = fi;
                        newDir = fi.Type == NodeType.Dir;
                    }
                    else if (fi.Type == NodeType.Dir) throw new InvalidDataException("ext4: directory hard link");
                    var e = new FsEntry { Name = de.Key, Inode = fi, Parent = d };
                    if (fi.Type == NodeType.Dir) e.Children = new List<FsEntry>();
                    d.Children.Add(e);
                    if (newDir) stack.Push(e);
                }
            }
            FsUtil.CountLinks(root);
            return root;
        }

        List<KeyValuePair<string, uint>> ReadDir(Ino d)
        {
            var res = new List<KeyValuePair<string, uint>>();
            var data = ReadAllBytes(d);
            int start = 0;
            if ((d.Flags & 0x10000000) != 0)
            {
                // inline dir: first 4 bytes = parent inode
                res.Add(new KeyValuePair<string, uint>("..", BitConverter.ToUInt32(data, 0)));
                start = 4;
                ParseDirBlock(data, start, Math.Min(60, data.Length), res);
                if (data.Length > 60) ParseDirBlock(data, 60, data.Length, res);
                return res;
            }
            for (int b = 0; b < data.Length; b += bs) ParseDirBlock(data, b, Math.Min(b + bs, data.Length), res);
            return res;
        }

        void ParseDirBlock(byte[] data, int p, int end, List<KeyValuePair<string, uint>> res)
        {
            bool filetype = (incompat & 0x2) != 0;
            while (p + 8 <= end)
            {
                uint ino = BitConverter.ToUInt32(data, p);
                int rec = BitConverter.ToUInt16(data, p + 4);
                int nl = filetype ? data[p + 6] : BitConverter.ToUInt16(data, p + 6);
                if (rec < 8) break;
                if (ino != 0 && nl > 0 && p + 8 + nl <= end)
                    res.Add(new KeyValuePair<string, uint>(Encoding.UTF8.GetString(data, p + 8, nl), ino));
                p += rec;
            }
        }

        FsInode Convert(Ino i)
        {
            var raw = i.Raw;
            var f = new FsInode
            {
                Mode = i.Mode,
                Uid = BitConverter.ToUInt16(raw, 2) | ((uint)BitConverter.ToUInt16(raw, 120) << 16),
                Gid = BitConverter.ToUInt16(raw, 24) | ((uint)BitConverter.ToUInt16(raw, 122) << 16),
                Mtime = BitConverter.ToUInt32(raw, 16),
                Size = i.Size,
                Type = FsUtil.TypeFromMode(i.Mode),
                SourceRef = i
            };
            f.Xattrs = RawXattrs(i, false);
            switch (f.Type)
            {
                case NodeType.Symlink:
                    {
                        bool fast = (i.Flags & (0x80000 | 0x10000000)) == 0 && i.Size < 60 &&
                                    BitConverter.ToUInt32(raw, 28) == (BitConverter.ToUInt32(raw, 104) != 0 ? (uint)(bs / 512) : 0u);
                        byte[] t = fast ? Sub(raw, 40, (int)i.Size) : ReadAllBytes(i);
                        f.LinkTarget = Encoding.UTF8.GetString(t);
                        break;
                    }
                case NodeType.CharDev:
                case NodeType.BlockDev:
                    {
                        uint b0 = BitConverter.ToUInt32(raw, 40), b1 = BitConverter.ToUInt32(raw, 44);
                        if (b0 != 0) { f.RdevMajor = (b0 >> 8) & 0xff; f.RdevMinor = b0 & 0xff; }
                        else { f.RdevMajor = (b1 & 0xfff00) >> 8; f.RdevMinor = (b1 & 0xff) | ((b1 >> 12) & 0xfff00); }
                        f.Size = 0;
                        break;
                    }
                case NodeType.File: break;
                default: f.Size = 0; break;
            }
            return f;
        }

        static byte[] Sub(byte[] b, int o, int n) { var x = new byte[n]; Buffer.BlockCopy(b, o, x, 0, n); return x; }

        public void ReadData(FsInode inode, Action<byte[], int, int> sink)
        {
            if (inode.OverrideData != null) { sink(inode.OverrideData, 0, inode.OverrideData.Length); return; }
            Stream((Ino)inode.SourceRef, sink);
        }
    }
}
