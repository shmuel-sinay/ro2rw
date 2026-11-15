using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ro2rwPc
{
    /// <summary>Android dynamic partitions ("super") metadata - liblp format.</summary>
    public sealed class LpMetadata
    {
        public const uint GeometryMagic = 0x616c4467;
        public const uint HeaderMagic = 0x414C5030;
        public const int ReservedBytes = 4096;
        public const int GeometrySize = 4096;
        public const int Sector = 512;

        public const uint AttrReadonly = 1, AttrSlotSuffixed = 2, AttrUpdated = 4, AttrDisabled = 8;

        public class Extent { public ulong NumSectors; public uint TargetType; public ulong TargetData; public uint TargetSource; }
        public class Partition { public string Name; public uint Attributes; public uint GroupIndex; public List<Extent> Extents = new List<Extent>();
            public long SizeBytes { get { long s = 0; foreach (var e in Extents) s += (long)e.NumSectors * Sector; return s; } } }
        public class Group { public string Name; public uint Flags; public ulong MaximumSize; }
        public class BlockDevice { public ulong FirstLogicalSector; public uint Alignment; public uint AlignmentOffset; public ulong Size; public string PartitionName; public uint Flags; }

        // geometry
        public uint MetadataMaxSize, MetadataSlotCount, LogicalBlockSize;
        // header
        public ushort Major, Minor;
        public uint HeaderFlags;
        public List<Partition> Partitions = new List<Partition>();
        public List<Group> Groups = new List<Group>();
        public List<BlockDevice> BlockDevices = new List<BlockDevice>();

        static string CStr(byte[] b, int off, int len)
        {
            int n = 0; while (n < len && b[off + n] != 0) n++;
            return Encoding.ASCII.GetString(b, off, n);
        }
        static void PutStr(byte[] b, int off, int len, string s)
        {
            var x = Encoding.ASCII.GetBytes(s ?? "");
            if (x.Length > len) throw new InvalidDataException("Name too long: " + s);
            Buffer.BlockCopy(x, 0, b, off, x.Length);
        }
        static byte[] Sha(byte[] b, int off, int len) { using (var h = SHA256.Create()) return h.ComputeHash(b, off, len); }

        public static bool LooksLikeSuper(IByteSource src)
        {
            if (src.Length < ReservedBytes + GeometrySize * 2) return false;
            var g = src.ReadBytes(ReservedBytes, 4);
            return BitConverter.ToUInt32(g, 0) == GeometryMagic;
        }

        public static LpMetadata Read(IByteSource src)
        {
            var md = new LpMetadata();
            byte[] geo = null;
            foreach (long off in new long[] { ReservedBytes, ReservedBytes + GeometrySize })
            {
                var g = src.ReadBytes(off, GeometrySize);
                if (BitConverter.ToUInt32(g, 0) != GeometryMagic) continue;
                uint ss = BitConverter.ToUInt32(g, 4);
                if (ss < 52 || ss > GeometrySize) continue;
                var chk = new byte[32]; Buffer.BlockCopy(g, 8, chk, 0, 32);
                var tmp = (byte[])g.Clone(); Array.Clear(tmp, 8, 32);
                if (!Eq(Sha(tmp, 0, (int)ss), chk)) continue;
                geo = g; break;
            }
            if (geo == null) throw new InvalidDataException("לא נמצאה מטא-דאטה של super (LP geometry). זה לא קובץ super תקין.");
            md.MetadataMaxSize = BitConverter.ToUInt32(geo, 40);
            md.MetadataSlotCount = BitConverter.ToUInt32(geo, 44);
            md.LogicalBlockSize = BitConverter.ToUInt32(geo, 48);

            Exception last = null;
            for (int copy = 0; copy < 2; copy++)
            {
                long off = copy == 0 ? PrimaryOffset(md, 0) : BackupOffset(md, 0);
                try { md.ParseMetadata(src.ReadBytes(off, (int)md.MetadataMaxSize)); return md; }
                catch (Exception ex) { last = ex; md.Partitions.Clear(); md.Groups.Clear(); md.BlockDevices.Clear(); }
            }
            throw new InvalidDataException("המטא-דאטה של super פגומה: " + (last != null ? last.Message : ""));
        }

        static bool Eq(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }

        public static long PrimaryOffset(LpMetadata m, int slot) { return ReservedBytes + GeometrySize * 2 + (long)m.MetadataMaxSize * slot; }
        public static long BackupOffset(LpMetadata m, int slot) { return ReservedBytes + GeometrySize * 2 + (long)m.MetadataMaxSize * m.MetadataSlotCount + (long)m.MetadataMaxSize * slot; }

        void ParseMetadata(byte[] b)
        {
            if (BitConverter.ToUInt32(b, 0) != HeaderMagic) throw new InvalidDataException("bad header magic");
            Major = BitConverter.ToUInt16(b, 4); Minor = BitConverter.ToUInt16(b, 6);
            if (Major != 10) throw new InvalidDataException("unsupported metadata version " + Major + "." + Minor);
            uint hsize = BitConverter.ToUInt32(b, 8);
            var hdr = new byte[hsize]; Buffer.BlockCopy(b, 0, hdr, 0, (int)hsize);
            var hchk = new byte[32]; Buffer.BlockCopy(hdr, 12, hchk, 0, 32);
            Array.Clear(hdr, 12, 32);
            if (!Eq(Sha(hdr, 0, (int)hsize), hchk)) throw new InvalidDataException("header checksum mismatch");
            uint tsize = BitConverter.ToUInt32(b, 44);
            var tchk = new byte[32]; Buffer.BlockCopy(b, 48, tchk, 0, 32);
            if (!Eq(Sha(b, (int)hsize, (int)tsize), tchk)) throw new InvalidDataException("tables checksum mismatch");
            HeaderFlags = hsize >= 132 ? BitConverter.ToUInt32(b, 128) : 0;
            int tb = (int)hsize;
            Func<int, int[]> desc = o => new[] { (int)BitConverter.ToUInt32(b, o), (int)BitConverter.ToUInt32(b, o + 4), (int)BitConverter.ToUInt32(b, o + 8) };
            var pd = desc(80); var ed = desc(92); var gd = desc(104); var bd = desc(116);

            var extents = new List<Extent>();
            for (int i = 0; i < ed[1]; i++)
            {
                int o = tb + ed[0] + i * ed[2];
                extents.Add(new Extent { NumSectors = BitConverter.ToUInt64(b, o), TargetType = BitConverter.ToUInt32(b, o + 8), TargetData = BitConverter.ToUInt64(b, o + 12), TargetSource = BitConverter.ToUInt32(b, o + 20) });
            }
            for (int i = 0; i < pd[1]; i++)
            {
                int o = tb + pd[0] + i * pd[2];
                var p = new Partition { Name = CStr(b, o, 36), Attributes = BitConverter.ToUInt32(b, o + 36), GroupIndex = BitConverter.ToUInt32(b, o + 48) };
                uint first = BitConverter.ToUInt32(b, o + 40), num = BitConverter.ToUInt32(b, o + 44);
                for (uint k = 0; k < num; k++) p.Extents.Add(extents[(int)(first + k)]);
                Partitions.Add(p);
            }
            for (int i = 0; i < gd[1]; i++)
            {
                int o = tb + gd[0] + i * gd[2];
                Groups.Add(new Group { Name = CStr(b, o, 36), Flags = BitConverter.ToUInt32(b, o + 36), MaximumSize = BitConverter.ToUInt64(b, o + 40) });
            }
            for (int i = 0; i < bd[1]; i++)
            {
                int o = tb + bd[0] + i * bd[2];
                BlockDevices.Add(new BlockDevice
                {
                    FirstLogicalSector = BitConverter.ToUInt64(b, o), Alignment = BitConverter.ToUInt32(b, o + 8), AlignmentOffset = BitConverter.ToUInt32(b, o + 12),
                    Size = BitConverter.ToUInt64(b, o + 16), PartitionName = CStr(b, o + 24, 36), Flags = BitConverter.ToUInt32(b, o + 60)
                });
            }
            if (BlockDevices.Count == 0) throw new InvalidDataException("no block devices");
        }

        /// <summary>Opens the data of a partition as a byte source over the super image.</summary>
        public IByteSource OpenPartition(IByteSource super, Partition p)
        {
            var list = new List<ExtentSource.Ext>();
            long pos = 0;
            foreach (var e in p.Extents)
            {
                if (e.TargetSource != 0) throw new NotSupportedException("מחיצה " + p.Name + " נמצאת על התקן נוסף (retrofit) - לא נתמך");
                long len = (long)e.NumSectors * Sector;
                list.Add(new ExtentSource.Ext { Start = pos, Length = len, Target = (long)e.TargetData * Sector, Zero = e.TargetType != 0 });
                pos += len;
            }
            return new ExtentSource(super, list);
        }

        public byte[] SerializeGeometry()
        {
            var g = new byte[GeometrySize];
            BitConverter.GetBytes(GeometryMagic).CopyTo(g, 0);
            BitConverter.GetBytes(52u).CopyTo(g, 4);
            BitConverter.GetBytes(MetadataMaxSize).CopyTo(g, 40);
            BitConverter.GetBytes(MetadataSlotCount).CopyTo(g, 44);
            BitConverter.GetBytes(LogicalBlockSize).CopyTo(g, 48);
            Sha(g, 0, 52).CopyTo(g, 8);
            return g;
        }

        public byte[] SerializeMetadata()
        {
            int hsize = Minor >= 2 ? 256 : 128;
            var extents = new List<Extent>();
            var pt = new MemoryStream(); var et = new MemoryStream(); var gt = new MemoryStream(); var bt = new MemoryStream();
            foreach (var p in Partitions)
            {
                var r = new byte[52];
                PutStr(r, 0, 36, p.Name);
                BitConverter.GetBytes(p.Attributes).CopyTo(r, 36);
                BitConverter.GetBytes((uint)extents.Count).CopyTo(r, 40);
                BitConverter.GetBytes((uint)p.Extents.Count).CopyTo(r, 44);
                BitConverter.GetBytes(p.GroupIndex).CopyTo(r, 48);
                extents.AddRange(p.Extents);
                pt.Write(r, 0, r.Length);
            }
            foreach (var e in extents)
            {
                var r = new byte[24];
                BitConverter.GetBytes(e.NumSectors).CopyTo(r, 0);
                BitConverter.GetBytes(e.TargetType).CopyTo(r, 8);
                BitConverter.GetBytes(e.TargetData).CopyTo(r, 12);
                BitConverter.GetBytes(e.TargetSource).CopyTo(r, 20);
                et.Write(r, 0, r.Length);
            }
            foreach (var g in Groups)
            {
                var r = new byte[48];
                PutStr(r, 0, 36, g.Name);
                BitConverter.GetBytes(g.Flags).CopyTo(r, 36);
                BitConverter.GetBytes(g.MaximumSize).CopyTo(r, 40);
                gt.Write(r, 0, r.Length);
            }
            foreach (var d in BlockDevices)
            {
                var r = new byte[64];
                BitConverter.GetBytes(d.FirstLogicalSector).CopyTo(r, 0);
                BitConverter.GetBytes(d.Alignment).CopyTo(r, 8);
                BitConverter.GetBytes(d.AlignmentOffset).CopyTo(r, 12);
                BitConverter.GetBytes(d.Size).CopyTo(r, 16);
                PutStr(r, 24, 36, d.PartitionName);
                BitConverter.GetBytes(d.Flags).CopyTo(r, 60);
                bt.Write(r, 0, r.Length);
            }
            var tables = new MemoryStream();
            int pOff = 0; tables.Write(pt.ToArray(), 0, (int)pt.Length);
            int eOff = (int)tables.Length; tables.Write(et.ToArray(), 0, (int)et.Length);
            int gOff = (int)tables.Length; tables.Write(gt.ToArray(), 0, (int)gt.Length);
            int bOff = (int)tables.Length; tables.Write(bt.ToArray(), 0, (int)bt.Length);
            var tb = tables.ToArray();
            if (hsize + tb.Length > MetadataMaxSize) throw new InvalidDataException("Metadata too large");

            var h = new byte[hsize];
            BitConverter.GetBytes(HeaderMagic).CopyTo(h, 0);
            BitConverter.GetBytes(Major).CopyTo(h, 4);
            BitConverter.GetBytes(Minor).CopyTo(h, 6);
            BitConverter.GetBytes((uint)hsize).CopyTo(h, 8);
            BitConverter.GetBytes((uint)tb.Length).CopyTo(h, 44);
            Sha(tb, 0, tb.Length).CopyTo(h, 48);
            Action<int, int, int, int> put = (o, off, n, sz) => { BitConverter.GetBytes((uint)off).CopyTo(h, o); BitConverter.GetBytes((uint)n).CopyTo(h, o + 4); BitConverter.GetBytes((uint)sz).CopyTo(h, o + 8); };
            put(80, pOff, Partitions.Count, 52);
            put(92, eOff, extents.Count, 24);
            put(104, gOff, Groups.Count, 48);
            put(116, bOff, BlockDevices.Count, 64);
            if (hsize >= 132) BitConverter.GetBytes(HeaderFlags).CopyTo(h, 128);
            Sha(h, 0, hsize).CopyTo(h, 12);

            var all = new byte[hsize + tb.Length];
            Buffer.BlockCopy(h, 0, all, 0, hsize);
            Buffer.BlockCopy(tb, 0, all, hsize, tb.Length);
            return all;
        }

        /// <summary>Writes geometry + all metadata slots (primary and backup) into the image.</summary>
        public void WriteAll(Stream o)
        {
            var geo = SerializeGeometry();
            var md = SerializeMetadata();
            var padded = new byte[MetadataMaxSize];
            Buffer.BlockCopy(md, 0, padded, 0, md.Length);
            o.Position = 0; o.Write(new byte[ReservedBytes], 0, ReservedBytes);
            o.Write(geo, 0, geo.Length);
            o.Write(geo, 0, geo.Length);
            for (int s = 0; s < MetadataSlotCount; s++) { o.Position = PrimaryOffset(this, s); o.Write(padded, 0, padded.Length); }
            for (int s = 0; s < MetadataSlotCount; s++) { o.Position = BackupOffset(this, s); o.Write(padded, 0, padded.Length); }
        }

        public long MetadataEnd { get { return BackupOffset(this, (int)MetadataSlotCount); } }
    }
}
