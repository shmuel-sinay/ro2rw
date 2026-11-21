using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Ro2rwPc
{
    // Dump through SP Flash Tool: choose partitions, one file per partition or one dump cut by the partition table,
    // read the partition table from the device when there is none, build the scatter, flash by name when there is no scatter.
    public sealed partial class MainForm
    {
        string spChip, spCom;
        bool spUfs;
        int spPart;

        bool SpEnsureTool(out string da)
        {
            da = null;
            if (ResolveSpTool() == null)
            {
                using (var d = new OpenFileDialog { Title = "בחר את flash_tool.exe (או: לשונית SP ← התקן SP לתוך התוכנה)", Filter = "SP Flash Tool|flash_tool*.exe;FlashTool*.exe|כל הקבצים|*.exe" })
                { if (d.ShowDialog(this) != DialogResult.OK) return false; sptoolPath = d.FileName; }
            }
            da = SpFlashTool.FindDa(sptoolPath);
            if (da == null)
            {
                using (var d = new OpenFileDialog { Title = "בחר MTK_AllInOne_DA.bin", Filter = "DA|*.bin|כל הקבצים|*.*" })
                { if (d.ShowDialog(this) != DialogResult.OK) return false; da = d.FileName; }
            }
            return true;
        }

        Gpt LoadProjectGpt()
        {
            string p = project != null ? Path.Combine(project.Root, "gpt.bin") : null;
            if (p == null || !File.Exists(p)) return null;
            try { return Gpt.Parse(File.ReadAllBytes(p)); } catch { return null; }
        }

        /// <summary>Chip (MTxxxx) and storage from the first scatter in the project folder; null when there is none.</summary>
        string DetectChip(Project proj, out bool ufs)
        {
            ufs = false;
            if (proj == null || !Directory.Exists(proj.Root)) return null;
            foreach (var sc in Directory.GetFiles(proj.Root, "*scatter*.txt").OrderBy(x => x))
            {
                string t = File.ReadAllText(sc);
                var pm = Regex.Match(t, @"platform:\s*(MT[0-9]{4}[A-Z]?)");
                if (!pm.Success) continue;
                ufs = Regex.IsMatch(t, @"storage:\s*UFS", RegexOptions.IgnoreCase);
                return pm.Groups[1].Value.ToUpperInvariant();
            }
            return null;
        }

        string WriteDeviceScatter(Project proj, Gpt gpt, string chip, bool ufs)
        {
            // a firmware scatter with the usual name is never overwritten; our own earlier one is refreshed
            string usual = Path.Combine(proj.Root, chip + "_Android_scatter.txt");
            bool firmware = File.Exists(usual) && !File.ReadAllText(usual).Contains(ScatterWriter.GeneratedMarker);
            string name = firmware ? chip + "_device_scatter.txt" : chip + "_Android_scatter.txt";
            return ScatterWriter.Write(proj.Root, proj.Name, gpt, chip, ufs, n => File.Exists(Path.Combine(proj.Root, n + ".img")) ? n + ".img" : null, name);
        }

        int RunSpConsole(string ft, string cfg, string com, string logDir)
        {
            var runner = new MtkClient { Log = s => Log(s), Status = SetStatus, Cancelled = () => cancel };
            string args = "-i \"" + cfg + "\"" + (string.IsNullOrEmpty(com) ? "" : " -p " + com);
            var cap = new StringBuilder();
            int rc = runner.RunExe(ft, args, Path.GetDirectoryName(ft), cap, "flash_tool");
            try { File.WriteAllText(Path.Combine(logDir, "sp_readback_log.txt"), cap.ToString(), new UTF8Encoding(true)); } catch { }
            return rc;
        }

        // ============================================================ dump dialog
        void DumpRomSp()
        {
            string da;
            if (!SpEnsureTool(out da)) return;
            ShowSpDumpDialog(da);
        }

        void ShowSpDumpDialog(string da)
        {
            if (project == null) return;
            bool ufs; string chip = DetectChip(project, out ufs);
            if (chip == null) { chip = spChip; ufs = spUfs; }
            SpDumpPlan plan;
            using (var f = new SpDumpForm(LoadProjectGpt(), chip, ufs, spCom, spPart))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.Plan.Action == SpDumpAction.Cancel) return;
                plan = f.Plan;
            }
            spChip = plan.Platform; spUfs = plan.Ufs; spCom = plan.Com; spPart = plan.PartId;
            if (plan.GptFile != null)
            {
                try
                {
                    var head = new byte[0x8000];
                    int n; using (var s = File.OpenRead(plan.GptFile)) n = s.Read(head, 0, head.Length);
                    File.WriteAllBytes(Path.Combine(project.Root, "gpt.bin"), head.Take(n).ToArray());
                    Log("נטען gpt.bin: " + plan.GptFile, Color.LightGreen);
                }
                catch (Exception ex) { Msg(this, "לא ניתן לשמור את gpt.bin בפרויקט: " + ex.Message, MessageBoxIcon.Error); return; }
            }
            if (plan.Action == SpDumpAction.ReadGpt) RunSpReadGpt(da, plan, () => ShowSpDumpDialog(da));
            else RunSpDump(da, plan);
        }

        // ============================================================ read the partition table from the device
        void RunSpReadGpt(string da, SpDumpPlan plan, Action then)
        {
            if (!Ui.Ask(this,
                "קריאת טבלת המחיצות מהמכשיר דרך SP (32KB בלבד, קריאה בלבד, לא נכתב כלום למכשיר).\n\n" +
                "כבה את המכשיר, לחץ כן, ואז חבר אותו עם לחצני העוצמה.\n\nלהמשיך?")) return;
            var proj = project; string ft = sptoolPath;
            cancel = false; SetBusy(true); SetStatus("");
            worker = new Thread(() =>
            {
                string tmp = Path.Combine(proj.Root, "_sp_dump");
                bool ok = false;
                try
                {
                    Log(""); Log("▶ SP Flash Tool - קריאת טבלת מחיצות. ממתין למכשיר במצב BROM...", Color.DeepSkyBlue);
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    Directory.CreateDirectory(tmp);
                    string scatter = SpDump.WriteMinimalScatter(tmp, plan.Platform, plan.Ufs);
                    string gptOut = Path.Combine(tmp, "gpt.bin");
                    string cfg = SpDump.BuildRangeConfig(tmp, "sp_gpt_config.xml", da, plan.Platform, plan.Ufs, string.IsNullOrEmpty(plan.Com) ? null : plan.Com, scatter,
                        new[] { new SpRange { PartId = plan.PartId > 0 ? plan.PartId : SpDump.UserPartId(plan.Ufs), Start = 0, Length = SpDump.GptReadBytes, OutPath = gptOut } });
                    int rc = RunSpConsole(ft, cfg, plan.Com, tmp);
                    byte[] bytes = File.Exists(gptOut) ? File.ReadAllBytes(gptOut) : new byte[0];
                    if (!SpDump.LooksLikeGpt(bytes))
                        throw new IOException("SP לא החזירה טבלת GPT תקינה (קוד " + rc + ", part-id " + plan.PartId + "). אם המכשיר UFS בחר UFS, אפשר לנסות part-id אחר בהגדרות המתקדמות, או לטעון gpt.bin ידנית. ראה sp_readback_log.txt");
                    File.WriteAllBytes(Path.Combine(proj.Root, "gpt.bin"), bytes);
                    var g = Gpt.Parse(bytes);
                    string sc = WriteDeviceScatter(proj, g, plan.Platform, plan.Ufs);
                    Log("✔ נקראה טבלת מחיצות: " + g.Parts.Count + " מחיצות. נוצר scatter: " + Path.GetFileName(sc), Color.LightGreen);
                    ok = true;
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally
                {
                    try { var lg = Path.Combine(tmp, "sp_readback_log.txt"); if (File.Exists(lg)) File.Copy(lg, Path.Combine(proj.Root, "sp_readback_log.txt"), true); } catch { }
                    try { Directory.Delete(tmp, true); } catch { }
                    SetStatus(""); SetBusy(false);
                    BeginInvoke(new Action(() => { RefreshFiles(); if (ok && then != null) then(); }));
                }
            }) { IsBackground = true };
            worker.Start();
        }

        // ============================================================ the dump
        void RunSpDump(string da, SpDumpPlan plan)
        {
            var gpt = LoadProjectGpt();
            if (gpt == null) { Msg(this, "אין טבלת מחיצות בפרויקט.", MessageBoxIcon.Warning); return; }
            var names = plan.Names.Where(n => gpt.Parts.Any(p => p.Name == n)).ToList();
            long total = gpt.Parts.Where(p => names.Contains(p.Name)).Sum(p => p.Size);
            string what = plan.Single
                ? "דאמפ אחד ופיצול: קריאה רציפה של " + Converter.Human(SpDump.RangeLength(gpt, names)) + " ואז חיתוך ל-" + names.Count + " מחיצות."
                : "מחיצות נפרדות: " + names.Count + " מחיצות (" + Converter.Human(total) + "), כל אחת לקובץ משלה.";
            if (!Ui.Ask(this,
                "שאיבה דרך SP Flash Tool (קריאה בלבד, לא נכתב כלום למכשיר) לפרויקט \"" + project.Name + "\":\n\n" + what + "\n" +
                (plan.Preloader ? "כולל גיבוי preloader.\n" : "") +
                "\nכבה את המכשיר, לחץ כן, ואז חבר אותו עם לחצני העוצמה. אל תנתק עד הסוף.\n\nלהמשיך?")) return;
            var proj = project; string ft = sptoolPath; string com = string.IsNullOrEmpty(plan.Com) ? null : plan.Com;
            cancel = false; SetBusy(true); SetStatus("");
            worker = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                string tmp = Path.Combine(proj.Root, "_sp_dump");
                try
                {
                    var dumpStart = DateTime.UtcNow;
                    Log(""); Log("▶ שאיבה דרך SP Flash Tool - ממתין למכשיר במצב BROM...", Color.DeepSkyBlue);
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    Directory.CreateDirectory(tmp);
                    var moved = new List<string>();
                    if (!plan.Single)
                    {
                        string cfg = SpFlashTool.BuildReadbackConfig(tmp, tmp, da, plan.Platform, plan.Ufs, names, com);
                        var runner = new MtkClient { Log = s => Log(s), Status = SetStatus, Cancelled = () => cancel };
                        var done = SpFlashTool.RunReadback(ft, cfg, tmp, names, com, runner);
                        foreach (var p in done) { MoveInto(proj, SpFlashTool.ReadbackFile(tmp, p), p + ".img"); moved.Add(p); }
                        var missing = names.Except(done).ToList();
                        if (missing.Count > 0) Log("! לא נשאבו: " + string.Join(", ", missing), Color.Khaki);
                    }
                    else
                    {
                        string scatter = SpDump.WriteMinimalScatter(tmp, plan.Platform, plan.Ufs);
                        string full = Path.Combine(tmp, "full_dump.bin");
                        long len = SpDump.RangeLength(gpt, names);
                        var ranges = new List<SpRange> { new SpRange { PartId = plan.PartId > 0 ? plan.PartId : SpDump.UserPartId(plan.Ufs), Start = 0, Length = len, OutPath = full } };
                        string pre = Path.Combine(tmp, "preloader.bin");
                        if (plan.Preloader) ranges.Add(new SpRange { PartId = SpDump.Boot1PartId, Start = 0, Length = SpDump.PreloaderReadBytes, OutPath = pre });
                        string cfg = SpDump.BuildRangeConfig(tmp, "sp_dump_config.xml", da, plan.Platform, plan.Ufs, com, scatter, ranges);
                        int rc = RunSpConsole(ft, cfg, com, tmp);
                        long got = File.Exists(full) ? new FileInfo(full).Length : 0;
                        if (got == 0) throw new IOException("SP לא יצרה את קובץ הדאמפ (קוד " + rc + "). ראה sp_readback_log.txt");
                        if (got < len) Log("! הדאמפ קטן מהצפוי (" + Converter.Human(got) + " מתוך " + Converter.Human(len) + "). מחיצות חלקיות ידולגו.", Color.Khaki);
                        Log("חותך את הדאמפ לפי טבלת המחיצות...");
                        List<string> skipped;
                        var written = SpDump.Split(full, gpt, names, tmp, () => cancel, p => SetProgress(p), out skipped);
                        foreach (var p in written) { MoveInto(proj, Path.Combine(tmp, p + ".img"), p + ".img"); moved.Add(p); }
                        if (skipped.Count > 0) Log("! דולגו (לא נקראו עד הסוף): " + string.Join(", ", skipped), Color.Khaki);
                        if (plan.Preloader && File.Exists(pre) && new FileInfo(pre).Length > 0) { MoveInto(proj, pre, "preloader.bin"); Log("נשמר preloader.bin"); }
                    }
                    if (moved.Count == 0) throw new IOException("לא נשאבה אף מחיצה.");
                    string sc = WriteDeviceScatter(proj, gpt, plan.Platform, plan.Ufs);
                    Log(string.Format("✔ נשאבו {0} מחיצות לתוך הפרויקט ({1}). scatter: {2}", moved.Count, sw.Elapsed.ToString(@"hh\:mm\:ss"), Path.GetFileName(sc)), Color.LightGreen);
                    MakeDumpBackup(proj, dumpStart);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally
                {
                    try { var lg = Path.Combine(tmp, "sp_readback_log.txt"); if (File.Exists(lg)) File.Copy(lg, Path.Combine(proj.Root, "sp_readback_log.txt"), true); } catch { }
                    try { Directory.Delete(tmp, true); } catch { }
                    SetStatus(""); SetBusy(false); BeginInvoke(new Action(RefreshFiles));
                }
            }) { IsBackground = true };
            worker.Start();
        }

        static void MoveInto(Project proj, string src, string name)
        {
            string dst = Path.Combine(proj.Root, name);
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }

        // ============================================================ scatter tool
        void OpenScatterTool()
        {
            if (project == null) { Msg(this, "בחר פרויקט קודם."); return; }
            bool ufs; string chip = DetectChip(project, out ufs);
            if (chip == null) { chip = spChip; ufs = spUfs; }
            using (var f = new ScatterToolForm(project.Name, project.Root, LoadProjectGpt(), chip, ufs))
            {
                f.ShowDialog(this);
                if (f.Result != ScatterToolResult.ReadDevice) return;
                spChip = f.Chip; spUfs = f.Ufs;
                string da;
                if (!SpEnsureTool(out da)) return;
                RunSpReadGpt(da, new SpDumpPlan { Platform = f.Chip, Ufs = f.Ufs, Com = spCom, PartId = spPart > 0 ? spPart : SpDump.UserPartId(f.Ufs) }, OpenScatterTool);
            }
        }

        // ============================================================ flash by partition name (no scatter)
        void RunSpByName(bool restore)
        {
            string gptPath = Path.Combine(project.Root, "gpt.bin");
            if (!File.Exists(gptPath))
            {
                Msg(this, "אין scatter ואין gpt.bin בפרויקט, ולכן SP לא יודע לאילו מחיצות לצרוב.\n\nקרא את טבלת המחיצות מהמכשיר: לשונית שאיבה ← בחירת SP Flash Tool ← \"קרא טבלת מחיצות מהמכשיר\", או בכלי scatter (לשונית כלים נוספים).", MessageBoxIcon.Warning);
                return;
            }
            string folder = restore ? project.Root : project.Edited;
            var items = Flasher.Plan(folder, gptPath).Where(i => i.Problem == null).ToList();
            if (restore)
            {
                var m = Backup.Load(project.Root);
                var set = new HashSet<string>(m == null ? new List<string>() : Backup.RestoreSet(project.Root, project.Edited, m), StringComparer.OrdinalIgnoreCase);
                items = items.Where(i => set.Contains(Path.GetFileName(i.File))).ToList();
            }
            if (items.Count == 0) { Msg(this, "אין קבצים לצריבה.", MessageBoxIcon.Warning); return; }
            string da;
            if (!SpEnsureTool(out da)) return;
            Gpt g = LoadProjectGpt();
            bool ufs; string chip = DetectChip(project, out ufs);
            if (chip == null) { chip = spChip; ufs = g != null && g.SectorSize == 4096; }
            if (chip == null)
            {
                chip = Prompt("דגם המעבד (למשל MT6765):", "SP Flash Tool");
                if (string.IsNullOrWhiteSpace(chip)) return;
                chip = chip.Trim().ToUpperInvariant();
            }
            if (!Ui.Ask(this,
                (restore ? "שחזור מהגיבוי" : "צריבה") + " דרך SP Flash Tool לפי שם מחיצה (בלי scatter, Download Only, בלי format):\n" +
                string.Join("\n", items.Select(i => "   • " + i.Partition + "  ←  " + Path.GetFileName(i.File))) + "\n\nDA: " + Path.GetFileName(da) + "\n\nלהמשיך?", MessageBoxIcon.Warning)) return;
            string com = Prompt("מספר COM של המכשיר (אופציונלי - השאר ריק לזיהוי אוטומטי, למשל COM40):", "SP Flash Tool");
            if (com != null) com = com.Trim();
            string cfg;
            try { cfg = SpDump.BuildByNameFlashConfig(folder, da, chip, ufs, string.IsNullOrEmpty(com) ? null : com, items.Select(i => new KeyValuePair<string, string>(i.Partition, i.File)).ToList()); }
            catch (Exception ex) { Msg(this, ex.Message, MessageBoxIcon.Error); return; }
            string ft = sptoolPath;
            cancel = false; SetBusy(true); SetStatus("");
            worker = new Thread(() =>
            {
                try
                {
                    Log(""); Log("▶ SP Flash Tool - צריבה לפי שם מחיצה. חבר את המכשיר כבוי כשתתבקש.", Color.DeepSkyBlue);
                    var runner = new MtkClient { Log = s => Log(s), Status = SetStatus, Cancelled = () => cancel };
                    SpFlashTool.Run(ft, cfg, string.IsNullOrEmpty(com) ? null : com, runner);
                    Log("✔ SP Flash Tool סיימה. בדוק שהמכשיר עלה.", Color.LightGreen);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }
    }
}
