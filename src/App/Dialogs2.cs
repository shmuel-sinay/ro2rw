using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Ro2rwPc
{
    // ======================================================================= detailed super plan
    public sealed class SuperPlanForm : Form
    {
        readonly string superPath;
        readonly Settings baseSettings;
        readonly DataGridView grid;
        readonly Label lblInfo, lblFree;
        readonly ProgressBar bar;
        readonly Button btnSave, btnCancelScan, btnReset;
        Converter conv;
        volatile bool cancel;
        Thread worker;

        public SuperPlanForm(string superPath, Settings st)
        {
            this.superPath = superPath; baseSettings = st;
            Ui.Rtl(this);
            Text = "תכנון מפורט של super - " + Path.GetFileName(superPath);
            ClientSize = new Size(860, 560);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            Controls.Add(new Label { Text = "לכל מחיצה: האם להמיר ל-ext4 לקריאה וכתיבה, וכמה MB להוסיף לה. מחיצה שלא מומרת מועתקת כמו שהיא.", Location = new Point(12, 10), Size = new Size(836, 24) });
            lblInfo = new Label { Location = new Point(12, 36), Size = new Size(836, 24), ForeColor = Color.DimGray };
            grid = new DataGridView
            {
                Location = new Point(12, 64), Size = new Size(836, 340), AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RightToLeft = RightToLeft.Yes, BackgroundColor = SystemColors.Window, Enabled = false
            };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "המר", FillWeight = 40, ToolTipText = "סמן כדי להפוך את המחיצה לקריאה וכתיבה" });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "מחיצה", ReadOnly = true, FillWeight = 100 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "מערכת קבצים", ReadOnly = true, FillWeight = 70 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "גודל נוכחי", ReadOnly = true, FillWeight = 80 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "מינימום ext4", ReadOnly = true, FillWeight = 80, ToolTipText = "הגודל הקטן ביותר שהתוכן נכנס בו כ-ext4" });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "+MB להוסיף", FillWeight = 70, ToolTipText = "כמה מקום פנוי לכתיבה להוסיף למחיצה" });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "גודל סופי", ReadOnly = true, FillWeight = 80 });
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => { if (e.RowIndex >= 0) Recalc(); };
            grid.CellValidating += (s, e) =>
            {
                if (e.ColumnIndex != 5) return;
                int v;
                if (!int.TryParse(Convert.ToString(e.FormattedValue), out v) || v < 0) { e.Cancel = true; Ui.Msg(this, "הכנס מספר שלם של MB (0 ומעלה)."); }
            };
            bar = new ProgressBar { Location = new Point(12, 414), Size = new Size(836, 22), Maximum = 1000 };
            lblFree = new Label { Location = new Point(12, 442), Size = new Size(836, 48), Font = Ui.Bold };
            var btnSplit = new Button { Text = "חלק את כל המקום הפנוי", Location = new Point(12, 500), Size = new Size(190, 34) };
            btnSplit.Click += (s, e) => SplitFree();
            btnReset = new Button { Text = "אפס MB", Location = new Point(210, 500), Size = new Size(90, 34) };
            btnReset.Click += (s, e) => { foreach (DataGridViewRow r in grid.Rows) r.Cells[5].Value = "0"; Recalc(); };
            var btnAllP = new Button { Text = "המר הכל", Location = new Point(308, 500), Size = new Size(90, 34) };
            btnAllP.Click += (s, e) => { SetAllConvert(true); };
            var btnNoneP = new Button { Text = "המר כלום", Location = new Point(406, 500), Size = new Size(90, 34) };
            btnNoneP.Click += (s, e) => { SetAllConvert(false); };
            btnSave = new Button { Text = "שמור תכנון", Location = new Point(612, 500), Size = new Size(120, 34), Font = Ui.Bold, Enabled = false };
            btnSave.Click += (s, e) => Save();
            btnCancelScan = new Button { Text = "סגור", Location = new Point(738, 500), Size = new Size(110, 34) };
            btnCancelScan.Click += (s, e) => { cancel = true; DialogResult = DialogResult.Cancel; };
            var tip = new ToolTip();
            tip.SetToolTip(btnSplit, "מחלק את כל המקום הפנוי ב-super בין המחיצות המסומנות, לפי האחוזים שבלשונית RO2RW");
            tip.SetToolTip(btnSave, "התכנון נשמר ליד קובץ ה-super, ומשמש בכל עריכה הבאה (גם באשף)");
            Controls.AddRange(new Control[] { lblInfo, grid, bar, lblFree, btnSplit, btnReset, btnAllP, btnNoneP, btnSave, btnCancelScan });
            Shown += (s, e) => Scan();
            FormClosing += (s, e) => { cancel = true; if (conv != null && (worker == null || !worker.IsAlive)) conv.Dispose(); };
        }

        void Scan()
        {
            lblInfo.Text = "סורק את מערכות הקבצים כדי לחשב את הגודל המינימלי (יכול לקחת דקה-שתיים)...";
            worker = new Thread(() =>
            {
                try
                {
                    var c = new Converter { Cancelled = () => cancel, Progress = p => BeginInvoke(new Action(() => bar.Value = Math.Min(1000, p * 10))) };
                    c.Log = s => BeginInvoke(new Action(() => lblInfo.Text = s.Trim()));
                    c.Open(superPath);
                    var defaults = c.Parts.ToDictionary(p => p, p => p.Convert);
                    foreach (var p in c.Parts) if (p.Size > 0 && (p.Fs == "EROFS" || p.Fs == "EXT4")) p.Convert = true;  // analyse all candidates
                    c.Analyse(baseSettings);
                    foreach (var p in c.Parts) p.Convert = defaults[p];
                    var old = SuperPlan.Load(superPath);
                    BeginInvoke(new Action(() => Fill(c, old)));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { BeginInvoke(new Action(() => { lblInfo.Text = "שגיאה: " + ex.Message; lblInfo.ForeColor = Color.Firebrick; })); }
            }) { IsBackground = true };
            worker.Start();
        }

        void Fill(Converter c, SuperPlan old)
        {
            if (IsDisposed) { c.Dispose(); return; }
            conv = c;
            bar.Value = 1000;
            lblInfo.Text = string.Format("super: {0}. {1}", Converter.Human(c.DeviceSize), old != null ? "נטען תכנון קיים." : "ערכי ברירת מחדל.");
            foreach (var p in c.Parts.Where(x => x.Size > 0))
            {
                bool can = p.Fs == "EROFS" || p.Fs == "EXT4";
                bool convert = p.Convert; int mb = 0;
                SuperPlan.Entry e;
                if (old != null && old.Parts.TryGetValue(p.Name, out e)) { convert = e.Convert && can; mb = e.ExtraMb; }
                int r = grid.Rows.Add(convert, p.Name, p.Fs, Converter.Human(p.Size), can ? Converter.Human(p.MinBytes) : "-", mb.ToString(), "");
                grid.Rows[r].Tag = p;
                if (!can) { grid.Rows[r].ReadOnly = true; grid.Rows[r].DefaultCellStyle.ForeColor = Color.Gray; }
            }
            grid.Enabled = true; btnSave.Enabled = true;
            Recalc();
        }

        long Free()
        {
            foreach (DataGridViewRow r in grid.Rows) ((PartInfo)r.Tag).Convert = Convert.ToBoolean(r.Cells[0].Value);
            return conv.FreeForGrowth() - (8L << 20);
        }

        void Recalc()
        {
            if (conv == null) return;
            long free = Free(), used = 0;
            foreach (DataGridViewRow r in grid.Rows)
            {
                var p = (PartInfo)r.Tag;
                int mb; int.TryParse(Convert.ToString(r.Cells[5].Value), out mb);
                if (p.Convert) { used += (long)mb << 20; r.Cells[6].Value = Converter.Human(p.MinBytes + ((long)mb << 20)); r.Cells[5].ReadOnly = false; }
                else { r.Cells[6].Value = Converter.Human(p.Size) + " (העתקה)"; r.Cells[5].ReadOnly = true; }
            }
            long left = free - used;
            lblFree.Text = string.Format("מקום פנוי ב-super: {0}   |   מתוכנן להוספה: {1}   |   נשאר: {2}", Converter.Human(Math.Max(0, free)), Converter.Human(used), (left < 0 ? "-" : "") + Converter.Human(Math.Abs(left)));
            lblFree.ForeColor = left < 0 ? Color.Firebrick : Color.DarkGreen;
            btnSave.Enabled = left >= 0;
        }

        void SplitFree()
        {
            if (conv == null) return;
            long free = Math.Max(0, Free());
            var rows = grid.Rows.Cast<DataGridViewRow>().Where(r => ((PartInfo)r.Tag).Convert).ToList();
            var pct = new Dictionary<string, int> { { "S", baseSettings.PctS }, { "V", baseSettings.PctV }, { "P", baseSettings.PctP }, { "SE", baseSettings.PctSE }, { "OT", baseSettings.PctOT } };
            foreach (var r in rows)
            {
                var p = (PartInfo)r.Tag;
                int same = rows.Count(x => ((PartInfo)x.Tag).Category == p.Category);
                double share = pct[p.Category] / 100.0 / Math.Max(1, same);
                r.Cells[5].Value = ((long)(free * share) >> 20).ToString();
            }
            Recalc();
        }

        void SetAllConvert(bool on)
        {
            foreach (DataGridViewRow r in grid.Rows)
            {
                var p = (PartInfo)r.Tag;
                if (p.Fs == "EROFS" || p.Fs == "EXT4") r.Cells[0].Value = on;
            }
            Recalc();
        }

        void Save()
        {
            grid.EndEdit();
            var plan = new SuperPlan();
            foreach (DataGridViewRow r in grid.Rows)
            {
                var p = (PartInfo)r.Tag;
                int mb; int.TryParse(Convert.ToString(r.Cells[5].Value), out mb);
                plan.Parts[p.Name] = new SuperPlan.Entry { Convert = Convert.ToBoolean(r.Cells[0].Value) && (p.Fs == "EROFS" || p.Fs == "EXT4"), ExtraMb = mb };
            }
            plan.Save(superPath);
            DialogResult = DialogResult.OK;
        }
    }

    // ======================================================================= hex editor (find & replace)
    public sealed class HexForm : Form
    {
        readonly TextBox tbFile, tbFind, tbRepl;
        readonly CheckBox cbAll;
        readonly RichTextBox log;

        public HexForm(string startFile)
        {
            Ui.Rtl(this);
            Text = "עורך HEX - חיפוש והחלפה";
            ClientSize = new Size(720, 470);
            StartPosition = FormStartPosition.CenterParent;
            Controls.Add(Ui.L("קובץ:", 12, 16, 70));
            tbFile = new TextBox { Location = new Point(86, 12), Size = new Size(500, 28), RightToLeft = RightToLeft.No, Text = startFile ?? "" };
            var btnPick = new Button { Text = "בחר...", Location = new Point(594, 10), Size = new Size(110, 30) };
            btnPick.Click += (s, e) => { using (var d = new OpenFileDialog()) if (d.ShowDialog(this) == DialogResult.OK) tbFile.Text = d.FileName; };
            Controls.Add(Ui.L("חפש (HEX):", 12, 58, 90));
            tbFind = new TextBox { Location = new Point(106, 54), Size = new Size(598, 28), RightToLeft = RightToLeft.No, Font = new Font("Consolas", 11f) };
            Controls.Add(Ui.L("החלף ב:", 12, 98, 90));
            tbRepl = new TextBox { Location = new Point(106, 94), Size = new Size(598, 28), RightToLeft = RightToLeft.No, Font = new Font("Consolas", 11f) };
            Controls.Add(new Label { Text = "?? = כל בית (בחיפוש) / השאר כמו שהוא (בהחלפה). רווחים מותרים. דוגמה: 08B5 ???? 7B44 1B68 1B68 022B", Location = new Point(106, 128), Size = new Size(598, 40), ForeColor = Color.DimGray });
            cbAll = new CheckBox { Text = "החלף את כל ההתאמות (לא רק הראשונה)", Location = new Point(106, 170), Size = new Size(400, 24), Checked = true };
            var presets = new ComboBox { Location = new Point(106, 200), Size = new Size(420, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            presets.Items.AddRange(new object[] { "תבניות מוכנות...", "LK: השהיית 5 שניות - אנדרואיד 10+", "LK: השהיית 5 שניות - אנדרואיד 9-" });
            presets.SelectedIndex = 0;
            presets.SelectedIndexChanged += (s, e) =>
            {
                if (presets.SelectedIndex == 1) { tbFind.Text = "08B5 ???? 7B44 1B68 1B68 022B"; tbRepl.Text = "08B5 0020 08BD 1B68 1B68 022B"; }
                if (presets.SelectedIndex == 2) { tbFind.Text = "08B5 ???? 7B44 1B68 1B68 012B"; tbRepl.Text = "08B5 0020 08BD 1B68 1B68 012B"; }
            };
            var btnFind = new Button { Text = "חפש בלבד", Location = new Point(106, 238), Size = new Size(120, 34) };
            var btnDo = new Button { Text = "החלף ושמור ל\"ערוך\"", Location = new Point(234, 238), Size = new Size(180, 34), Font = Ui.Bold };
            log = new RichTextBox { Location = new Point(12, 284), Size = new Size(692, 174), ReadOnly = true, RightToLeft = RightToLeft.Yes, Font = new Font("Consolas", 10f) };
            btnFind.Click += (s, e) => Run(false);
            btnDo.Click += (s, e) => Run(true);
            Controls.AddRange(new Control[] { tbFile, btnPick, tbFind, tbRepl, cbAll, presets, btnFind, btnDo, log });
        }

        void Run(bool write)
        {
            log.Clear();
            try
            {
                if (!File.Exists(tbFile.Text)) throw new FileNotFoundException("בחר קובץ קיים");
                var b = File.ReadAllBytes(tbFile.Text);
                if (!write)
                {
                    var hits = HexPatcher.Find(b, HexPatcher.Parse(tbFind.Text), 1000);
                    log.AppendText(hits.Count == 0 ? "לא נמצאה התאמה\n" : "נמצאו " + hits.Count + " התאמות:\n" + string.Join("\n", hits.Take(50).Select(h => "  0x" + h.ToString("X"))) + "\n");
                    return;
                }
                int n = HexPatcher.Replace(b, tbFind.Text, tbRepl.Text, cbAll.Checked, s => log.AppendText(s + "\n"));
                if (n == 0) return;
                string outDir = Path.Combine(Path.GetDirectoryName(tbFile.Text), Project.EditedDir);
                Directory.CreateDirectory(outDir);
                string outp = Path.Combine(outDir, Path.GetFileName(tbFile.Text));
                if (Path.GetFullPath(outp) == Path.GetFullPath(tbFile.Text)) throw new IOException("הקובץ כבר נמצא בתיקייה \"ערוך\" - בחר את המקור");
                File.WriteAllBytes(outp, b);
                log.AppendText("✔ " + n + " החלפות. נשמר: " + outp + "\n");
            }
            catch (Exception ex) { log.AppendText("✖ " + ex.Message + "\n"); }
        }
    }

    // ======================================================================= detailed help
    public sealed class HelpForm : Form
    {
        public HelpForm()
        {
            Ui.Rtl(this);
            Text = "מדריך מפורט - RO2RW PC";
            ClientSize = new Size(820, 600);
            StartPosition = FormStartPosition.CenterParent;
            var tabs = new TabControl { Dock = DockStyle.Fill, RightToLeftLayout = true };
            foreach (var t in Topics)
            {
                var page = new TabPage(t.Key);
                page.Controls.Add(new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = t.Value, Font = new Font("Segoe UI", 11f), BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, RightToLeft = RightToLeft.Yes });
                tabs.TabPages.Add(page);
            }
            Controls.Add(tabs);
        }

        static readonly KeyValuePair<string, string>[] Topics =
        {
            new KeyValuePair<string, string>("התחלה", @"RO2RW PC עורך רום של אנדרואיד על המחשב. הקבצים המקוריים אף פעם לא משתנים: כל קובץ ערוך נשמר בתיקייה ""ערוך"".

שלוש דרכי עבודה:
• אשף (Ctrl+W): שלב אחרי שלב, מתאים להתחלה.
• פרויקט ידני: בוחרים פרויקט, מוסיפים קבצים או שואבים מהמכשיר, מסמנים, ולוחצים ""בצע עריכה"" (Ctrl+E).
• כלים בודדים: תפריט ""כלים בודדים"". כלי אחד על קובץ אחד, בלי פרויקט.

עצירה: כל פעולה אפשר לעצור באמצע, בכפתור ""ביטול"" או במקש Esc. קבצים חלקיים נמחקים.

קיצורי מקשים:
  Ctrl+W  אשף        Ctrl+N  פרויקט חדש      Ctrl+O  הוספת קבצים
  Ctrl+E  בצע עריכה   Ctrl+F  צרוב חזרה       F5  רענון
  Ctrl+ +/-  הגדלה או הקטנה של התצוגה      F1  המדריך הזה      Esc  עצירה"),
            new KeyValuePair<string, string>("RO2RW", @"RO2RW ממיר את המחיצות שבתוך super (system, vendor, product...) מקריאה בלבד (EROFS או ext4 עם shared_blocks) ל-ext4 רגיל לקריאה וכתיבה, ומוסיף להן מקום פנוי.

אפשרויות:
• MAX (אחוזים): כל המקום הפנוי ב-super מתחלק לפי האחוזים. למשל System 70% מקבל 70% מהמקום הפנוי.
• FIXED (MB קבוע): אותו מספר MB לכל מחיצה. באשף אפשר מספר שונה לכל סוג מחיצה.
• תכנון מפורט לפי מחיצה: כפתור ""תכנון מפורט..."" בלשונית RO2RW. לכל מחיצה בנפרד בוחרים אם להמיר וכמה MB להוסיף, ורואים בזמן אמת כמה מקום נשאר. התכנון נשמר ליד קובץ ה-super, וגובר על ההגדרות הכלליות.
• פלט sparse: קובץ קטן יותר. מתאים ל-fastboot ול-SP Flash Tool. בצריבה עם mtkclient התוכנה ממירה אותו אוטומטית לקובץ רגיל.
• fstab של vendor: מוסיף שורות ext4 לצד שורות erofs, כדי שהמערכת תדע לעגן ext4.

כברירת מחדל מומרות רק מחיצות חריץ A. מחיצות _b מועתקות כמו שהן.
אם אין מספיק מקום, מוצגת שגיאה עם הגודל הפנוי האמיתי."),
            new KeyValuePair<string, string>("Magisk", @"Root עם Magisk: התוכנה עורכת את boot.img או init_boot.img על המחשב, בדיוק כמו אפליקציית Magisk. Magisk הרשמי מובנה בתוכנה.

• אם יש init_boot (אנדרואיד 13 ומעלה עם GKI), עורכים אותו ולא את boot.
• ABI: ארכיטקטורת המערכת. במצב auto היא מזוהה לפי קובץ init שב-ramdisk. ידנית: arm64-v8a למערכת 64 ביט, armeabi-v7a למערכת 32 ביט.
• KEEPVERITY: משאיר את dm-verity ב-fstab של ה-ramdisk. בדרך כלל לא מסמנים.
• KEEPFORCEENCRYPT: משאיר הצפנה כפויה. בדרך כלל לא מסמנים.
• RECOVERYMODE: למכשירים שבהם Magisk מותקן במחיצת recovery (בלי ramdisk ב-boot). רק אם אתה יודע שזה המקרה.
• PATCHVBMETAFLAG: מכבה את האימות בתוך ה-vbmeta הפנימי של ה-boot (ב-AVB footer). שימושי כשלא צורבים vbmeta נפרד.

אפשר לבחור Magisk.apk אחר בלשונית Magisk. אחרי הצריבה מתקינים בטלפון את אפליקציית Magisk."),
            new KeyValuePair<string, string>("vbmeta ו-LK", @"vbmeta (ביטול AVB):
• ""הכול"" (מומלץ): verity כבוי וגם verification כבוי. שווה ערך ל-fastboot --disable-verity --disable-verification.
• ""רק verity"": מכבה רק את בדיקת השלמות של המחיצות (hashtree).
• ""רק verification"": מכבה רק את בדיקת החתימות.
בדרך כלל מספיק לערוך את vbmeta הראשי. vbmeta_system ו-vbmeta_vendor לא מסומנים כברירת מחדל.

LK (Orange State):
• ביטול ההשהיה: מבטל את 5 השניות שהמכשיר מחכה (ולפעמים נכבה) אחרי פתיחת בוטלואדר.
• מחיקת הטקסט: מוחק את הודעות האזהרה מהמסך.
• גרסת אנדרואיד: auto מזהה לבד. אם המכשיר לא מגיב, אפשר לבחור ידנית ""9 ומטה"" או ""10 ומעלה"".
• עורך HEX (כלים בודדים): חיפוש והחלפה ידניים עם ?? כתו כללי, כולל תבניות מוכנות ל-LK."),
            new KeyValuePair<string, string>("מכשיר", @"שאיבה וצריבה עם mtkclient (מובנה), למכשירי MediaTek.

הכנה, פעם אחת: תפריט מכשיר ← ""התקן דרייבר USB"" (UsbDk). נדרשות הרשאות מנהל.

כניסה למצב BROM: מכבים את המכשיר, מחזיקים לחצן עוצמה (או את שניהם, לפי הדגם) ומחברים כבל.

שאיבה: שואבת את כל המחיצות לתוך הפרויקט בשמות המחיצות, ויוצרת gpt.bin, scatter ויומן. userdata לא נשאבת כברירת מחדל.

צריבה חזרה (Ctrl+F):
• התוכנה מתאימה כל קובץ למחיצה לפי gpt.bin (boot.img ← boot_a). אפשר לשנות כל שם בטבלה.
• גודל כל קובץ נבדק מול גודל המחיצה.
• super בפורמט sparse מומר אוטומטית לקובץ רגיל לפני הצריבה.
• מחיצות רגישות (preloader, nvram, proinfo, seccfg...) חסומות.
• קליק ימני על קובץ ← ""צרוב את הגרסה הערוכה שלו"" צורב מחיצה אחת בלבד.

SP Flash Tool (מכשירים שרק דרייבר VCOM קולט): התקן אותה בעצמך, ואז תפריט מכשיר ← ""פתח ב-SP Flash Tool"" - היא תיפתח עם התיקייה ""ערוך"" ונתיב ה-scatter בלוח. טען scatter, Download Only, Download."),
            new KeyValuePair<string, string>("תקלות", @"• ""mtkclient נכשל"": בדוק שהדרייבר UsbDk מותקן, נסה כבל או חיבור USB אחר, וודא שהמכשיר במצב BROM. היומן המלא נשמר ב-mtkclient_log.txt בפרויקט.
• ""אין מספיק מקום"": הקטן את ה-MB או את האחוזים, או בטל המרה של חלק מהמחיצות (תכנון מפורט).
• ""ה-boot כבר עבר Magisk"": צריך boot מקורי שלא נערך.
• ""לא נמצא קוד ההשהיה"" ב-LK: ייתכן שהקובץ כבר תוקן, או שהמכשיר משתמש בקוד אחר. נסה את עורך HEX.
• המכשיר לא עולה אחרי צריבה: צרוב חזרה את הקבצים המקוריים מהפרויקט (קליק ימני ← פתח מיקום), ובדוק שצרבת גם vbmeta ערוך.
• שמירת יומן לשליחה: תפריט קובץ ← ""שמור יומן"".")
        };
    }
}

namespace Ro2rwPc
{
    /// <summary>Step-by-step guide for flashing already-edited files and backing up with SP Flash Tool.</summary>
    public sealed class SpGuideForm : Form
    {
        public SpGuideForm(string editedFolder, IEnumerable<string> editedFiles, string scatterPath)
        {
            Ui.Rtl(this);
            Text = "SP Flash Tool - צריבה וגיבוי (בלי לערוך מחדש)";
            ClientSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterParent;
            var tabs = new TabControl { Dock = DockStyle.Fill, RightToLeftLayout = true };

            string files = editedFiles != null && editedFiles.Any()
                ? string.Join("\n", editedFiles.Select(f => "   • " + Path.GetFileName(f)))
                : "   (לא נמצאו קבצים ערוכים - בצע עריכה קודם)";
            string edDir = editedFolder ?? "התיקייה \"ערוך\" שבתוך הפרויקט";
            string scat = scatterPath != null ? Path.GetFileName(scatterPath) : "(אין scatter - ראו הערה למטה)";

            tabs.TabPages.Add(Page("צריבת מה שכבר ערכת",
"אתה לא צריך לערוך מחדש! הקבצים הערוכים כבר מוכנים ב:\n" + edDir + "\n\n" +
"קבצים ערוכים:\n" + files + "\n\n" +
"--- דרך א' (הכי פשוט, אם יש scatter בתיקייה \"ערוך\"): ---\n" +
"1. פתח SP Flash Tool והתקן את דרייבר ה-VCOM של MediaTek.\n" +
"2. Scatter-loading ← בחר את הקובץ: " + scat + "\n   (הנתיב שלו הועתק ללוח - אפשר Ctrl+V).\n" +
"3. מסומנות רק המחיצות שערכת. בחר \"Download Only\".\n" +
"4. לחץ Download, חבר את המכשיר כבוי, חכה לסיום.\n\n" +
"--- דרך ב' (אם אין scatter, אבל יש לך את ה-scatter של הקושחה): ---\n" +
"1. ב-SP Flash Tool טען את ה-scatter של הקושחה המקורית.\n" +
"2. בטבלה, לכל מחיצה שערכת (boot / vbmeta / lk / super) - לחיצה כפולה על השורה\n" +
"   ובחר את הקובץ הערוך מתוך התיקייה \"ערוך\".\n" +
"3. הסר סימון מכל שאר המחיצות (שלא ערכת, ובמיוחד preloader).\n" +
"4. Download Only ← Download.\n\n" +
"⚠️ אל תצרוב preloader. ⚠️ גבה את המכשיר קודם (ראה לשונית \"גיבוי\")."));

            tabs.TabPages.Add(Page("גיבוי (ReadBack)",
"גיבוי מלא לפני צריבה, במכשיר שרק SP Flash Tool קולט:\n\n" +
"1. ב-SP Flash Tool טען scatter (כל scatter תקין של המכשיר נותן את הכתובות).\n" +
"2. עבור ללשונית ReadBack ← Add.\n" +
"3. לחיצה כפולה על השורה ← בחר נתיב לשמירת הגיבוי.\n" +
"4. בחלון שנפתח:\n" +
"      Region = EMMC_USER\n" +
"      Start Address = 0x0\n" +
"      Length = גודל כל הזיכרון של המכשיר\n" +
"   (אם לא יודעים את הגודל - משתמשים ב-WwR כדי לקרוא אותו, או משאירים את ברירת המחדל).\n" +
"5. לחץ ReadBack (החץ הירוק), חבר את המכשיר כבוי.\n" +
"6. המתן 15-35 דקות. אל תנתק.\n\n" +
"גיבוי של מחיצה אחת בלבד: ב-ReadBack הזן את Start Address והגודל של אותה מחיצה\n" +
"(מופיעים ב-scatter: linear_start_addr ו-partition_size). הרבה יותר מהיר.\n\n" +
"הערה: את הגיבוי הגולמי אפשר לפצל חזרה למחיצות עם הכלי WwR."));

            tabs.TabPages.Add(Page("למה רק SP?",
"במכשיר שלך רק דרייבר ה-VCOM של SP Flash Tool קולט את המכשיר.\n" +
"mtkclient והמנוע המהיר עובדים דרך UsbDk, ולכן הם נתקעים בשלב \"sending image data\"\n" +
"כשה-USB לא משלים את ההעברה. זו מגבלה של המכשיר מול UsbDk, לא תקלה בתוכנה.\n\n" +
"לכן במכשיר הזה: עורכים ב-RO2RW PC (על המחשב, בלי מכשיר), ומעבירים את התוצאה\n" +
"מהתיקייה \"ערוך\" ל-SP Flash Tool לצריבה ולגיבוי. כפתור \"פתח ב-SP Flash Tool\"\n" +
"עושה בשבילך את הצעד הראשון."));

            Controls.Add(tabs);
            var close = new Button { Text = "סגור", Dock = DockStyle.Bottom, Height = 36 };
            close.Click += (s, e) => Close();
            Controls.Add(close);
        }

        static TabPage Page(string title, string body)
        {
            var p = new TabPage(title);
            p.Controls.Add(new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = body, Font = new Font("Segoe UI", 11f), BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, RightToLeft = RightToLeft.Yes });
            return p;
        }
    }
}
