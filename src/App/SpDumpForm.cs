using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Ro2rwPc
{
    public enum SpDumpAction { Cancel, ReadGpt, Dump }

    public sealed class SpDumpPlan
    {
        public SpDumpAction Action;
        public string Platform;
        public bool Ufs;
        public string Com;
        public int PartId;
        public List<string> Names = new List<string>();
        public bool Single;          // one continuous read, then cut by the partition table
        public bool Preloader;       // also back up the preloader (BOOT1 region)
        public string GptFile;       // a gpt.bin the user loaded from disk (null = use the project's)
    }

    /// <summary>Window for a dump through SP Flash Tool: which partitions, and one file per partition or one dump that is split afterwards.</summary>
    public sealed class SpDumpForm : Form
    {
        readonly TextBox tbChip = new TextBox(), tbCom = new TextBox();
        readonly RadioButton rbEmmc = new RadioButton { Text = "eMMC", AutoSize = true, Checked = true }, rbUfs = new RadioButton { Text = "UFS (לא נבדק)", AutoSize = true };
        readonly RadioButton rbSeparate = new RadioButton { AutoSize = true, Checked = true }, rbSingle = new RadioButton { AutoSize = true };
        readonly NumericUpDown nPart = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 8, Width = 60 };
        readonly CheckBox cbPre = new CheckBox { Text = "גם preloader (גיבוי מאזור BOOT1)", AutoSize = true, Enabled = false };
        readonly DataGridView grid = new DataGridView();
        readonly Label lblTable = new Label { AutoSize = true, MaximumSize = new Size(700, 0) };
        readonly Button btnDump = new Button { Text = "שאב", Size = new Size(140, 38) };
        Gpt gpt;
        string gptFile;

        public SpDumpPlan Plan { get; private set; }

        public SpDumpForm(Gpt table, string chip, bool ufs, string com, int partId)
        {
            Plan = new SpDumpPlan { Action = SpDumpAction.Cancel };
            gpt = table;
            Text = "שאיבה דרך SP Flash Tool";
            Font = new Font("Segoe UI", 10f);
            RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(780, 780); BackColor = Color.White;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18, 14, 18, 12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(new Label { Text = "שאיבה דרך SP Flash Tool", Font = new Font("Segoe UI", 13f, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(21, 101, 192), Margin = new Padding(0, 0, 0, 8) });

            // chip / storage / COM
            var top = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
            tbChip.Width = 100; tbChip.Text = chip ?? ""; tbChip.RightToLeft = RightToLeft.No;
            tbCom.Width = 80; tbCom.Text = com ?? ""; tbCom.RightToLeft = RightToLeft.No;
            rbUfs.Checked = ufs; rbEmmc.Checked = !ufs;
            nPart.Value = partId > 0 ? partId : SpDump.UserPartId(ufs);
            top.Controls.AddRange(new Control[] { new Label { Text = "מעבד:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, tbChip, new Label { Text = "  אחסון:", AutoSize = true, Margin = new Padding(8, 6, 4, 0) }, rbEmmc, rbUfs, new Label { Text = "  COM (אופציונלי):", AutoSize = true, Margin = new Padding(8, 6, 4, 0) }, tbCom });
            root.Controls.Add(top);
            rbEmmc.CheckedChanged += (s, e) => { if (rbEmmc.Checked) nPart.Value = SpDump.UserPartId(false); };
            rbUfs.CheckedChanged += (s, e) => { if (rbUfs.Checked) nPart.Value = SpDump.UserPartId(true); };

            // partition table
            root.Controls.Add(lblTable);
            var tbtn = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 4) };
            var btnRead = Btn("קרא טבלת מחיצות מהמכשיר", 230);
            btnRead.Click += (s, e) => { if (!ValidChip()) return; Plan = Make(SpDumpAction.ReadGpt); DialogResult = DialogResult.OK; };
            var btnLoad = Btn("טען gpt.bin...", 130);
            btnLoad.Click += (s, e) => LoadGptFile();
            tbtn.Controls.AddRange(new Control[] { btnRead, btnLoad });
            root.Controls.Add(tbtn);

            grid.Dock = DockStyle.Fill; grid.Height = 250; grid.RightToLeft = RightToLeft.Yes; grid.AutoGenerateColumns = false;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.FixedSingle;
            grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 236, 242); grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(225, 229, 236);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "שאב", Width = 60 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "מחיצה", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "גודל", ReadOnly = true, Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "התחלה", ReadOnly = true, Width = 130 });
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => UpdateSummary();
            root.Controls.Add(grid);

            var presets = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 8) };
            presets.Controls.Add(PresetBtn("לעריכה", n => SpDump.IsEditPartition(n)));
            presets.Controls.Add(PresetBtn("הכול חוץ מ-userdata", n => !n.Equals("userdata", StringComparison.OrdinalIgnoreCase)));
            presets.Controls.Add(PresetBtn("הכול", n => true));
            presets.Controls.Add(PresetBtn("נקה", n => false));
            root.Controls.Add(presets);

            // mode
            rbSeparate.Text = "מחיצות נפרדות (מומלץ): כל מחיצה נשאבת לקובץ משלה, לפי שם";
            rbSingle.Text = "דאמפ אחד ופיצול (כמו במדריך): קריאה רציפה אחת, ואז חיתוך לפי טבלת המחיצות";
            rbSeparate.CheckedChanged += (s, e) => { cbPre.Enabled = rbSingle.Checked; if (!rbSingle.Checked) cbPre.Checked = false; };
            root.Controls.Add(rbSeparate);
            root.Controls.Add(rbSingle);
            cbPre.Margin = new Padding(24, 2, 0, 4);
            root.Controls.Add(cbPre);

            var adv = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 2, 0, 6) };
            adv.Controls.Add(new Label { Text = "מתקדם: part-id של אזור המשתמש (eMMC=8):", AutoSize = true, ForeColor = Color.FromArgb(90, 99, 110), Margin = new Padding(0, 6, 6, 0) });
            adv.Controls.Add(nPart);
            root.Controls.Add(adv);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            btnDump.BackColor = Color.FromArgb(21, 101, 192); btnDump.ForeColor = Color.White; btnDump.FlatStyle = FlatStyle.Flat;
            btnDump.Click += (s, e) => DoDump();
            var cancel = new Button { Text = "ביטול", DialogResult = DialogResult.Cancel, Size = new Size(100, 38), FlatStyle = FlatStyle.Flat };
            btns.Controls.AddRange(new Control[] { btnDump, cancel });
            root.Controls.Add(btns);
            CancelButton = cancel;
            Controls.Add(root);

            Fill();
        }

        static Button Btn(string text, int w) { return new Button { Text = text, Size = new Size(w, 34), FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0) }; }

        Button PresetBtn(string text, Func<string, bool> pick)
        {
            var b = Btn(text, text.Length > 8 ? 170 : 90);
            b.Click += (s, e) => { foreach (DataGridViewRow r in grid.Rows) r.Cells[0].Value = pick((string)r.Cells[1].Value); };
            return b;
        }

        bool ValidChip()
        {
            string c = (tbChip.Text ?? "").Trim().ToUpperInvariant();
            if (!Regex.IsMatch(c, @"^MT[0-9]{4}[A-Z]?$")) { MessageBox.Show(this, "הזן דגם מעבד תקין, למשל MT6765.", "SP Flash Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign); return false; }
            tbChip.Text = c;
            return true;
        }

        void LoadGptFile()
        {
            using (var d = new OpenFileDialog { Title = "בחר gpt.bin", Filter = "gpt.bin / דאמפ של תחילת הדיסק|*.bin;*.img;*|כל הקבצים|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var head = new byte[0x8000];
                    int n; using (var f = File.OpenRead(d.FileName)) n = f.Read(head, 0, head.Length);
                    Array.Resize(ref head, n);
                    gpt = Gpt.Parse(head); gptFile = d.FileName;
                    Fill();
                }
                catch (Exception ex) { MessageBox.Show(this, "הקובץ אינו טבלת GPT תקינה: " + ex.Message, "SP Flash Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign); }
            }
        }

        void Fill()
        {
            grid.Rows.Clear();
            if (gpt == null)
            {
                lblTable.Text = "עוד אין טבלת מחיצות. קרא אותה מהמכשיר (SP קורא 32KB, קריאה בלבד) או טען gpt.bin.";
                lblTable.ForeColor = Color.Firebrick;
                btnDump.Enabled = false;
                return;
            }
            foreach (var p in gpt.Parts)
                grid.Rows.Add(SpDump.IsEditPartition(p.Name), p.Name, Converter.Human(p.Size), "0x" + p.Start.ToString("x"));
            lblTable.ForeColor = Color.FromArgb(46, 125, 50);
            btnDump.Enabled = true;
            UpdateSummary();
        }

        void UpdateSummary()
        {
            if (gpt == null) return;
            var names = Checked();
            long total = gpt.Parts.Where(p => names.Contains(p.Name)).Sum(p => p.Size);
            lblTable.Text = "טבלת מחיצות: " + gpt.Parts.Count + " מחיצות (סקטור " + gpt.SectorSize + "). נבחרו " + names.Count + ", סה\"כ " + Converter.Human(total) + ".";
        }

        List<string> Checked()
        {
            return grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is bool && (bool)r.Cells[0].Value).Select(r => (string)r.Cells[1].Value).ToList();
        }

        SpDumpPlan Make(SpDumpAction a)
        {
            return new SpDumpPlan
            {
                Action = a, Platform = tbChip.Text.Trim().ToUpperInvariant(), Ufs = rbUfs.Checked, Com = tbCom.Text.Trim().ToUpperInvariant(),
                PartId = (int)nPart.Value, Names = Checked(), Single = rbSingle.Checked, Preloader = rbSingle.Checked && cbPre.Checked, GptFile = gptFile
            };
        }

        void DoDump()
        {
            if (!ValidChip()) return;
            if (gpt == null || Checked().Count == 0) { MessageBox.Show(this, "סמן לפחות מחיצה אחת.", "SP Flash Tool", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign); return; }
            Plan = Make(SpDumpAction.Dump);
            DialogResult = DialogResult.OK;
        }
    }
}
