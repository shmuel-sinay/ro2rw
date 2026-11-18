using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ro2rwPc
{
    public enum MtkEngine { Mtkclient, Fast }

    /// <summary>
    /// Optional faster flash/dump engine built from alir325000's MTK Flash Tool
    /// (bundled mtkflash.exe + DA_PL.bin). Experimental; mtkclient stays the default.
    /// </summary>
    public static class FastEngine
    {
        public static MtkEngine Selected = MtkEngine.Mtkclient;
        /// <summary>Fast engine over the MTK VCOM serial COM port (no UsbDk). Works where only SP's driver binds.</summary>
        public static bool Serial = false;
        public static string Com = null;   // optional forced COM port, e.g. "COM40"
        /// <summary>SP Flash Tool is the active engine: flash + dump are routed through the user's SP Flash Tool.</summary>
        public static bool Sp = false;

        static string SerialArgs { get { return Serial ? " --serial" + (string.IsNullOrEmpty(Com) ? "" : " --com " + Com) : ""; } }

        public static string Dir { get { return Path.Combine(Project.ToolsDir, "mtkflash"); } }
        public static string Exe { get { return Path.Combine(Dir, "mtkflash.exe"); } }
        public static string Da { get { return Path.Combine(Dir, "DA_PL.bin"); } }

        public static bool Bundled
        {
            get
            {
                var names = Assembly.GetExecutingAssembly().GetManifestResourceNames();
                return names.Contains("mtkflash.exe") && names.Contains("DA_PL.bin");
            }
        }

        public static bool Available { get { return Bundled || (File.Exists(Exe) && File.Exists(Da)); } }

        static void Extract(string res, string dest)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(res))
            {
                if (s == null) { if (File.Exists(dest)) return; throw new FileNotFoundException("המנוע המהיר לא מובנה בגרסה הזו: " + res); }
                Directory.CreateDirectory(Dir);
                if (!File.Exists(dest) || new FileInfo(dest).Length != s.Length)
                    using (var f = File.Create(dest)) s.CopyTo(f);
            }
        }

        public static void Ensure()
        {
            Extract("mtkflash.exe", Exe);
            Extract("DA_PL.bin", Da);
            foreach (var r in new[] { "mtkflash-LICENSE.txt", "mtkflash-README.txt" })
                try { Extract(r, Path.Combine(Dir, r)); } catch { }
        }

        static MtkClient Proc(Action<string> log, Action<string> status, Func<bool> cancelled)
        {
            return new MtkClient { Log = log, Status = status, Cancelled = cancelled };
        }

        public static string Warning
        {
            get { return "המנוע המהיר ניסיוני, נבדק על מעט מכשירים. אם משהו משתבש עבור למנוע mtkclient (ברירת המחדל)."; }
        }

        /// <summary>Dump all partitions into the project with the fast engine (single DA session).</summary>
        public static int DumpProject(Project proj, bool skipUserdata, Action<string> log, Action<string> status, Func<bool> cancelled)
        {
            Ensure();
            string tmp = Path.Combine(proj.Root, "_dump");
            if (Directory.Exists(tmp)) try { Directory.Delete(tmp, true); } catch { }
            Directory.CreateDirectory(tmp);
            var cap = new StringBuilder();
            string args = "dumpall --da \"" + Da + "\" --out \"" + tmp + "\" --no-reboot" + (skipUserdata ? " --skip userdata" : "") + SerialArgs;
            int rc = Proc(log, status, cancelled).RunExe(Exe, args, proj.Root, cap, "mtkflash");
            try { File.WriteAllText(Path.Combine(proj.Root, "mtkflash_log.txt"), cap.ToString(), new UTF8Encoding(true)); } catch { }
            string gpt = Path.Combine(tmp, "gpt.bin");
            if (!File.Exists(gpt))
                throw new IOException(rc != 0 ? "המנוע המהיר נכשל (קוד " + rc + "). ראה mtkflash_log.txt" : "לא נוצרה טבלת מחיצות - השאיבה לא הושלמה");
            return MtkClient.FinishDump(proj, tmp, gpt, cap.ToString(), log);
        }

        /// <summary>Flash the given items via a scatter built from gpt.bin. Caller already unsparsed what it needs.</summary>
        public static void FlashViaScatter(List<FlashItem> items, string gptPath, string workDir, bool reboot,
            Action<string> log, Action<string> status, Func<bool> cancelled)
        {
            Ensure();
            if (gptPath == null || !File.Exists(gptPath))
                throw new InvalidOperationException("המנוע המהיר צריך gpt.bin (צריבה לפי scatter). שאב קודם מהמכשיר, או עבור למנוע mtkclient.");
            var g = Gpt.Parse(File.ReadAllBytes(gptPath));
            var byName = items.ToDictionary(i => i.Partition, i => i, StringComparer.OrdinalIgnoreCase);
            string scatterDir = Path.Combine(workDir, "_fastflash");
            if (Directory.Exists(scatterDir)) try { Directory.Delete(scatterDir, true); } catch { }
            Directory.CreateDirectory(scatterDir);
            var fileByPart = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                string src = it.FlashFile ?? it.File;   // FlashFile = already-unsparsed path if set
                string dest = Path.Combine(scatterDir, it.Partition + Path.GetExtension(src));
                File.Copy(src, dest, true);
                fileByPart[it.Partition] = Path.GetFileName(dest);
            }
            string scatter = ScatterWriter.Write(scatterDir, "fastflash", g, null, g.SectorSize == 4096,
                name => byName.ContainsKey(name) ? fileByPart[name] : null);
            var cap = new StringBuilder();
            string args = "flash --scatter \"" + scatter + "\" --da \"" + Da + "\"" + (reboot ? "" : " --no-reboot") + SerialArgs;
            int rc = Proc(log, status, cancelled).RunExe(Exe, args, scatterDir, cap, "mtkflash");
            string text = cap.ToString();
            try { File.WriteAllText(Path.Combine(workDir, "mtkflash_flash_log.txt"), text, new UTF8Encoding(true)); } catch { }
            try { Directory.Delete(scatterDir, true); } catch { }
            if (rc != 0 || text.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Traceback", StringComparison.Ordinal) >= 0)
                throw new IOException("הצריבה במנוע המהיר נכשלה (קוד " + rc + "). ראה mtkflash_flash_log.txt");
            log("✔ נצרבו (מנוע מהיר): " + string.Join(", ", items.Select(i => i.Partition)));
        }
    }
}
