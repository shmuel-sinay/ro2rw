using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Ro2rwPc
{
    // Backup of the originals taken by a device dump: kept for Backup.DumpRetentionHours, then deleted. Restore flashes the originals of the edited partitions.
    public sealed partial class MainForm
    {
        const int PageBackup = 6;
        Label lblBkStatus;
        Button btnBkVerify, btnBkRestore, btnBkRegister, btnBkDelete;
        System.Windows.Forms.Timer bkTimer;
        string lastVerify = "";
        bool bkWarned;

        TabPage BuildBackupTab()
        {
            var t = ToolPage("גיבוי ושחזור");
            t.Padding = new Padding(18, 14, 18, 0);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            flow.Controls.Add(new Label { Text = "גיבוי ושחזור", Font = fTitle, AutoSize = true, ForeColor = CardText, Margin = new Padding(0, 0, 0, 6) });
            flow.Controls.Add(new Label
            {
                Text = "אחרי שאיבה מהמכשיר נוצר גיבוי של הקבצים המקוריים (עם SHA-256). הוא זמין " + Backup.DumpRetentionHours + " שעות ואז נמחק מיד. בזמן הזה אפשר לשחזר ממנו את הקבצים שנערכו.",
                AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Color.FromArgb(90, 99, 110), Margin = new Padding(0, 0, 0, 14)
            });
            lblBkStatus = new Label { AutoSize = true, MaximumSize = new Size(680, 0), Margin = new Padding(0, 0, 0, 16), Font = fBold };
            flow.Controls.Add(lblBkStatus);
            btnBkVerify = BkButton("אמת את הגיבוי (SHA-256)", (s, e) => VerifyBackup());
            btnBkRestore = BkButton("שחזר את הקבצים שנערכו מהגיבוי", (s, e) => RestoreFromBackup());
            btnBkRestore.BackColor = Color.FromArgb(198, 40, 40); btnBkRestore.ForeColor = Color.White;
            btnBkRegister = BkButton("רשום את קבצי הפרויקט כגיבוי (בלי מחיקה אוטומטית)", (s, e) => RegisterBackup());
            btnBkDelete = BkButton("מחק את הגיבוי עכשיו", (s, e) => DeleteBackupNow());
            flow.Controls.AddRange(new Control[] { btnBkVerify, btnBkRestore, btnBkRegister, btnBkDelete });
            t.Controls.Add(flow);
            return t;
        }

        static Button BkButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, Size = new Size(380, 38), Margin = new Padding(0, 0, 0, 10) };
            b.Click += click;
            return b;
        }

        static string Span(TimeSpan r) { if (r < TimeSpan.Zero) r = TimeSpan.Zero; return (int)r.TotalHours + ":" + r.Minutes.ToString("00"); }

        bool BusyNow() { return btnCancel != null && btnCancel.Enabled; }

        string BackupRoot(string name) { return Path.Combine(Project.BaseDir, name); }

        void StartBackupTimer()
        {
            bkTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            bkTimer.Tick += (s, e) => { SweepBackups(); UpdateBackupUi(); };
            bkTimer.Start();
        }

        /// <summary>Deletes every expired backup (all projects). The project being worked on is skipped while an operation runs.</summary>
        void SweepBackups()
        {
            foreach (var name in Project.List())
            {
                string root = BackupRoot(name);
                var m = Backup.Load(root);
                if (m == null) continue;
                bool current = project != null && project.Name == name;
                if (Backup.IsExpired(m, DateTime.UtcNow))
                {
                    if (current && BusyNow()) continue;
                    int n = Backup.Expire(root, m);
                    Log("הגיבוי של \"" + name + "\" פג תוקף ונמחק (" + n + " קבצים).", Color.Khaki);
                    if (current) { lastVerify = ""; RefreshFiles(); }
                }
                else if (current && !bkWarned)
                {
                    var r = Backup.Remaining(m, DateTime.UtcNow);
                    if (r != null && r.Value <= TimeSpan.FromMinutes(15)) { bkWarned = true; Log("אזהרה: הגיבוי של \"" + name + "\" יימחק בעוד " + Span(r.Value) + ". אם צריך לשחזר, עשה את זה עכשיו.", Color.Khaki); }
                }
            }
        }

        void UpdateBackupUi()
        {
            if (lblBkStatus == null) return;
            if (InvokeRequired) { BeginInvoke(new Action(UpdateBackupUi)); return; }
            BackupManifest m = project != null ? Backup.Load(project.Root) : null;
            bool busy = BusyNow();
            if (m == null)
            {
                lblBkStatus.Text = project == null ? "לא נבחר פרויקט." : "אין גיבוי לפרויקט.";
                if (rail != null) rail.SetStatus(PageBackup, "");
            }
            else
            {
                long total = m.Files.Sum(f => f.Size);
                string when = m.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                var r = Backup.Remaining(m, DateTime.UtcNow);
                string life = r == null ? "ללא מחיקה אוטומטית." : "יימחק בעוד " + Span(r.Value) + " שעות.";
                lblBkStatus.Text = m.Files.Count + " קבצים (" + Converter.Human(total) + "), נוצר " + when + ". " + life + (lastVerify.Length > 0 ? "\nאימות אחרון: " + lastVerify : "");
                if (rail != null) rail.SetStatus(PageBackup, r == null ? "ללא מחיקה" : "נמחק בעוד " + Span(r.Value));
            }
            bool has = m != null;
            btnBkVerify.Enabled = has && !busy;
            btnBkRestore.Enabled = has && !busy;
            btnBkDelete.Enabled = has && !busy;
            btnBkRegister.Enabled = project != null && !busy;
        }

        /// <summary>Called on the worker thread right after a dump: hashes the files the dump produced and starts the 2-hour clock.</summary>
        void MakeDumpBackup(Project proj, DateTime startedUtc)
        {
            try
            {
                var names = Backup.CandidateImages(proj.Root).Where(n => File.GetLastWriteTimeUtc(Path.Combine(proj.Root, n)) >= startedUtc.AddMinutes(-1)).ToList();
                if (names.Count == 0) { Log("לא נוצר גיבוי: לא נמצאו קבצים שנשאבו.", Color.Khaki); return; }
                Log("מחשב SHA-256 ל-" + names.Count + " קבצים (גיבוי ל-" + Backup.DumpRetentionHours + " שעות)...");
                Backup.Create(proj.Root, names, Backup.DumpRetentionHours, DateTime.UtcNow, () => cancel, s => SetStatus("SHA-256: " + s));
                bkWarned = false; lastVerify = "";
                Log("✔ גיבוי נוצר. הוא יימחק בעוד " + Backup.DumpRetentionHours + " שעות.", Color.LightGreen);
            }
            catch (OperationCanceledException) { Log("יצירת הגיבוי בוטלה.", Color.Khaki); }
            catch (Exception ex) { Log("✖ יצירת הגיבוי נכשלה: " + ex.Message); }
            finally { SetStatus(""); UpdateBackupUi(); }
        }

        static string ProblemText(string p)
        {
            int i = p.LastIndexOf(':');
            string name = p.Substring(0, i), kind = p.Substring(i + 1);
            return name + (kind == "missing" ? " (חסר)" : kind == "size" ? " (גודל שונה)" : " (SHA-256 שונה)");
        }

        void VerifyBackup()
        {
            if (project == null) return;
            var proj = project; var m = Backup.Load(proj.Root);
            if (m == null) { UpdateBackupUi(); return; }
            cancel = false; SetBusy(true);
            worker = new Thread(() =>
            {
                try
                {
                    Log("מאמת את הגיבוי (SHA-256)...");
                    var problems = Backup.Verify(proj.Root, m, null, () => cancel);
                    lastVerify = problems.Count == 0 ? "תקין (" + DateTime.Now.ToString("HH:mm") + ")" : "נמצאו בעיות: " + string.Join(", ", problems.Select(ProblemText));
                    if (problems.Count == 0) Log("✔ הגיבוי תקין.", Color.LightGreen);
                    else Log("✖ " + lastVerify);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); SetBusy(false); UpdateBackupUi(); }
            }) { IsBackground = true };
            worker.Start();
        }

        void RestoreFromBackup()
        {
            if (project == null) { Msg(this, "בחר פרויקט קודם."); return; }
            var m = Backup.Load(project.Root);
            if (m == null) { Msg(this, "אין גיבוי לפרויקט."); return; }
            var set = Backup.RestoreSet(project.Root, project.Edited, m);
            if (set.Count == 0) { Msg(this, "אין בגיבוי קבצים שיש להם גרסה ערוכה, ולכן אין מה לשחזר.", MessageBoxIcon.Warning); return; }
            if (!Ui.Ask(this, "לשחזר מהגיבוי את הקבצים המקוריים של:\n" + string.Join("\n", set.Select(n => "   • " + n)) + "\n\nלפני הצריבה יתבצע אימות SHA-256 של הקבצים. להמשיך?", MessageBoxIcon.Warning)) return;
            var proj = project;
            cancel = false; SetBusy(true);
            worker = new Thread(() =>
            {
                bool ok = false;
                try
                {
                    Log("מאמת את קבצי השחזור (SHA-256)...");
                    var problems = Backup.Verify(proj.Root, m, set, () => cancel);
                    ok = problems.Count == 0;
                    if (!ok) Log("✖ הגיבוי פגום, השחזור נעצר: " + string.Join(", ", problems.Select(ProblemText)));
                    else Log("✔ הקבצים תקינים.", Color.LightGreen);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); SetBusy(false); }
                if (ok)
                    BeginInvoke(new Action(() =>
                    {
                        if (FastEngine.Sp) RunSpAuto(true);
                        else FlashBack(set.Select(n => Path.Combine(proj.Root, n)).ToArray(), false, null);
                    }));
            }) { IsBackground = true };
            worker.Start();
        }

        /// <summary>For an SP restore: the partitions the edited scatter flashes, pointed at the original files from the backup.</summary>
        List<SpFlashTool.Rom> SpRestoreRoms(List<SpFlashTool.Rom> editedRoms)
        {
            var m = Backup.Load(project.Root);
            var set = new HashSet<string>(m == null ? new List<string>() : Backup.RestoreSet(project.Root, project.Edited, m), StringComparer.OrdinalIgnoreCase);
            return editedRoms
                .Select(r => new SpFlashTool.Rom { Index = r.Index, Name = r.Name, File = Path.Combine(project.Root, Path.GetFileName(r.File)) })
                .Where(r => set.Contains(Path.GetFileName(r.File))).ToList();
        }

        void RegisterBackup()
        {
            if (project == null) return;
            var proj = project;
            var names = Backup.CandidateImages(proj.Root);
            if (names.Count == 0) { Msg(this, "אין קובצי img/bin בתיקיית הפרויקט."); return; }
            if (!Ui.Ask(this, "לחשב SHA-256 ל-" + names.Count + " הקבצים שבתיקיית הפרויקט ולרשום אותם כגיבוי?\n\nגיבוי כזה לא נמחק אוטומטית, כי הקבצים הם לא רק תוצאה של שאיבה.")) return;
            cancel = false; SetBusy(true);
            worker = new Thread(() =>
            {
                try
                {
                    Log("מחשב SHA-256 ל-" + names.Count + " קבצים...");
                    Backup.Create(proj.Root, names, 0, DateTime.UtcNow, () => cancel, s => SetStatus("SHA-256: " + s));
                    lastVerify = ""; bkWarned = false;
                    Log("✔ הקבצים נרשמו כגיבוי (ללא מחיקה אוטומטית).", Color.LightGreen);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); SetBusy(false); UpdateBackupUi(); }
            }) { IsBackground = true };
            worker.Start();
        }

        void DeleteBackupNow()
        {
            if (project == null) return;
            var m = Backup.Load(project.Root);
            if (m == null) return;
            if (!Ui.Ask(this, "למחוק עכשיו את " + m.Files.Count + " קבצי הגיבוי מהתיקייה?\n\nאי אפשר לשחזר אחרי המחיקה.", MessageBoxIcon.Warning)) return;
            int n = Backup.Expire(project.Root, m);
            lastVerify = "";
            Log("הגיבוי נמחק (" + n + " קבצים).", Color.Khaki);
            RefreshFiles();
            UpdateBackupUi();
        }
    }
}
