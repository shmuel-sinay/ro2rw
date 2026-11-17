using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ro2rwPc
{
    /// <summary>
    /// Drives a user-installed SP Flash Tool in console mode (flash_tool.exe -b -i config.xml)
    /// to flash the edited partitions automatically. SP Flash Tool itself is NOT bundled
    /// (closed source) - the user points to their own flash_tool.exe.
    /// The generated config performs DOWNLOAD ONLY - never a format.
    /// </summary>
    public static class SpFlashTool
    {
        public sealed class Rom { public int Index; public string Name; public string File; }

        /// <summary>Parses an SP scatter to the list of partitions marked is_download=true that have a real file.</summary>
        public static List<Rom> EnabledRoms(string scatterPath, string filesDir)
        {
            var text = File.ReadAllText(scatterPath).Replace("\r\n", "\n");
            var blocks = Regex.Split(text, @"(?m)^-?\s*partition_index\s*:");
            var roms = new List<Rom>();
            for (int i = 1; i < blocks.Length; i++)
            {
                var b = "partition_index:" + blocks[i];
                int idx = ParseIndex(Field(b, "partition_index"));
                string name = Field(b, "partition_name");
                string file = Field(b, "file_name");
                bool dl = (Field(b, "is_download") ?? "").Trim().ToLowerInvariant() == "true";
                if (!dl || string.IsNullOrEmpty(file) || file.ToUpperInvariant() == "NONE") continue;
                string full = Path.IsPathRooted(file) ? file : Path.Combine(filesDir, Path.GetFileName(file));
                if (!File.Exists(full)) continue;
                roms.Add(new Rom { Index = idx, Name = name, File = full });
            }
            return roms;
        }

        static string Field(string block, string key)
        {
            var m = Regex.Match(block, @"(?m)^\s*" + Regex.Escape(key) + @"\s*:\s*(.+?)\s*$");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        static int ParseIndex(string s)
        {
            if (s == null) return -1;
            var m = Regex.Match(s, @"(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        public static string FindDa(string flashToolExe)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(flashToolExe));
            foreach (var n in new[] { "MTK_AllInOne_DA.bin", "DA_PL.bin", "MTK_AllInOne_DA_5.2308.bin" })
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
            var any = Directory.GetFiles(dir, "MTK_AllInOne_DA*.bin").OrderBy(x => x).FirstOrDefault();
            return any;
        }

        /// <summary>Writes a console-mode config.xml (download-only) and returns its path.</summary>
        public static string BuildConfig(string outDir, string scatterPath, string daPath, string platform, bool ufs, List<Rom> roms, string comPort)
        {
            if (roms.Count == 0) throw new InvalidOperationException("אין מחיצות מסומנות לצריבה ב-scatter");
            if (string.IsNullOrEmpty(daPath) || !File.Exists(daPath))
                throw new FileNotFoundException("לא נמצא קובץ DA (MTK_AllInOne_DA.bin). בחר אותו מתיקיית SP Flash Tool.");
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<flashtool-config version=\"2.0\">");
            sb.AppendLine("  <general>");
            sb.AppendLine("    <chip-name>" + (string.IsNullOrEmpty(platform) ? "MT6765" : platform) + "</chip-name>");
            sb.AppendLine("    <storage-type>" + (ufs ? "UFS" : "EMMC") + "</storage-type>");
            sb.AppendLine("    <download-agent>" + Esc(daPath) + "</download-agent>");
            sb.AppendLine("    <scatter>" + Esc(scatterPath) + "</scatter>");
            sb.AppendLine("    <rom-list>");
            foreach (var r in roms)
                sb.AppendLine("      <rom index=\"" + r.Index + "\" enable=\"true\">" + Esc(r.File) + "</rom>");
            sb.AppendLine("    </rom-list>");
            if (!string.IsNullOrEmpty(comPort))
                sb.AppendLine("    <connection type=\"BromUART\" port=\"" + Esc(comPort) + "\"/>");
            sb.AppendLine("  </general>");
            sb.AppendLine("  <commands>");
            sb.AppendLine("    <!-- DOWNLOAD ONLY - no format, so nothing is erased -->");
            sb.AppendLine("    <da-download-all>");
            sb.AppendLine("    </da-download-all>");
            sb.AppendLine("  </commands>");
            sb.AppendLine("</flashtool-config>");
            string path = Path.Combine(outDir, "sp_flash_config.xml");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        static string Esc(string s) { return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"); }

        /// <summary>Checks whether flash_tool.exe exposes a console/batch mode (-i / -b / console).</summary>
        public static bool SupportsConsole(string flashToolExe, MtkClient runner)
        {
            try
            {
                var cap = new StringBuilder();
                runner.RunExe(flashToolExe, "--help", Path.GetDirectoryName(flashToolExe), cap, "flash_tool");
                string h = cap.ToString().ToLowerInvariant();
                return h.Contains("-i") || h.Contains("console") || h.Contains("-b") || h.Contains("scatter");
            }
            catch { return false; }
        }

        /// <summary>Runs flash_tool.exe -b -i config.xml, streaming output. Returns true on success.</summary>
        public static void Run(string flashToolExe, string configPath, string comPort, MtkClient runner)
        {
            string args = "-b -i \"" + configPath + "\"" + (string.IsNullOrEmpty(comPort) ? "" : " -p " + comPort);
            var cap = new StringBuilder();
            int rc = runner.RunExe(flashToolExe, args, Path.GetDirectoryName(flashToolExe), cap, "flash_tool");
            string text = cap.ToString();
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(configPath), "sp_flash_log.txt"), text, new UTF8Encoding(true)); } catch { }
            string low = text.ToLowerInvariant();
            bool ok = rc == 0 && (low.Contains("download ok") || low.Contains("all command exec done") || low.Contains("pass") || low.Contains("100"));
            bool bad = low.Contains("error") || low.Contains("fail") || low.Contains("s_") && low.Contains("err");
            if (rc != 0 || (bad && !low.Contains("download ok")))
                throw new IOException("SP Flash Tool הסתיימה עם שגיאה (קוד " + rc + "). ראה sp_flash_log.txt, ושלח לי אותו.");
        }
    }
}
