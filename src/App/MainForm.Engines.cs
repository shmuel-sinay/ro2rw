using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Ro2rwPc
{
    // Dump and flash each have their own default tool; before every operation a window lets the user switch tool.
    public sealed partial class MainForm
    {
        const int PageSp = 3;
        int defDump, defFlash;
        Panel spMissing, spReady;
        Label lblSpPath;

        static object[] EngineItems()
        {
            return EngineCatalog.All.Select((e, i) => (object)EngineCatalog.Short(i)).ToArray();
        }

        string EngineUnavailable(int i)
        {
            if (i == 4 && ResolveSpTool() == null) return "SP Flash Tool עוד לא הותקן. התקן אותו בלשונית SP Flash Tool.";
            if ((i == 2 || i == 3) && !FastEngine.Available) return "המנוע המהיר לא מובנה בגרסה הזו.";
            return null;
        }

        /// <summary>Shows the tool picker (default preselected). With defaultOnly it just stores the new default. Returns false if cancelled.</summary>
        bool ChooseEngine(bool forDump, bool defaultOnly = false)
        {
            int pre = forDump ? defDump : defFlash;
            using (var f = new EngineForm(forDump, pre, EngineUnavailable, defaultOnly))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                if (defaultOnly || f.SaveDefault) SetDefaultEngine(forDump, f.Choice);
                if (defaultOnly) return true;
                SetEngineByIndex(f.Choice);
                return true;
            }
        }

        void SetDefaultEngine(bool forDump, int idx)
        {
            if (idx < 0 || idx >= EngineCatalog.All.Length) return;
            if (forDump) defDump = idx; else defFlash = idx;
            engineUiUpdating = true;
            try
            {
                var c = forDump ? cmbEngineDump : cmbEngineFlash;
                if (c != null && idx < c.Items.Count) c.SelectedIndex = idx;
            }
            finally { engineUiUpdating = false; }
        }

        void LoadEngineDefaults(IDictionary<string, string> d)
        {
            int ix; bool got = false;
            if (d.ContainsKey("engdump") && int.TryParse(d["engdump"], out ix)) { SetDefaultEngine(true, ix); got = true; }
            if (d.ContainsKey("engflash") && int.TryParse(d["engflash"], out ix)) { SetDefaultEngine(false, ix); got = true; }
            if (!got && d.ContainsKey("engine"))   // older settings file: one engine for both
            {
                string e = d["engine"];
                int m = e == "sp" ? 4 : e == "mtkserial" ? 1 : e == "serial" ? 3 : e == "fast" ? 2 : 0;
                SetDefaultEngine(true, m); SetDefaultEngine(false, m);
            }
        }

        // ============================================================ SP Flash Tool page: install notice first, tools only once installed
        TabPage BuildSpTab()
        {
            var t = ToolPage("SP Flash Tool");
            t.Controls.Add(Head("SP Flash Tool"));

            spMissing = new Panel { Location = new Point(6, 44), Size = new Size(720, 240) };
            spMissing.Controls.Add(new Label { Text = "עוד לא התקנת את SP Flash Tool.", Font = fHead, ForeColor = Color.Firebrick, AutoSize = true, Location = new Point(0, 0) });
            spMissing.Controls.Add(new Label { Text = "SP Flash Tool היא תוכנה סגורה של MediaTek ולכן היא לא מובנית בתוכנה. התקן אותה פעם אחת לתוך התוכנה, ואז היא תהיה זמינה לשאיבה ולצריבה.", AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.FromArgb(90, 99, 110), Location = new Point(0, 34) });
            var btnInstall = new Button { Text = "התקן SP Flash Tool לתוך התוכנה...", Location = new Point(0, 110), Size = new Size(320, 38), Font = fBold, BackColor = Color.FromArgb(21, 101, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnInstall.Click += (s, e) => InstallSpIntoTool();
            var btnDownload = new Button { Text = "הורד SP Flash Tool (אתר רשמי)", Location = new Point(0, 156), Size = new Size(320, 34) };
            btnDownload.Click += (s, e) => { try { Process.Start(new ProcessStartInfo("https://spflashtool.com/") { UseShellExecute = true }); } catch { } };
            spMissing.Controls.AddRange(new Control[] { btnInstall, btnDownload });

            spReady = new Panel { Location = new Point(6, 44), Size = new Size(720, 300), Visible = false };
            lblSpPath = new Label { AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Color.FromArgb(46, 125, 50), Font = fBold, Location = new Point(0, 0) };
            spReady.Controls.Add(lblSpPath);
            spReady.Controls.Add(new Label { Text = "אפשר לבחור בו לשאיבה (ReadBack) ולצריבה, או להפעיל אותו מכאן ישירות.", AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.FromArgb(90, 99, 110), Location = new Point(0, 28) });
            btnSp = new Button { Text = "צרוב אוטומטית (Download Only)", Location = new Point(0, 70), Size = new Size(320, 40), Font = fBold, BackColor = Color.FromArgb(21, 101, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnSp.Click += (s, e) => RunSpAuto();
            var btnManual = new Button { Text = "פתח ידנית (תלחץ Download בעצמך)", Location = new Point(0, 118), Size = new Size(320, 34) };
            btnManual.Click += (s, e) => OpenInSpTool();
            var btnGuide = new Button { Text = "מדריך צריבה + גיבוי ReadBack...", Location = new Point(0, 160), Size = new Size(320, 34) };
            btnGuide.Click += (s, e) => ShowSpGuide();
            spReady.Controls.AddRange(new Control[] { btnSp, btnManual, btnGuide });

            t.Controls.AddRange(new Control[] { spMissing, spReady });
            UpdateSpUi();
            return t;
        }

        void UpdateSpUi()
        {
            if (spMissing == null) return;
            string path = ResolveSpTool();
            bool installed = path != null;
            spMissing.Visible = !installed;
            spReady.Visible = installed;
            if (installed) lblSpPath.Text = "SP Flash Tool מותקן: " + path;
        }
    }
}
