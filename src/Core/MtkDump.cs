using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Ro2rwPc
{
    /// <summary>GUID partition table parsed from mtkclient's gpt.bin (or any disk start).</summary>
    public sealed class Gpt
    {
        public sealed class Part { public string Name; public long FirstLba, LastLba; public long Start { get; set; } public long Size { get; set; } }
        public int SectorSize;
        public long LastUsableLba, AlternateLba, EntriesLba;
        public List<Part> Parts = new List<Part>();
        public long DiskSize { get { return (AlternateLba + 1) * SectorSize; } }

        public static Gpt Parse(byte[] d)
        {
            foreach (int ss in new[] { 512, 4096 })
            {
                if (d.Length < ss + 92 || Encoding.ASCII.GetString(d, ss, 8) != "EFI PART") continue;
                var g = new Gpt { SectorSize = ss };
                g.AlternateLba = BitConverter.ToInt64(d, ss + 32);
                g.LastUsableLba = BitConverter.ToInt64(d, ss + 48);
                g.EntriesLba = BitConverter.ToInt64(d, ss + 72);
                int count = BitConverter.ToInt32(d, ss + 80), esz = BitConverter.ToInt32(d, ss + 84);
                long eoff = g.EntriesLba * ss;
                for (int i = 0; i < count; i++)
                {
                    long o = eoff + (long)i * esz;
                    if (o + 128 > d.Length) break;
                    bool empty = true;
                    for (int k = 0; k < 16; k++) if (d[o + k] != 0) { empty = false; break; }
                    if (empty) continue;
                    var p = new Part
                    {
                        FirstLba = BitConverter.ToInt64(d, (int)o + 32),
                        LastLba = BitConverter.ToInt64(d, (int)o + 40),
                        Name = Encoding.Unicode.GetString(d, (int)o + 56, 72).TrimEnd('\0')
                    };
                    p.Start = p.FirstLba * ss;
                    p.Size = (p.LastLba - p.FirstLba + 1) * ss;
                    g.Parts.Add(p);
                }
                g.Parts.Sort((a, b) => a.FirstLba.CompareTo(b.FirstLba));
                return g;
            }
            throw new InvalidDataException("לא נמצאה טבלת GPT בקובץ");
        }
    }

    public static class ScatterWriter
    {
        /// <summary>Writes an SP Flash Tool scatter file (V1.1.x layout) for the dumped user area.</summary>
        public static string Write(string dir, string projectName, Gpt g, string platform, bool ufs, Func<string, string> fileForPartition)
        {
            platform = string.IsNullOrEmpty(platform) ? "MT6XXX" : platform;
            string region = ufs ? "UFS_LU2" : "EMMC_USER";
            string storage = ufs ? "HW_STORAGE_UFS" : "HW_STORAGE_EMMC";
            var sb = new StringBuilder();
            string bar = "############################################################################################################";
            sb.AppendLine(bar).AppendLine("#").AppendLine("#  General Setting").AppendLine("#").AppendLine(bar);
            sb.AppendLine("- general: MTK_PLATFORM_CFG");
            sb.AppendLine("  info: ");
            sb.AppendLine("    - config_version: V1.1.2");
            sb.AppendLine("      platform: " + platform);
            sb.AppendLine("      project: " + Regex.Replace(projectName, @"\s+", "_"));
            sb.AppendLine("      storage: " + (ufs ? "UFS" : "EMMC"));
            sb.AppendLine("      boot_channel: " + (ufs ? "UFS_0" : "MSDC_0"));
            sb.AppendLine("      block_size: 0x20000");
            sb.AppendLine(bar).AppendLine("#").AppendLine("#  Layout Setting").AppendLine("#").AppendLine(bar);

            int idx = 0;
            Action<string, string, bool, long, long, string, string> entry = (name, file, dl, start, size, type, op) =>
            {
                sb.AppendLine("- partition_index: SYS" + idx++);
                sb.AppendLine("  partition_name: " + name);
                sb.AppendLine("  file_name: " + (file ?? "NONE"));
                sb.AppendLine("  is_download: " + (dl ? "true" : "false"));
                sb.AppendLine("  type: " + type);
                sb.AppendLine("  linear_start_addr: 0x" + start.ToString("x"));
                sb.AppendLine("  physical_start_addr: 0x" + start.ToString("x"));
                sb.AppendLine("  partition_size: 0x" + size.ToString("x"));
                sb.AppendLine("  region: " + region);
                sb.AppendLine("  storage: " + storage);
                sb.AppendLine("  boundary_check: true");
                sb.AppendLine("  is_reserved: false");
                sb.AppendLine("  operation_type: " + op);
                sb.AppendLine("  is_upgradable: " + (dl ? "true" : "false"));
                sb.AppendLine("  empty_boot_needed: false");
                sb.AppendLine("  combo_partsize_check: false");
                sb.AppendLine("  reserve: 0x00");
                sb.AppendLine();
            };
            long pgptSize = g.Parts.Count > 0 ? g.Parts[0].Start : 0x8000;
            entry("pgpt", null, false, 0, pgptSize, "NORMAL_ROM", "INVISIBLE");
            foreach (var p in g.Parts)
            {
                string f = fileForPartition(p.Name);
                string lname = p.Name.ToLowerInvariant();
                string op = lname == "userdata" ? "INVISIBLE" : (f != null ? "UPDATE" : "INVISIBLE");
                entry(p.Name, f, f != null, p.Start, p.Size, "NORMAL_ROM", op);
            }
            long sgptStart = (g.LastUsableLba + 1) * g.SectorSize;
            long sgptSize = g.DiskSize - sgptStart;
            if (sgptSize > 0) entry("sgpt", null, false, sgptStart, sgptSize, "NORMAL_ROM", "RESERVED");

            string path = Path.Combine(dir, platform + "_Android_scatter.txt");
            File.WriteAllText(path, sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
            return path;
        }
    }

    /// <summary>Bundled mtkclient (mtk.exe) + UsbDk driver handling and the dump flow.</summary>
    public sealed class MtkClient
    {
        public Action<string> Log = s => { };
        public Action<string> Status = s => { };
        public Func<bool> Cancelled = () => false;

        public static string Dir { get { return Path.Combine(Project.ToolsDir, "mtkclient"); } }
        public static string Exe { get { return Path.Combine(Dir, "mtk.exe"); } }

        public static bool Bundled
        {
            get { return Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("mtk.exe"); }
        }

        /// <summary>Extracts the embedded mtk.exe next to the program (once per version).</summary>
        public static string EnsureExe()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("mtk.exe"))
            {
                if (s == null)
                {
                    if (File.Exists(Exe)) return Exe;
                    throw new FileNotFoundException("mtkclient לא מובנה בגרסה הזו. הורד את הגרסה מדף המהדורות.");
                }
                Directory.CreateDirectory(Dir);
                if (!File.Exists(Exe) || new FileInfo(Exe).Length != s.Length)
                {
                    using (var f = File.Create(Exe)) s.CopyTo(f);
                }
            }
            foreach (var res in new[] { "mtkclient-LICENSE.txt" })
                using (var s = asm.GetManifestResourceStream(res))
                    if (s != null && !File.Exists(Path.Combine(Dir, res)))
                        using (var f = File.Create(Path.Combine(Dir, res))) s.CopyTo(f);
            return Exe;
        }

        public static bool UsbDkInstalled
        {
            get
            {
                foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
                    if (!string.IsNullOrEmpty(pf) && File.Exists(Path.Combine(pf, "UsbDk Runtime Library", "UsbDkController.exe"))) return true;
                return File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "UsbDk.sys"));
            }
        }

        /// <summary>Downloads the official UsbDk x64 MSI (daynix/UsbDk) and starts the installer (asks for admin).</summary>
        public void InstallUsbDk()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            Log("מוריד את דרייבר UsbDk הרשמי (daynix/UsbDk)...");
            string json;
            using (var wc = new WebClient()) { wc.Headers.Add("User-Agent", "RO2RW-PC"); json = wc.DownloadString("https://api.github.com/repos/daynix/UsbDk/releases/latest"); }
            var m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+_x64\\.msi)\"");
            if (!m.Success) throw new IOException("לא נמצא קובץ התקנה של UsbDk");
            string msi = Path.Combine(Path.GetTempPath(), Path.GetFileName(m.Groups[1].Value));
            using (var wc = new WebClient()) { wc.Headers.Add("User-Agent", "RO2RW-PC"); wc.DownloadFile(m.Groups[1].Value, msi); }
            Log("מפעיל את ההתקנה (יש לאשר הרשאות מנהל)...");
            var p = Process.Start(new ProcessStartInfo("msiexec.exe", "/i \"" + msi + "\" /passive") { UseShellExecute = true, Verb = "runas" });
            p.WaitForExit();
            Log(p.ExitCode == 0 ? "UsbDk הותקן. ייתכן שיידרש אתחול של המחשב." : "התקנת UsbDk הסתיימה עם קוד " + p.ExitCode);
        }

        /// <summary>Runs mtk.exe with arguments, streaming output. Returns exit code and the full log text.</summary>
        /// <summary>mtkclient over the MTK VCOM serial COM port (--serialport): same driver SP Flash Tool uses, no UsbDk.</summary>
        public static bool Serial = false;
        public static string Com = null;   // null/empty = auto-detect (DETECT)
        static string SerialArg { get { return Serial ? " --serialport" + (string.IsNullOrEmpty(Com) ? "" : " " + Com) : ""; } }

        public int Run(string args, string workDir, StringBuilder captured) { return RunExe(EnsureExe(), args + SerialArg, workDir, captured, "mtk"); }

        /// <summary>Runs any bundled exe with streamed output, cancellable. Shared by mtkclient and the fast engine.</summary>
        public int RunExe(string exe, string args, string workDir, StringBuilder captured, string label)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                CreateNoWindow = true, WorkingDirectory = workDir,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            Log("> " + label + " " + args);
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close();
                var t1 = new Thread(() => Pump(p.StandardOutput, captured)) { IsBackground = true };
                var t2 = new Thread(() => Pump(p.StandardError, captured)) { IsBackground = true };
                t1.Start(); t2.Start();
                while (!p.WaitForExit(300))
                {
                    if (Cancelled()) { try { p.Kill(); } catch { } throw new OperationCanceledException(); }
                }
                t1.Join(2000); t2.Join(2000);
                return p.ExitCode;
            }
        }

        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[A-Za-z]");

        void Pump(StreamReader r, StringBuilder captured)
        {
            var line = new StringBuilder();
            var buf = new char[1];
            while (r.Read(buf, 0, 1) > 0)
            {
                char c = buf[0];
                if (c == '\n' || c == '\r')
                {
                    string s = Ansi.Replace(line.ToString(), "").TrimEnd();
                    line.Clear();
                    if (s.Length == 0) continue;
                    lock (captured) captured.AppendLine(s);
                    if (c == '\r' || s.Contains("Progress:") || Regex.IsMatch(s, @"\d+(\.\d+)?%")) Status(s);
                    else Log("  " + s);
                }
                else line.Append(c);
            }
            if (line.Length > 0) { var s = Ansi.Replace(line.ToString(), "").Trim(); if (s.Length > 0) { lock (captured) captured.AppendLine(s); Log("  " + s); } }
        }

        /// <summary>Full dump into the project: mtk rl → rename to .img → scatter.</summary>
        public int DumpProject(Project proj, bool skipUserdata)
        {
            string tmp = Path.Combine(proj.Root, "_dump");
            Directory.CreateDirectory(tmp);
            var log = new StringBuilder();
            string args = "rl \"" + tmp + "\"" + (skipUserdata ? " --skip userdata" : "");
            int rc = Run(args, proj.Root, log);
            string text = log.ToString();
            File.WriteAllText(Path.Combine(proj.Root, "mtkclient_log.txt"), text, new UTF8Encoding(true));
            string gptPath = Path.Combine(tmp, "gpt.bin");
            if (!File.Exists(gptPath))
                throw new IOException(rc != 0 ? "mtkclient נכשל (קוד " + rc + "). ראה את היומן ואת mtkclient_log.txt" : "לא נוצרה טבלת מחיצות - השאיבה לא הושלמה");

            return FinishDump(proj, tmp, gptPath, text, Log);
        }

        /// <summary>Moves dumped &lt;name&gt;.bin/.img into the project, writes gpt.bin, generates a scatter. Shared by both engines.</summary>
        public static int FinishDump(Project proj, string tmp, string gptPath, string logText, Action<string> Log)
        {
            var g = Gpt.Parse(File.ReadAllBytes(gptPath));
            Log(string.Format("טבלת מחיצות: {0} מחיצות, סקטור {1}, דיסק {2}", g.Parts.Count, g.SectorSize, Converter.Human(g.DiskSize)));
            int moved = 0;
            foreach (var p in g.Parts)
            {
                string src = Path.Combine(tmp, p.Name + ".bin");
                if (!File.Exists(src)) src = Path.Combine(tmp, p.Name + ".img");
                if (!File.Exists(src)) continue;
                string dst = Path.Combine(proj.Root, p.Name + ".img");
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
                moved++;
            }
            string gptDst = Path.Combine(proj.Root, "gpt.bin");
            if (Path.GetFullPath(gptPath) != Path.GetFullPath(gptDst)) { if (File.Exists(gptDst)) File.Delete(gptDst); File.Move(gptPath, gptDst); }
            foreach (var f in Directory.GetFiles(tmp)) { var d = Path.Combine(proj.Root, Path.GetFileName(f)); if (!File.Exists(d)) File.Move(f, d); }
            try { Directory.Delete(tmp, true); } catch { }

            var pm = Regex.Match(logText ?? "", @"\b(MT[0-9]{4}[A-Z]?)\b");
            string platform = pm.Success ? pm.Groups[1].Value.ToUpperInvariant() : null;
            bool ufs = g.SectorSize == 4096 || Regex.IsMatch(logText ?? "", @"\bUFS\b");
            string scatter = ScatterWriter.Write(proj.Root, proj.Name, g, platform, ufs,
                name => File.Exists(Path.Combine(proj.Root, name + ".img")) ? name + ".img" : null);
            Log("נוצר קובץ scatter: " + Path.GetFileName(scatter) + (platform == null ? " (לא זוהה מעבד - ערוך את שורת platform)" : ""));
            Log("הערה: preloader אינו נשאב ואינו נכלל ב-scatter (נמצא באזור BOOT1).");
            return moved;
        }
    }
}

