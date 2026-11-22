using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Ro2rwPc;

// Read-only SP probe: one session that reads the GPT and the preloader under every candidate part-id,
// then reports which ones returned valid data. Nothing is ever written to the device.
//   spprobe <flash_tool.exe> <chip e.g. MT6765> <outDir> [ufs] [COMx]
static class Probe
{
    static int Main(string[] a)
    {
        if (a.Length < 3) { Console.WriteLine("usage: spprobe <flash_tool.exe> <chip> <outDir> [ufs] [COMx]"); return 2; }
        string ft = a[0], chip = a[1].ToUpperInvariant(), dir = Path.GetFullPath(a[2]);
        bool ufs = a.Skip(3).Any(x => x.Equals("ufs", StringComparison.OrdinalIgnoreCase));
        string com = a.Skip(3).FirstOrDefault(x => x.StartsWith("COM", StringComparison.OrdinalIgnoreCase));
        string da = SpFlashTool.FindDa(ft);
        if (da == null) { Console.WriteLine("DA not found next to flash_tool.exe"); return 2; }
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "*.bin")) File.Delete(f);

        string scatter = SpDump.WriteMinimalScatter(dir, chip, ufs);
        var ranges = SpDump.UserPartIds(ufs).Select(id => new SpRange { PartId = id, Start = 0, Length = SpDump.GptReadBytes, OutPath = Path.Combine(dir, "gpt_" + id + ".bin") })
            .Concat(SpDump.Boot1PartIds.Select(id => new SpRange { PartId = id, Start = 0, Length = SpDump.PreloaderReadBytes, OutPath = Path.Combine(dir, "pre_" + id + ".bin") })).ToList();
        string cfg = SpDump.BuildRangeConfig(dir, "probe_config.xml", da, chip, ufs, com, scatter, ranges);
        Console.WriteLine("config: " + cfg);
        Console.WriteLine("Connect the powered-off device now (waiting up to 10 minutes)...");

        var log = new StringBuilder();
        var sw = Stopwatch.StartNew();
        // SP fails the BROM handshake when the cable goes in at the wrong moment (STATUS_BROM_CMD_STARTCMD_FAIL): start it again and let the user re-plug
        for (int attempt = 1; attempt <= 15 && sw.Elapsed < TimeSpan.FromMinutes(14); attempt++)
        {
            var psi = new ProcessStartInfo(ft, "-r -i \"" + cfg + "\"" + (com != null ? " -p " + com : ""))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ft), StandardOutputEncoding = Encoding.UTF8 };
            var one = new StringBuilder();
            Console.WriteLine("--- attempt " + attempt + ": SP is waiting - plug in the powered-off device now");
            int code;
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (log) { one.AppendLine(e.Data); log.AppendLine(e.Data); Console.WriteLine("  " + e.Data); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) { one.AppendLine(e.Data); log.AppendLine(e.Data); } };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(13 * 60 * 1000)) { try { p.Kill(); } catch { } Console.WriteLine("TIMEOUT: no device connected, SP stopped."); code = -999; }
                else { p.WaitForExit(); code = p.ExitCode; }
            }
            Console.WriteLine("exit code: " + code + "  (" + sw.Elapsed.TotalSeconds.ToString("0") + "s)");
            if (code == 0 || one.ToString().IndexOf("STARTCMD_FAIL", StringComparison.OrdinalIgnoreCase) < 0) break;
            Console.WriteLine("*** handshake failed: UNPLUG the cable now, wait 2 seconds, then plug it in again (hold a volume key while plugging). Restarting SP in 5s...");
            Thread.Sleep(5000);
        }
        File.WriteAllText(Path.Combine(dir, "probe_log.txt"), log.ToString(), new UTF8Encoding(true));

        Console.WriteLine();
        Console.WriteLine("=== results ===");
        foreach (var r in ranges)
        {
            string name = Path.GetFileName(r.OutPath);
            if (!File.Exists(r.OutPath)) { Console.WriteLine(name.PadRight(14) + " part-id " + r.PartId + ": no file"); continue; }
            var b = File.ReadAllBytes(r.OutPath);
            bool gpt = name.StartsWith("gpt") && SpDump.LooksLikeGpt(b), pre = name.StartsWith("pre") && SpDump.LooksLikePreloader(b);
            Console.WriteLine(name.PadRight(14) + " part-id " + r.PartId + ": " + b.Length + " bytes, " + (gpt ? "VALID GPT" : pre ? "VALID PRELOADER (MMM)" : b.All(x => x == 0) ? "all zeros" : "data, not recognized"));
        }
        var good = ranges.Where(r => r.OutPath.Contains("gpt_") && File.Exists(r.OutPath) && SpDump.LooksLikeGpt(File.ReadAllBytes(r.OutPath))).FirstOrDefault();
        if (good != null)
        {
            var g = Gpt.Parse(File.ReadAllBytes(good.OutPath));
            Console.WriteLine();
            Console.WriteLine("GPT via part-id " + good.PartId + ": " + g.Parts.Count + " partitions, sector " + g.SectorSize + ", disk " + Converter.Human(g.DiskSize));
            foreach (var p in g.Parts) Console.WriteLine("  " + p.Name.PadRight(18) + " start 0x" + p.Start.ToString("x").PadRight(12) + " size " + Converter.Human(p.Size));
            File.Copy(good.OutPath, Path.Combine(dir, "gpt.bin"), true);
        }
        else Console.WriteLine("No valid GPT under any part-id.");
        return good != null ? 0 : 1;
    }
}
