using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Ro2rwPc
{
    static class Ui
    {
        public static readonly Font Base = new Font("Segoe UI", 10f);
        public static readonly Font Bold = new Font("Segoe UI", 10f, FontStyle.Bold);
        public static readonly Font Title = new Font("Segoe UI", 14f, FontStyle.Bold);
        public static Form Rtl(Form f) { f.RightToLeft = RightToLeft.Yes; f.RightToLeftLayout = true; f.Font = Base; f.AutoScaleMode = AutoScaleMode.Dpi; return f; }
        public static Label L(string t, int x, int y, int w = 400, int h = 24) { return new Label { Text = t, Location = new Point(x, y), Size = new Size(w, h) }; }
        public static void Msg(IWin32Window w, string text, MessageBoxIcon icon = MessageBoxIcon.Information)
        {
            MessageBox.Show(w, text, "RO2RW PC", MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
        public static bool Ask(IWin32Window w, string text, MessageBoxIcon icon = MessageBoxIcon.Question)
        {
            return MessageBox.Show(w, text, "RO2RW PC", MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == DialogResult.Yes;
        }
        public const string BromHelp =
            "1. כבה את המכשיר לגמרי ונתק את הכבל.\n" +
            "2. לחץ אישור.\n" +
            "3. החזק את שני לחצני העוצמה (או עוצמה+ / עוצמה-, לפי הדגם) וחבר את הכבל.";
    }

    // ======================================================================= flash dialog
    public sealed class FlashForm : Form
    {
        readonly List<FlashItem> items;
        readonly DataGridView grid;
        public readonly CheckBox Reboot;

        public FlashForm(List<FlashItem> items, string title)
        {
            this.items = items;
            Ui.Rtl(this);
            Text = "צריבה חזרה למכשיר (mtkclient)";
            ClientSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            Controls.Add(Ui.L(title, 12, 10, 790, 24));
            Controls.Add(new Label { Text = "סמן מה לצרוב. אפשר לשנות את שם המחיצה (למשל boot_a). שורות אדומות לא ייצרבו.", Location = new Point(12, 36), Size = new Size(560, 22), ForeColor = Color.DimGray });
            var chkAll = new CheckBox { Text = "סמן / נקה הכל", Location = new Point(600, 34), Size = new Size(208, 24), Font = Ui.Bold };
            Controls.Add(chkAll);
            grid = new DataGridView
            {
                Location = new Point(12, 64), Size = new Size(796, 370), AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RightToLeft = RightToLeft.Yes, BackgroundColor = SystemColors.Window
            };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "צרוב", FillWeight = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "קובץ", ReadOnly = true, FillWeight = 140 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "מחיצה ביעד", FillWeight = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "גודל", ReadOnly = true, FillWeight = 70 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "גודל מחיצה", ReadOnly = true, FillWeight = 80 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "מצב", ReadOnly = true, FillWeight = 180 });
            foreach (var it in items)
            {
                int r = grid.Rows.Add(it.Selected && it.Problem == null, Path.GetFileName(it.File), it.Partition,
                    Converter.Human(it.Size) + (it.Sparse ? " (sparse)" : ""), it.PartSize > 0 ? Converter.Human(it.PartSize) : "?", "");
                grid.Rows[r].Tag = it;
                Refresh(r);
            }
            grid.CellEndEdit += (s, e) =>
            {
                var it = (FlashItem)grid.Rows[e.RowIndex].Tag;
                if (e.ColumnIndex == 2)
                {
                    it.Partition = Convert.ToString(grid.Rows[e.RowIndex].Cells[2].Value).Trim();
                    if (it.Problem == "המחיצה לא קיימת בטבלת המחיצות") it.Problem = null;
                    Flasher.Validate(it);
                }
                Refresh(e.RowIndex);
            };
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            Controls.Add(grid);
            chkAll.CheckedChanged += (s, e) =>
            {
                grid.EndEdit();
                foreach (DataGridViewRow r in grid.Rows)
                {
                    var it2 = (FlashItem)r.Tag;
                    r.Cells[0].Value = chkAll.Checked && it2.Problem == null;
                }
            };
            Reboot = new CheckBox { Text = "אתחל את המכשיר בסיום", Location = new Point(12, 444), Size = new Size(300, 24), Checked = true };
            Controls.Add(Reboot);
            var ok = new Button { Text = "צרוב", Location = new Point(12, 476), Size = new Size(120, 34), Font = Ui.Bold, BackColor = Color.FromArgb(198, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            ok.Click += (s, e) =>
            {
                grid.EndEdit();
                for (int r = 0; r < grid.Rows.Count; r++) ((FlashItem)grid.Rows[r].Tag).Selected = Convert.ToBoolean(grid.Rows[r].Cells[0].Value);
                var sel = items.Where(i => i.Selected && i.Problem == null).ToList();
                if (sel.Count == 0) { Ui.Msg(this, "לא נבחרה אף מחיצה תקינה לצריבה."); return; }
                if (!Ui.Ask(this, "לצרוב למכשיר את המחיצות:\n" + string.Join("\n", sel.Select(i => "  " + i.Partition + "  ←  " + Path.GetFileName(i.File))) +
                    "\n\nצריבה שגויה עלולה להשבית את המכשיר. יש לך גיבוי של המקור?", MessageBoxIcon.Warning)) return;
                DialogResult = DialogResult.OK;
            };
            var cancel = new Button { Text = "ביטול", Location = new Point(140, 476), Size = new Size(100, 34), DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            CancelButton = cancel;
        }

        void Refresh(int r)
        {
            var it = (FlashItem)grid.Rows[r].Tag;
            grid.Rows[r].Cells[5].Value = it.Problem ?? "מוכן";
            grid.Rows[r].DefaultCellStyle.ForeColor = it.Problem != null ? Color.Firebrick : Color.Black;
            if (it.Problem != null) grid.Rows[r].Cells[0].Value = false;
        }
    }

    // ======================================================================= wizard
    public sealed class WizardResult
    {
        public string ProjectName;
        public bool NewProject;
        public int Source;              // 0 = dump from device, 1 = add files, 2 = use project files
        public string[] AddFiles = new string[0];
        public bool SkipUserdata = true;
        public bool DoRoot, DoRo2rw, DoAvb, DoLk;
        public EditSettings Settings = new EditSettings();
        public bool FlashAfter, RebootAfter = true;
    }

    public sealed class WizardForm : Form
    {
        readonly List<Panel> pages = new List<Panel>();
        readonly List<string> titles = new List<string>();
        int page;
        readonly Label lblTitle, lblStep;
        readonly Button btnBack, btnNext;
        public WizardResult Result = new WizardResult();

        // page controls
        ComboBox cmbProj; TextBox tbNew; RadioButton rbNew, rbExisting;
        RadioButton rbDump, rbFiles, rbHave; CheckBox cbSkip; Label lblFiles; string[] files = new string[0];
        CheckBox cbRoot, cbRo2rw, cbAvb, cbLk;
        RadioButton rbMax, rbFixed; NumericUpDown pS, pV, pP, pSE, pOT, mS, mV, mP, mSE, mOT; CheckBox cbSparse, cbFstab; Label lblPct;
        ComboBox cmbAbi; CheckBox cbKeepV, cbKeepE; CheckBox cbNoWifi;
        CheckBox cbDelay, cbText;
        CheckBox cbFlash, cbReboot;
        Label lblSummary;

        public WizardForm(IEnumerable<string> projects, string current)
        {
            Ui.Rtl(this);
            Text = "אשף - RO2RW PC";
            ClientSize = new Size(640, 500);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            lblTitle = new Label { Location = new Point(16, 12), Size = new Size(500, 32), Font = Ui.Title };
            lblStep = new Label { Location = new Point(520, 18), Size = new Size(104, 24), ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(lblTitle); Controls.Add(lblStep);
            Controls.Add(new Label { Location = new Point(0, 50), Size = new Size(640, 2), BorderStyle = BorderStyle.Fixed3D });

            BuildProjectPage(projects, current);
            BuildSourcePage();
            BuildEditsPage();
            BuildRo2rwPage();
            BuildMagiskPage();
            BuildLkPage();
            BuildFinishPage();
            BuildSummaryPage();

            Controls.Add(new Label { Location = new Point(0, 440), Size = new Size(640, 2), BorderStyle = BorderStyle.Fixed3D });
            btnNext = new Button { Text = "הבא ›", Location = new Point(16, 452), Size = new Size(120, 34), Font = Ui.Bold };
            btnBack = new Button { Text = "‹ הקודם", Location = new Point(144, 452), Size = new Size(110, 34) };
            var btnCancel = new Button { Text = "ביטול", Location = new Point(514, 452), Size = new Size(110, 34), DialogResult = DialogResult.Cancel };
            btnNext.Click += (s, e) => Next();
            btnBack.Click += (s, e) => Go(PrevPage(page));
            Controls.Add(btnNext); Controls.Add(btnBack); Controls.Add(btnCancel);
            CancelButton = btnCancel;
            Go(0);
        }

        Panel NewPage(string title)
        {
            var p = new Panel { Location = new Point(16, 60), Size = new Size(608, 370), Visible = false };
            pages.Add(p); titles.Add(title); Controls.Add(p);
            return p;
        }

        static NumericUpDown Num(Panel p, string label, int x, int y, int val, int max)
        {
            p.Controls.Add(Ui.L(label, x, y, 100, 20));
            var n = new NumericUpDown { Location = new Point(x, y + 22), Size = new Size(90, 26), Maximum = max, Value = Math.Min(val, max) };
            p.Controls.Add(n);
            return n;
        }

        void BuildProjectPage(IEnumerable<string> projects, string current)
        {
            var p = NewPage("פרויקט");
            p.Controls.Add(Ui.L("כל מכשיר נשמר בפרויקט נפרד (תיקייה עם שם המכשיר).", 0, 0, 600));
            rbNew = new RadioButton { Text = "פרויקט חדש - שם המכשיר:", Location = new Point(0, 40), Size = new Size(260, 24) };
            tbNew = new TextBox { Location = new Point(264, 38), Size = new Size(300, 28), RightToLeft = RightToLeft.No };
            rbExisting = new RadioButton { Text = "פרויקט קיים:", Location = new Point(0, 84), Size = new Size(260, 24) };
            cmbProj = new ComboBox { Location = new Point(264, 82), Size = new Size(300, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var pr in projects) cmbProj.Items.Add(pr);
            if (cmbProj.Items.Count > 0) { cmbProj.SelectedItem = current != null && cmbProj.Items.Contains(current) ? current : cmbProj.Items[0]; rbExisting.Checked = true; }
            else { rbNew.Checked = true; rbExisting.Enabled = false; cmbProj.Enabled = false; }
            tbNew.TextChanged += (s, e) => { if (tbNew.Text.Length > 0) rbNew.Checked = true; };
            p.Controls.AddRange(new Control[] { rbNew, tbNew, rbExisting, cmbProj });
        }

        void BuildSourcePage()
        {
            var p = NewPage("מאיפה הרום?");
            rbDump = new RadioButton { Text = "לשאוב רום מלא מהמכשיר (MediaTek, mtkclient מובנה)", Location = new Point(0, 0), Size = new Size(600, 24) };
            cbSkip = new CheckBox { Text = "בלי userdata (מומלץ: מחיצה ענקית עם מידע אישי)", Location = new Point(24, 28), Size = new Size(560, 24), Checked = true };
            rbFiles = new RadioButton { Text = "יש לי קבצי רום במחשב - להוסיף אותם לפרויקט", Location = new Point(0, 74), Size = new Size(600, 24) };
            var btnPick = new Button { Text = "בחר קבצים...", Location = new Point(24, 102), Size = new Size(130, 30) };
            lblFiles = new Label { Location = new Point(162, 108), Size = new Size(430, 40), ForeColor = Color.DimGray };
            btnPick.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Multiselect = true, Filter = "קבצי רום|*.img;*.bin;*.img.*;*.txt|כל הקבצים|*.*" })
                    if (d.ShowDialog(this) == DialogResult.OK) { files = d.FileNames; rbFiles.Checked = true; lblFiles.Text = files.Length + " קבצים: " + string.Join(", ", files.Select(Path.GetFileName).Take(6)) + (files.Length > 6 ? "..." : ""); }
            };
            rbHave = new RadioButton { Text = "הקבצים כבר נמצאים בפרויקט", Location = new Point(0, 160), Size = new Size(600, 24), Checked = true };
            p.Controls.Add(new Label { Text = "טיפ: הוסף גם את קובץ ה-scatter מהקושחה, כדי שייווצר scatter לצריבה ב-SP Flash Tool.", Location = new Point(0, 210), Size = new Size(600, 44), ForeColor = Color.DimGray });
            p.Controls.AddRange(new Control[] { rbDump, cbSkip, rbFiles, btnPick, lblFiles, rbHave });
        }

        void BuildEditsPage()
        {
            var p = NewPage("מה לעשות?");
            p.Controls.Add(Ui.L("בחר את העריכות. אפשר גם רק אחת:", 0, 0, 600));
            cbRoot = new CheckBox { Text = "Root עם Magisk (boot / init_boot)", Location = new Point(0, 34), Size = new Size(600, 24), Checked = true };
            cbRo2rw = new CheckBox { Text = "RO2RW - מערכת לקריאה וכתיבה עם מקום פנוי (super)", Location = new Point(0, 64), Size = new Size(600, 24), Checked = true };
            cbAvb = new CheckBox { Text = "ביטול אימות AVB (vbmeta) - נדרש כמעט תמיד", Location = new Point(0, 94), Size = new Size(600, 24), Checked = true };
            cbLk = new CheckBox { Text = "הסרת Orange State וההשהיה של 5 שניות (lk)", Location = new Point(0, 124), Size = new Size(600, 24), Checked = true };
            p.Controls.Add(new Label { Text = "אחרי עריכת boot או super צריך לצרוב גם vbmeta ערוך, אחרת המכשיר עלול לא לעלות.", Location = new Point(0, 170), Size = new Size(600, 44), ForeColor = Color.DarkOrange });
            p.Controls.AddRange(new Control[] { cbRoot, cbRo2rw, cbAvb, cbLk });
        }

        void BuildRo2rwPage()
        {
            var p = NewPage("RO2RW - כמה להגדיל?");
            rbFixed = new RadioButton { Text = "גודל קבוע - כמה MB להוסיף לכל מחיצה:", Location = new Point(0, 0), Size = new Size(600, 24), Checked = true };
            int y = 30;
            mS = Num(p, "System MB", 20, y, 500, 100000); mV = Num(p, "Vendor MB", 130, y, 100, 100000); mP = Num(p, "Product MB", 240, y, 100, 100000);
            mSE = Num(p, "System_ext MB", 350, y, 50, 100000); mOT = Num(p, "אחרות MB", 460, y, 0, 100000);
            rbMax = new RadioButton { Text = "אחוזים - לחלק את כל המקום הפנוי ב-super (MAX):", Location = new Point(0, 100), Size = new Size(600, 24) };
            y = 130;
            pS = Num(p, "System %", 20, y, 70, 100); pV = Num(p, "Vendor %", 130, y, 10, 100); pP = Num(p, "Product %", 240, y, 10, 100);
            pSE = Num(p, "System_ext %", 350, y, 5, 100); pOT = Num(p, "אחרות %", 460, y, 5, 100);
            lblPct = new Label { Location = new Point(20, 184), Size = new Size(560, 22) };
            cbSparse = new CheckBox { Text = "פלט sparse (קטן יותר, מתאים ל-fastboot ול-SP Flash Tool)", Location = new Point(0, 220), Size = new Size(600, 24), Checked = true };
            cbFstab = new CheckBox { Text = "הוסף שורות ext4 ל-fstab של vendor", Location = new Point(0, 248), Size = new Size(600, 24), Checked = true };
            cbNoWifi = new CheckBox { Text = "הסר WiFi (מכשיר מסונן) - מוחק wpa_supplicant מ-vendor", Location = new Point(0, 276), Size = new Size(600, 24) };
            p.Controls.Add(new Label { Text = "אם אין מספיק מקום פנוי ב-super, תופיע שגיאה עם הגודל הפנוי האמיתי.", Location = new Point(0, 306), Size = new Size(600, 36), ForeColor = Color.DimGray });
            p.Controls.AddRange(new Control[] { rbFixed, rbMax, lblPct, cbSparse, cbFstab, cbNoWifi });
            EventHandler upd = (s, e) =>
            {
                foreach (var n in new[] { mS, mV, mP, mSE, mOT }) n.Enabled = rbFixed.Checked;
                foreach (var n in new[] { pS, pV, pP, pSE, pOT }) n.Enabled = rbMax.Checked;
                int sum = (int)(pS.Value + pV.Value + pP.Value + pSE.Value + pOT.Value);
                lblPct.Text = rbMax.Checked ? "סה\"כ: " + sum + "%" + (sum > 100 ? " - יותר מ-100%!" : "") : "";
                lblPct.ForeColor = sum > 100 ? Color.Firebrick : Color.Black;
            };
            rbFixed.CheckedChanged += upd; rbMax.CheckedChanged += upd;
            foreach (var n in new[] { pS, pV, pP, pSE, pOT }) n.ValueChanged += upd;
            upd(null, null);
        }

        void BuildMagiskPage()
        {
            var p = NewPage("Root עם Magisk");
            p.Controls.Add(Ui.L("ABI (ארכיטקטורת המערכת):", 0, 4, 200));
            cmbAbi = new ComboBox { Location = new Point(204, 0), Size = new Size(160, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAbi.Items.AddRange(new object[] { "auto", "arm64-v8a", "armeabi-v7a", "x86_64", "x86" });
            cmbAbi.SelectedIndex = 0;
            p.Controls.Add(new Label { Text = "auto = זיהוי לפי קובץ init שב-ramdisk (מומלץ).", Location = new Point(0, 34), Size = new Size(600, 22), ForeColor = Color.DimGray });
            cbKeepV = new CheckBox { Text = "השאר verity (KEEPVERITY)", Location = new Point(0, 70), Size = new Size(600, 24) };
            cbKeepE = new CheckBox { Text = "השאר הצפנה כפויה (KEEPFORCEENCRYPT)", Location = new Point(0, 98), Size = new Size(600, 24) };
            p.Controls.Add(new Label { Text = "ברירת המחדל (בלי סימון) היא כמו באפליקציית Magisk ברוב המכשירים.", Location = new Point(0, 132), Size = new Size(600, 40), ForeColor = Color.DimGray });
            p.Controls.AddRange(new Control[] { cmbAbi, cbKeepV, cbKeepE });
        }

        void BuildLkPage()
        {
            var p = NewPage("Orange State (lk)");
            cbDelay = new CheckBox { Text = "בטל את ההשהיה של 5 שניות בהפעלה", Location = new Point(0, 0), Size = new Size(600, 24), Checked = true };
            cbText = new CheckBox { Text = "מחק את טקסט האזהרה מהמסך", Location = new Point(0, 30), Size = new Size(600, 24), Checked = true };
            p.Controls.Add(new Label { Text = "גרסת האנדרואיד מזוהה אוטומטית.", Location = new Point(0, 66), Size = new Size(600, 24), ForeColor = Color.DimGray });
            p.Controls.AddRange(new Control[] { cbDelay, cbText });
        }

        void BuildFinishPage()
        {
            var p = NewPage("אחרי העריכה");
            p.Controls.Add(Ui.L("הקבצים הערוכים נשמרים בתיקייה \"ערוך\" יחד עם scatter ל-SP Flash Tool.", 0, 0, 600, 44));
            cbFlash = new CheckBox { Text = "לצרוב את הקבצים הערוכים חזרה למכשיר (mtkclient)", Location = new Point(0, 56), Size = new Size(600, 24) };
            cbReboot = new CheckBox { Text = "לאתחל את המכשיר בסיום הצריבה", Location = new Point(24, 84), Size = new Size(560, 24), Checked = true };
            p.Controls.Add(new Label { Text = "לפני הצריבה יוצג חלון לאישור: אילו מחיצות ייצרבו ומאיזה קובץ.", Location = new Point(24, 116), Size = new Size(570, 40), ForeColor = Color.DimGray });
            cbFlash.CheckedChanged += (s, e) => cbReboot.Enabled = cbFlash.Checked;
            cbReboot.Enabled = false;
            p.Controls.AddRange(new Control[] { cbFlash, cbReboot });
        }

        void BuildSummaryPage()
        {
            var p = NewPage("סיכום");
            lblSummary = new Label { Location = new Point(0, 0), Size = new Size(600, 360), Font = new Font("Segoe UI", 10f) };
            p.Controls.Add(lblSummary);
        }

        bool Skip(int i)
        {
            if (i == 3) return !cbRo2rw.Checked;
            if (i == 4) return !cbRoot.Checked;
            if (i == 5) return !cbLk.Checked;
            return false;
        }
        int NextPage(int i) { do i++; while (i < pages.Count && Skip(i)); return i; }
        int PrevPage(int i) { do i--; while (i > 0 && Skip(i)); return Math.Max(0, i); }

        void Go(int i)
        {
            page = i;
            for (int k = 0; k < pages.Count; k++) pages[k].Visible = k == i;
            lblTitle.Text = titles[i];
            lblStep.Text = "שלב " + (i + 1) + " מתוך " + pages.Count;
            btnBack.Enabled = i > 0;
            btnNext.Text = i == pages.Count - 1 ? "התחל ✓" : "הבא ›";
            if (i == pages.Count - 1) { Collect(); lblSummary.Text = Summary(); }
        }

        void Next()
        {
            if (page == 0)
            {
                if (rbNew.Checked && string.IsNullOrWhiteSpace(Project.SafeName(tbNew.Text))) { Ui.Msg(this, "כתוב שם לפרויקט (שם המכשיר)."); return; }
            }
            if (page == 1 && rbFiles.Checked && files.Length == 0) { Ui.Msg(this, "בחר קבצים להוספה."); return; }
            if (page == 2 && !(cbRoot.Checked || cbRo2rw.Checked || cbAvb.Checked || cbLk.Checked) && !cbFlash.Checked) { Ui.Msg(this, "בחר לפחות עריכה אחת."); return; }
            if (page == 3 && rbMax.Checked && pS.Value + pV.Value + pP.Value + pSE.Value + pOT.Value > 100) { Ui.Msg(this, "סכום האחוזים גדול מ-100%."); return; }
            if (page == pages.Count - 1) { Collect(); DialogResult = DialogResult.OK; return; }
            Go(NextPage(page));
        }

        void Collect()
        {
            var r = Result;
            r.NewProject = rbNew.Checked;
            r.ProjectName = rbNew.Checked ? Project.SafeName(tbNew.Text) : (string)cmbProj.SelectedItem;
            r.Source = rbDump.Checked ? 0 : rbFiles.Checked ? 1 : 2;
            r.AddFiles = files;
            r.SkipUserdata = cbSkip.Checked;
            r.DoRoot = cbRoot.Checked; r.DoRo2rw = cbRo2rw.Checked; r.DoAvb = cbAvb.Checked; r.DoLk = cbLk.Checked;
            var st = r.Settings;
            st.Ro2rw = new Settings
            {
                MaxMode = rbMax.Checked, PctS = (int)pS.Value, PctV = (int)pV.Value, PctP = (int)pP.Value, PctSE = (int)pSE.Value, PctOT = (int)pOT.Value,
                FixedMbPer = new Dictionary<string, int> { { "S", (int)mS.Value }, { "V", (int)mV.Value }, { "P", (int)mP.Value }, { "SE", (int)mSE.Value }, { "OT", (int)mOT.Value } },
                SparseOutput = cbSparse.Checked, PatchVendorFstab = cbFstab.Checked, RemoveWifi = cbNoWifi.Checked
            };
            st.Magisk = new MagiskOptions { Abi = (string)cmbAbi.SelectedItem, KeepVerity = cbKeepV.Checked, KeepForceEncrypt = cbKeepE.Checked };
            st.LkDelay = cbDelay.Checked; st.LkText = cbText.Checked;
            r.FlashAfter = cbFlash.Checked; r.RebootAfter = cbReboot.Checked;
        }

        string Summary()
        {
            var r = Result;
            var l = new List<string>();
            l.Add("פרויקט: " + r.ProjectName + (r.NewProject ? " (חדש)" : ""));
            l.Add("מקור: " + (r.Source == 0 ? "שאיבה מהמכשיר" + (r.SkipUserdata ? " (בלי userdata)" : "") : r.Source == 1 ? "הוספת " + r.AddFiles.Length + " קבצים" : "קבצים שכבר בפרויקט"));
            l.Add("");
            l.Add("עריכות:");
            if (r.DoRoot) l.Add("  • Root עם Magisk (ABI: " + r.Settings.Magisk.Abi + ")");
            if (r.DoRo2rw)
            {
                var s = r.Settings.Ro2rw;
                l.Add("  • RO2RW: " + (s.MaxMode
                    ? string.Format("MAX - System {0}%, Vendor {1}%, Product {2}%, System_ext {3}%, אחרות {4}%", s.PctS, s.PctV, s.PctP, s.PctSE, s.PctOT)
                    : string.Format("+MB - System {0}, Vendor {1}, Product {2}, System_ext {3}, אחרות {4}", s.FixedMbPer["S"], s.FixedMbPer["V"], s.FixedMbPer["P"], s.FixedMbPer["SE"], s.FixedMbPer["OT"])));
            }
            if (r.DoRo2rw && r.Settings.Ro2rw.RemoveWifi) l.Add("  • הסרת WiFi (מכשיר מסונן)");
            if (r.DoAvb) l.Add("  • ביטול AVB");
            if (r.DoLk) l.Add("  • Orange State: " + (r.Settings.LkDelay ? "בלי השהיה" : "") + (r.Settings.LkDelay && r.Settings.LkText ? ", " : "") + (r.Settings.LkText ? "בלי טקסט" : ""));
            l.Add("");
            l.Add("בסיום: scatter ל-SP Flash Tool" + (r.FlashAfter ? " + צריבה חזרה למכשיר" + (r.RebootAfter ? " ואתחול" : "") : ""));
            l.Add("");
            l.Add("לחץ \"התחל\" כדי לבצע הכול ברצף.");
            return string.Join("\n", l);
        }
    }
}
