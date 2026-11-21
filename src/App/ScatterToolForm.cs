using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Ro2rwPc
{
    public enum ScatterToolResult { Close, ReadDevice }

    /// <summary>Builds an SP Flash Tool scatter from a partition table (gpt.bin from a dump, or read from the device through SP), and shows existing scatters.</summary>
    public sealed class ScatterToolForm : Form
    {
        readonly string projectName, projectRoot;
        readonly TextBox tbChip = new TextBox { Width = 100, RightToLeft = RightToLeft.No }, tbImages = new TextBox { RightToLeft = RightToLeft.No, Dock = DockStyle.Fill };
        readonly RadioButton rbEmmc = new RadioButton { Text = "eMMC", AutoSize = true, Checked = true }, rbUfs = new RadioButton { Text = "UFS", AutoSize = true };
        readonly DataGridView grid = new DataGridView();
        readonly Label lblInfo = new Label { AutoSize = true, MaximumSize = new Size(720, 0) };
        Gpt gpt;

        public ScatterToolResult Result { get; private set; }
        public string Chip { get { return tbChip.Text.Trim().ToUpperInvariant(); } }
        public bool Ufs { get { return rbUfs.Checked; } }

        public ScatterToolForm(string projectName, string projectRoot, Gpt table, string chip, bool ufs)
        {
            this.projectName = projectName; this.projectRoot = projectRoot; gpt = table;
            Text = "כלי scatter";
            Font = new Font("Segoe UI", 10f);
            RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(780, 720); BackColor = Color.White;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18, 14, 18, 12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(new Label { Text = "כלי scatter", Font = new Font("Segoe UI", 13f, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(21, 101, 192), Margin = new Padding(0, 0, 0, 4) });
            root.Controls.Add(new Label { Text = "ה-scatter הוא מפת המחיצות ש-SP Flash Tool צריך כדי לצרוב. אפשר לבנות אותו מטבלת מחיצות (gpt.bin) ולא צריך כלי חיצוני.", AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = Color.FromArgb(90, 99, 110), Margin = new Padding(0, 0, 0, 8) });

            tbChip.Text = chip ?? ""; rbUfs.Checked = ufs; rbEmmc.Checked = !ufs;
            var top = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 6) };
            top.Controls.AddRange(new Control[] { new Label { Text = "מעבד:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, tbChip, new Label { Text = "  אחסון:", AutoSize = true, Margin = new Padding(8, 6, 4, 0) }, rbEmmc, rbUfs });
            root.Controls.Add(top);

            var src = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 4) };
            var bLoad = Btn("טען gpt.bin...", 130); bLoad.Click += (s, e) => LoadGpt();
            var bDev = Btn("קרא טבלה מהמכשיר (SP)", 220); bDev.Click += (s, e) => { if (ValidChip()) { Result = ScatterToolResult.ReadDevice; DialogResult = DialogResult.OK; } };
            var bOpen = Btn("הצג scatter קיים...", 160); bOpen.Click += (s, e) => ShowExisting();
            src.Controls.AddRange(new Control[] { bLoad, bDev, bOpen });
            root.Controls.Add(src);
            root.Controls.Add(lblInfo);

            var imgRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 6) };
            imgRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); imgRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); imgRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tbImages.Text = projectRoot;
            var bBrowse = new Button { Text = "...", Width = 40 };
            bBrowse.Click += (s, e) => { using (var d = new FolderBrowserDialog { SelectedPath = tbImages.Text }) if (d.ShowDialog(this) == DialogResult.OK) { tbImages.Text = d.SelectedPath; Fill(); } };
            imgRow.Controls.Add(new Label { Text = "תיקיית הקבצים:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
            imgRow.Controls.Add(tbImages, 1, 0); imgRow.Controls.Add(bBrowse, 2, 0);
            root.Controls.Add(imgRow);

            grid.Dock = DockStyle.Fill; grid.Height = 360; grid.RightToLeft = RightToLeft.Yes; grid.AutoGenerateColumns = false; grid.ReadOnly = true;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.FixedSingle;
            grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 236, 242); grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(225, 229, 236);
            SetColumns(false);
            root.Controls.Add(grid);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 8, 0, 0) };
            var bMake = new Button { Text = "צור scatter", Size = new Size(160, 38), BackColor = Color.FromArgb(21, 101, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            bMake.Click += (s, e) => Make();
            var bClose = new Button { Text = "סגור", DialogResult = DialogResult.Cancel, Size = new Size(100, 38), FlatStyle = FlatStyle.Flat };
            btns.Controls.AddRange(new Control[] { bMake, bClose });
            root.Controls.Add(btns);
            CancelButton = bClose;
            Controls.Add(root);
            Fill();
        }

        static Button Btn(string text, int w) { return new Button { Text = text, Size = new Size(w, 34), FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0) }; }

        void Warn(string text) { MessageBox.Show(this, text, "כלי scatter", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign); }

        bool ValidChip()
        {
            string c = Chip;
            if (!Regex.IsMatch(c, @"^MT[0-9]{4}[A-Z]?$")) { Warn("הזן דגם מעבד תקין, למשל MT6765."); return false; }
            tbChip.Text = c;
            return true;
        }

        void SetColumns(bool existing)
        {
            grid.Columns.Clear();
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "מחיצה", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "התחלה", Width = 130 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "גודל", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = existing ? "קובץ (is_download)" : "קובץ בתיקייה", Width = 220 });
        }

        void LoadGpt()
        {
            using (var d = new OpenFileDialog { Title = "בחר gpt.bin", Filter = "gpt.bin / דאמפ של תחילת הדיסק|*.bin;*.img;*|כל הקבצים|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var head = new byte[0x8000];
                    int n; using (var f = File.OpenRead(d.FileName)) n = f.Read(head, 0, head.Length);
                    Array.Resize(ref head, n);
                    gpt = Gpt.Parse(head);
                    Fill();
                }
                catch (Exception ex) { Warn("הקובץ אינו טבלת GPT תקינה: " + ex.Message); }
            }
        }

        void Fill()
        {
            SetColumns(false);
            grid.Rows.Clear();
            if (gpt == null)
            {
                lblInfo.Text = "אין טבלת מחיצות. טען gpt.bin (למשל משאיבה קודמת) או קרא אותה מהמכשיר.";
                lblInfo.ForeColor = Color.Firebrick;
                return;
            }
            foreach (var p in gpt.Parts)
                grid.Rows.Add(p.Name, "0x" + p.Start.ToString("x"), Converter.Human(p.Size), FileFor(p.Name) ?? "-");
            int have = gpt.Parts.Count(p => FileFor(p.Name) != null);
            lblInfo.Text = "טבלת מחיצות: " + gpt.Parts.Count + " מחיצות (סקטור " + gpt.SectorSize + "). נמצאו קבצים ל-" + have + ". מחיצה עם קובץ תסומן בצריבה (is_download).";
            lblInfo.ForeColor = Color.FromArgb(46, 125, 50);
        }

        string FileFor(string name)
        {
            string dir = tbImages.Text;
            if (!Directory.Exists(dir)) return null;
            foreach (var ext in new[] { ".img", ".bin" })
                if (File.Exists(Path.Combine(dir, name + ext))) return name + ext;
            return null;
        }

        void Make()
        {
            if (gpt == null) { Warn("אין טבלת מחיצות."); return; }
            if (!ValidChip()) return;
            string name = Chip + "_Android_scatter.txt";
            if (File.Exists(Path.Combine(projectRoot, name))) name = Chip + "_device_scatter.txt";   // never overwrite a firmware scatter
            string path = ScatterWriter.Write(projectRoot, projectName, gpt, Chip, Ufs, FileFor, name);
            MessageBox.Show(this, "נוצר scatter:\n" + path, "כלי scatter", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }

        void ShowExisting()
        {
            using (var d = new OpenFileDialog { Title = "בחר scatter", Filter = "scatter|*scatter*.txt|כל הקבצים|*.*", InitialDirectory = projectRoot })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var list = SpDump.ReadScatter(d.FileName);
                    SetColumns(true);
                    grid.Rows.Clear();
                    foreach (var e in list) grid.Rows.Add(e.Name, "0x" + e.Start.ToString("x"), Converter.Human(e.Size), (e.File ?? "NONE") + (e.Download ? "  (כן)" : ""));
                    lblInfo.Text = Path.GetFileName(d.FileName) + ": " + list.Count + " מחיצות, " + list.Count(e => e.Download && e.File != null) + " מסומנות לצריבה.";
                    lblInfo.ForeColor = Color.FromArgb(31, 41, 51);
                }
                catch (Exception ex) { Warn(ex.Message); }
            }
        }
    }
}
