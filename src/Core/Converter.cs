using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ro2rwPc
{
    public sealed class PartInfo
    {
        public LpMetadata.Partition Lp;
        public string Name { get { return Lp.Name; } }
        public long Size;
        public string Fs = "";          // EROFS / EXT4 / RAW / ""
        public bool Convert;            // user choice
        public string Category = "OT";  // S V P SE OT
        // planning
        public IFsReader Reader;
        public Ext4Writer Writer;
        public long MinBytes;
        public long FinalBytes;
        public long NewOffset;
        public int FileCount;
    }

    public sealed class Settings
    {
        public bool MaxMode = true;
        public int PctS = 70, PctV = 10, PctP = 10, PctSE = 5, PctOT = 5;
        public int FixedMb = 100;
        /// <summary>FIXED mode per category (S/V/P/SE/OT) in MB; null = FixedMb for all.</summary>
        public Dictionary<string, int> FixedMbPer;
        /// <summary>Per partition name extra MB (overrides categories). Set by a SuperPlan.</summary>
        public Dictionary<string, int> PerPartitionMb;
        public HashSet<string> ConvertOnly;
        public bool SparseOutput = true;
        public bool PatchVendorFstab = true;
        public bool PatchVbmeta = true;
        /// <summary>Kosher/filtered device: delete /vendor/bin/hw/wpa_supplicant so Wi-Fi cannot connect.</summary>
        public bool RemoveWifi = false;
    }

    /// <summary>Whole pipeline: open super → analyse → plan → write new super.</summary>
    public sealed class Converter : IDisposable
    {
        public Action<string> Log = s => { };
        public Action<int> Progress = p => { };   // 0..1000
        public Func<bool> Cancelled = () => false;

        public string InputPath;
        public List<string> InputFiles = new List<string>();
        public IByteSource Super;
        public LpMetadata Md;
        public List<PartInfo> Parts = new List<PartInfo>();
        public bool IsSparse;

        public static List<string> FindSplitFiles(string path)
        {
            // super.img.0, super.img.1 ... or super_sparsechunk.0 ...
            var m = Regex.Match(path, @"^(.*\.)(\d+)$");
            var list = new List<string>();
            if (m.Success)
            {
                string prefix = m.Groups[1].Value;
                for (int i = 0; i < 1000; i++)
                {
                    string p = prefix + i;
                    if (File.Exists(p)) list.Add(p);
                    else if (i > 0) break;
                }
                if (list.Count == 0) list.Add(path);
                if (!list.Contains(path)) list.Add(path);
            }
            else list.Add(path);
            return list;
        }

        public void Open(string path)
        {
            InputPath = path;
            InputFiles = FindSplitFiles(path);
            IsSparse = SparseSource.IsSparse(path);
            if (IsSparse)
            {
                Log("קובץ sparse" + (InputFiles.Count > 1 ? " מפוצל ל-" + InputFiles.Count + " חלקים" : ""));
                Super = new SparseSource(InputFiles);
            }
            else
            {
                if (InputFiles.Count > 1) InputFiles = new List<string> { path };
                Super = new RawFileSource(path);
            }
            if (!LpMetadata.LooksLikeSuper(Super))
                throw new InvalidDataException("הקובץ שנבחר אינו super.img (לא נמצאה טבלת מחיצות דינמיות).");
            Md = LpMetadata.Read(Super);
            Log(string.Format("super: גודל {0}, מטא-דאטה v{1}.{2}, {3} חריצים", Human(DeviceSize), Md.Major, Md.Minor, Md.MetadataSlotCount));

            SaveOriginalExtents();
            foreach (var p in Md.Partitions)
            {
                var pi = new PartInfo { Lp = p, Size = p.SizeBytes };
                pi.Category = CategoryOf(p.Name);
                if (pi.Size > 0)
                {
                    var s = Md.OpenPartition(Super, p);
                    if (ErofsReader.Detect(s)) pi.Fs = "EROFS";
                    else if (Ext4Reader.Detect(s)) pi.Fs = "EXT4";
                    else pi.Fs = "RAW";
                    pi.Convert = pi.Fs == "EROFS" || pi.Fs == "EXT4";
                    // default: only the active (_a) slot is converted; _b leftovers are copied as-is
                    if (p.Name.EndsWith("_b") && Md.Partitions.Any(x => x.Name == p.Name.Substring(0, p.Name.Length - 2) + "_a" && x.SizeBytes > 0))
                        pi.Convert = false;
                }
                Parts.Add(pi);
            }
        }

        public long DeviceSize { get { return (long)Md.BlockDevices[0].Size; } }

        static string BaseName(string n)
        {
            if (n.EndsWith("_a") || n.EndsWith("_b")) return n.Substring(0, n.Length - 2);
            return n;
        }

        public static string CategoryOf(string name)
        {
            switch (BaseName(name))
            {
                case "system": return "S";
                case "vendor": return "V";
                case "product": return "P";
                case "system_ext": return "SE";
                default: return "OT";
            }
        }

        public static string Human(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            if (b >= 1L << 10) return (b / 1024.0).ToString("0.0") + " KB";
            return b + " B";
        }

        // ------------------------------------------------------------ analysis
        byte[] wifiBackup; string wifiBackupName;
        bool wifiRemoved;

        public void Analyse(Settings st)
        {
            var todo = Parts.Where(p => p.Convert && p.Size > 0).ToList();
            if (st.RemoveWifi && !todo.Any(p => BaseName(p.Name) == "vendor"))
                Log("! הסרת WiFi מבוקשת אבל מחיצת vendor אינה בעריכה - ה-WiFi לא יוסר. סמן גם את vendor.");
            int i = 0;
            foreach (var p in todo)
            {
                if (Cancelled()) throw new OperationCanceledException();
                Log("סורק את " + p.Name + " (" + p.Fs + ")...");
                var src = Md.OpenPartition(Super, p.Lp);
                p.Reader = p.Fs == "EROFS" ? (IFsReader)new ErofsReader(src) : new Ext4Reader(src);
                var tree = p.Reader.ReadTree();
                if (st.PatchVendorFstab && BaseName(p.Name) == "vendor") PatchFstabs(tree, p.Reader);
                if (st.RemoveWifi && BaseName(p.Name) == "vendor") RemoveWifiBinary(tree, p.Reader);
                p.Writer = new Ext4Writer(tree, BaseName(p.Name));
                p.MinBytes = p.Writer.MinimalBytes;
                p.FileCount = p.Writer.FileCount;
                Log(string.Format("  {0}: {1} קבצים, מינימום ext4 {2} (היה {3})", p.Name, p.FileCount, Human(p.MinBytes), Human(p.Size)));
                i++;
                Progress(i * 100 / Math.Max(1, todo.Count));
            }
        }

        void PatchFstabs(FsEntry root, IFsReader reader)
        {
            var etc = root.Children.FirstOrDefault(c => c.Name == "etc" && c.Children != null);
            if (etc == null) return;
            foreach (var f in etc.Children)
            {
                if (f.Inode.Type != NodeType.File || !f.Name.StartsWith("fstab")) continue;
                var ms = new MemoryStream();
                reader.ReadData(f.Inode, (b, o, n) => ms.Write(b, o, n));
                string text = Encoding.UTF8.GetString(ms.ToArray());
                string patched = PatchFstabText(text);
                if (patched != text)
                {
                    f.Inode.OverrideData = Encoding.UTF8.GetBytes(patched);
                    Log("  תוקן " + f.Path + " (נוספו שורות ext4)");
                }
            }
        }

        /// <summary>Removes the wpa_supplicant binary from vendor (keeps a backup). Does NOT touch any XML.</summary>
        void RemoveWifiBinary(FsEntry root, IFsReader reader)
        {
            // /vendor/bin/hw/wpa_supplicant  (also try /vendor/bin/wpa_supplicant)
            FsEntry target = FindChild(FindChild(FindChild(root, "bin"), "hw"), "wpa_supplicant")
                          ?? FindChild(FindChild(root, "bin"), "wpa_supplicant");
            if (target == null || target.Inode.Type != NodeType.File)
            {
                Log("! לא נמצא wpa_supplicant ב-vendor - ה-WiFi לא הוסר (אולי מכשיר ללא WiFi, או נתיב אחר).");
                return;
            }
            var ms = new MemoryStream();
            reader.ReadData(target.Inode, (b, o, n) => ms.Write(b, o, n));
            wifiBackup = ms.ToArray();
            wifiBackupName = "wpa_supplicant.backup";
            target.Parent.Children.Remove(target);
            wifiRemoved = true;
            Log("  הוסר WiFi: נמחק " + target.Path + " (גיבוי יישמר ב-\"ערוך\"). קובצי ה-XML לא שונו.");
        }

        static FsEntry FindChild(FsEntry e, string name)
        {
            if (e == null || e.Children == null) return null;
            return e.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        }

        public static string PatchFstabText(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            var ext4Mounts = new HashSet<string>();
            foreach (var l in lines)
            {
                var c = Regex.Split(l.Trim(), @"\s+");
                if (c.Length >= 3 && !l.TrimStart().StartsWith("#") && c[2] == "ext4") ext4Mounts.Add(c[1]);
            }
            var outl = new List<string>();
            foreach (var l in lines)
            {
                outl.Add(l);
                if (l.TrimStart().StartsWith("#")) continue;
                var c = Regex.Split(l.Trim(), @"\s+");
                if (c.Length >= 5 && c[2] == "erofs" && !ext4Mounts.Contains(c[1]))
                {
                    outl.Add(string.Join(" ", new[] { c[0], c[1], "ext4", "ro,barrier=1,discard" }.Concat(c.Skip(4))));
                    ext4Mounts.Add(c[1]);
                }
            }
            return string.Join("\n", outl);
        }

        // ------------------------------------------------------------ planning
        long Align { get { long a = Md.BlockDevices[0].Alignment; return a <= 0 ? 1 << 20 : a; } }
        long FirstUsable { get { return Math.Max((long)Md.BlockDevices[0].FirstLogicalSector * 512, Md.MetadataEnd); } }

        static long RoundUp(long v, long a) { return (v + a - 1) / a * a; }

        public long FreeForGrowth()
        {
            long used = 0; int count = 0;
            foreach (var p in Parts)
            {
                if (p.Size <= 0) continue;
                used += p.Convert ? p.MinBytes : p.Size;
                count++;
            }
            long avail = DeviceSize - FirstUsable - count * Align;
            long free = avail - used;
            // group limits
            foreach (var gi in Parts.Where(p => p.Convert && p.Size > 0).Select(p => p.Lp.GroupIndex).Distinct())
            {
                var g = Md.Groups[(int)gi];
                if (g.MaximumSize == 0) continue;
                long gu = Parts.Where(p => p.Size > 0 && p.Lp.GroupIndex == gi).Sum(p => p.Convert ? p.MinBytes : p.Size);
                free = Math.Min(free, (long)g.MaximumSize - gu);
            }
            return free;
        }

        public void Plan(Settings st)
        {
            var conv = Parts.Where(p => p.Convert && p.Size > 0).ToList();
            long free = FreeForGrowth();
            if (free < 0) throw new InvalidDataException("אין מספיק מקום ב-super גם בלי להגדיל (חסרים " + Human(-free) + "). בטל המרה של חלק מהמחיצות.");
            long freeAfterMargin = Math.Max(0, free - (8L << 20));
            var extra = new Dictionary<PartInfo, long>();
            if (st.MaxMode)
            {
                int others = conv.Count(p => p.Category == "OT");
                foreach (var p in conv)
                {
                    double pct;
                    switch (p.Category)
                    {
                        case "S": pct = st.PctS; break;
                        case "V": pct = st.PctV; break;
                        case "P": pct = st.PctP; break;
                        case "SE": pct = st.PctSE; break;
                        default: pct = others > 0 ? st.PctOT / (double)others : 0; break;
                    }
                    int sameCat = conv.Count(x => x.Category == p.Category && p.Category != "OT");
                    if (sameCat > 1) pct /= sameCat;
                    extra[p] = (long)(freeAfterMargin * pct / 100.0);
                }
            }
            else
            {
                long total = 0;
                foreach (var p in conv)
                {
                    int mb = st.FixedMb;
                    if (st.FixedMbPer != null && st.FixedMbPer.ContainsKey(p.Category)) mb = st.FixedMbPer[p.Category];
                    if (st.PerPartitionMb != null && st.PerPartitionMb.ContainsKey(p.Name)) mb = st.PerPartitionMb[p.Name];
                    extra[p] = (long)mb << 20;
                    total += extra[p];
                }
                if (total > freeAfterMargin)
                    throw new InvalidDataException(string.Format("אין מספיק מקום: ביקשת להוסיף {0} בסך הכול, אבל פנויים ב-super רק {1}.", Human(total), Human(freeAfterMargin)));
            }
            foreach (var p in conv)
            {
                long target = (p.MinBytes + extra[p]) / 4096 * 4096;
                p.FinalBytes = p.Writer.ChooseBlocks(target) * 4096L;
            }
            foreach (var p in Parts) if (!p.Convert) p.FinalBytes = p.Size;

            // layout
            long pos = RoundUp(FirstUsable, Align);
            foreach (var p in Parts)
            {
                if (p.FinalBytes <= 0) continue;
                p.NewOffset = pos;
                pos = RoundUp(pos + p.FinalBytes, Align);
            }
            if (pos > DeviceSize) throw new InvalidDataException("החישוב חרג מגודל super - נסה אחוזים קטנים יותר");
            foreach (var p in conv)
                Log(string.Format("  {0}: {1} → {2} (פנוי לכתיבה ≈ {3})", p.Name, Human(p.Size), Human(p.FinalBytes), Human(p.FinalBytes - p.MinBytes)));
        }

        // ------------------------------------------------------------ write
        public string Write(Settings st, string outDir, string outName = "super_rw.img")
        {
            string fp = Path.Combine(outDir, outName), rp = st.SparseOutput ? fp + ".raw.tmp" : fp;
            try { return WriteInner(st, outDir, outName); }
            catch
            {
                // remove partial output (cancel / error)
                foreach (var f in new[] { rp, fp })
                    try { if (File.Exists(f) && !Path.GetFullPath(f).Equals(Path.GetFullPath(InputPath), StringComparison.OrdinalIgnoreCase)) File.Delete(f); } catch { }
                throw;
            }
        }

        string WriteInner(Settings st, string outDir, string outName)
        {
            string finalPath = Path.Combine(outDir, outName);
            string rawPath = st.SparseOutput ? finalPath + ".raw.tmp" : finalPath;
            if (Path.GetFullPath(finalPath).Equals(Path.GetFullPath(InputPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("קובץ הפלט זהה לקובץ הקלט");
            if (File.Exists(rawPath)) File.Delete(rawPath);

            // new metadata
            foreach (var p in Parts)
            {
                p.Lp.Extents = new List<LpMetadata.Extent>();
                if (p.FinalBytes > 0)
                    p.Lp.Extents.Add(new LpMetadata.Extent { NumSectors = (ulong)(p.FinalBytes / 512), TargetType = 0, TargetData = (ulong)(p.NewOffset / 512), TargetSource = 0 });
                if (p.Convert && p.FinalBytes > 0) p.Lp.Attributes &= ~LpMetadata.AttrReadonly;
            }

            long totalBytes = Parts.Sum(p => p.FinalBytes);
            long doneBytes = 0;
            using (var o = new FileStream(rawPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                o.SetLength(DeviceSize);
                Md.WriteAll(o);
                foreach (var p in Parts)
                {
                    if (p.FinalBytes <= 0) continue;
                    if (Cancelled()) throw new OperationCanceledException();
                    long baseDone = doneBytes;
                    if (p.Convert)
                    {
                        Log("כותב את " + p.Name + " כ-ext4 לקריאה וכתיבה...");
                        p.Writer.Write(o, p.NewOffset, p.FinalBytes, p.Reader,
                            pm => Progress(100 + (int)((baseDone + p.FinalBytes * pm / 1000) * 800 / Math.Max(1, totalBytes))), Cancelled);
                    }
                    else
                    {
                        Log("מעתיק את " + p.Name + " ללא שינוי...");
                        CopyPartition(p, o, baseDone, totalBytes);
                    }
                    doneBytes += p.FinalBytes;
                }
            }
            Progress(900);
            if (st.SparseOutput)
            {
                Log("יוצר קובץ sparse (קטן יותר, מתאים ל-fastboot)...");
                if (File.Exists(finalPath)) File.Delete(finalPath);
                SparseWriter.Convert(rawPath, finalPath, pm => Progress(900 + pm / 10), Cancelled);
                File.Delete(rawPath);
            }
            if (wifiRemoved && wifiBackup != null)
            {
                try { File.WriteAllBytes(Path.Combine(outDir, wifiBackupName), wifiBackup); Log("גיבוי wpa_supplicant נשמר: " + Path.Combine(outDir, wifiBackupName)); } catch { }
            }
            Progress(1000);
            return finalPath;
        }

        // original extents (saved before Write replaces them)
        readonly Dictionary<LpMetadata.Partition, List<LpMetadata.Extent>> origExtents = new Dictionary<LpMetadata.Partition, List<LpMetadata.Extent>>();
        public void SaveOriginalExtents()
        {
            foreach (var p in Md.Partitions) origExtents[p] = new List<LpMetadata.Extent>(p.Extents);
        }

        void CopyPartition(PartInfo p, Stream o, long baseDone, long totalBytes)
        {
            var tmp = new LpMetadata.Partition { Name = p.Lp.Name, Extents = origExtents[p.Lp] };
            var s = Md.OpenPartition(Super, tmp);
            var buf = new byte[1 << 20];
            long done = 0;
            while (done < p.FinalBytes)
            {
                if (Cancelled()) throw new OperationCanceledException();
                int n = (int)Math.Min(buf.Length, p.FinalBytes - done);
                s.Read(done, buf, 0, n);
                o.Position = p.NewOffset + done;
                o.Write(buf, 0, n);
                done += n;
                Progress(100 + (int)((baseDone + done) * 800 / Math.Max(1, totalBytes)));
            }
        }

        public static string PatchVbmeta(string superPath, string outDir, Action<string> log)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(superPath));
            string vb = Path.Combine(dir, "vbmeta.img");
            if (!File.Exists(vb)) return null;
            var b = File.ReadAllBytes(vb);
            if (b.Length < 256 || Encoding.ASCII.GetString(b, 0, 4) != "AVB0") { log("vbmeta.img לא תקין - דילגתי"); return null; }
            uint flags = (uint)(b[120] << 24 | b[121] << 16 | b[122] << 8 | b[123]); // big endian
            flags |= 3;
            b[120] = (byte)(flags >> 24); b[121] = (byte)(flags >> 16); b[122] = (byte)(flags >> 8); b[123] = (byte)flags;
            string outp = Path.Combine(outDir, "vbmeta_disabled.img");
            File.WriteAllBytes(outp, b);
            return outp;
        }

        public void Dispose() { if (Super != null) Super.Dispose(); }
    }

    public static class SparseWriter
    {
        public static void Convert(string rawPath, string outPath, Action<int> progress, Func<bool> cancelled)
        {
            const int BS = 4096;
            const int MaxRaw = 16384; // blocks per raw chunk (64MB)
            using (var inp = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var o = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                long total = (inp.Length + BS - 1) / BS;
                var hdr = new byte[28];
                o.Write(hdr, 0, 28);
                uint chunks = 0;
                var blk = new byte[BS];
                var pending = new MemoryStream();
                int pendRaw = 0; long zeroRun = 0;
                Action flushRaw = () =>
                {
                    if (pendRaw == 0) return;
                    var ch = new byte[12];
                    BitConverter.GetBytes((ushort)0xCAC1).CopyTo(ch, 0);
                    BitConverter.GetBytes((uint)pendRaw).CopyTo(ch, 4);
                    BitConverter.GetBytes((uint)(12 + pendRaw * BS)).CopyTo(ch, 8);
                    o.Write(ch, 0, 12);
                    pending.Position = 0; pending.CopyTo(o);
                    pending.SetLength(0); pendRaw = 0; chunks++;
                };
                Action flushZero = () =>
                {
                    if (zeroRun == 0) return;
                    var ch = new byte[12];
                    BitConverter.GetBytes((ushort)0xCAC3).CopyTo(ch, 0);
                    BitConverter.GetBytes((uint)zeroRun).CopyTo(ch, 4);
                    BitConverter.GetBytes(12u).CopyTo(ch, 8);
                    o.Write(ch, 0, 12);
                    zeroRun = 0; chunks++;
                };
                for (long i = 0; i < total; i++)
                {
                    if ((i & 0x3FFF) == 0) { progress((int)(i * 1000 / total)); if (cancelled()) throw new OperationCanceledException(); }
                    Array.Clear(blk, 0, BS);
                    int got = 0;
                    while (got < BS) { int n = inp.Read(blk, got, BS - got); if (n <= 0) break; got += n; }
                    bool zero = true;
                    for (int k = 0; k < BS; k += 8) if (BitConverter.ToInt64(blk, k) != 0) { zero = false; break; }
                    if (zero) { flushRaw(); zeroRun++; }
                    else { flushZero(); pending.Write(blk, 0, BS); pendRaw++; if (pendRaw >= MaxRaw) flushRaw(); }
                }
                flushRaw(); flushZero();
                BitConverter.GetBytes(SparseSource.Magic).CopyTo(hdr, 0);
                BitConverter.GetBytes((ushort)1).CopyTo(hdr, 4);
                BitConverter.GetBytes((ushort)0).CopyTo(hdr, 6);
                BitConverter.GetBytes((ushort)28).CopyTo(hdr, 8);
                BitConverter.GetBytes((ushort)12).CopyTo(hdr, 10);
                BitConverter.GetBytes((uint)BS).CopyTo(hdr, 12);
                BitConverter.GetBytes((uint)total).CopyTo(hdr, 16);
                BitConverter.GetBytes(chunks).CopyTo(hdr, 20);
                o.Position = 0; o.Write(hdr, 0, 28);
            }
        }
    }
}
