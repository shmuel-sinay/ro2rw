using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Ro2rwPc
{
    public sealed class MainForm : Form
    {
        readonly Font fBase = new Font("Segoe UI", 10f);
        readonly Font fBold = new Font("Segoe UI", 10f, FontStyle.Bold);
        readonly Font fTitle = new Font("Segoe UI", 15f, FontStyle.Bold);

        Label lblFile, lblSuperInfo, lblTotal;
        Button btnPick, btnStart, btnCancel, btnOpenOut;
        ListView lvParts;
        RadioButton rbMax, rbFixed;
        NumericUpDown nS, nV, nP, nSE, nOT, nFixed;
        CheckBox cbSparse, cbFstab, cbVbmeta;
        ProgressBar prog;
        RichTextBox log;

        string superPath;
        string outDir;
        Thread worker;
        volatile bool cancel;
        Dictionary<string, PartInfo> lastParts = new Dictionary<string, PartInfo>();

        public MainForm()
        {
            Text = "RO2RW PC " + Program.Version + " - המרת super.img לקריאה וכתיבה";
            Font = fBase;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(940, 760);
            MinimumSize = new Size(900, 700);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => { var f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f != null && f.Length > 0) LoadSuper(f[0]); };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Build();
            Log("ברוך הבא. בחר קובץ super.img (או גרור אותו לחלון), בדוק את ההגדרות ולחץ \"התחל המרה\".", Color.LightGreen);
            Log("התוכנה עובדת על המחשב בלבד - בלי טלפון ובלי ADB. בסוף תקבל super חדש לצריבה.", Color.LightGreen);
        }

        // ------------------------------------------------------------ UI
        void Build()
        {
            var title = new Label { Text = "RO2RW למחשב", Font = fTitle, AutoSize = true, Location = new Point(14, 10) };
            var sub = new Label { Text = "ממיר את מחיצות super (EROFS / ext4 לקריאה בלבד) ל-ext4 לקריאה וכתיבה, עם מקום פנוי", AutoSize = true, Location = new Point(16, 44), ForeColor = Color.DimGray };
            Controls.Add(title); Controls.Add(sub);

            // step 1
            var g1 = new GroupBox { Text = "1. קובץ super", Location = new Point(12, 72), Size = new Size(916, 92) };
            btnPick = new Button { Text = "בחר super.img...", Location = new Point(14, 26), Size = new Size(170, 34), Font = fBold };
            btnPick.Click += (s, e) => PickFile();
            lblFile = new Label { Text = "לא נבחר קובץ. אפשר גם לגרור את הקובץ לחלון.", Location = new Point(196, 33), Size = new Size(700, 24), AutoEllipsis = true };
            lblSuperInfo = new Label { Text = "", Location = new Point(14, 64), Size = new Size(880, 22), ForeColor = Color.DarkGreen };
            g1.Controls.AddRange(new Control[] { btnPick, lblFile, lblSuperInfo });
            Controls.Add(g1);

            // step 2
            var g2 = new GroupBox { Text = "2. מחיצות (סמן את המחיצות להמרה)", Location = new Point(12, 170), Size = new Size(450, 274) };
            lvParts = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Location = new Point(10, 24), Size = new Size(430, 240), RightToLeftLayout = true };
            lvParts.Columns.Add("מחיצה", 130); lvParts.Columns.Add("מערכת קבצים", 110); lvParts.Columns.Add("גודל", 100); lvParts.Columns.Add("קטגוריה", 70);
            lvParts.ItemCheck += (s, e) =>
            {
                var pi = lvParts.Items[e.Index].Tag as PartInfo;
                if (pi == null || (pi.Fs != "EROFS" && pi.Fs != "EXT4")) e.NewValue = CheckState.Unchecked;
            };
            g2.Controls.Add(lvParts);
            Controls.Add(g2);

            // step 3
            var g3 = new GroupBox { Text = "3. הגדלת מחיצות", Location = new Point(472, 170), Size = new Size(456, 274) };
            rbMax = new RadioButton { Text = "לפי אחוזים מהמקום הפנוי ב-super (MAX)", Location = new Point(14, 24), Size = new Size(420, 24), Checked = true };
            rbFixed = new RadioButton { Text = "גודל קבוע לכל מחיצה (FIXED)", Location = new Point(14, 136), Size = new Size(420, 24) };
            int y = 54;
            nS = Num(g3, "System", 14, y, 70); nV = Num(g3, "Vendor", 100, y, 10); nP = Num(g3, "Product", 186, y, 10); nSE = Num(g3, "System_ext", 272, y, 5); nOT = Num(g3, "אחרות", 358, y, 5);
            lblTotal = new Label { Location = new Point(14, 108), Size = new Size(420, 22) };
            var lf = new Label { Text = "MB לכל מחיצה:", Location = new Point(34, 168), Size = new Size(110, 24) };
            nFixed = new NumericUpDown { Location = new Point(148, 165), Size = new Size(100, 26), Minimum = 0, Maximum = 100000, Value = 200 };
            cbSparse = new CheckBox { Text = "קובץ פלט sparse (קטן יותר, ל-fastboot)", Location = new Point(14, 200), Size = new Size(430, 22), Checked = true };
            cbFstab = new CheckBox { Text = "הוסף שורות ext4 ל-fstab של vendor", Location = new Point(14, 222), Size = new Size(430, 22), Checked = true };
            cbVbmeta = new CheckBox { Text = "צור vbmeta_disabled.img (אם יש vbmeta.img ליד super)", Location = new Point(14, 244), Size = new Size(430, 22), Checked = true };
            g3.Controls.AddRange(new Control[] { rbMax, rbFixed, lblTotal, lf, nFixed, cbSparse, cbFstab, cbVbmeta });
            Controls.Add(g3);
            EventHandler upd = (s, e) => UpdateSizeUi();
            foreach (var n in new[] { nS, nV, nP, nSE, nOT }) n.ValueChanged += upd;
            rbMax.CheckedChanged += upd;
            UpdateSizeUi();

            // run
            btnStart = new Button { Text = "התחל המרה", Location = new Point(12, 454), Size = new Size(200, 44), Font = new Font("Segoe UI", 12f, FontStyle.Bold), BackColor = Color.FromArgb(46, 125, 50), ForeColor = Color.White, Enabled = false };
            btnStart.FlatStyle = FlatStyle.Flat;
            btnStart.Click += (s, e) => StartConvert();
            btnCancel = new Button { Text = "ביטול", Location = new Point(222, 454), Size = new Size(100, 44), Enabled = false };
            btnCancel.Click += (s, e) => { cancel = true; Log("מבטל...", Color.Khaki); };
            btnOpenOut = new Button { Text = "פתח תיקיית פלט", Location = new Point(332, 454), Size = new Size(150, 44), Enabled = false };
            btnOpenOut.Click += (s, e) => { if (outDir != null && Directory.Exists(outDir)) Process.Start("explorer.exe", "\"" + outDir + "\""); };
            prog = new ProgressBar { Location = new Point(492, 464), Size = new Size(436, 26), Maximum = 1000 };
            Controls.AddRange(new Control[] { btnStart, btnCancel, btnOpenOut, prog });

            log = new RichTextBox
            {
                Location = new Point(12, 508), Size = new Size(916, 240), ReadOnly = true, BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro,
                Font = new Font("Consolas", 10f), RightToLeft = RightToLeft.Yes, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(log);
        }

        NumericUpDown Num(Control parent, string label, int x, int y, int val)
        {
            parent.Controls.Add(new Label { Text = label + " %", Location = new Point(x, y), Size = new Size(84, 20) });
            var n = new NumericUpDown { Location = new Point(x, y + 22), Size = new Size(70, 26), Minimum = 0, Maximum = 100, Value = val };
            parent.Controls.Add(n);
            return n;
        }

        void UpdateSizeUi()
        {
            bool max = rbMax.Checked;
            foreach (var n in new[] { nS, nV, nP, nSE, nOT }) n.Enabled = max;
            nFixed.Enabled = !max;
            int sum = (int)(nS.Value + nV.Value + nP.Value + nSE.Value + nOT.Value);
            lblTotal.Text = "סה\"כ: " + sum + "%" + (sum > 100 ? "  - יותר מ-100%!" : "");
            lblTotal.ForeColor = sum > 100 ? Color.Firebrick : Color.Black;
            lblTotal.Enabled = max;
        }

        void Log(string text, Color? c = null)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(text, c))); return; }
            log.SelectionStart = log.TextLength;
            log.SelectionColor = c ?? Color.Gainsboro;
            log.AppendText(text + "\n");
            log.ScrollToCaret();
        }

        void SetProgress(int p)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(p))); return; }
            prog.Value = Math.Max(0, Math.Min(1000, p));
        }

        void SetBusy(bool busy)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetBusy(busy))); return; }
            btnPick.Enabled = !busy; btnStart.Enabled = !busy && superPath != null; btnCancel.Enabled = busy;
            lvParts.Enabled = !busy;
            UseWaitCursor = busy;
        }

        // ------------------------------------------------------------ actions
        void PickFile()
        {
            using (var d = new OpenFileDialog { Title = "בחר super.img", Filter = "super image|super*.img;super*.img.*;super*;*.img|כל הקבצים|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) LoadSuper(d.FileName);
        }

        void LoadSuper(string path)
        {
            if (worker != null && worker.IsAlive) return;
            lvParts.Items.Clear();
            lblSuperInfo.Text = "";
            superPath = null;
            btnStart.Enabled = false;
            lblFile.Text = path;
            Log("טוען " + Path.GetFileName(path) + "...", Color.DeepSkyBlue);
            SetBusy(true);
            worker = new Thread(() =>
            {
                try
                {
                    using (var c = new Converter())
                    {
                        c.Log = s => Log(s);
                        c.Open(path);
                        var parts = c.Parts;
                        string info = string.Format("גודל super: {0}  |  {1} מחיצות עם תוכן  |  {2}",
                            Converter.Human(c.DeviceSize), parts.Count(p => p.Size > 0), c.IsSparse ? "sparse" : "raw");
                        BeginInvoke(new Action(() =>
                        {
                            superPath = path;
                            lblSuperInfo.Text = info;
                            lastParts.Clear();
                            foreach (var p in parts.Where(x => x.Size > 0))
                            {
                                var it = new ListViewItem(new[] { p.Name, p.Fs == "RAW" ? "לא מוכר (יועתק)" : p.Fs, Converter.Human(p.Size), p.Category }) { Tag = p };
                                lvParts.Items.Add(it);
                                it.Checked = p.Convert;
                                lastParts[p.Name] = p;
                            }
                            Log("הקובץ נטען. סמן מחיצות ולחץ \"התחל המרה\".", Color.LightGreen);
                        }));
                    }
                }
                catch (Exception ex) { Log("שגיאה: " + ex.Message, Color.Tomato); }
                finally { SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        void StartConvert()
        {
            if (superPath == null) return;
            int sum = (int)(nS.Value + nV.Value + nP.Value + nSE.Value + nOT.Value);
            if (rbMax.Checked && sum > 100) { MessageBox.Show(this, "סכום האחוזים גדול מ-100%.", "RO2RW", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign); return; }
            var selected = new HashSet<string>(lvParts.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((PartInfo)i.Tag).Name));
            if (selected.Count == 0) { MessageBox.Show(this, "לא נבחרה אף מחיצה להמרה.", "RO2RW", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign); return; }
            var st = new Settings
            {
                MaxMode = rbMax.Checked, PctS = (int)nS.Value, PctV = (int)nV.Value, PctP = (int)nP.Value, PctSE = (int)nSE.Value, PctOT = (int)nOT.Value,
                FixedMb = (int)nFixed.Value, SparseOutput = cbSparse.Checked, PatchVendorFstab = cbFstab.Checked, PatchVbmeta = cbVbmeta.Checked
            };
            string path = superPath;
            outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "RO2RW_output");
            cancel = false;
            SetBusy(true);
            btnOpenOut.Enabled = false;
            SetProgress(0);
            worker = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    Directory.CreateDirectory(outDir);
                    using (var c = new Converter())
                    {
                        c.Log = s => Log(s);
                        c.Progress = SetProgress;
                        c.Cancelled = () => cancel;
                        c.Open(path);
                        CheckDisk(c.DeviceSize, st.SparseOutput);
                        foreach (var p in c.Parts) p.Convert = selected.Contains(p.Name) && (p.Fs == "EROFS" || p.Fs == "EXT4");
                        Log("שלב 1/3: קריאת מערכות הקבצים", Color.DeepSkyBlue);
                        c.Analyse(st);
                        Log("שלב 2/3: חישוב גדלים", Color.DeepSkyBlue);
                        c.Plan(st);
                        Log("שלב 3/3: כתיבת super חדש", Color.DeepSkyBlue);
                        string outFile = c.Write(st, outDir);
                        string vb = st.PatchVbmeta ? Converter.PatchVbmeta(path, outDir, s => Log(s)) : null;
                        WriteReadme(outFile, vb, c);
                        Log("", null);
                        Log("הסתיים בהצלחה תוך " + sw.Elapsed.ToString(@"mm\:ss") + "!", Color.LightGreen);
                        Log("קובץ: " + outFile, Color.LightGreen);
                        if (vb != null) Log("vbmeta עם אימות כבוי: " + vb, Color.LightGreen);
                        else if (st.PatchVbmeta) Log("לא נמצא vbmeta.img ליד super - דלג או צרוב עם: fastboot --disable-verity --disable-verification flash vbmeta vbmeta.img", Color.Khaki);
                        Log("הוראות צריבה נשמרו בקובץ HOW_TO_FLASH.txt בתיקיית הפלט.", Color.LightGreen);
                        BeginInvoke(new Action(() => { btnOpenOut.Enabled = true; }));
                    }
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex)
                {
                    Log("שגיאה: " + ex.Message, Color.Tomato);
                    try { File.AppendAllText(Path.Combine(outDir, "error.log"), DateTime.Now + "\r\n" + ex + "\r\n\r\n"); } catch { }
                }
                finally { SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        void CheckDisk(long superSize, bool sparse)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(outDir));
                long need = superSize + (sparse ? superSize : 0) + (64L << 20);
                if (drive.AvailableFreeSpace < need)
                    throw new IOException(string.Format("אין מספיק מקום בכונן {0}: צריך כ-{1}, פנויים {2}.", drive.Name, Converter.Human(need), Converter.Human(drive.AvailableFreeSpace)));
            }
            catch (ArgumentException) { }
        }

        void WriteReadme(string outFile, string vbmeta, Converter c)
        {
            var lines = new List<string>
            {
                "RO2RW PC - הוראות צריבה",
                "=========================",
                "",
                "!!! גבה את המכשיר לפני הצריבה. האחריות עליך. !!!",
                "",
                "קבצים:",
                "  " + Path.GetFileName(outFile) + "  - super חדש עם מחיצות ext4 לקריאה וכתיבה",
            };
            if (vbmeta != null) lines.Add("  " + Path.GetFileName(vbmeta) + "  - vbmeta עם אימות (verity) כבוי");
            lines.Add("");
            lines.Add("צריבה ב-fastboot (הטלפון במצב fastboot / fastbootd):");
            lines.Add("  fastboot flash super " + Path.GetFileName(outFile));
            if (vbmeta != null) lines.Add("  fastboot flash vbmeta " + Path.GetFileName(vbmeta));
            else lines.Add("  fastboot --disable-verity --disable-verification flash vbmeta vbmeta.img");
            lines.Add("  fastboot reboot");
            lines.Add("");
            lines.Add("הערות:");
            lines.Add(" * חובה לכבות את האימות (vbmeta), אחרת המכשיר לא יעלה עם מחיצות ששונו.");
            lines.Add(" * אם ה-fstab שבתוך ה-boot / vendor_boot מכיר רק erofs, ייתכן שתצטרך גם לתקן אותו.");
            lines.Add("   (ב-vendor עצמו נוספו שורות ext4 אוטומטית.)");
            lines.Add(" * אחרי ההפעלה, עם root: mount -o rw,remount /  (או המחיצה הרצויה)");
            lines.Add("");
            lines.Add("מחיצות:");
            foreach (var p in c.Parts.Where(x => x.FinalBytes > 0))
                lines.Add(string.Format("  {0,-16} {1,-6} {2,10}  {3}", p.Name, p.Convert ? "ext4" : p.Fs, Converter.Human(p.FinalBytes), p.Convert ? "(פנוי ≈ " + Converter.Human(p.FinalBytes - p.MinBytes) + ")" : "(הועתק)"));
            File.WriteAllLines(Path.Combine(outDir, "HOW_TO_FLASH.txt"), lines, new System.Text.UTF8Encoding(true));
        }
    }
}
