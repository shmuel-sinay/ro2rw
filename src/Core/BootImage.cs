using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ro2rwPc
{
    /// <summary>cpio "newc" archive, same semantics as magiskboot (sorted map, '.'/'..' dropped).</summary>
    public sealed class Cpio
    {
        public sealed class Entry { public uint Mode, Uid, Gid, RdevMajor, RdevMinor; public byte[] Data = new byte[0]; }
        public const uint S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFMT = 0xF000;
        public readonly SortedDictionary<string, Entry> Entries = new SortedDictionary<string, Entry>(StringComparer.Ordinal);

        static uint Hex(byte[] d, int p) { return Convert.ToUInt32(Encoding.ASCII.GetString(d, p, 8), 16); }
        static int Align4(int v) { return (v + 3) & ~3; }

        public static Cpio Load(byte[] d)
        {
            var c = new Cpio();
            int pos = 0;
            while (pos + 110 <= d.Length)
            {
                if (Encoding.ASCII.GetString(d, pos, 6) != "070701") throw new InvalidDataException("cpio: bad magic");
                uint mode = Hex(d, pos + 14), uid = Hex(d, pos + 22), gid = Hex(d, pos + 30);
                int fsz = (int)Hex(d, pos + 54);
                uint rmaj = Hex(d, pos + 78), rmin = Hex(d, pos + 86);
                int nsz = (int)Hex(d, pos + 94);
                int hp = pos + 110;
                string name = Encoding.UTF8.GetString(d, hp, Math.Max(0, nsz - 1));
                pos = Align4(hp + nsz);
                if (name == "." || name == "..") { pos = Align4(pos + fsz); continue; }
                if (name == "TRAILER!!!")
                {
                    int next = IndexOf(d, Encoding.ASCII.GetBytes("070701"), pos);
                    if (next < 0) break;
                    pos = next; continue;
                }
                var e = new Entry { Mode = mode, Uid = uid, Gid = gid, RdevMajor = rmaj, RdevMinor = rmin, Data = new byte[fsz] };
                Buffer.BlockCopy(d, pos, e.Data, 0, fsz);
                c.Entries[Norm(name)] = e;
                pos = Align4(pos + fsz);
            }
            return c;
        }

        static int IndexOf(byte[] d, byte[] pat, int from)
        {
            for (int i = from; i + pat.Length <= d.Length; i++)
            {
                int k = 0; while (k < pat.Length && d[i + k] == pat[k]) k++;
                if (k == pat.Length) return i;
            }
            return -1;
        }

        public static string Norm(string p)
        {
            var parts = new List<string>();
            foreach (var s in p.Split('/')) if (s.Length > 0 && s != ".") parts.Add(s);
            return string.Join("/", parts);
        }

        public byte[] Dump()
        {
            var o = new MemoryStream();
            long inode = 300000;
            Action<string, uint, uint, uint, uint, uint, byte[]> put = (name, mode, uid, gid, rmaj, rmin, data) =>
            {
                var nb = Encoding.UTF8.GetBytes(name);
                string h = string.Format("070701{0:x8}{1:x8}{2:x8}{3:x8}{4:x8}{5:x8}{6:x8}{7:x8}{8:x8}{9:x8}{10:x8}{11:x8}{12:x8}",
                    inode, mode, uid, gid, 1, 0, data.Length, 0, 0, rmaj, rmin, nb.Length + 1, 0);
                var hb = Encoding.ASCII.GetBytes(h);
                o.Write(hb, 0, hb.Length);
                o.Write(nb, 0, nb.Length); o.WriteByte(0);
                while (o.Length % 4 != 0) o.WriteByte(0);
                o.Write(data, 0, data.Length);
                while (o.Length % 4 != 0) o.WriteByte(0);
                inode++;
            };
            foreach (var kv in Entries) put(kv.Key, kv.Value.Mode, kv.Value.Uid, kv.Value.Gid, kv.Value.RdevMajor, kv.Value.RdevMinor, kv.Value.Data);
            put("TRAILER!!!", 0x1ED, 0, 0, 0, 0, new byte[0]);
            return o.ToArray();
        }

        public bool Exists(string p) { return Entries.ContainsKey(Norm(p)); }
        public void Add(uint mode, string path, byte[] data) { Entries[Norm(path)] = new Entry { Mode = mode | S_IFREG, Data = data }; }
        public void Mkdir(uint mode, string path) { Entries[Norm(path)] = new Entry { Mode = mode | S_IFDIR }; }
        public void Rm(string path, bool recursive)
        {
            path = Norm(path);
            Entries.Remove(path);
            if (recursive)
            {
                var pre = path + "/";
                var del = new List<string>();
                foreach (var k in Entries.Keys) if (k.StartsWith(pre, StringComparison.Ordinal)) del.Add(k);
                foreach (var k in del) Entries.Remove(k);
            }
        }
        public Cpio Clone()
        {
            var c = new Cpio();
            foreach (var kv in Entries) c.Entries[kv.Key] = new Entry { Mode = kv.Value.Mode, Uid = kv.Value.Uid, Gid = kv.Value.Gid, RdevMajor = kv.Value.RdevMajor, RdevMinor = kv.Value.RdevMinor, Data = kv.Value.Data };
            return c;
        }
    }

    /// <summary>Android boot / init_boot image (header v0-v4, MTK section headers, AVB footer).</summary>
    public sealed class BootImage
    {
        public byte[] Raw;
        public int HeaderVersion;
        public uint PageSize;
        public int HeaderSize;
        public byte[] Kernel = new byte[0], Ramdisk = new byte[0], Second = new byte[0], Extra = new byte[0], Dtb = new byte[0], Signature = new byte[0];
        public byte[] KernelMtk, RamdiskMtk;  // 512-byte MTK headers if present
        public long AvbFooterOffset = -1;
        public uint LegacyDtSize;
        public string Info = "";

        const uint MtkMagic = 0x58881688;

        static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
        static long Align(long v, long a) { return (v + a - 1) / a * a; }
        static byte[] Slice(byte[] b, long o, long n) { var r = new byte[n]; if (n > 0) Buffer.BlockCopy(b, (int)o, r, 0, (int)n); return r; }

        public static bool IsBoot(byte[] b) { return b.Length > 1648 && Encoding.ASCII.GetString(b, 0, 8) == "ANDROID!"; }

        public static BootImage Parse(byte[] b)
        {
            var bi = new BootImage { Raw = b };
            if (!IsBoot(b))
            {
                if (b.Length > 8 && Encoding.ASCII.GetString(b, 0, 8) == "VNDRBOOT") throw new NotSupportedException("vendor_boot עדיין לא נתמך. השתמש ב-boot או init_boot.");
                throw new InvalidDataException("זה לא קובץ boot תקין (חסר ANDROID!)");
            }
            uint hv = U32(b, 40);
            if (hv >= 3 && hv <= 4)
            {
                bi.HeaderVersion = (int)hv; bi.PageSize = 4096;
                uint ks = U32(b, 8), rs = U32(b, 12);
                bi.HeaderSize = (int)U32(b, 20);
                long p = 4096;
                bi.Kernel = Slice(b, p, ks); p += Align(ks, 4096);
                bi.Ramdisk = Slice(b, p, rs); p += Align(rs, 4096);
                if (hv == 4) { uint ss = U32(b, 1580); bi.Signature = Slice(b, p, ss); p += Align(ss, 4096); }
            }
            else
            {
                bi.HeaderVersion = hv <= 2 ? (int)hv : 0;
                bi.LegacyDtSize = hv > 4 ? hv : 0;   // old QCOM/Samsung v0 "dt" section
                uint ks = U32(b, 8), rs = U32(b, 16), ss = U32(b, 24);
                bi.PageSize = U32(b, 36);
                if (bi.PageSize < 2048 || bi.PageSize > 16384) bi.PageSize = 2048;
                long pg = bi.PageSize;
                long p = pg;
                bi.Kernel = Slice(b, p, ks); p += Align(ks, pg);
                bi.Ramdisk = Slice(b, p, rs); p += Align(rs, pg);
                bi.Second = Slice(b, p, ss); p += Align(ss, pg);
                if (bi.HeaderVersion >= 1) { uint es = U32(b, 1632); bi.Extra = Slice(b, p, es); p += Align(es, pg); }
                else if (bi.LegacyDtSize > 0) { bi.Extra = Slice(b, p, bi.LegacyDtSize); p += Align(bi.LegacyDtSize, pg); }
                if (bi.HeaderVersion >= 2) { uint ds = U32(b, 1648); bi.Dtb = Slice(b, p, ds); p += Align(ds, pg); }
            }
            bi.KernelMtk = StripMtk(ref bi.Kernel);
            bi.RamdiskMtk = StripMtk(ref bi.Ramdisk);
            if (b.Length >= 64 && Encoding.ASCII.GetString(b, b.Length - 64, 4) == "AVBf") bi.AvbFooterOffset = b.Length - 64;
            bi.Info = string.Format("header v{0}, page {1}, kernel {2}, ramdisk {3}{4}{5}", bi.HeaderVersion, bi.PageSize,
                Converter.Human(bi.Kernel.Length), Converter.Human(bi.Ramdisk.Length), bi.RamdiskMtk != null ? ", MTK header" : "", bi.AvbFooterOffset >= 0 ? ", AVB footer" : "");
            return bi;
        }

        static byte[] StripMtk(ref byte[] sec)
        {
            if (sec.Length >= 512 && U32(sec, 0) == MtkMagic)
            {
                var h = Slice(sec, 0, 512);
                sec = Slice(sec, 512, Math.Min(U32(sec, 4), sec.Length - 512));
                return h;
            }
            return null;
        }

        static byte[] WithMtk(byte[] hdr, byte[] data)
        {
            if (hdr == null) return data;
            var r = new byte[512 + data.Length];
            Buffer.BlockCopy(hdr, 0, r, 0, 512);
            BitConverter.GetBytes((uint)data.Length).CopyTo(r, 4);
            Buffer.BlockCopy(data, 0, r, 512, data.Length);
            return r;
        }

        public bool KernelIsArm64
        {
            get { return Kernel.Length > 0x40 && Encoding.ASCII.GetString(Kernel, 0x38, 4) == "ARM\x64"; }
        }

        /// <summary>Rebuilds the image with a new ramdisk. Result keeps the original file size (AVB footer kept).</summary>
        public byte[] Repack(byte[] newRamdisk)
        {
            var o = new MemoryStream();
            var hdr = Slice(Raw, 0, HeaderVersion >= 3 ? 4096 : PageSize);
            var k = WithMtk(KernelMtk, Kernel);
            var r = WithMtk(RamdiskMtk, newRamdisk);
            long pg = HeaderVersion >= 3 ? 4096 : PageSize;
            Action<byte[]> sec = s => { o.Write(s, 0, s.Length); while (o.Length % pg != 0) o.WriteByte(0); };
            o.Write(hdr, 0, hdr.Length);
            sec(k); sec(r);
            if (HeaderVersion >= 3)
            {
                BitConverter.GetBytes((uint)k.Length).CopyTo(hdr, 8);
                BitConverter.GetBytes((uint)r.Length).CopyTo(hdr, 12);
                if (HeaderVersion == 4) sec(Signature);
            }
            else
            {
                sec(Second);
                long extraOff = o.Length;
                if (HeaderVersion >= 1 || LegacyDtSize > 0) sec(Extra);
                if (HeaderVersion >= 1 && Extra.Length > 0) BitConverter.GetBytes((ulong)extraOff).CopyTo(hdr, 1636);
                if (HeaderVersion >= 2) sec(Dtb);
                BitConverter.GetBytes((uint)k.Length).CopyTo(hdr, 8);
                BitConverter.GetBytes((uint)r.Length).CopyTo(hdr, 16);
                // id: SHA1 (or SHA256 if the original id looks like one)
                bool sha256 = false;
                for (int i = 576 + 20; i < 576 + 32; i++) if (Raw[i] != 0) sha256 = true;
                using (HashAlgorithm h = sha256 ? (HashAlgorithm)SHA256.Create() : SHA1.Create())
                {
                    Action<byte[]> add = s => { h.TransformBlock(s, 0, s.Length, null, 0); var sz = BitConverter.GetBytes((uint)s.Length); h.TransformBlock(sz, 0, 4, null, 0); };
                    add(k); add(r); add(Second);
                    if (HeaderVersion >= 1 || LegacyDtSize > 0) add(Extra);
                    if (HeaderVersion >= 2) add(Dtb);
                    h.TransformFinalBlock(new byte[0], 0, 0);
                    var id = new byte[32];
                    Buffer.BlockCopy(h.Hash, 0, id, 0, h.Hash.Length);
                    Buffer.BlockCopy(id, 0, hdr, 576, 32);
                }
            }
            var img = o.ToArray();
            Buffer.BlockCopy(hdr, 0, img, 0, hdr.Length);

            // keep original size, AVB footer + vbmeta blob
            long total = Raw.Length;
            if (AvbFooterOffset >= 0)
            {
                var f = Slice(Raw, AvbFooterOffset, 64);
                long vbOff = BE64(f, 20), vbSize = BE64(f, 28);
                long newVbOff = Align(img.Length, 4096);
                if (newVbOff + vbSize > total - 64) throw new InvalidDataException("ה-boot החדש גדול מדי למחיצה");
                var outb = new byte[total];
                Buffer.BlockCopy(img, 0, outb, 0, img.Length);
                if (vbSize > 0 && vbOff + vbSize <= Raw.Length) Buffer.BlockCopy(Raw, (int)vbOff, outb, (int)newVbOff, (int)vbSize);
                PutBE64(f, 12, img.Length);
                PutBE64(f, 20, newVbOff);
                Buffer.BlockCopy(f, 0, outb, (int)(total - 64), 64);
                return outb;
            }
            if (img.Length > total)
            {
                // no footer: allow growth (partition size unknown), but warn via Info
                Info += " | גדל מהמקור";
                return img;
            }
            var res = new byte[total];
            Buffer.BlockCopy(img, 0, res, 0, img.Length);
            return res;
        }

        static long BE64(byte[] b, int o) { long v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i]; return v; }
        static void PutBE64(byte[] b, int o, long v) { for (int i = 7; i >= 0; i--) { b[o + i] = (byte)v; v >>= 8; } }
    }
}
