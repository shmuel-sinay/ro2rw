using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ro2rwPc
{
    public sealed class FlashItem
    {
        public string File;        // full path of the image
        public string Partition;   // target partition on the device
        public long Size;
        public long PartSize = -1; // from gpt.bin, -1 if unknown
        public bool Sparse;
        public bool Selected = true;
        public string Problem;     // non-null = cannot flash
        public string FlashFile;   // path actually flashed (unsparsed copy), set during Flash
    }

    /// <summary>Writes edited images back to the device with the bundled mtkclient ("mtk w").</summary>
    public sealed class Flasher
    {
        public Action<string> Log = s => { };
        public Action<string> Status = s => { };
        public Action<int> Progress = p => { };
        public Func<bool> Cancelled = () => false;
        public string GptPath;     // for the fast engine (scatter needs addresses)

        static readonly HashSet<string> Dangerous = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "preloader", "preloader_a", "preloader_b", "pgpt", "sgpt", "gpt", "proinfo", "nvram", "nvdata", "nvcfg", "protect1", "protect2", "persist", "seccfg", "efuse", "otp", "flashinfo" };

        public static bool IsDangerous(string part) { return Dangerous.Contains(part); }

        /// <summary>Builds the default plan: every image in the folder, partition name = file name (fixed up against gpt.bin).</summary>
        public static List<FlashItem> Plan(string folder, string gptPath)
        {
            Gpt g = null;
            if (gptPath != null && File.Exists(gptPath)) { try { g = Gpt.Parse(File.ReadAllBytes(gptPath)); } catch { } }
            var list = new List<FlashItem>();
            foreach (var f in Directory.GetFiles(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".img" && ext != ".bin") continue;
                string part = Path.GetFileNameWithoutExtension(f);
                var it = new FlashItem { File = f, Size = new FileInfo(f).Length, Partition = part };
                try { it.Sparse = SparseSource.IsSparse(f); } catch { }
                if (g != null)
                {
                    var gp = g.Parts.FirstOrDefault(p => p.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                          ?? g.Parts.FirstOrDefault(p => p.Name.Equals(part + "_a", StringComparison.OrdinalIgnoreCase));
                    if (gp != null) { it.Partition = gp.Name; it.PartSize = gp.Size; }
                    else it.Problem = "המחיצה לא קיימת בטבלת המחיצות";
                }
                Validate(it);
                list.Add(it);
            }
            return list;
        }

        public static void Validate(FlashItem it)
        {
            if (it.Problem == "המחיצה לא קיימת בטבלת המחיצות") return;
            it.Problem = null;
            if (string.IsNullOrWhiteSpace(it.Partition)) { it.Problem = "חסר שם מחיצה"; return; }
            if (IsDangerous(it.Partition)) { it.Problem = "מחיצה רגישה - צריבה חסומה"; return; }
            long real = it.Size;
            if (it.Sparse) { try { using (var s = new SparseSource(new[] { it.File })) real = s.Length; } catch { } }
            if (it.PartSize > 0 && real > it.PartSize) it.Problem = "הקובץ גדול מהמחיצה (" + Converter.Human(real) + " > " + Converter.Human(it.PartSize) + ")";
        }

        string Unsparse(string path, string tmpDir, Action<int> prog)
        {
            Directory.CreateDirectory(tmpDir);
            string outp = Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(path) + ".raw.img");
            using (var s = new SparseSource(new[] { path }))
            using (var o = new FileStream(outp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[4 << 20];
                for (long pos = 0; pos < s.Length; pos += buf.Length)
                {
                    if (Cancelled()) throw new OperationCanceledException();
                    int n = (int)Math.Min(buf.Length, s.Length - pos);
                    s.Read(pos, buf, 0, n);
                    o.Write(buf, 0, n);
                    prog((int)(pos * 1000 / Math.Max(1, s.Length)));
                }
            }
            return outp;
        }

        public void Flash(List<FlashItem> items, string workDir, bool rebootAfter)
        {
            var todo = items.Where(i => i.Selected && i.Problem == null).ToList();
            if (todo.Count == 0) throw new InvalidOperationException("לא נבחרו מחיצות לצריבה");
            string tmp = Path.Combine(workDir, "_flash_tmp");
            var files = new List<string>();
            try
            {
                foreach (var it in todo)
                {
                    if (Cancelled()) throw new OperationCanceledException();
                    if (it.Sparse)
                    {
                        Log("  ממיר " + Path.GetFileName(it.File) + " מ-sparse לקובץ רגיל לצורך צריבה...");
                        it.FlashFile = Unsparse(it.File, tmp, p => Status("ממיר sparse... " + p / 10 + "%"));
                    }
                    else it.FlashFile = it.File;
                    files.Add(it.FlashFile);
                }
                if (FastEngine.Selected == MtkEngine.Fast)
                {
                    FastEngine.FlashViaScatter(todo, GptPath, workDir, rebootAfter, Log, Status, Cancelled);
                    return;
                }
                foreach (var f in files) if (f.Contains(",")) throw new IOException("נתיב עם פסיק לא נתמך: " + f);
                var mc = new MtkClient { Log = Log, Status = Status, Cancelled = Cancelled };
                var log = new StringBuilder();
                string args = "w " + string.Join(",", todo.Select(t => t.Partition)) + " \"" + string.Join(",", files) + "\"";
                int rc = mc.Run(args, workDir, log);
                string text = log.ToString();
                try { File.WriteAllText(Path.Combine(workDir, "mtkclient_flash_log.txt"), text, new UTF8Encoding(true)); } catch { }
                bool failed = rc != 0 || text.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Error", StringComparison.Ordinal) >= 0;
                if (failed) throw new IOException("הצריבה נכשלה או הסתיימה עם שגיאות (קוד " + rc + "). ראה mtkclient_flash_log.txt");
                Log("✔ נצרבו: " + string.Join(", ", todo.Select(t => t.Partition)));
                if (rebootAfter)
                {
                    Log("מאתחל את המכשיר...");
                    try { mc.Run("reset", workDir, new StringBuilder()); } catch (Exception ex) { Log("  לא הצלחתי לאתחל אוטומטית (" + ex.Message + "). נתק את הכבל והדלק ידנית."); }
                }
            }
            finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
        }
    }
}