namespace Ro2rwPc
{
    /// <summary>Builds a scatter inside "ערוך" so SP Flash Tool flashes only the edited partitions.</summary>
    public static class EditedScatter
    {
        static string FindEdited(string editedDir, string fileName, string partName)
        {
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(fileName) && fileName != "NONE") cands.Add(Path.GetFileName(fileName));
            cands.Add(partName + ".img"); cands.Add(partName + ".bin");
            foreach (var c in cands)
                if (File.Exists(Path.Combine(editedDir, c))) return c;
            return null;
        }

        /// <returns>path of the new scatter, or null if no source (stock scatter / gpt.bin) exists.</returns>
        public static string Create(Project proj, Action<string> log)
        {
            string src = Directory.GetFiles(proj.Root, "*_scatter.txt").OrderBy(f => f).FirstOrDefault()
                      ?? Directory.GetFiles(proj.Root, "*scatter*.txt").OrderBy(f => f).FirstOrDefault();
            var enabled = new List<string>();
            string outPath;
            if (src != null)
            {
                var lines = File.ReadAllLines(src).ToList();
                // split into blocks starting at "partition_index"
                var starts = new List<int>();
                for (int i = 0; i < lines.Count; i++) if (Regex.IsMatch(lines[i], @"^\s*-?\s*partition_index\s*:")) starts.Add(i);
                for (int b = 0; b < starts.Count; b++)
                {
                    int s = starts[b], e = b + 1 < starts.Count ? starts[b + 1] : lines.Count;
                    string name = null, file = null; int fileLine = -1, dlLine = -1;
                    for (int i = s; i < e; i++)
                    {
                        var m = Regex.Match(lines[i], @"^(\s*)(partition_name|file_name|is_download)\s*:\s*(.*?)\s*$");
                        if (!m.Success) continue;
                        if (m.Groups[2].Value == "partition_name") name = m.Groups[3].Value;
                        else if (m.Groups[2].Value == "file_name") { file = m.Groups[3].Value; fileLine = i; }
                        else dlLine = i;
                    }
                    if (name == null || dlLine < 0) continue;
                    string ed = FindEdited(proj.Edited, file, name);
                    string indent = Regex.Match(lines[dlLine], @"^\s*").Value;
                    lines[dlLine] = indent + "is_download: " + (ed != null ? "true" : "false");
                    if (ed != null)
                    {
                        if (fileLine >= 0) lines[fileLine] = Regex.Match(lines[fileLine], @"^\s*").Value + "file_name: " + ed;
                        enabled.Add(name);
                    }
                }
                outPath = Path.Combine(proj.Edited, Path.GetFileName(src));
                File.WriteAllLines(outPath, lines, new UTF8Encoding(false));
                log("נוצר scatter ל-SP Flash Tool (על בסיס " + Path.GetFileName(src) + "): " + Project.EditedDir + "\\" + Path.GetFileName(outPath));
            }
            else if (File.Exists(Path.Combine(proj.Root, "gpt.bin")))
            {
                var g = Gpt.Parse(File.ReadAllBytes(Path.Combine(proj.Root, "gpt.bin")));
                string platform = null;
                var mlog = Path.Combine(proj.Root, "mtkclient_log.txt");
                if (File.Exists(mlog)) { var pm = Regex.Match(File.ReadAllText(mlog), @"\b(MT[0-9]{4}[A-Z]?)\b"); if (pm.Success) platform = pm.Groups[1].Value; }
                outPath = ScatterWriter.Write(proj.Edited, proj.Name, g, platform, g.SectorSize == 4096, n =>
                {
                    var ed = FindEdited(proj.Edited, null, n);
                    if (ed != null) enabled.Add(n);
                    return ed;
                });
                log("נוצר scatter ל-SP Flash Tool (מטבלת gpt.bin): " + Project.EditedDir + "\\" + Path.GetFileName(outPath));
            }
            else
            {
                log("! לא נוצר scatter: אין בפרויקט קובץ scatter מהקושחה או gpt.bin. העתק לפרויקט את קובץ ה-*_scatter.txt מהקושחה ובצע שוב.");
                return null;
            }
            log("  מסומנות לצריבה: " + (enabled.Count > 0 ? string.Join(", ", enabled) : "(אף מחיצה)") + ". שאר המחיצות לא ייצרבו.");
            return outPath;
        }
    }
}
