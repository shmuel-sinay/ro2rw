using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Ro2rwPc
{
    // One screen for files and edits: a row per project file, a column per edit; the tool options sit below.
    public sealed partial class MainForm
    {
        StepRail rail;
        DataGridView gridFiles;
        Button btnFilesAuto, btnFilesNone;
        Label lblFilesSummary;
        bool gridBusy;

        static readonly EditAction[] ActCols = { EditAction.Magisk, EditAction.Vbmeta, EditAction.Lk, EditAction.Ro2rw };
        static readonly string[] ActHeads = { "Root\n(Magisk)", "ביטול\nAVB", "הסרת\nOrange State", "הוספת מקום\n(RO2RW)" };
        const int ColActFirst = 3;
        const int ColEdited = ColActFirst + 4;

        static TableLayoutPanel OptRow(string label, Control c)
        {
            var r = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            r.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            r.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            r.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            r.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, 0);
            c.Margin = new Padding(0);
            r.Controls.Add(c, 1, 0);
            return r;
        }

        static CheckBox Opt(string text, bool on) { return new CheckBox { Text = text, AutoSize = true, Checked = on, Margin = new Padding(0, 0, 0, 4) }; }

        TabPage BuildFilesEditTab()
        {
            var t = ToolPage("קבצים ועריכה");
            t.Padding = new Padding(18, 14, 18, 0);
            var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            page.Controls.Add(new Label { Text = "קבצים ועריכה", Font = fTitle, AutoSize = true, ForeColor = CardText }, 0, 0);
            page.Controls.Add(new Label { Text = "סמן לכל קובץ איזו עריכה לבצע; אפשר לסמן כמה קבצים לאותה עריכה. המקור לא משתנה, והפלט נשמר בתיקייה \"ערוך\".", AutoSize = true, ForeColor = Color.FromArgb(90, 99, 110) }, 0, 1);
            gridFiles = BuildGrid();
            page.Controls.Add(gridFiles, 0, 2);

            var groups = new[]
            {
                new OptionGroup("אפשרויות Root (Magisk)", "מקבל הרשאות Root: עריכת boot / init_boot עם Magisk."),
                new OptionGroup("אפשרויות ביטול AVB", "מכבה אימות verity ו-verification ב-vbmeta."),
                new OptionGroup("אפשרויות הסרת Orange State (LK)", "מסיר השהיה והודעת אזהרה באתחול."),
                new OptionGroup("אפשרויות הוספת מקום (RO2RW)", "הופך את מחיצות super לקריאה וכתיבה, ומסיר WiFi אוטומטית.")
            };
            BuildMagiskOptions(groups[0].Body);
            BuildAvbOptions(groups[1].Body);
            BuildLkOptions(groups[2].Body);
            BuildRo2rwOptions(groups[3].Body);
            var list = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
            list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < groups.Length; i++) { list.RowStyles.Add(new RowStyle(SizeType.AutoSize)); list.Controls.Add(groups[i], 0, i); }
            var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            host.Controls.Add(list);
            page.Controls.Add(host, 0, 3);

            var foot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Padding = new Padding(0, 12, 0, 10) };
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int w in new[] { 150, 80, 120, 100, 190 }) foot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            lblFilesSummary = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(90, 99, 110) };
            btnAddFiles = new Button { Text = "הוסף קבצים...", Dock = DockStyle.Fill, Margin = new Padding(4, 0, 4, 0) };
            btnAddFiles.Click += (s, e) => PickFiles();
            btnRefresh = new Button { Text = "רענן", Dock = DockStyle.Fill, Margin = new Padding(4, 0, 4, 0) };
            btnRefresh.Click += (s, e) => RefreshFiles();
            btnFilesAuto = new Button { Text = "ברירת מחדל", Dock = DockStyle.Fill, Margin = new Padding(4, 0, 4, 0) };
            btnFilesAuto.Click += (s, e) => { if (project != null) { files = project.Scan(); FillGrid(); } };
            btnFilesNone = new Button { Text = "נקה הכל", Dock = DockStyle.Fill, Margin = new Padding(4, 0, 4, 0) };
            btnFilesNone.Click += (s, e) => { foreach (var f in files) f.Selected = false; FillGrid(); };
            btnRun = new Button { Text = "בצע עריכה", Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0), Font = new Font("Segoe UI", 11f, FontStyle.Bold), BackColor = Color.FromArgb(46, 125, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnRun.Click += (s, e) => RunEdits();
            foot.Controls.Add(lblFilesSummary, 0, 0);
            foot.Controls.Add(btnAddFiles, 1, 0);
            foot.Controls.Add(btnRefresh, 2, 0);
            foot.Controls.Add(btnFilesAuto, 3, 0);
            foot.Controls.Add(btnFilesNone, 4, 0);
            foot.Controls.Add(btnRun, 5, 0);
            page.Controls.Add(foot, 0, 4);
            t.Controls.Add(page);
            BuildFileContextMenu();
            return t;
        }

        DataGridView BuildGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill, RightToLeft = RightToLeft.Yes, AutoGenerateColumns = false,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 46,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Color.FromArgb(225, 229, 236), Margin = new Padding(0, 0, 0, 8)
            };
            g.RowTemplate.Height = 32;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 236, 242);
            g.ColumnHeadersDefaultCellStyle.ForeColor = CardText;
            g.ColumnHeadersDefaultCellStyle.Font = fBold;
            g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            g.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 238, 252);
            g.DefaultCellStyle.SelectionForeColor = CardText;
            g.DefaultCellStyle.ForeColor = CardText;

            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "קובץ", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 160 });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "סוג", ReadOnly = true, Width = 110 });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "גודל", ReadOnly = true, Width = 90 });
            for (int i = 0; i < ActCols.Length; i++)
                g.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = ActHeads[i], Width = 116, FalseValue = false, TrueValue = true });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "נערך", ReadOnly = true, Width = 70 });
            foreach (DataGridViewColumn c in g.Columns) { c.SortMode = DataGridViewColumnSortMode.NotSortable; if (c.Index >= 1) c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; }

            g.CurrentCellDirtyStateChanged += (s, e) => { if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            g.CellValueChanged += OnGridCellChanged;
            g.CellMouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { g.ClearSelection(); g.Rows[e.RowIndex].Selected = true; g.CurrentCell = g.Rows[e.RowIndex].Cells[0]; }
            };
            g.DataError += (s, e) => { e.ThrowException = false; };
            return g;
        }

        static bool IsEditableImage(ProjectFile pf)
        {
            string ext = Path.GetExtension(pf.Name).ToLowerInvariant();
            return (ext == ".img" || ext == ".bin") && pf.Kind != "GPT" && pf.Kind != "מידע";
        }

        void StyleRow(DataGridViewRow row, ProjectFile pf)
        {
            bool editable = IsEditableImage(pf);
            for (int i = 0; i < ActCols.Length; i++)
            {
                var cell = row.Cells[ColActFirst + i];
                bool on = pf.Selected && pf.Action == ActCols[i];
                cell.Value = on;
                cell.ReadOnly = !editable;
                bool override_ = on && pf.Action != pf.Detected;
                cell.Style.BackColor = override_ ? Color.FromArgb(255, 243, 205) : Color.Empty;
                cell.ToolTipText = override_ ? "שונה מהזיהוי האוטומטי" + (pf.Detected == EditAction.None ? " (הקובץ לא זוהה)" : " (" + Project.ActionName(pf.Detected) + ")") : "";
            }
            row.DefaultCellStyle.ForeColor = editable ? CardText : Color.Gray;
            row.Cells[0].ToolTipText = pf.Path;
        }

        void OnGridCellChanged(object s, DataGridViewCellEventArgs e)
        {
            if (gridBusy || e.RowIndex < 0 || e.ColumnIndex < ColActFirst || e.ColumnIndex >= ColActFirst + ActCols.Length) return;
            var row = gridFiles.Rows[e.RowIndex];
            var pf = row.Tag as ProjectFile;
            if (pf == null) return;
            bool on = row.Cells[e.ColumnIndex].Value is bool && (bool)row.Cells[e.ColumnIndex].Value;
            gridBusy = true;
            try
            {
                if (on) { pf.Action = ActCols[e.ColumnIndex - ColActFirst]; pf.Selected = true; }
                else pf.Selected = false;
                StyleRow(row, pf);
            }
            finally { gridBusy = false; }
            UpdateFilesSummary();
        }

        void FillGrid()
        {
            if (gridFiles == null) return;
            gridBusy = true;
            try
            {
                gridFiles.Rows.Clear();
                foreach (var f in files)
                {
                    int i = gridFiles.Rows.Add(f.Name, f.Kind, Converter.Human(f.Size), false, false, false, false, f.Edited ? "כן" : "");
                    var row = gridFiles.Rows[i];
                    row.Tag = f;
                    StyleRow(row, f);
                }
            }
            finally { gridBusy = false; }
            UpdateFilesSummary();
        }

        void RefreshFiles()
        {
            var prev = files != null ? files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) : new Dictionary<string, ProjectFile>();
            files = project != null ? project.Scan() : new List<ProjectFile>();
            foreach (var f in files)
            {
                ProjectFile p;
                if (prev.TryGetValue(f.Path, out p)) { f.Action = p.Action; f.Selected = p.Selected; }   // keep what the user marked
            }
            FillGrid();
            if (project != null && files.Count == 0) Log("הפרויקט ריק. הוסף קבצי רום (super, boot, init_boot, vbmeta, lk...) בכפתור \"הוסף קבצים\" או בגרירה.", Color.Khaki);
        }

        ProjectFile SelectedFile()
        {
            if (gridFiles == null || gridFiles.SelectedRows.Count == 0) return null;
            return gridFiles.SelectedRows[0].Tag as ProjectFile;
        }

        void SetActionForSelected(EditAction a)
        {
            var f = SelectedFile(); if (f == null) return;
            f.Action = a; f.Selected = a != EditAction.None;
            gridBusy = true;
            try { StyleRow(gridFiles.SelectedRows[0], f); } finally { gridBusy = false; }
            UpdateFilesSummary();
        }

        void UpdateFilesSummary()
        {
            if (lblFilesSummary == null || btnRun == null) return;
            int n = files == null ? 0 : files.Count(f => f.Selected && f.Action != EditAction.None);
            lblFilesSummary.Text = n == 0 ? "לא נבחרו עריכות." : (n == 1 ? "קובץ אחד לעריכה." : n + " קבצים לעריכה.");
            btnRun.Enabled = n > 0 && !BusyNow();
        }

        string PlanTarget()
        {
            var f = files.FirstOrDefault(x => x.Selected && x.Action == EditAction.Ro2rw) ?? files.FirstOrDefault(x => x.Action == EditAction.Ro2rw);
            return f != null ? f.Path : null;
        }

        // ---- tool options (shown under the table) ----
        void BuildMagiskOptions(Control p)
        {
            cmbAbi = new ComboBox { Size = new Size(160, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAbi.Items.AddRange(new object[] { "auto", "arm64-v8a", "armeabi-v7a", "x86_64", "x86" });
            cmbAbi.SelectedIndex = 0;
            p.Controls.Add(OptRow("ABI:", cmbAbi));
            cbKeepVerity = Opt("השאר verity (KEEPVERITY)", false);
            cbKeepEnc = Opt("השאר הצפנה כפויה (KEEPFORCEENCRYPT)", false);
            cbRecovery = Opt("מצב recovery (RECOVERYMODE)", false);
            cbPatchVbFlag = Opt("כבה אימות ב-vbmeta של ה-boot (PATCHVBMETAFLAG)", false);
            lblMagisk = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 4) };
            var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var btnApk = new Button { Text = "בחר Magisk.apk אחר...", Size = new Size(190, 30) };
            btnApk.Click += (s, e) => PickMagiskApk();
            var btnVer = new Button { Text = "הורד גרסה (למשל 26)...", Size = new Size(190, 30) };
            btnVer.Click += (s, e) => DownloadMagiskVersion();
            btns.Controls.AddRange(new Control[] { btnApk, btnVer });
            p.Controls.AddRange(new Control[] { cbKeepVerity, cbKeepEnc, cbRecovery, cbPatchVbFlag, lblMagisk, btns });
            UpdateMagiskLabel();
        }

        void BuildAvbOptions(Control p)
        {
            cmbVbMode = new ComboBox { Size = new Size(260, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbVbMode.Items.AddRange(new object[] { "הכול (verity + verification)", "רק verity (hashtree)", "רק verification (חתימות)" });
            cmbVbMode.SelectedIndex = 0;
            p.Controls.Add(OptRow("מה לכבות:", cmbVbMode));
        }

        void BuildLkOptions(Control p)
        {
            cbLkDelay = Opt("בטל את ההשהיה של 5 שניות", true);
            cbLkText = Opt("מחק את טקסט האזהרה מהמסך", true);
            cmbLkAndroid = new ComboBox { Size = new Size(200, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLkAndroid.Items.AddRange(new object[] { "זיהוי אוטומטי", "אנדרואיד 9 ומטה", "אנדרואיד 10 ומעלה" });
            cmbLkAndroid.SelectedIndex = 0;
            p.Controls.AddRange(new Control[] { cbLkDelay, cbLkText });
            p.Controls.Add(OptRow("גרסת אנדרואיד:", cmbLkAndroid));
        }

        void BuildRo2rwOptions(Control p)
        {
            rbMax = new RadioButton { Text = "אחוזים מהמקום הפנוי (MAX)", AutoSize = true, Checked = true, Margin = new Padding(0, 0, 0, 4) };
            var pct = new Panel { Size = new Size(390, 54), Margin = new Padding(0, 0, 0, 2) };
            nS = Num(pct, "System", 6, 0, 70); nV = Num(pct, "Vendor", 80, 0, 10); nP = Num(pct, "Product", 154, 0, 10); nSE = Num(pct, "Sys_ext", 228, 0, 5); nOT = Num(pct, "אחרות", 302, 0, 5);
            lblTotal = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            rbFixed = new RadioButton { Text = "גודל קבוע לכל מחיצה (FIXED)", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            nFixed = new NumericUpDown { Size = new Size(90, 26), Maximum = 100000, Value = 200 };
            cbSparse = Opt("פלט sparse (קטן יותר)", true);
            cbFstab = Opt("הוסף שורות ext4 ל-fstab של vendor", true);
            btnPlan = new Button { Text = "תכנון מפורט לפי מחיצה...", Size = new Size(230, 32) };
            btnPlan.Click += (s, e) => OpenSuperPlan(PlanTarget());
            p.Controls.AddRange(new Control[] { rbMax, pct, lblTotal, rbFixed });
            p.Controls.Add(OptRow("MB:", nFixed));
            p.Controls.AddRange(new Control[] { cbSparse, cbFstab, new Label { Text = "הסרת WiFi מתבצעת אוטומטית בכל עריכת super.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 8) }, btnPlan });
            EventHandler upd = (s, e) => UpdateSizeUi();
            foreach (var n in new[] { nS, nV, nP, nSE, nOT }) n.ValueChanged += upd;
            rbMax.CheckedChanged += upd;
            UpdateSizeUi();
        }
    }
}
