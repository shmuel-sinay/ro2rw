using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ro2rwPc
{
    /// <summary>One address range read by SP Flash Tool ("ReadBack" by start address and length).</summary>
    public sealed class SpRange
    {
        public int PartId;      // storage region: eMMC 1=BOOT1, 8=USER (SP's part-id numbering)
        public long Start, Length;
        public string OutPath;
    }

    /// <summary>Everything the SP-based dump needs that does not touch the UI: console configs, GPT sanity check, splitting a full dump.</summary>
    public static class SpDump
    {
        /// <summary>The primary GPT (header + 128 entries) fits in the first 32 KiB for both 512 and 4096 byte sectors.</summary>
        public const long GptReadBytes = 0x8000;
        /// <summary>SP's own readback_ui_bak.xml sample uses part-id 34 for a user-area partition and 32 for the preloader; 8 and 1 are the DA enum values. The program tries both and keeps the one that returns valid data.</summary>
        public static readonly int[] Boot1PartIds = { 32, 1 };
        public static bool LooksLikePreloader(byte[] b) { return b != null && b.Length > 4 && b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x4D; }
        public const long PreloaderReadBytes = 0x100000;

        public static int[] UserPartIds(bool ufs) { return ufs ? new[] { 34, 2, 3 } : new[] { 34, 8 }; }
        public static int UserPartId(bool ufs) { return UserPartIds(ufs)[0]; }

        static readonly Regex EditNames = new Regex(@"^(boot|init_boot|vendor_boot|recovery|dtbo|vbmeta|vbmeta_system|vbmeta_vendor|lk|logo|super)(_[ab])?$", RegexOptions.IgnoreCase);

        /// <summary>Partitions that matter for editing (boot, vbmeta, lk, super...), with or without a slot suffix.</summary>
        public static bool IsEditPartition(string name) { return EditNames.IsMatch(name); }

        public static bool LooksLikeGpt(byte[] head)
        {
            try { return head != null && Gpt.Parse(head).Parts.Count > 0; } catch { return false; }
        }

        /// <summary>Bytes from the start of the user area to the end of the last selected partition.</summary>
        /// <summary>SP reads the readback length as 32 bits: a range of 4 GiB or more silently wraps (5830082560 came back as 1535115264). Each read stays well below that.</summary>
        public const long MaxReadBytes = 1L << 30;

        /// <summary>Splits one range into consecutive reads of at most <paramref name="max"/> bytes; the pieces are written to OutPath + ".partN" (a single piece keeps OutPath).</summary>
        public static List<SpRange> Chunk(SpRange r, long max)
        {
            var list = new List<SpRange>();
            if (r.Length <= max) { list.Add(r); return list; }
            for (long off = 0; off < r.Length; off += max)
                list.Add(new SpRange { PartId = r.PartId, Start = r.Start + off, Length = Math.Min(max, r.Length - off), OutPath = r.OutPath + ".part" + list.Count });
            return list;
        }

        /// <summary>Joins the pieces made by <see cref="Chunk"/> back into <paramref name="target"/> and deletes them. Returns the total size; a missing or short piece stops the join, so the result is a shorter file.</summary>
        public static long Join(IList<SpRange> pieces, string target, long expectedTotal)
        {
            if (pieces.Count == 1 && pieces[0].OutPath == target) return File.Exists(target) ? new FileInfo(target).Length : 0;
            long total = 0;
            using (var o = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                foreach (var p in pieces)
                {
                    if (!File.Exists(p.OutPath)) break;
                    var fi = new FileInfo(p.OutPath);
                    using (var s = new FileStream(p.OutPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) s.CopyTo(o, 1 << 20);
                    total += fi.Length;
                    if (fi.Length < p.Length) break;
                }
            }
            foreach (var p in pieces) { try { File.Delete(p.OutPath); } catch { } }
            return total;
        }

        public static long RangeLength(Gpt g, IEnumerable<string> names)
        {
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            long end = 0;
            foreach (var p in g.Parts) if (set.Contains(p.Name)) end = Math.Max(end, p.Start + p.Size);
            return end;
        }

        /// <summary>Cuts the selected partitions out of a dump that starts at address 0 of the user area. Partitions that extend past the end of the dump are skipped.</summary>
        public static List<string> Split(string fullFile, Gpt g, IEnumerable<string> names, string outDir, Func<bool> cancelled, Action<int> progress, out List<string> skipped)
        {
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var written = new List<string>(); skipped = new List<string>();
            Directory.CreateDirectory(outDir);
            using (var src = new FileStream(fullFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var todo = g.Parts.Where(p => set.Contains(p.Name)).ToList();
                long total = Math.Max(1, todo.Where(p => p.Start + p.Size <= src.Length).Sum(p => p.Size)), done = 0;
                var buf = new byte[4 << 20];
                foreach (var p in todo)
                {
                    if (p.Start + p.Size > src.Length) { skipped.Add(p.Name); continue; }
                    string dst = Path.Combine(outDir, p.Name + ".img");
                    src.Position = p.Start;
                    using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        long left = p.Size;
                        while (left > 0)
                        {
                            if (cancelled != null && cancelled()) { o.Close(); try { File.Delete(dst); } catch { } throw new OperationCanceledException(); }
                            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) break;
                            o.Write(buf, 0, n);
                            left -= n; done += n;
                            if (progress != null) progress((int)(done * 1000 / total));
                        }
                    }
                    written.Add(p.Name);
                }
            }
            return written;
        }

        static string Hex(long v) { return "0x" + v.ToString("x16"); }

        static void CheckBase(string platform, string da)
        {
            if (string.IsNullOrEmpty(platform) || !Regex.IsMatch(platform, @"^MT[0-9]{4}[A-Z]?$"))
                throw new InvalidOperationException("לא זוהה מעבד (למשל MT6765). הזן אותו ידנית.");
            if (string.IsNullOrEmpty(da) || !File.Exists(da))
                throw new FileNotFoundException("לא נמצא קובץ DA (MTK_AllInOne_DA.bin). בחר אותו מתיקיית SP Flash Tool.");
        }

        static string Header(string platform, string da, bool ufs, string scatterPath, string comPort, string disabledRom = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>");
            sb.AppendLine("<flashtool-config version=\"2.0\">");
            sb.AppendLine("  <general>");
            sb.AppendLine("    <chip-name>" + platform + "</chip-name>");
            sb.AppendLine("    <storage-type>" + (ufs ? "UFS" : "EMMC") + "</storage-type>");
            sb.AppendLine("    <download-agent>" + SpFlashTool.Esc(da) + "</download-agent>");
            sb.AppendLine("    <scatter>" + (string.IsNullOrEmpty(scatterPath) ? "" : SpFlashTool.Esc(scatterPath)) + "</scatter>");
            sb.AppendLine(disabledRom == null ? "    <rom-list></rom-list>" : "    <rom-list><rom index=\"0\" enable=\"false\">" + SpFlashTool.Esc(disabledRom) + "</rom></rom-list>");
            sb.AppendLine("    " + SpFlashTool.Connection(comPort));
            sb.AppendLine("  </general>");
            sb.AppendLine("  <commands>");
            return sb.ToString();
        }

        /// <summary>Console config that reads raw address ranges (GPT, preloader, or a whole user-area dump). Read only.</summary>
        public static string BuildRangeConfig(string cfgDir, string fileName, string da, string platform, bool ufs, string comPort, string scatterPath, IList<SpRange> ranges)
        {
            CheckBase(platform, da);
            if (ranges == null || ranges.Count == 0) throw new InvalidOperationException("אין טווחים לשאיבה");
            // SP matches the chip against the scatter's platform (an empty scatter fails with STATUS_SCATTER_HW_CHIP_ID_MISMATCH), and with a scatter but an
            // empty rom-list it tries to auto-load images and exits (S_FILEPATH_NOT_SPECIFIED_YET). A scatter plus one disabled rom entry avoids both.
            string placeholder = null;
            if (!string.IsNullOrEmpty(scatterPath))
            {
                placeholder = Path.Combine(Path.GetDirectoryName(scatterPath), "placeholder.img");
                if (!File.Exists(placeholder)) File.WriteAllBytes(placeholder, new byte[16]);
            }
            var sb = new StringBuilder(Header(platform, da, ufs, scatterPath, comPort, placeholder));
            sb.AppendLine("    <!-- READ ONLY - nothing is written to the device -->");
            sb.AppendLine("    <readback>");
            sb.AppendLine("      <physical-readback is-physical-readback=\"false\" />");
            sb.AppendLine("      <readback-list>");
            for (int i = 0; i < ranges.Count; i++)
            {
                var r = ranges[i];
                sb.AppendLine("        <readback-rom-item readback-index=\"" + i + "\" readback-enable=\"true\" readback-flag=\"NUTL_READ_PAGE_ONLY\" start-address=\"" + Hex(r.Start) +
                    "\" readback-length=\"" + Hex(r.Length) + "\" addr-flag=\"NUTL_ADDR_LOGICAL\" part-id=\"" + r.PartId + "\">" + SpFlashTool.Esc(r.OutPath) + "</readback-rom-item>");
            }
            sb.AppendLine("      </readback-list>");
            sb.AppendLine("    </readback>");
            sb.AppendLine("  </commands>");
            sb.AppendLine("</flashtool-config>");
            Directory.CreateDirectory(cfgDir);
            string path = Path.Combine(cfgDir, fileName);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        /// <summary>Console config that flashes by partition name (no scatter needed). Download only, never a format.</summary>
        public static string BuildByNameFlashConfig(string cfgDir, string da, string platform, bool ufs, string comPort, IList<KeyValuePair<string, string>> partitionToFile)
        {
            CheckBase(platform, da);
            if (partitionToFile == null || partitionToFile.Count == 0) throw new InvalidOperationException("אין מחיצות לצריבה");
            var sb = new StringBuilder(Header(platform, da, ufs, null, comPort));
            sb.AppendLine("    <!-- DOWNLOAD ONLY - no format, so nothing is erased -->");
            sb.AppendLine("    <download-withoutScatter>");
            sb.AppendLine("      <download-list>");
            foreach (var kv in partitionToFile)
                sb.AppendLine("        <download-rom-item partition=\"" + SpFlashTool.Esc(kv.Key) + "\">" + SpFlashTool.Esc(kv.Value) + "</download-rom-item>");
            sb.AppendLine("      </download-list>");
            sb.AppendLine("    </download-withoutScatter>");
            sb.AppendLine("  </commands>");
            sb.AppendLine("</flashtool-config>");
            Directory.CreateDirectory(cfgDir);
            string path = Path.Combine(cfgDir, "sp_flash_byname_config.xml");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        /// <summary>A scatter with only the pgpt entry - enough for SP to know the chip and storage (what WwR's "Auto mode" makes).</summary>
        public static string WriteMinimalScatter(string dir, string platform, bool ufs)
        {
            Directory.CreateDirectory(dir);
            return ScatterWriter.Write(dir, "empty", new Gpt(), platform, ufs, n => null, platform + "_empty_scatter.txt");
        }

        /// <summary>Lists the partitions of an existing scatter (name, file, is_download, start, size).</summary>
        public static List<ScatterEntry> ReadScatter(string path)
        {
            var list = new List<ScatterEntry>();
            ScatterEntry cur = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var l = raw.Trim();
                if (Regex.IsMatch(l, @"^-?\s*partition_index\s*:")) { cur = new ScatterEntry(); list.Add(cur); continue; }
                if (cur == null) continue;
                var m = Regex.Match(l, @"^(partition_name|file_name|is_download|linear_start_addr|partition_size|region)\s*:\s*(\S+)");
                if (!m.Success) continue;
                string v = m.Groups[2].Value;
                switch (m.Groups[1].Value)
                {
                    case "partition_name": cur.Name = v; break;
                    case "file_name": cur.File = v == "NONE" ? null : v; break;
                    case "is_download": cur.Download = v.ToLowerInvariant() == "true"; break;
                    case "linear_start_addr": cur.Start = ParseHex(v); break;
                    case "partition_size": cur.Size = ParseHex(v); break;
                    case "region": cur.Region = v; break;
                }
            }
            return list;
        }

        static long ParseHex(string v)
        {
            try { return v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(v.Substring(2), 16) : long.Parse(v); } catch { return 0; }
        }
    }

    public sealed class ScatterEntry
    {
        public string Name, File, Region;
        public bool Download;
        public long Start, Size;
    }
}
