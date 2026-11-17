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
        Button btnWizard, btnFlash, btnPlan, btnSp;
        ToolStripMenuItem miEngMtk, miEngFast;
        Form logWin; RichTextBox logWinBox;
        CheckBox cbRecovery, cbPatchVbFlag;
        ComboBox cmbLkAndroid, cmbVbMode;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };
        float zoom = 1f;
        MenuStrip menu;
        Button btnNewProj, btnOpenDir, btnAddFiles, btnRefresh, btnRun, btnCancel, btnOpenEdited, btnDump, btnDriver;
        CheckBox cbSkipUserdata;
        Label lblStatus;
        ListView lvFiles;
        Label lblProj;
        // RO2RW
        RadioButton rbMax, rbFixed;
        NumericUpDown nS, nV, nP, nSE, nOT, nFixed;
        CheckBox cbSparse, cbFstab, cbNoWifi;
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
            WrapWithMenu();
            SetupTooltips();
            ApplyTheme();
            Load += (s, e) => FitToScreen();
            LoadUiSettings();
            FormClosing += (s, e) => SaveUiSettings();
            LoadProjects(null);
            Log("ברוך הבא! הכי פשוט: לחץ \"אשף\" והוא יעבור איתך על הכול, שלב אחרי שלב.", Color.LightGreen);
            Log("או ידנית: צור פרויקט (שם המכשיר), הוסף קבצי רום, סמן מה לערוך ולחץ \"בצע עריכה\". כלים בודדים נמצאים בתפריט \"כלים\".", Color.LightGreen);
            Log("הקבצים הערוכים נשמרים בתיקייה \"ערוך\" שבתוך הפרויקט, עם אותם שמות. המקור לא משתנה.", Color.LightGreen);
            Log("אפשר גם לשאוב רום מלא מהמכשיר (MTK) ישירות לתוך הפרויקט, בכפתור \"שאב רום מהמכשיר\".", Color.LightGreen);
        }

        // ------------------------------------------------------------ UI
        void Build()
        {
            Controls.Add(new Label { Text = "RO2RW PC", Font = fTitle, AutoSize = true, Location = new Point(14, 8), ForeColor = Accent });
            Controls.Add(new Label { Text = "RO2RW, Root עם Magisk, ביטול AVB והסרת Orange State - הכול על המחשב", AutoSize = true, Location = new Point(16, 42), ForeColor = Color.FromArgb(110, 119, 129) });

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
            btnRefresh = new Button { Text = "רענן", Location = new Point(148, 274), Size = new Size(70, 32) };
            btnRefresh.Click += (s, e) => RefreshFiles();
            var btnSelAll = new Button { Text = "סמן הכל", Location = new Point(226, 274), Size = new Size(90, 32) };
            btnSelAll.Click += (s, e) => SelectAllFiles(true);
            var btnSelNone = new Button { Text = "נקה הכל", Location = new Point(324, 274), Size = new Size(90, 32) };
            btnSelNone.Click += (s, e) => SelectAllFiles(false);
            var hint = new Label { Text = "קליק ימני על קובץ = אפשרויות", Location = new Point(420, 281), Size = new Size(130, 22), ForeColor = Color.DimGray };
            gf.Controls.AddRange(new Control[] { lvFiles, btnAddFiles, btnRefresh, btnSelAll, btnSelNone, hint });
            Controls.Add(gf);

            // settings tabs
            var tabs = new TabControl { Location = new Point(582, 178), Size = new Size(406, 316), RightToLeftLayout = true };
            tabs.TabPages.Add(BuildRo2rwTab());
            tabs.TabPages.Add(BuildMagiskTab());
            tabs.TabPages.Add(BuildLkTab());
            Controls.Add(tabs);

            // run
            btnWizard = new Button { Text = "🧙 אשף", Location = new Point(12, 502), Size = new Size(130, 44), Font = new Font("Segoe UI", 12f, FontStyle.Bold), BackColor = Color.FromArgb(21, 101, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnWizard.Click += (s, e) => RunWizard();
            btnRun = new Button { Text = "בצע עריכה", Location = new Point(150, 502), Size = new Size(160, 44), Font = new Font("Segoe UI", 12f, FontStyle.Bold), BackColor = Color.FromArgb(46, 125, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnRun.Click += (s, e) => RunEdits();
            btnSp = new Button { Text = "📲 SP Flash Tool", Location = new Point(318, 502), Size = new Size(160, 44), Font = new Font("Segoe UI", 11f, FontStyle.Bold), BackColor = Color.FromArgb(21, 101, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnSp.Click += (s, e) => SpFlashMenu();
            btnFlash = new Button { Text = "⬆ צרוב (USB)", Location = new Point(484, 502), Size = new Size(120, 44), Font = new Font("Segoe UI", 10f, FontStyle.Bold), BackColor = Color.FromArgb(198, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnFlash.Click += (s, e) => FlashBack(null, true, null);
            btnCancel = new Button { Text = "ביטול", Location = new Point(610, 502), Size = new Size(80, 44), Enabled = false };
            btnCancel.Click += (s, e) => { cancel = true; Log("מבטל...", Color.Khaki); };
            prog = new ProgressBar { Location = new Point(698, 512), Size = new Size(290, 26), Maximum = 1000 };
            Controls.AddRange(new Control[] { btnWizard, btnRun, btnSp, btnFlash, btnCancel, prog });

            log = new RichTextBox
            {
                // double-click opens the resizable log window (handy on small screens)

                Location = new Point(12, 554), Size = new Size(976, 234), ReadOnly = true, BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro,
                Font = new Font("Consolas", 10f), RightToLeft = RightToLeft.Yes, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            log.DoubleClick += (s, e) => OpenLogWindow();
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
            cbNoWifi = new CheckBox { Text = "הסר WiFi (מכשיר מסונן - מוחק wpa_supplicant מ-vendor)", Location = new Point(10, 204), Size = new Size(370, 24) };
            btnPlan = new Button { Text = "תכנון מפורט לפי מחיצה...", Location = new Point(10, 232), Size = new Size(230, 32) };
            btnPlan.Click += (s, e) => OpenSuperPlan(null);
            t.Controls.AddRange(new Control[] { rbMax, lblTotal, rbFixed, nFixed, cbSparse, cbFstab, cbNoWifi, btnPlan });
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
            cbRecovery = new CheckBox { Text = "מצב recovery (RECOVERYMODE)", Location = new Point(10, 100), Size = new Size(370, 24) };
            cbPatchVbFlag = new CheckBox { Text = "כבה אימות ב-vbmeta של ה-boot (PATCHVBMETAFLAG)", Location = new Point(10, 126), Size = new Size(380, 24) };
            lblMagisk = new Label { Location = new Point(10, 156), Size = new Size(376, 88), ForeColor = Color.DimGray };
            var btnApk = new Button { Text = "בחר Magisk.apk אחר...", Location = new Point(10, 248), Size = new Size(180, 30) };
            btnApk.Click += (s, e) => PickMagiskApk();
            var btnVer = new Button { Text = "הורד גרסה (למשל 26)...", Location = new Point(196, 248), Size = new Size(190, 30) };
            btnVer.Click += (s, e) => DownloadMagiskVersion();
            t.Controls.AddRange(new Control[] { cmbAbi, cbKeepVerity, cbKeepEnc, cbRecovery, cbPatchVbFlag, lblMagisk, btnApk, btnVer });
            UpdateMagiskLabel();
            return t;
        }

        TabPage BuildLkTab()
        {
            var t = new TabPage("LK / vbmeta");
            cbLkDelay = new CheckBox { Text = "בטל את ההשהיה של 5 שניות (Orange State)", Location = new Point(10, 12), Size = new Size(380, 24), Checked = true };
            cbLkText = new CheckBox { Text = "מחק את טקסט האזהרה מהמסך", Location = new Point(10, 40), Size = new Size(380, 24), Checked = true };
            var l1 = new Label { Text = "גרסת אנדרואיד:", Location = new Point(10, 74), Size = new Size(120, 24) };
            cmbLkAndroid = new ComboBox { Location = new Point(134, 70), Size = new Size(200, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLkAndroid.Items.AddRange(new object[] { "זיהוי אוטומטי", "אנדרואיד 9 ומטה", "אנדרואיד 10 ומעלה" });
            cmbLkAndroid.SelectedIndex = 0;
            var sep = new Label { Location = new Point(10, 112), Size = new Size(380, 2), BorderStyle = BorderStyle.Fixed3D };
            var l2 = new Label { Text = "vbmeta - מה לכבות:", Location = new Point(10, 128), Size = new Size(140, 24) };
            cmbVbMode = new ComboBox { Location = new Point(154, 124), Size = new Size(230, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbVbMode.Items.AddRange(new object[] { "הכול (verity + verification)", "רק verity (hashtree)", "רק verification (חתימות)" });
            cmbVbMode.SelectedIndex = 0;
            var l3 = new Label { Text = "\"הכול\" שווה ערך ל-fastboot --disable-verity --disable-verification. מבוסס על AVB-Disabler, hex-replacer ו-hovatek.", Location = new Point(10, 162), Size = new Size(380, 60), ForeColor = Color.DimGray };
            t.Controls.AddRange(new Control[] { cbLkDelay, cbLkText, l1, cmbLkAndroid, sep, l2, cmbVbMode, l3 });
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
                "\nהעריכה על המחשב זהה לאפליקציית Magisk. אחרי הצריבה מתקינים את האפליקציה." +
                "\n\nה-ABI מזוהה אוטומטית מקובץ init שב-ramdisk.";
        }

        void DownloadMagiskVersion()
        {
            string v = Prompt("איזו גרסת Magisk להוריד? (למשל 26.4)", "הורדת Magisk");
            if (string.IsNullOrWhiteSpace(v)) return;
            SetBusy(true);
            worker = new Thread(() =>
            {
                try { MagiskPatcher.DownloadApkVersion(Project.ToolsDir, v, s => Log(s)); Log("✔ גרסת Magisk מוכנה. היא תשמש בעריכת boot/init_boot.", Color.LightGreen); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetBusy(false); BeginInvoke(new Action(UpdateMagiskLabel)); }
            }) { IsBackground = true };
            worker.Start();
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
            if (logWinBox != null && !logWinBox.IsDisposed)
            {
                logWinBox.SelectionStart = logWinBox.TextLength; logWinBox.SelectionColor = c ?? Color.Gainsboro;
                logWinBox.AppendText(text + "\n"); logWinBox.ScrollToCaret();
            }
        }

        void OpenLogWindow()
        {
            if (logWin != null && !logWin.IsDisposed) { logWin.WindowState = FormWindowState.Normal; logWin.Activate(); return; }
            logWin = new Form { Text = "יומן - RO2RW PC", Size = new Size(820, 560), StartPosition = FormStartPosition.CenterScreen, RightToLeft = RightToLeft.Yes, RightToLeftLayout = true, ShowInTaskbar = true };
            logWinBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 11f), RightToLeft = RightToLeft.No, WordWrap = true };
            try { logWinBox.Rtf = log.Rtf; } catch { logWinBox.Text = log.Text; }
            logWinBox.SelectionStart = logWinBox.TextLength; logWinBox.ScrollToCaret();
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 36 };
            var clr = new Button { Text = "נקה", Dock = DockStyle.Right, Width = 90 };
            clr.Click += (s2, e2) => { logWinBox.Clear(); };
            var sav = new Button { Text = "שמור...", Dock = DockStyle.Right, Width = 100 };
            sav.Click += (s2, e2) => SaveLog();
            bar.Controls.Add(clr); bar.Controls.Add(sav);
            logWin.Controls.Add(logWinBox); logWin.Controls.Add(bar);
            logWin.FormClosed += (s2, e2) => { logWinBox = null; logWin = null; };
            logWin.Show(this);
        }

        void SetProgress(int p)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(p))); return; }
            prog.Value = Math.Max(0, Math.Min(1000, p));
            if (p > 0 && p < 1000) lblStatus.Text = "מתבצע... " + (p / 10) + "%";
            else if (p >= 1000) lblStatus.Text = "";
        }

        void SetBusy(bool busy)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetBusy(busy))); return; }
            foreach (var b in new Control[] { btnRun, btnNewProj, btnAddFiles, btnRefresh, cmbProject, lvFiles, btnDump, btnDriver, btnWizard, btnFlash, btnSp }) b.Enabled = !busy;
            if (menu != null) foreach (ToolStripMenuItem m in menu.Items) if (m.Tag as string != "always") m.Enabled = !busy;
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
            if (select == null) select = lastProject;
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

        void SelectAllFiles(bool on)
        {
            foreach (ListViewItem it in lvFiles.Items)
            {
                var pf = it.Tag as ProjectFile;
                if (pf != null && pf.Action != EditAction.None) it.Checked = on;
            }
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
            cancel = false;
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
                        Project.CopyFile(p, dest, () => cancel, SetProgress);
                    }
                }
                catch (OperationCanceledException) { Log("ההעתקה הופסקה.", Color.Khaki); }
                catch (Exception ex) { Log("שגיאה בהעתקה: " + ex.Message, Color.Tomato); }
                finally { SetProgress(0); SetBusy(false); BeginInvoke(new Action(RefreshFiles)); }
            }) { IsBackground = true };
            worker.Start();
        }

        void SetEngine(MtkEngine e)
        {
            if (e == MtkEngine.Fast && !FastEngine.Available) { Msg(this, "המנוע המהיר לא מובנה בגרסה הזו. הורד את הגרסה האחרונה מדף המהדורות.", MessageBoxIcon.Warning); return; }
            FastEngine.Selected = e;
            if (miEngMtk != null) { miEngMtk.Checked = e == MtkEngine.Mtkclient; miEngFast.Checked = e == MtkEngine.Fast; }
            Log(e == MtkEngine.Fast ? "מנוע צריבה/שאיבה: מהיר (ניסיוני). " + FastEngine.Warning : "מנוע צריבה/שאיבה: mtkclient (בטוח).", e == MtkEngine.Fast ? Color.Khaki : Color.LightGreen);
        }

        // ------------------------------------------------------------ dump
        static string EngineName() { return FastEngine.Selected == MtkEngine.Fast ? "מנוע מהיר" : "mtkclient"; }

        int DumpEngine(Project proj, bool skip)
        {
            if (FastEngine.Selected == MtkEngine.Fast)
                return FastEngine.DumpProject(proj, skip, s => Log(s), SetStatus, () => cancel);
            return new MtkClient { Log = s => Log(s), Status = SetStatus, Cancelled = () => cancel }.DumpProject(proj, skip);
        }

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
                    Log("▶ שאיבת רום (" + EngineName() + ") - ממתין למכשיר במצב BROM...");
                    if (FastEngine.Selected == MtkEngine.Fast) Log("  " + FastEngine.Warning, Color.Khaki);
                    int n = DumpEngine(proj, skip);
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

            var st = UiSettings();

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

        EditSettings UiSettings()
        {
            var st = new EditSettings();
            st.Ro2rw = new Settings
            {
                MaxMode = rbMax.Checked, PctS = (int)nS.Value, PctV = (int)nV.Value, PctP = (int)nP.Value, PctSE = (int)nSE.Value, PctOT = (int)nOT.Value,
                FixedMb = (int)nFixed.Value, SparseOutput = cbSparse.Checked, PatchVendorFstab = cbFstab.Checked, RemoveWifi = cbNoWifi.Checked
            };
            st.Magisk = new MagiskOptions { KeepVerity = cbKeepVerity.Checked, KeepForceEncrypt = cbKeepEnc.Checked, Abi = (string)cmbAbi.SelectedItem, RecoveryMode = cbRecovery.Checked, PatchVbmetaFlag = cbPatchVbFlag.Checked };
            st.LkDelay = cbLkDelay.Checked; st.LkText = cbLkText.Checked;
            st.LkAndroid = cmbLkAndroid.SelectedIndex == 1 ? 9 : cmbLkAndroid.SelectedIndex == 2 ? 10 : 0;
            st.VbmetaFlags = cmbVbMode.SelectedIndex == 1 ? 1 : cmbVbMode.SelectedIndex == 2 ? 2 : 3;
            return st;
        }

        // ------------------------------------------------------------ menu / context menu
        void WrapWithMenu()
        {
            var body = new Panel { Dock = DockStyle.Fill };
            foreach (var c in Controls.Cast<Control>().ToList()) body.Controls.Add(c);
            menu = new MenuStrip { RightToLeft = RightToLeft.Yes, Font = fBase };
            var mFile = new ToolStripMenuItem("קובץ");
            mFile.DropDownItems.Add("פרויקט חדש...", null, (s, e) => NewProject());
            mFile.DropDownItems.Add("פתח את תיקיית הפרויקטים", null, (s, e) => { Directory.CreateDirectory(Project.BaseDir); Process.Start("explorer.exe", "\"" + Project.BaseDir + "\""); });
            mFile.DropDownItems.Add("בחר מיקום לתיקיית הפרויקטים...", null, (s, e) => PickProjectsDir());
            mFile.DropDownItems.Add("שמור יומן...", null, (s, e) => SaveLog());
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add("יציאה", null, (s, e) => Close());
            var mTools = new ToolStripMenuItem("כלים בודדים");
            mTools.DropDownItems.Add("Root עם Magisk לקובץ boot / init_boot...", null, (s, e) => SingleTool(EditAction.Magisk, "בחר boot.img או init_boot.img"));
            mTools.DropDownItems.Add("RO2RW לקובץ super...", null, (s, e) => SingleTool(EditAction.Ro2rw, "בחר super.img"));
            mTools.DropDownItems.Add("ביטול AVB בקובץ vbmeta...", null, (s, e) => SingleTool(EditAction.Vbmeta, "בחר vbmeta.img"));
            mTools.DropDownItems.Add("הסרת Orange State מקובץ lk...", null, (s, e) => SingleTool(EditAction.Lk, "בחר lk.img"));
            mTools.DropDownItems.Add(new ToolStripSeparator());
            mTools.DropDownItems.Add("יצירת scatter לתיקיית \"ערוך\" של הפרויקט", null, (s, e) => { if (project != null) EditedScatter.Create(project, x => Log(x)); });
            mTools.DropDownItems.Add("מידע על קובץ...", null, (s, e) => FileInfoTool());
            mTools.DropDownItems.Add("תכנון מפורט של super...", null, (s, e) => OpenSuperPlan(null));
            mTools.DropDownItems.Add("עורך HEX - חיפוש והחלפה...", null, (s, e) => { var f = SelectedFile(); using (var h = new HexForm(f != null ? f.Path : null)) h.ShowDialog(this); });
            var mDev = new ToolStripMenuItem("מכשיר");
            mDev.DropDownItems.Add("שאב רום מלא לפרויקט...", null, (s, e) => DumpRom());
            mDev.DropDownItems.Add("צרוב חזרה את תיקיית \"ערוך\"...", null, (s, e) => FlashBack(null, true, null));
            mDev.DropDownItems.Add("צרוב קבצים נבחרים...", null, (s, e) => FlashPickFiles());
            mDev.DropDownItems.Add("פתח ב-SP Flash Tool (צריבה ידנית)...", null, (s, e) => OpenInSpTool());
            mDev.DropDownItems.Add("SP Flash Tool - אוטומטי (ניסיוני)...", null, (s, e) => RunSpAuto());
            mDev.DropDownItems.Add("מדריך SP Flash Tool (צריבה + גיבוי)...", null, (s, e) => ShowSpGuide());
            mDev.DropDownItems.Add(new ToolStripSeparator());
            mDev.DropDownItems.Add("התקן דרייבר USB (UsbDk)", null, (s, e) => InstallDriver());
            mDev.DropDownItems.Add("הורד SP Flash Tool (אתר רשמי)", null, (s, e) => { try { Process.Start(new ProcessStartInfo("https://spflashtool.com/") { UseShellExecute = true }); } catch { } });
            mDev.DropDownItems.Add("התקן SP Flash Tool לתוך התוכנה...", null, (s, e) => InstallSpIntoTool());
            mDev.DropDownItems.Add(new ToolStripSeparator());
            var mEng = new ToolStripMenuItem("מנוע צריבה / שאיבה");
            miEngMtk = new ToolStripMenuItem("mtkclient (בטוח, ברירת מחדל)", null, (s, e) => SetEngine(MtkEngine.Mtkclient)) { Checked = true };
            miEngFast = new ToolStripMenuItem("מנוע מהיר של אליר (ניסיוני)", null, (s, e) => SetEngine(MtkEngine.Fast));
            mEng.DropDownItems.Add(miEngMtk);
            mEng.DropDownItems.Add(miEngFast);
            mDev.DropDownItems.Add(mEng);
            var mView = new ToolStripMenuItem("תצוגה") { Tag = "always" };
            mView.DropDownItems.Add("הגדל (Ctrl +)", null, (s, e) => SetZoom(zoom + 0.1f));
            mView.DropDownItems.Add("הקטן (Ctrl -)", null, (s, e) => SetZoom(zoom - 0.1f));
            mView.DropDownItems.Add("גודל רגיל (Ctrl 0)", null, (s, e) => SetZoom(1f));
            mView.DropDownItems.Add(new ToolStripSeparator());
            mView.DropDownItems.Add("יומן בחלון נפרד (ניתן להגדלה)", null, (s, e) => OpenLogWindow());
            var mHelp = new ToolStripMenuItem("עזרה") { Tag = "always" };
            mHelp.DropDownItems.Add("מדריך מפורט (F1)", null, (s, e) => { using (var h = new HelpForm()) h.ShowDialog(this); });
            mHelp.DropDownItems.Add("מדריך קצר", null, (s, e) => Ui.Msg(this,
                "1. אשף: עובר על הכול שלב אחרי שלב.\n\n" +
                "2. ידני: פרויקט ← הוספת קבצים או שאיבה ← סימון ← \"בצע עריכה\" ← \"צרוב חזרה\".\n\n" +
                "3. כלים בודדים: עריכת קובץ אחד בלי פרויקט. הפלט נשמר בתיקייה \"ערוך\" ליד הקובץ.\n\n" +
                "4. קליק ימני על קובץ ברשימה: עריכה של הקובץ הזה בלבד, שינוי סוג העריכה, פתיחת המיקום ועוד.\n\n" +
                "הקבצים המקוריים אף פעם לא משתנים."));
            mHelp.DropDownItems.Add("אודות", null, (s, e) => Ui.Msg(this,
                "RO2RW PC " + Program.Version + "\n\n" +
                "רכיבים: RO2RW (LeeGarChat - רעיון), Magisk (topjohnwu, GPLv3), mtkclient (bkerler, GPLv3), AVB-Disabler, hex-replacer, hovatek.\n\n" +
                "https://github.com/shmuel-sinay/ro2rw"));
            menu.Items.AddRange(new ToolStripItem[] { mFile, mTools, mDev, mView, mHelp });
            MainMenuStrip = menu;
            Controls.Add(body);
            Controls.Add(menu);
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + menu.PreferredSize.Height);

            // context menu on files
            var cm = new ContextMenuStrip { RightToLeft = RightToLeft.Yes, Font = fBase };
            var miOnly = new ToolStripMenuItem("ערוך רק את הקובץ הזה", null, (s, e) => EditOnlySelected());
            var miAct = new ToolStripMenuItem("סוג עריכה");
            foreach (EditAction a in new[] { EditAction.Magisk, EditAction.Ro2rw, EditAction.Vbmeta, EditAction.Lk, EditAction.None })
            {
                var act = a;
                miAct.DropDownItems.Add(Project.ActionName(a), null, (s, e) => SetActionForSelected(act));
            }
            var miFlash = new ToolStripMenuItem("צרוב את הגרסה הערוכה שלו", null, (s, e) => { var f = SelectedFile(); if (f != null) FlashBack(new[] { Path.Combine(project.Edited, f.Name) }, false, null); });
            var miPlan = new ToolStripMenuItem("תכנון מפורט (super)...", null, (s, e) => { var f = SelectedFile(); if (f != null) OpenSuperPlan(f.Path); });
            var miHex = new ToolStripMenuItem("פתח בעורך HEX...", null, (s, e) => { var f = SelectedFile(); if (f != null) using (var h = new HexForm(f.Path)) h.ShowDialog(this); });
            var miOpen = new ToolStripMenuItem("פתח מיקום", null, (s, e) => { var f = SelectedFile(); if (f != null) Process.Start("explorer.exe", "/select,\"" + f.Path + "\""); });
            var miInfo = new ToolStripMenuItem("מידע על הקובץ", null, (s, e) => { var f = SelectedFile(); if (f != null) ShowFileInfo(f.Path); });
            var miDel = new ToolStripMenuItem("הסר מהפרויקט (מחיקה)", null, (s, e) => DeleteSelected());
            cm.Items.AddRange(new ToolStripItem[] { miOnly, miAct, miPlan, miHex, miFlash, new ToolStripSeparator(), miOpen, miInfo, new ToolStripSeparator(), miDel });
            cm.Opening += (s, e) =>
            {
                var f = SelectedFile();
                if (f == null) { e.Cancel = true; return; }
                miOnly.Enabled = f.Action != EditAction.None;
                miFlash.Enabled = File.Exists(Path.Combine(project.Edited, f.Name));
                miPlan.Visible = f.Action == EditAction.Ro2rw;
            };
            lvFiles.ContextMenuStrip = cm;
        }

        ProjectFile SelectedFile()
        {
            if (lvFiles.SelectedItems.Count == 0) return null;
            return lvFiles.SelectedItems[0].Tag as ProjectFile;
        }

        void SetActionForSelected(EditAction a)
        {
            var f = SelectedFile(); if (f == null) return;
            f.Action = a; f.Selected = a != EditAction.None;
            var it = lvFiles.SelectedItems[0];
            it.SubItems[3].Text = Project.ActionName(a);
            it.ForeColor = a == EditAction.None ? Color.Gray : Color.Black;
            it.Checked = f.Selected;
        }

        void EditOnlySelected()
        {
            var f = SelectedFile(); if (f == null) return;
            foreach (var x in files) x.Selected = x == f;
            foreach (ListViewItem it in lvFiles.Items) it.Checked = it.Tag == f;
            RunEdits();
        }

        void DeleteSelected()
        {
            var f = SelectedFile(); if (f == null) return;
            if (!Ui.Ask(this, "למחוק את " + f.Name + " מהפרויקט?\n(הגרסה הערוכה שבתיקייה \"ערוך\" לא תימחק)", MessageBoxIcon.Warning)) return;
            try { File.Delete(f.Path); } catch (Exception ex) { Ui.Msg(this, ex.Message, MessageBoxIcon.Error); }
            RefreshFiles();
        }

        void SaveLog()
        {
            using (var d = new SaveFileDialog { Filter = "טקסט|*.txt", FileName = "RO2RW-log.txt" })
                if (d.ShowDialog(this) == DialogResult.OK) File.WriteAllText(d.FileName, log.Text, new System.Text.UTF8Encoding(true));
        }

        void FileInfoTool()
        {
            using (var d = new OpenFileDialog { Title = "בחר קובץ", Filter = "קבצי רום|*.img;*.bin;*.img.*|כל הקבצים|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) ShowFileInfo(d.FileName);
        }

        void ShowFileInfo(string path)
        {
            var pf = new ProjectFile { Path = path, Size = new FileInfo(path).Length };
            Project.Classify(pf);
            var l = new List<string> { pf.Name, "גודל: " + Converter.Human(pf.Size), "סוג: " + (pf.Kind.Length > 0 ? pf.Kind : "לא מזוהה"), "עריכה מתאימה: " + Project.ActionName(pf.Action) };
            try
            {
                if (pf.Action == EditAction.Magisk)
                {
                    var bi = BootImage.Parse(File.ReadAllBytes(path));
                    l.Add(bi.Info);
                    var fmt = Compress.Detect(bi.Ramdisk);
                    if (fmt != RamdiskFormat.Unknown && bi.Ramdisk.Length > 0)
                    {
                        var c = Cpio.Load(Compress.Decompress(bi.Ramdisk, fmt));
                        string why; l.Add("ABI: " + MagiskPatcher.DetectAbi(c, bi, out why) + " (" + why + ")");
                        l.Add(c.Exists(".backup/.magisk") ? "כבר עבר Magisk" : "מקורי (לא עבר Magisk)");
                    }
                }
                else if (pf.Action == EditAction.Vbmeta)
                {
                    var b = File.ReadAllBytes(path);
                    l.Add((b[123] & 3) == 3 ? "AVB כבוי" : "AVB פעיל");
                }
                else if (pf.Action == EditAction.Ro2rw)
                {
                    using (var c = new Converter())
                    {
                        c.Open(path);
                        foreach (var p in c.Parts.Where(x => x.Size > 0)) l.Add("  " + p.Name + ": " + p.Fs + ", " + Converter.Human(p.Size));
                    }
                }
            }
            catch (Exception ex) { l.Add("שגיאה בקריאה: " + ex.Message); }
            Ui.Msg(this, string.Join("\n", l));
        }

        // ------------------------------------------------------------ single tools (no project)
        void SingleTool(EditAction action, string title)
        {
            string path;
            using (var d = new OpenFileDialog { Title = title, Filter = "קבצי רום|*.img;*.bin;*.img.*|כל הקבצים|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                path = d.FileName;
            }
            var dir = Path.GetDirectoryName(path);
            var adhoc = new Project { Name = Path.GetFileName(dir), Root = dir };
            var pf = new ProjectFile { Path = path, Size = new FileInfo(path).Length };
            Project.Classify(pf);
            if (pf.Action != action && !Ui.Ask(this, "הקובץ לא זוהה כמתאים לעריכה הזו (זוהה: " + (pf.Kind.Length > 0 ? pf.Kind : "לא ידוע") + ").\nלהמשיך בכל זאת?")) return;
            pf.Action = action; pf.Selected = true;
            var st = UiSettings();
            cancel = false; SetBusy(true); SetProgress(0);
            worker = new Thread(() =>
            {
                try
                {
                    var ed = new Editor { Log = s => Log(s), Progress = SetProgress, Cancelled = () => cancel };
                    int ok = ed.Run(adhoc, new List<ProjectFile> { pf }, st);
                    if (ok == 1) Log("✔ הקובץ הערוך נמצא ב: " + Path.Combine(adhoc.Edited, pf.Name));
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        // ------------------------------------------------------------ flash back
        bool AskBrom(string what)
        {
            bool ok = false;
            Invoke(new Action(() =>
            {
                if (!MtkClient.Bundled && !File.Exists(MtkClient.Exe)) { Ui.Msg(this, "mtkclient לא מובנה בגרסה הזו.", MessageBoxIcon.Warning); return; }
                if (!MtkClient.UsbDkInstalled && !Ui.Ask(this, "דרייבר UsbDk לא מותקן, ובלעדיו mtkclient לא יזהה את המכשיר.\nלהמשיך בכל זאת? (אפשר להתקין מתפריט \"מכשיר\")")) return;
                ok = MessageBox.Show(this, what + "\n\n" + Ui.BromHelp, "RO2RW PC", MessageBoxButtons.OKCancel, MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == DialogResult.OK;
            }));
            return ok;
        }

        void FlashPickFiles()
        {
            using (var d = new OpenFileDialog { Multiselect = true, Title = "בחר קבצים לצריבה", Filter = "קבצי מחיצות|*.img;*.bin|כל הקבצים|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) FlashBack(d.FileNames, false, null);
        }

        /// <summary>files == null → whole "ערוך" folder of the project.</summary>
        void FlashBack(string[] fileList, bool wholeEdited, Action after)
        {
            List<FlashItem> items;
            string workDir;
            string gpt = project != null ? Path.Combine(project.Root, "gpt.bin") : null;
            if (fileList == null)
            {
                if (project == null) { Msg(this, "בחר פרויקט קודם."); return; }
                if (!Directory.Exists(project.Edited) || Directory.GetFiles(project.Edited).Length == 0) { Msg(this, "התיקייה \"ערוך\" ריקה. בצע עריכה קודם."); return; }
                items = Flasher.Plan(project.Edited, gpt);
                workDir = project.Root;
            }
            else
            {
                var dir = Path.GetDirectoryName(fileList[0]);
                items = Flasher.Plan(dir, gpt).Where(i => fileList.Any(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(i.File), StringComparison.OrdinalIgnoreCase))).ToList();
                workDir = project != null ? project.Root : dir;
            }
            if (items.Count == 0) { Msg(this, "לא נמצאו קבצי מחיצות לצריבה."); return; }
            bool reboot;
            using (var f = new FlashForm(items, "קבצים לצריבה" + (fileList == null ? " מהתיקייה \"ערוך\"" : "") + (File.Exists(gpt ?? "") ? " (נבדקו מול gpt.bin)" : " (אין gpt.bin - שמות המחיצות לפי שמות הקבצים)")))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                reboot = f.Reboot.Checked;
            }
            string gptForFlash = gpt;
            cancel = false; SetBusy(true);
            worker = new Thread(() =>
            {
                try
                {
                    if (!AskBrom("צריבה חזרה למכשיר:")) { Log("הצריבה בוטלה."); return; }
                    Log("");
                    Log("▶ צריבה חזרה למכשיר - ממתין למכשיר במצב BROM...");
                    if (FastEngine.Selected == MtkEngine.Fast) Log("  " + FastEngine.Warning, Color.Khaki);
                    var fl = new Flasher { Log = s => Log(s), Status = SetStatus, Cancelled = () => cancel, GptPath = gptForFlash };
                    fl.Flash(items, workDir, reboot);
                    if (after != null) after();
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        // ------------------------------------------------------------ wizard
        public void OpenWizard() { RunWizard(); }

        void RunWizard()
        {
            WizardResult w;
            using (var f = new WizardForm(Project.List(), project != null ? project.Name : null))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                w = f.Result;
            }
            Project proj;
            try { proj = Project.Open(w.ProjectName); } catch (Exception ex) { Msg(this, ex.Message, MessageBoxIcon.Error); return; }
            LoadProjects(proj.Name);
            cancel = false; SetBusy(true); SetProgress(0);
            bool handedOff = false;
            worker = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    Log("");
                    Log("▶ אשף: פרויקט " + proj.Name, Color.DeepSkyBlue);
                    // 1. source
                    if (w.Source == 0)
                    {
                        if (!AskBrom("שאיבת רום מלא לפרויקט \"" + proj.Name + "\":")) { Log("בוטל."); return; }
                        Log("▶ שלב 1: שאיבה מהמכשיר (" + EngineName() + ")...");
                        int n = DumpEngine(proj, w.SkipUserdata);
                        Log("✔ נשאבו " + n + " מחיצות");
                    }
                    else if (w.Source == 1)
                    {
                        Log("▶ שלב 1: מעתיק קבצים לפרויקט...");
                        foreach (var p in w.AddFiles)
                        {
                            if (cancel) throw new OperationCanceledException();
                            var dest = Path.Combine(proj.Root, Path.GetFileName(p));
                            if (!string.Equals(Path.GetFullPath(dest), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)) { Log("  " + Path.GetFileName(p)); Project.CopyFile(p, dest, () => cancel, SetProgress); }
                        }
                    }
                    // 2. edits
                    var all = proj.Scan();
                    var want = new HashSet<EditAction>();
                    if (w.DoRoot) want.Add(EditAction.Magisk);
                    if (w.DoRo2rw) want.Add(EditAction.Ro2rw);
                    if (w.DoAvb) want.Add(EditAction.Vbmeta);
                    if (w.DoLk) want.Add(EditAction.Lk);
                    foreach (var f in all) f.Selected = f.Selected && want.Contains(f.Action);
                    var sel = all.Where(f => f.Selected).ToList();
                    foreach (var a in want) if (!sel.Any(f => f.Action == a)) Log("! לא נמצא בפרויקט קובץ עבור: " + Project.ActionName(a), Color.Khaki);
                    if (sel.Count > 0)
                    {
                        Log("▶ שלב 2: עריכה (" + sel.Count + " קבצים)...");
                        var ed = new Editor { Log = s => Log(s), Progress = SetProgress, Cancelled = () => cancel };
                        int ok = ed.Run(proj, sel, w.Settings);
                        WriteFlashHelp(proj, sel);
                        Log(string.Format("✔ נערכו {0} מתוך {1}", ok, sel.Count));
                    }
                    BeginInvoke(new Action(RefreshFiles));
                    Log("סיום האשף (" + sw.Elapsed.ToString(@"hh\:mm\:ss") + "). הקבצים: " + proj.Edited, Color.LightGreen);
                    // 3. flash
                    if (w.FlashAfter)
                    {
                        handedOff = true;
                        BeginInvoke(new Action(() => { SetBusy(false); FlashBack(null, true, null); }));
                        return;
                    }
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); if (!handedOff) SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        // ------------------------------------------------------------ super plan / projects dir / zoom / shortcuts / tooltips
        void OpenSuperPlan(string path)
        {
            if (path == null)
            {
                var sup = files.FirstOrDefault(f => f.Action == EditAction.Ro2rw);
                if (sup != null) path = sup.Path;
                else
                    using (var d = new OpenFileDialog { Title = "בחר super.img", Filter = "super|super*;*.img|כל הקבצים|*.*" })
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) return;
                        path = d.FileName;
                    }
            }
            using (var f = new SuperPlanForm(path, UiSettings().Ro2rw))
                if (f.ShowDialog(this) == DialogResult.OK)
                    Log("✔ התכנון המפורט נשמר עבור " + Path.GetFileName(path) + ". הוא ישמש בעריכה הבאה (גובר על MAX/FIXED). למחיקה: מחק את הקובץ " + Path.GetFileName(SuperPlan.PathFor(path)));
        }

        void PickProjectsDir()
        {
            using (var d = new FolderBrowserDialog { Description = "בחר תיקייה לשמירת הפרויקטים (לביטול: לחץ ביטול ואשר חזרה לברירת המחדל)", SelectedPath = Project.BaseDir })
            {
                if (d.ShowDialog(this) == DialogResult.OK) Project.BaseDirOverride = d.SelectedPath;
                else if (Project.BaseDirOverride != null && Ui.Ask(this, "לחזור לתיקיית ברירת המחדל (Projects ליד התוכנה)?")) Project.BaseDirOverride = null;
                else return;
            }
            Log("תיקיית הפרויקטים: " + Project.BaseDir);
            LoadProjects(null);
        }

        void SetZoom(float z)
        {
            z = Math.Max(0.8f, Math.Min(1.6f, z));
            float f = z / zoom;
            if (Math.Abs(f - 1f) < 0.01f) return;
            SuspendLayout();
            Scale(new SizeF(f, f));
            ScaleFonts(this, f);
            ResumeLayout();
            zoom = z;
        }

        static void ScaleFonts(Control c, float f)
        {
            c.Font = new Font(c.Font.FontFamily, c.Font.Size * f, c.Font.Style);
            foreach (Control ch in c.Controls) ScaleFonts(ch, f);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: if (btnCancel.Enabled) { cancel = true; Log("מבטל...", Color.Khaki); return true; } break;
                case Keys.F1: using (var h = new HelpForm()) h.ShowDialog(this); return true;
                case Keys.F5: if (btnRefresh.Enabled) RefreshFiles(); return true;
                case Keys.Control | Keys.W: if (btnWizard.Enabled) RunWizard(); return true;
                case Keys.Control | Keys.N: if (btnNewProj.Enabled) NewProject(); return true;
                case Keys.Control | Keys.O: if (btnAddFiles.Enabled) PickFiles(); return true;
                case Keys.Control | Keys.E: if (btnRun.Enabled) RunEdits(); return true;
                case Keys.Control | Keys.F: if (btnFlash.Enabled) FlashBack(null, true, null); return true;
                case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Add: SetZoom(zoom + 0.1f); return true;
                case Keys.Control | Keys.OemMinus: case Keys.Control | Keys.Subtract: SetZoom(zoom - 0.1f); return true;
                case Keys.Control | Keys.D0: case Keys.Control | Keys.NumPad0: SetZoom(1f); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void SetupTooltips()
        {
            Action<Control, string> t = (c, text) => { tips.SetToolTip(c, text); c.AccessibleDescription = text; };
            t(btnWizard, "אשף שלב אחרי שלב: פרויקט, מקור, עריכות, גדלים וצריבה (Ctrl+W)");
            t(btnRun, "מבצע את העריכות המסומנות. הפלט נשמר בתיקייה \"ערוך\" (Ctrl+E)");
            t(btnSp, "הדרך המומלצת: מכין את התיקייה \"ערוך\" + scatter ופותח את SP Flash Tool לצריבה ידנית. עובד גם כשדרייבר UsbDk לא נתמך במכשיר");
            t(btnFlash, "צריבה ישירה דרך USB (mtkclient/מנוע מהיר) - דורש דרייבר UsbDk. אם המכשיר לא מזוהה, השתמש ב-SP Flash Tool");
            t(btnCancel, "עוצר מיד את הפעולה הנוכחית. קבצים חלקיים נמחקים (Esc)");
            t(btnDump, "שואב את כל המחיצות מהמכשיר לתוך הפרויקט (MediaTek, מצב BROM)");
            t(btnDriver, "מתקין את דרייבר UsbDk ש-mtkclient צריך (פעם אחת, הרשאות מנהל)");
            t(cbSkipUserdata, "לא לשאוב את userdata: מחיצה ענקית עם המידע האישי");
            t(btnNewProj, "פרויקט חדש = תיקייה חדשה בשם המכשיר (Ctrl+N)");
            t(btnAddFiles, "מעתיק קבצי רום לפרויקט. המקור לא משתנה (Ctrl+O)");
            t(btnRefresh, "סורק מחדש את קבצי הפרויקט (F5)");
            t(lvFiles, "סמן קבצים לעריכה. קליק ימני: עריכה של קובץ אחד, שינוי סוג העריכה, צריבה, מידע");
            t(rbMax, "כל המקום הפנוי ב-super יחולק לפי האחוזים");
            t(rbFixed, "אותו מספר MB לכל מחיצה מומרת. לגדלים שונים לכל מחיצה: תכנון מפורט");
            t(nFixed, "כמה MB פנויים להוסיף לכל מחיצה");
            t(cbSparse, "קובץ קטן יותר. מתאים ל-fastboot ול-SP Flash Tool");
            t(cbFstab, "מוסיף שורות ext4 ל-fstab של vendor, כדי שהמערכת תעגן ext4");
            t(cbNoWifi, "למכשיר מסונן: מוחק את wpa_supplicant מ-vendor כך שאי אפשר להתחבר ל-WiFi (לא נוגע בקובצי XML). גיבוי נשמר. חובה שגם vendor יומר");
            t(btnPlan, "לכל מחיצה: האם להמיר וכמה MB להוסיף, עם מד מקום פנוי");
            t(cmbAbi, "auto מזהה לפי init שב-ramdisk. arm64-v8a = מערכת 64 ביט, armeabi-v7a = מערכת 32 ביט");
            t(cbKeepVerity, "משאיר את dm-verity ב-fstab. בדרך כלל לא מסמנים");
            t(cbKeepEnc, "משאיר הצפנה כפויה. בדרך כלל לא מסמנים");
            t(cbRecovery, "רק למכשירים שבהם Magisk מותקן במחיצת recovery");
            t(cbPatchVbFlag, "מכבה את האימות בתוך ה-vbmeta הפנימי של ה-boot. שימושי אם לא צורבים vbmeta נפרד");
            t(cbLkDelay, "מבטל את 5 השניות שהמכשיר מחכה אחרי פתיחת בוטלואדר");
            t(cbLkText, "מוחק את הודעות Orange State מהמסך");
            t(cmbLkAndroid, "בדרך כלל אוטומטי. אם לא נמצא קוד, נסה לבחור ידנית");
            t(cmbVbMode, "\"הכול\" מומלץ. האפשרויות האחרות מכבות רק חלק מהאימות");
            foreach (var pair in new[] { new { c = (Control)btnWizard, n = "אשף" }, new { c = (Control)btnFlash, n = "צרוב חזרה" }, new { c = (Control)btnDump, n = "שאב רום מהמכשיר" } })
                pair.c.AccessibleName = pair.n;
        }

        void FitToScreen()
        {
            try
            {
                var wa = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(760, wa.Height));
                if (Height > wa.Height - 8 || Width > wa.Width - 8)
                {
                    // small screen (e.g. 14\" with scaling): use all available space so the log is readable
                    WindowState = FormWindowState.Maximized;
                }
            }
            catch { }
        }

        // ------------------------------------------------------------ theme (light & clean)
        static readonly Color Accent = Color.FromArgb(21, 101, 192);      // blue
        static readonly Color Bg = Color.FromArgb(238, 241, 245);         // light gray-blue
        static readonly Color CardText = Color.FromArgb(31, 41, 51);
        static readonly Color[] PrimaryBg =
        {
            Color.FromArgb(46, 125, 50), Color.FromArgb(21, 101, 192), Color.FromArgb(198, 40, 40)
        };

        static void SetDoubleBuffered(Control c)
        {
            try { typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(c, true, null); } catch { }
        }

        void ApplyTheme()
        {
            BackColor = Bg;
            SetDoubleBuffered(this);
            if (menu != null) { menu.BackColor = Color.White; menu.ForeColor = CardText; menu.Padding = new Padding(4, 2, 0, 2); }
            StyleTree(this);
        }

        static void StyleTree(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Panel) { c.BackColor = Bg; SetDoubleBuffered(c); }
                else if (c is GroupBox) { c.ForeColor = Accent; c.BackColor = Bg; }
                else if (c is Button) StyleButton((Button)c);
                else if (c is TabControl) c.BackColor = Bg;
                else if (c is Label && c.ForeColor == SystemColors.ControlText) c.ForeColor = CardText;
                else if (c is ListView) { c.BackColor = Color.White; ((ListView)c).BorderStyle = BorderStyle.FixedSingle; }
                else if (c is TextBox || c is ComboBox || c is NumericUpDown) c.BackColor = Color.White;
                if (c.Controls.Count > 0) StyleTree(c);
            }
        }

        static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            bool primary = Array.IndexOf(PrimaryBg, b.BackColor) >= 0;
            if (primary)
            {
                b.FlatAppearance.BorderColor = b.BackColor;
                b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(b.BackColor, 0.15f);
                b.ForeColor = Color.White;
            }
            else
            {
                b.BackColor = Color.White;
                b.ForeColor = Accent;
                b.FlatAppearance.BorderColor = Color.FromArgb(205, 212, 220);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 240, 251);
            }
            b.Cursor = Cursors.Hand;
        }

                // ------------------------------------------------------------ SP Flash Tool
        string SpToolDir { get { return Path.Combine(Project.ToolsDir, "SPFlashTool"); } }

        string ResolveSpTool()
        {
            if (!string.IsNullOrEmpty(sptoolPath) && File.Exists(sptoolPath)) return sptoolPath;
            if (Directory.Exists(SpToolDir))
            {
                var f = Directory.GetFiles(SpToolDir, "flash_tool*.exe").FirstOrDefault()
                     ?? Directory.GetFiles(SpToolDir, "*.exe").FirstOrDefault(x => Path.GetFileName(x).ToLowerInvariant().Contains("flash"));
                if (f != null) { sptoolPath = f; return f; }
            }
            return null;
        }

        void InstallSpIntoTool()
        {
            string exe;
            using (var d = new OpenFileDialog { Title = "בחר את flash_tool.exe שבתיקיית SP Flash Tool שלך", Filter = "SP Flash Tool|flash_tool*.exe;FlashTool*.exe|כל הקבצים|*.exe" })
            { if (d.ShowDialog(this) != DialogResult.OK) return; exe = d.FileName; }
            string srcDir = Path.GetDirectoryName(exe);
            if (!Ui.Ask(this, "להעתיק את כל תיקיית SP Flash Tool לתוך התוכנה?\n\nמ: " + srcDir + "\nאל: " + SpToolDir + "\n\nכך SP ילווה את התוכנה ותמיד יעבוד מתוכה.")) return;
            cancel = false; SetBusy(true);
            worker = new Thread(() =>
            {
                try
                {
                    Log("מעתיק את SP Flash Tool לתוך התוכנה...");
                    CopyDir(srcDir, SpToolDir, () => cancel, f => SetStatus("מעתיק " + f));
                    sptoolPath = Path.Combine(SpToolDir, Path.GetFileName(exe));
                    Log("✔ SP Flash Tool הועתק לתוכנה: " + SpToolDir, Color.LightGreen);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        static void CopyDir(string src, string dst, Func<bool> cancelled, Action<string> status)
        {
            Directory.CreateDirectory(dst);
            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(src, dst));
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                if (status != null) status(Path.GetFileName(f));
                File.Copy(f, f.Replace(src, dst), true);
            }
        }

        void SpFlashMenu()
        {
            var r = MessageBox.Show(this,
                "איך לצרוב עם SP Flash Tool?\n\n" +
                "כן = אוטומטי: RO2RW יפעיל את SP Flash Tool וירכיב את ההורדה לבד (ניסיוני).\n" +
                "לא = ידני: נפתח את SP Flash Tool ותלחץ Download בעצמך.\n" +
                "ביטול = חזרה.",
                "SP Flash Tool", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (r == DialogResult.Yes) RunSpAuto();
            else if (r == DialogResult.No) OpenInSpTool();
        }

        void RunSpAuto()
        {
            if (project == null) { Msg(this, "צור או בחר פרויקט קודם."); return; }
            // 1. scatter
            string scatter = Directory.Exists(project.Edited) ? Directory.GetFiles(project.Edited, "*scatter*.txt").OrderBy(x => x).FirstOrDefault() : null;
            if (scatter == null) { try { EditedScatter.Create(project, x => Log(x)); } catch { } scatter = Directory.Exists(project.Edited) ? Directory.GetFiles(project.Edited, "*scatter*.txt").OrderBy(x => x).FirstOrDefault() : null; }
            if (scatter == null) { Msg(this, "לא נמצא scatter בתיקייה \"ערוך\". בצע עריכה, או הוסף את ה-scatter של הקושחה לפרויקט.", MessageBoxIcon.Warning); return; }
            // 2. flash_tool.exe (from tools/SPFlashTool, remembered path, or pick)
            if (ResolveSpTool() == null)
            {
                using (var d = new OpenFileDialog { Title = "בחר את flash_tool.exe (או: מכשיר ← התקן SP לתוך התוכנה)", Filter = "SP Flash Tool|flash_tool*.exe;FlashTool*.exe|כל הקבצים|*.exe" })
                { if (d.ShowDialog(this) != DialogResult.OK) return; sptoolPath = d.FileName; }
            }
            // 3. roms
            List<SpFlashTool.Rom> roms;
            try { roms = SpFlashTool.EnabledRoms(scatter, project.Edited); } catch (Exception ex) { Msg(this, ex.Message, MessageBoxIcon.Error); return; }
            if (roms.Count == 0) { Msg(this, "אין מחיצות מסומנות לצריבה ב-scatter.", MessageBoxIcon.Warning); return; }
            // 4. DA
            string da = SpFlashTool.FindDa(sptoolPath);
            if (da == null)
            {
                if (!Ui.Ask(this, "לא נמצא קובץ DA (MTK_AllInOne_DA.bin) ליד flash_tool.exe.\nלבחור אותו? (בדרך כלל בתיקיית SP Flash Tool)")) return;
                using (var d = new OpenFileDialog { Title = "בחר MTK_AllInOne_DA.bin", Filter = "DA|*.bin|כל הקבצים|*.*" })
                { if (d.ShowDialog(this) != DialogResult.OK) return; da = d.FileName; }
            }
            // 5. platform/storage from scatter
            string stext = File.ReadAllText(scatter);
            var pm = System.Text.RegularExpressions.Regex.Match(stext, @"platform:\s*(\S+)");
            string platform = pm.Success ? pm.Groups[1].Value : null;
            bool ufs = System.Text.RegularExpressions.Regex.IsMatch(stext, @"storage:\s*UFS", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // 6. build config
            string cfg;
            try { cfg = SpFlashTool.BuildConfig(project.Edited, scatter, da, platform, ufs, roms, null); }
            catch (Exception ex) { Msg(this, ex.Message, MessageBoxIcon.Error); return; }
            // 7. confirm
            if (!Ui.Ask(this,
                "SP Flash Tool תופעל אוטומטית ותצרוב (Download Only, בלי format):\n" +
                string.Join("\n", roms.Select(x => "   • " + x.Name)) + "\n\n" +
                "DA: " + Path.GetFileName(da) + "\n" +
                "⚠️ ניסיוני, ותלוי בגרסת SP שלך. גבה את המכשיר. מומלץ לנסות קודם עם vbmeta בלבד.\n\n" +
                "להמשיך?", MessageBoxIcon.Warning)) return;
            string com = Prompt("מספר COM של המכשיר (אופציונלי - השאר ריק לזיהוי אוטומטי, למשל COM40):", "SP Flash Tool");
            if (com != null) com = com.Trim();
            cancel = false; SetBusy(true); SetStatus("");
            string ft = sptoolPath;
            worker = new Thread(() =>
            {
                try
                {
                    Log(""); Log("▶ SP Flash Tool - הפעלה אוטומטית (v5 + console). חבר את המכשיר כבוי כשתתבקש.", Color.DeepSkyBlue);
                    var runner = new MtkClient { Log = s => Log(s), Status = SetStatus, Cancelled = () => cancel };
                    SpFlashTool.Run(ft, cfg, string.IsNullOrEmpty(com) ? null : com, runner);
                    Log("✔ SP Flash Tool סיימה. בדוק שהמכשיר עלה.", Color.LightGreen);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); Log("  אפשר תמיד לצרוב ידנית: כפתור SP Flash Tool ← ידני.", Color.Khaki); }
                finally { SetStatus(""); SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        void OpenInSpTool()
        {
            // 1. decide which folder holds the scatter + images
            string folder = null;
            if (project != null && Directory.Exists(project.Edited) && Directory.GetFiles(project.Edited).Length > 0)
                folder = project.Edited;
            if (folder == null)
            {
                using (var d = new FolderBrowserDialog { Description = "בחר את התיקייה עם ה-scatter והקבצים לצריבה (למשל התיקייה \"ערוך\")" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    folder = d.SelectedPath;
                }
            }
            // 2. make sure a scatter exists (generate from the project if needed)
            string scatter = Directory.GetFiles(folder, "*scatter*.txt").OrderBy(x => x).FirstOrDefault();
            if (scatter == null && project != null)
            {
                try { EditedScatter.Create(project, x => Log(x)); } catch (Exception ex) { Log("! " + ex.Message, Color.Khaki); }
                scatter = Directory.GetFiles(folder, "*scatter*.txt").OrderBy(x => x).FirstOrDefault();
            }
            if (scatter == null)
            {
                Msg(this, "לא נמצא קובץ scatter.\n\nאפשרויות:\n1. העתק לפרויקט את קובץ ה-scatter של הקושחה (‏*_scatter.txt) ולחץ שוב.\n2. או בצע עריכה (RO2RW) - אם יש בפרויקט scatter מקורי, ייווצר scatter לתיקייה \"ערוך\".\n\n(ל-SP Flash Tool אפשר גם לטעון ישירות את ה-scatter של הקושחה ולהחליף ידנית את נתיבי הקבצים לקבצים שבתיקייה \"ערוך\".)", MessageBoxIcon.Warning);
                return;
            }
            // 3. locate flash_tool.exe (ask once, remember)
            if (ResolveSpTool() == null)
            {
                if (!Ui.Ask(this, "SP Flash Tool אינה מובנית (היא תוכנה סגורה, אי אפשר לארוז אותה). יש להתקין אותה בעצמך (תפריט מכשיר ← הורד SP Flash Tool), או להעתיק אותה לתוך התוכנה (מכשיר ← התקן SP Flash Tool לתוך התוכנה).\n\nלבחור עכשיו את הקובץ flash_tool.exe?")) return;
                using (var d = new OpenFileDialog { Title = "בחר את flash_tool.exe", Filter = "SP Flash Tool|flash_tool*.exe;FlashTool*.exe|כל הקבצים|*.exe" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    sptoolPath = d.FileName;
                }
            }
            // 4. hand everything over
            try { Clipboard.SetText(scatter); } catch { }
            try { Process.Start("explorer.exe", "/select,\"" + scatter + "\""); } catch { }
            try { Process.Start(new ProcessStartInfo(sptoolPath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(sptoolPath) }); }
            catch (Exception ex) { Msg(this, "לא ניתן להפעיל את SP Flash Tool: " + ex.Message, MessageBoxIcon.Error); return; }
            Log("SP Flash Tool נפתחה. נתיב ה-scatter הועתק ללוח: " + scatter, Color.LightGreen);
            using (var g = new SpGuideForm(folder, Directory.GetFiles(folder).Where(f => { var e2 = Path.GetExtension(f).ToLowerInvariant(); return e2 == ".img" || e2 == ".bin"; }), scatter)) g.ShowDialog(this);
        }

        void ShowSpGuide()
        {
            string folder = project != null && Directory.Exists(project.Edited) ? project.Edited : null;
            var files = folder != null ? Directory.GetFiles(folder).Where(f => { var e = Path.GetExtension(f).ToLowerInvariant(); return e == ".img" || e == ".bin"; }) : Enumerable.Empty<string>();
            string scatter = folder != null ? Directory.GetFiles(folder, "*scatter*.txt").OrderBy(x => x).FirstOrDefault() : null;
            using (var g = new SpGuideForm(folder, files, scatter)) g.ShowDialog(this);
        }

        // ------------------------------------------------------------ settings persistence
        string SettingsPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RO2RW-PC.ini"); } }

        void SaveUiSettings()
        {
            try
            {
                var kv = new List<string>
                {
                    "max=" + rbMax.Checked, "s=" + nS.Value, "v=" + nV.Value, "p=" + nP.Value, "se=" + nSE.Value, "ot=" + nOT.Value, "fixed=" + nFixed.Value,
                    "sparse=" + cbSparse.Checked, "fstab=" + cbFstab.Checked, "nowifi=" + cbNoWifi.Checked, "abi=" + cmbAbi.SelectedItem, "keepv=" + cbKeepVerity.Checked, "keepe=" + cbKeepEnc.Checked,
                    "lkdelay=" + cbLkDelay.Checked, "lktext=" + cbLkText.Checked, "skipud=" + cbSkipUserdata.Checked, "project=" + (project != null ? project.Name : ""),
                    "recovery=" + cbRecovery.Checked, "vbflag=" + cbPatchVbFlag.Checked, "lkandroid=" + cmbLkAndroid.SelectedIndex, "vbmode=" + cmbVbMode.SelectedIndex,
                    "projdir=" + (Project.BaseDirOverride ?? ""), "zoom=" + zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), "engine=" + (FastEngine.Selected == MtkEngine.Fast ? "fast" : "mtkclient"), "sptool=" + (sptoolPath ?? "")
                };
                File.WriteAllLines(SettingsPath, kv);
            }
            catch { }
        }

        string lastProject;
        string sptoolPath;
        void LoadUiSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var d = File.ReadAllLines(SettingsPath).Where(l => l.Contains("=")).ToDictionary(l => l.Substring(0, l.IndexOf('=')), l => l.Substring(l.IndexOf('=') + 1));
                Func<string, bool> b = k => d.ContainsKey(k) && d[k] == "True";
                Action<NumericUpDown, string> n = (c, k) => { decimal v; if (d.ContainsKey(k) && decimal.TryParse(d[k], out v)) c.Value = Math.Max(c.Minimum, Math.Min(c.Maximum, v)); };
                if (d.ContainsKey("max")) { rbMax.Checked = b("max"); rbFixed.Checked = !b("max"); }
                n(nS, "s"); n(nV, "v"); n(nP, "p"); n(nSE, "se"); n(nOT, "ot"); n(nFixed, "fixed");
                if (d.ContainsKey("sparse")) cbSparse.Checked = b("sparse");
                if (d.ContainsKey("fstab")) cbFstab.Checked = b("fstab");
                if (d.ContainsKey("nowifi")) cbNoWifi.Checked = b("nowifi");
                if (d.ContainsKey("abi") && cmbAbi.Items.Contains(d["abi"])) cmbAbi.SelectedItem = d["abi"];
                cbKeepVerity.Checked = b("keepv"); cbKeepEnc.Checked = b("keepe");
                if (d.ContainsKey("lkdelay")) cbLkDelay.Checked = b("lkdelay");
                if (d.ContainsKey("lktext")) cbLkText.Checked = b("lktext");
                if (d.ContainsKey("skipud")) cbSkipUserdata.Checked = b("skipud");
                if (d.ContainsKey("project")) lastProject = d["project"];
                cbRecovery.Checked = b("recovery"); cbPatchVbFlag.Checked = b("vbflag");
                int ix;
                if (d.ContainsKey("lkandroid") && int.TryParse(d["lkandroid"], out ix) && ix >= 0 && ix < 3) cmbLkAndroid.SelectedIndex = ix;
                if (d.ContainsKey("vbmode") && int.TryParse(d["vbmode"], out ix) && ix >= 0 && ix < 3) cmbVbMode.SelectedIndex = ix;
                if (d.ContainsKey("projdir") && d["projdir"].Length > 0 && Directory.Exists(d["projdir"])) Project.BaseDirOverride = d["projdir"];
                if (d.ContainsKey("engine") && d["engine"] == "fast" && FastEngine.Available) SetEngine(MtkEngine.Fast);
                if (d.ContainsKey("sptool") && d["sptool"].Length > 0) sptoolPath = d["sptool"];
                float zz;
                if (d.ContainsKey("zoom") && float.TryParse(d["zoom"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out zz) && Math.Abs(zz - 1f) > 0.01f) Shown += (s2, e2) => SetZoom(zz);
            }
            catch { }
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
            lines.Add(" * SP Flash Tool: טען את קובץ ה-scatter שבתיקייה הזו. רק המחיצות שנערכו מסומנות. בחר Download Only.");
            lines.Add(" * vbmeta ערוך מבטל את האימות, ואין צורך בדגלי --disable-verity.");
            lines.Add(" * אחרי צריבת boot/init_boot עם Magisk, התקן את אפליקציית Magisk בטלפון.");
            lines.Add(" * אחרי super עם RO2RW (ext4): mount -o rw,remount / עם root.");
            try { File.WriteAllLines(Path.Combine(proj.Edited, "HOW_TO_FLASH.txt"), lines, new System.Text.UTF8Encoding(true)); } catch { }
        }
    }
}
