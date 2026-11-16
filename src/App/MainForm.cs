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

        ComboBox cmbProject;
        Button btnNewProj, btnOpenDir, btnAddFiles, btnRefresh, btnRun, btnCancel, btnOpenEdited, btnDump, btnDriver;
        CheckBox cbSkipUserdata;
        Label lblStatus;
        ListView lvFiles;
        Label lblProj;
        // RO2RW
        RadioButton rbMax, rbFixed;
        NumericUpDown nS, nV, nP, nSE, nOT, nFixed;
        CheckBox cbSparse, cbFstab;
        Label lblTotal;
        // Magisk
        CheckBox cbKeepVerity, cbKeepEnc;
        ComboBox cmbAbi;
        Label lblMagisk;
        // LK
        CheckBox cbLkDelay, cbLkText;
        ProgressBar prog;
        RichTextBox log;

        Project project;
        List<ProjectFile> files = new List<ProjectFile>();
        Thread worker;
        volatile bool cancel;

        public MainForm()
        {
            Text = "RO2RW PC " + Program.Version + " - ערכת כלים לעריכת רום";
            Font = fBase;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1000, 800);
            MinimumSize = new Size(960, 760);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => { var f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f != null) AddFiles(f); };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Build();
            LoadProjects(null);
            Log("ברוך הבא! צור פרויקט (שם המכשיר), הוסף אליו את קבצי הרום, סמן מה לערוך ולחץ \"בצע עריכה\".", Color.LightGreen);
            Log("הקבצים הערוכים נשמרים בתיקייה \"ערוך\" שבתוך הפרויקט, עם אותם שמות. המקור לא משתנה.", Color.LightGreen);
            Log("אפשר גם לשאוב רום מלא מהמכשיר (MTK) ישירות לתוך הפרויקט, בכפתור \"שאב רום מהמכשיר\".", Color.LightGreen);
        }

        // ------------------------------------------------------------ UI
        void Build()
        {
            Controls.Add(new Label { Text = "RO2RW PC", Font = fTitle, AutoSize = true, Location = new Point(14, 8) });
            Controls.Add(new Label { Text = "RO2RW, Root עם Magisk, ביטול AVB והסרת Orange State - הכול על המחשב", AutoSize = true, Location = new Point(16, 42), ForeColor = Color.DimGray });

            // project bar
            var gp = new GroupBox { Text = "פרויקט (מכשיר)", Location = new Point(12, 68), Size = new Size(976, 104) };
            cmbProject = new ComboBox { Location = new Point(14, 26), Size = new Size(250, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbProject.SelectedIndexChanged += (s, e) => { if (cmbProject.SelectedItem != null) OpenProject((string)cmbProject.SelectedItem); };
            btnNewProj = new Button { Text = "פרויקט חדש...", Location = new Point(274, 24), Size = new Size(130, 32) };
            btnNewProj.Click += (s, e) => NewProject();
            btnOpenDir = new Button { Text = "פתח תיקייה", Location = new Point(412, 24), Size = new Size(110, 32) };
            btnOpenDir.Click += (s, e) => { if (project != null) Process.Start("explorer.exe", "\"" + project.Root + "\""); };
            btnOpenEdited = new Button { Text = "פתח \"ערוך\"", Location = new Point(530, 24), Size = new Size(110, 32) };
            btnOpenEdited.Click += (s, e) => { if (project != null) Process.Start("explorer.exe", "\"" + project.Edited + "\""); };
            lblProj = new Label { Location = new Point(650, 30), Size = new Size(316, 24), ForeColor = Color.DimGray, AutoEllipsis = true };
            btnDump = new Button { Text = "⬇ שאב רום מהמכשיר (mtkclient)", Location = new Point(14, 62), Size = new Size(250, 32), Font = fBold };
            btnDump.Click += (s, e) => DumpRom();
            cbSkipUserdata = new CheckBox { Text = "בלי userdata", Location = new Point(274, 67), Size = new Size(120, 24), Checked = true };
            btnDriver = new Button { Text = "דרייבר USB", Location = new Point(400, 62), Size = new Size(110, 32) };
            btnDriver.Click += (s, e) => InstallDriver();
            lblStatus = new Label { Location = new Point(520, 68), Size = new Size(446, 24), ForeColor = Color.DimGray, AutoEllipsis = true, RightToLeft = RightToLeft.No };
            gp.Controls.AddRange(new Control[] { cmbProject, btnNewProj, btnOpenDir, btnOpenEdited, lblProj, btnDump, cbSkipUserdata, btnDriver, lblStatus });
            Controls.Add(gp);

            // files
            var gf = new GroupBox { Text = "קבצי הרום בפרויקט (סמן מה לערוך)", Location = new Point(12, 178), Size = new Size(560, 316) };
            lvFiles = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Location = new Point(10, 24), Size = new Size(540, 242), RightToLeftLayout = true };
            lvFiles.Columns.Add("קובץ", 150); lvFiles.Columns.Add("סוג", 90); lvFiles.Columns.Add("גודל", 80); lvFiles.Columns.Add("עריכה", 150); lvFiles.Columns.Add("ערוך", 50);
            lvFiles.ItemCheck += (s, e) =>
            {
                var pf = lvFiles.Items[e.Index].Tag as ProjectFile;
                if (pf == null || pf.Action == EditAction.None) e.NewValue = CheckState.Unchecked;
                else pf.Selected = e.NewValue == CheckState.Checked;
            };
            btnAddFiles = new Button { Text = "הוסף קבצים...", Location = new Point(10, 274), Size = new Size(130, 32) };
            btnAddFiles.Click += (s, e) => PickFiles();
            btnRefresh = new Button { Text = "רענן", Location = new Point(148, 274), Size = new Size(80, 32) };
            btnRefresh.Click += (s, e) => RefreshFiles();
            var hint = new Label { Text = "אפשר גם לגרור קבצים לחלון", Location = new Point(236, 281), Size = new Size(300, 22), ForeColor = Color.DimGray };
            gf.Controls.AddRange(new Control[] { lvFiles, btnAddFiles, btnRefresh, hint });
            Controls.Add(gf);

            // settings tabs
            var tabs = new TabControl { Location = new Point(582, 178), Size = new Size(406, 316), RightToLeftLayout = true };
            tabs.TabPages.Add(BuildRo2rwTab());
            tabs.TabPages.Add(BuildMagiskTab());
            tabs.TabPages.Add(BuildLkTab());
            Controls.Add(tabs);

            // run
            btnRun = new Button { Text = "בצע עריכה", Location = new Point(12, 502), Size = new Size(200, 44), Font = new Font("Segoe UI", 12f, FontStyle.Bold), BackColor = Color.FromArgb(46, 125, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnRun.Click += (s, e) => RunEdits();
            btnCancel = new Button { Text = "ביטול", Location = new Point(222, 502), Size = new Size(100, 44), Enabled = false };
            btnCancel.Click += (s, e) => { cancel = true; Log("מבטל...", Color.Khaki); };
            prog = new ProgressBar { Location = new Point(334, 512), Size = new Size(654, 26), Maximum = 1000 };
            Controls.AddRange(new Control[] { btnRun, btnCancel, prog });

            log = new RichTextBox
            {
                Location = new Point(12, 554), Size = new Size(976, 234), ReadOnly = true, BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro,
                Font = new Font("Consolas", 10f), RightToLeft = RightToLeft.Yes, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(log);
        }

        TabPage BuildRo2rwTab()
        {
            var t = new TabPage("RO2RW (super)");
            rbMax = new RadioButton { Text = "אחוזים מהמקום הפנוי (MAX)", Location = new Point(10, 10), Size = new Size(370, 24), Checked = true };
            int y = 38;
            nS = Num(t, "System", 10, y, 70); nV = Num(t, "Vendor", 84, y, 10); nP = Num(t, "Product", 158, y, 10); nSE = Num(t, "Sys_ext", 232, y, 5); nOT = Num(t, "אחרות", 306, y, 5);
            lblTotal = new Label { Location = new Point(10, 90), Size = new Size(370, 22) };
            rbFixed = new RadioButton { Text = "גודל קבוע לכל מחיצה (FIXED), MB:", Location = new Point(10, 118), Size = new Size(260, 24) };
            nFixed = new NumericUpDown { Location = new Point(276, 117), Size = new Size(90, 26), Maximum = 100000, Value = 200 };
            cbSparse = new CheckBox { Text = "פלט sparse (קטן יותר)", Location = new Point(10, 154), Size = new Size(370, 24), Checked = true };
            cbFstab = new CheckBox { Text = "הוסף שורות ext4 ל-fstab של vendor", Location = new Point(10, 180), Size = new Size(370, 24), Checked = true };
            t.Controls.AddRange(new Control[] { rbMax, lblTotal, rbFixed, nFixed, cbSparse, cbFstab });
            EventHandler upd = (s, e) => UpdateSizeUi();
            foreach (var n in new[] { nS, nV, nP, nSE, nOT }) n.ValueChanged += upd;
            rbMax.CheckedChanged += upd;
            UpdateSizeUi();
            return t;
        }

        TabPage BuildMagiskTab()
        {
            var t = new TabPage("Magisk");
            t.Controls.Add(new Label { Text = "ABI של המכשיר:", Location = new Point(10, 14), Size = new Size(110, 24) });
            cmbAbi = new ComboBox { Location = new Point(124, 10), Size = new Size(150, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAbi.Items.AddRange(new object[] { "auto", "arm64-v8a", "armeabi-v7a", "x86_64", "x86" });
            cmbAbi.SelectedIndex = 0;
            cbKeepVerity = new CheckBox { Text = "השאר verity (KEEPVERITY)", Location = new Point(10, 48), Size = new Size(370, 24) };
            cbKeepEnc = new CheckBox { Text = "השאר הצפנה כפויה (KEEPFORCEENCRYPT)", Location = new Point(10, 74), Size = new Size(370, 24) };
            lblMagisk = new Label { Location = new Point(10, 104), Size = new Size(376, 140), ForeColor = Color.DimGray };
            var btnApk = new Button { Text = "בחר Magisk.apk אחר...", Location = new Point(10, 248), Size = new Size(180, 30) };
            btnApk.Click += (s, e) => PickMagiskApk();
            t.Controls.AddRange(new Control[] { cmbAbi, cbKeepVerity, cbKeepEnc, lblMagisk, btnApk });
            UpdateMagiskLabel();
            return t;
        }

        TabPage BuildLkTab()
        {
            var t = new TabPage("LK / vbmeta");
            cbLkDelay = new CheckBox { Text = "בטל את ההשהיה של 5 שניות (Orange State)", Location = new Point(10, 12), Size = new Size(380, 24), Checked = true };
            cbLkText = new CheckBox { Text = "מחק את טקסט האזהרה מהמסך", Location = new Point(10, 40), Size = new Size(380, 24), Checked = true };
            var l1 = new Label { Text = "גרסת האנדרואיד מזוהה אוטומטית (01/02), אין צורך לבחור.\nמבוסס על המדריך של hovatek ועל hex-replacer.", Location = new Point(10, 70), Size = new Size(380, 44), ForeColor = Color.DimGray };
            var l2 = new Label { Text = "vbmeta: מכבה verity ו-verification (דגלים = 3), לפי AVB-Disabler. מתאים לצריבה ב-SP Flash Tool או mtkclient.", Location = new Point(10, 130), Size = new Size(380, 64), ForeColor = Color.DimGray };
            t.Controls.AddRange(new Control[] { cbLkDelay, cbLkText, l1, l2 });
            return t;
        }

        NumericUpDown Num(Control parent, string label, int x, int y, int val)
        {
            parent.Controls.Add(new Label { Text = label + " %", Location = new Point(x, y), Size = new Size(72, 20) });
            var n = new NumericUpDown { Location = new Point(x, y + 22), Size = new Size(64, 26), Maximum = 100, Value = val };
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
        }

        void UpdateMagiskLabel()
        {
            string apk = null;
            try { if (Directory.Exists(Project.ToolsDir)) apk = Directory.GetFiles(Project.ToolsDir, "*.apk").FirstOrDefault(f => Path.GetFileName(f).ToLowerInvariant().Contains("magisk")); } catch { }
            bool builtin = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("Magisk.apk");
            lblMagisk.Text = (apk != null ? "Magisk: " + Path.GetFileName(apk) + " (נבחר ידנית)" : builtin ? "Magisk: מובנה בתוכנה" : "Magisk: יורד אוטומטית מ-GitHub בפעם הראשונה") +
                "\n\nה-boot או ה-init_boot נערכים על המחשב, כמו שאפליקציית Magisk עושה." +
                "\nאחרי הצריבה מתקינים את אפליקציית Magisk בטלפון." +
                "\n\nה-ABI מזוהה אוטומטית מקובץ init שב-ramdisk.";
        }

        void PickMagiskApk()
        {
            using (var d = new OpenFileDialog { Filter = "Magisk APK|*.apk", Title = "בחר Magisk.apk" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Directory.CreateDirectory(Project.ToolsDir);
                foreach (var old in Directory.GetFiles(Project.ToolsDir, "*.apk")) File.Delete(old);
                var name = Path.GetFileName(d.FileName);
                if (!name.ToLowerInvariant().Contains("magisk")) name = "Magisk-" + name;
                File.Copy(d.FileName, Path.Combine(Project.ToolsDir, name), true);
                UpdateMagiskLabel();
            }
        }

        // ------------------------------------------------------------ helpers
        void Log(string text, Color? c = null)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(text, c))); return; }
            log.SelectionStart = log.TextLength;
            log.SelectionColor = c ?? (text.Contains("✖") ? Color.Tomato : text.Contains("✔") ? Color.LightGreen : text.StartsWith("▶") ? Color.DeepSkyBlue : Color.Gainsboro);
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
            foreach (var b in new Control[] { btnRun, btnNewProj, btnAddFiles, btnRefresh, cmbProject, lvFiles, btnDump, btnDriver }) b.Enabled = !busy;
            btnCancel.Enabled = busy;
            UseWaitCursor = busy;
        }

        static void Msg(IWin32Window w, string text, MessageBoxIcon icon = MessageBoxIcon.Information)
        {
            MessageBox.Show(w, text, "RO2RW PC", MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }

        // ------------------------------------------------------------ projects
        void LoadProjects(string select)
        {
            cmbProject.Items.Clear();
            foreach (var p in Project.List()) cmbProject.Items.Add(p);
            if (select != null && cmbProject.Items.Contains(select)) cmbProject.SelectedItem = select;
            else if (cmbProject.Items.Count > 0) cmbProject.SelectedIndex = 0;
            else { project = null; lblProj.Text = "אין פרויקטים. לחץ \"פרויקט חדש\"."; lvFiles.Items.Clear(); }
        }

        void NewProject()
        {
            string name = Prompt("שם הפרויקט (שם המכשיר, למשל Cubot_J20):", "פרויקט חדש");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var p = Project.Open(name);
                Log("נוצר פרויקט: " + p.Root, Color.LightGreen);
                LoadProjects(p.Name);
            }
            catch (Exception ex) { Msg(this, ex.Message, MessageBoxIcon.Error); }
        }

        string Prompt(string text, string title)
        {
            using (var f = new Form { Text = title, Size = new Size(460, 170), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, RightToLeft = RightToLeft.Yes, RightToLeftLayout = true, Font = fBase })
            {
                var l = new Label { Text = text, Location = new Point(12, 12), Size = new Size(420, 24) };
                var tb = new TextBox { Location = new Point(12, 40), Size = new Size(420, 28), RightToLeft = RightToLeft.No };
                var ok = new Button { Text = "אישור", DialogResult = DialogResult.OK, Location = new Point(12, 80), Size = new Size(100, 32) };
                var cn = new Button { Text = "ביטול", DialogResult = DialogResult.Cancel, Location = new Point(120, 80), Size = new Size(100, 32) };
                f.Controls.AddRange(new Control[] { l, tb, ok, cn });
                f.AcceptButton = ok; f.CancelButton = cn;
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        void OpenProject(string name)
        {
            try
            {
                project = Project.Open(name);
                lblProj.Text = project.Root;
                RefreshFiles();
            }
            catch (Exception ex) { Log("שגיאה: " + ex.Message, Color.Tomato); }
        }

        void RefreshFiles()
        {
            lvFiles.Items.Clear();
            if (project == null) return;
            files = project.Scan();
            foreach (var f in files)
            {
                var it = new ListViewItem(new[] { f.Name, f.Kind, Converter.Human(f.Size), Project.ActionName(f.Action), f.Edited ? "✔" : "" }) { Tag = f };
                if (f.Action == EditAction.None) it.ForeColor = Color.Gray;
                lvFiles.Items.Add(it);
                it.Checked = f.Selected;
            }
            if (files.Count == 0) Log("הפרויקט ריק. הוסף קבצי רום (super, boot, init_boot, vbmeta, lk...) בכפתור \"הוסף קבצים\" או בגרירה.", Color.Khaki);
        }

        void PickFiles()
        {
            if (project == null) { Msg(this, "צור או בחר פרויקט קודם."); return; }
            using (var d = new OpenFileDialog { Multiselect = true, Title = "בחר קבצי רום להוספה לפרויקט", Filter = "קבצי רום|*.img;*.bin;*.img.*|כל הקבצים|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
        }

        void AddFiles(string[] paths)
        {
            if (project == null) { Msg(this, "צור או בחר פרויקט קודם."); return; }
            if (worker != null && worker.IsAlive) return;
            var list = paths.Where(File.Exists).ToArray();
            SetBusy(true);
            worker = new Thread(() =>
            {
                try
                {
                    foreach (var p in list)
                    {
                        string dest = Path.Combine(project.Root, Path.GetFileName(p));
                        if (Path.GetFullPath(dest).Equals(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)) continue;
                        Log("מעתיק " + Path.GetFileName(p) + "...");
                        File.Copy(p, dest, true);
                    }
                }
                catch (Exception ex) { Log("שגיאה בהעתקה: " + ex.Message, Color.Tomato); }
                finally { SetBusy(false); BeginInvoke(new Action(RefreshFiles)); }
            }) { IsBackground = true };
            worker.Start();
        }

        // ------------------------------------------------------------ dump
        void SetStatus(string t)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(t))); return; }
            lblStatus.Text = t;
        }

        void InstallDriver()
        {
            if (MtkClient.UsbDkInstalled)
            {
                Msg(this, "דרייבר UsbDk כבר מותקן במחשב.");
                return;
            }
            SetBusy(true);
            worker = new Thread(() =>
            {
                try { new MtkClient { Log = s => Log(s) }.InstallUsbDk(); }
                catch (Exception ex) { Log("שגיאה: " + ex.Message, Color.Tomato); }
                finally { SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        void DumpRom()
        {
            if (project == null) { Msg(this, "צור או בחר פרויקט קודם (שם המכשיר)."); return; }
            if (!MtkClient.Bundled && !File.Exists(MtkClient.Exe)) { Msg(this, "mtkclient לא מובנה בגרסה הזו. הורד את הגרסה האחרונה מדף המהדורות.", MessageBoxIcon.Warning); return; }
            if (!MtkClient.UsbDkInstalled)
            {
                var r = MessageBox.Show(this, "כדי ש-mtkclient יזהה את המכשיר צריך את דרייבר UsbDk, והוא לא מותקן.\n\nלהתקין עכשיו? (נדרשות הרשאות מנהל)", "RO2RW PC",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) { InstallDriver(); return; }
            }
            var ok = MessageBox.Show(this,
                "שאיבת רום מלא לפרויקט \"" + project.Name + "\":\n\n" +
                "1. כבה את המכשיר לגמרי (והוצא את הכבל).\n" +
                "2. לחץ אישור כאן.\n" +
                "3. החזק את שני לחצני העוצמה (או עוצמה+) וחבר את הכבל.\n" +
                "   (במכשירים מסוימים: עוצמה- בלבד)\n\n" +
                "השאיבה יכולה לקחת זמן רב. אל תנתק את הכבל עד הסוף." +
                (cbSkipUserdata.Checked ? "\n\nמחיצת userdata (המידע האישי) לא תישאב." : "\n\n! userdata תישאב - זה גדול מאוד ומכיל מידע אישי."),
                "שאיבת רום", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (ok != DialogResult.OK) return;
            cancel = false;
            SetBusy(true);
            var proj = project; bool skip = cbSkipUserdata.Checked;
            worker = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    Log("");
                    Log("▶ שאיבת רום עם mtkclient - ממתין למכשיר במצב BROM...");
                    var mc = new MtkClient { Log = s => Log(s), Status = SetStatus, Cancelled = () => cancel };
                    int n = mc.DumpProject(proj, skip);
                    Log(string.Format("✔ נשאבו {0} מחיצות לתוך הפרויקט ({1})", n, sw.Elapsed.ToString(@"hh\:mm\:ss")));
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); SetBusy(false); BeginInvoke(new Action(RefreshFiles)); }
            }) { IsBackground = true };
            worker.Start();
        }

        // ------------------------------------------------------------ run
        void RunEdits()
        {
            if (project == null) { Msg(this, "צור או בחר פרויקט קודם."); return; }
            var sel = files.Where(f => f.Selected && f.Action != EditAction.None).ToList();
            if (sel.Count == 0) { Msg(this, "לא סומנו קבצים לעריכה."); return; }
            int sum = (int)(nS.Value + nV.Value + nP.Value + nSE.Value + nOT.Value);
            if (rbMax.Checked && sum > 100 && sel.Any(f => f.Action == EditAction.Ro2rw)) { Msg(this, "סכום האחוזים ב-RO2RW גדול מ-100%.", MessageBoxIcon.Warning); return; }

            var st = new EditSettings();
            st.Ro2rw = new Settings
            {
                MaxMode = rbMax.Checked, PctS = (int)nS.Value, PctV = (int)nV.Value, PctP = (int)nP.Value, PctSE = (int)nSE.Value, PctOT = (int)nOT.Value,
                FixedMb = (int)nFixed.Value, SparseOutput = cbSparse.Checked, PatchVendorFstab = cbFstab.Checked
            };
            st.Magisk = new MagiskOptions { KeepVerity = cbKeepVerity.Checked, KeepForceEncrypt = cbKeepEnc.Checked, Abi = (string)cmbAbi.SelectedItem };
            st.LkDelay = cbLkDelay.Checked; st.LkText = cbLkText.Checked;

            cancel = false;
            SetBusy(true);
            SetProgress(0);
            var proj = project;
            worker = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var ed = new Editor { Log = s => Log(s), Progress = SetProgress, Cancelled = () => cancel };
                    int ok = ed.Run(proj, sel, st);
                    Log("");
                    Log(string.Format("הסתיים: {0} מתוך {1} קבצים נערכו בהצלחה ({2})", ok, sel.Count, sw.Elapsed.ToString(@"mm\:ss")), ok == sel.Count ? Color.LightGreen : Color.Khaki);
                    Log("הקבצים הערוכים נמצאים ב: " + proj.Edited, Color.LightGreen);
                    WriteFlashHelp(proj, sel);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("שגיאה: " + ex.Message, Color.Tomato); }
                finally
                {
                    SetBusy(false);
                    BeginInvoke(new Action(() => { RefreshFiles(); UpdateMagiskLabel(); }));
                }
            }) { IsBackground = true };
            worker.Start();
        }

        static void WriteFlashHelp(Project proj, List<ProjectFile> sel)
        {
            var lines = new List<string> { "RO2RW PC - קבצים ערוכים", "=========================", "", "!!! גבה את המכשיר לפני הצריבה. האחריות עליך. !!!", "", "צריבה ב-fastboot (או ב-SP Flash Tool / mtkclient לפי שם המחיצה):" };
            foreach (var f in sel)
            {
                string path = Path.Combine(proj.Edited, f.Name);
                if (!File.Exists(path)) continue;
                string part = Path.GetFileNameWithoutExtension(f.Name);
                lines.Add(string.Format("  fastboot flash {0} {1}", part, f.Name));
            }
            lines.Add("");
            lines.Add("הערות:");
            lines.Add(" * vbmeta ערוך מבטל את האימות, ואין צורך בדגלי --disable-verity.");
            lines.Add(" * אחרי צריבת boot/init_boot עם Magisk, התקן את אפליקציית Magisk בטלפון.");
            lines.Add(" * אחרי super עם RO2RW (ext4): mount -o rw,remount / עם root.");
            try { File.WriteAllLines(Path.Combine(proj.Edited, "HOW_TO_FLASH.txt"), lines, new System.Text.UTF8Encoding(true)); } catch { }
        }
    }
}
