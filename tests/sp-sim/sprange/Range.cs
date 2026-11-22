using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Ro2rwPc;

// Read-only SP dump: one session reads the user area from 0 up to the end of the last requested partition, then splits it by the GPT.
//   sprange <flash_tool.exe> <chip> <gpt.bin> <outDir> <part-id> <name,name,...>
static class RangeTool
{
    static int Main(string[] a)
    {
        if (a.Length < 6) { Console.WriteLine("usage: sprange <flash_tool.exe> <chip> <gpt.bin> <outDir> <part-id> <names>"); return 2; }
        string ft = a[0], chip = a[1].ToUpperInvariant(), dir = Path.GetFullPath(a[3]);
        int partId = int.Parse(a[4]);
        var names = a[5].Split(',');
        var g = Gpt.Parse(File.ReadAllBytes(a[2]));
        string da = SpFlashTool.FindDa(ft);
        Directory.CreateDirectory(dir);
        long len = SpDump.RangeLength(g, names);
        string full = Path.Combine(dir, "full.bin");
        if (File.Exists(full)) File.Delete(full);
        string scatter = SpDump.WriteMinimalScatter(dir, chip, false);
        var ranges = new List<SpRange> { new SpRange { PartId = partId, Start = 0, Length = len, OutPath = full } };
        string cfg = SpDump.BuildRangeConfig(dir, "range_config.xml", da, chip, false, null, scatter, ranges);
        Console.WriteLine("range 0.." + len + " (" + Converter.Human(len) + ")  Connect the powered-off device now...");
        var log = new StringBuilder(); var sw = Stopwatch.StartNew();
        for (int attempt = 1; attempt <= 15 && sw.Elapsed < TimeSpan.FromMinutes(14); attempt++)
        {
            var psi = new ProcessStartInfo(ft, "-r -i \"" + cfg + "\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ft), StandardOutputEncoding = Encoding.UTF8 };
            var one = new StringBuilder(); int code;
            Console.WriteLine("--- attempt " + attempt);
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (log) { one.AppendLine(e.Data); log.AppendLine(e.Data); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) { one.AppendLine(e.Data); log.AppendLine(e.Data); } };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(13 * 60 * 1000)) { try { p.Kill(); } catch { } code = -999; } else { p.WaitForExit(); code = p.ExitCode; }
            }
            Console.WriteLine("exit code: " + code + " (" + sw.Elapsed.TotalSeconds.ToString("0") + "s)");
            if (code == 0 || one.ToString().IndexOf("STARTCMD_FAIL", StringComparison.OrdinalIgnoreCase) < 0) break;
            Console.WriteLine("*** handshake failed: unplug, wait 2s, plug in again. Restarting SP in 5s...");
            Thread.Sleep(5000);
        }
        File.WriteAllText(Path.Combine(dir, "range_log.txt"), log.ToString(), new UTF8Encoding(true));
        if (!File.Exists(full)) { Console.WriteLine("no dump file"); return 1; }
        Console.WriteLine("full.bin: " + new FileInfo(full).Length + " bytes (expected " + len + ")");
        List<string> skipped;
        var w = SpDump.Split(full, g, names, Path.Combine(dir, "split"), null, null, out skipped);
        Console.WriteLine("split: " + string.Join(",", w) + (skipped.Count > 0 ? "  skipped: " + string.Join(",", skipped) : ""));
        return 0;
    }
}
