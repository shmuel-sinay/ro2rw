using System;
using System.Drawing;
using System.Windows.Forms;

namespace Ro2rwPc
{
    public sealed class EngineInfo
    {
        public string Title, Quality, DumpDetail, FlashDetail;
    }

    /// <summary>The five dump/flash engines, in the order used by MainForm.SetEngineByIndex.</summary>
    public static class EngineCatalog
    {
        public static readonly EngineInfo[] All =
        {
            new EngineInfo { Title = "mtkclient דרך USB", Quality = "יציב",
                DumpDetail = "הכלי הפתוח המקובל, נבדק על אלפי מכשירים. עובד דרך USB ודורש דרייבר UsbDk.",
                FlashDetail = "הכלי הפתוח המקובל, נבדק על אלפי מכשירים. עובד דרך USB ודורש דרייבר UsbDk." },
            new EngineInfo { Title = "mtkclient דרך COM / VCOM", Quality = "יציב, מומלץ",
                DumpDetail = "אותו כלי, דרך דרייבר ה-VCOM של MediaTek (הדרייבר ש-SP Flash Tool משתמש בו). לא דורש UsbDk.",
                FlashDetail = "אותו כלי, דרך דרייבר ה-VCOM של MediaTek (הדרייבר ש-SP Flash Tool משתמש בו). לא דורש UsbDk." },
            new EngineInfo { Title = "מנוע מהיר דרך USB", Quality = "מהיר, ניסיוני",
                DumpDetail = "העברה מהירה (כמה MB לשנייה). נבדק על מעט מכשירים. דורש UsbDk.",
                FlashDetail = "העברה מהירה (כמה MB לשנייה). נבדק על מעט מכשירים, צריבה עלולה להשבית מכשיר. דורש UsbDk." },
            new EngineInfo { Title = "מנוע מהיר דרך COM", Quality = "מהיר, ניסיוני",
                DumpDetail = "המנוע המהיר דרך דרייבר VCOM, בלי UsbDk. נבדק על מעט מכשירים.",
                FlashDetail = "המנוע המהיר דרך דרייבר VCOM, בלי UsbDk. נבדק על מעט מכשירים, צריבה עלולה להשבית מכשיר." },
            new EngineInfo { Title = "SP Flash Tool", Quality = "רשמי של MediaTek",
                DumpDetail = "שאיבה (ReadBack) של המחיצות הנפוצות לעריכה. חדש, עוד לא נבדק מול מכשיר. דורש התקנת SP לתוך התוכנה.",
                FlashDetail = "צריבה אוטומטית (Download Only), נבדקה בהצלחה. דורש התקנת SP לתוך התוכנה." }
        };

        public static string Short(int i) { return All[i].Title + " (" + All[i].Quality + ")"; }
    }

    /// <summary>Asks which tool to use for this dump/flash, with a one-line explanation of each, and optionally keeps the answer as the default.</summary>
    public sealed class EngineForm : Form
    {
        readonly RadioButton[] radios = new RadioButton[EngineCatalog.All.Length];
        readonly CheckBox cbDefault;

        public int Choice
        {
            get { for (int i = 0; i < radios.Length; i++) if (radios[i].Checked) return i; return 0; }
        }

        public bool SaveDefault { get { return cbDefault.Checked; } }

        /// <param name="unavailable">returns a reason when an engine cannot be used right now, otherwise null</param>
        public EngineForm(bool forDump, int preselect, Func<int, string> unavailable, bool defaultOnly)
        {
            Text = forDump ? "בחירת כלי לשאיבה" : "בחירת כלי לצריבה";
            Font = new Font("Segoe UI", 10f);
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(640, 640);
            BackColor = Color.White;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(new Label
            {
                Text = defaultOnly ? (forDump ? "כלי ברירת המחדל לשאיבה" : "כלי ברירת המחדל לצריבה") : (forDump ? "באיזה כלי לשאוב?" : "באיזה כלי לצרוב?"),
                Font = new Font("Segoe UI", 13f, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(21, 101, 192), Margin = new Padding(0, 0, 0, 10)
            });

            for (int i = 0; i < EngineCatalog.All.Length; i++)
            {
                var info = EngineCatalog.All[i];
                string reason = unavailable != null ? unavailable(i) : null;
                var p = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 10), Dock = DockStyle.Top };
                p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                radios[i] = new RadioButton { Text = info.Title + "  |  " + info.Quality, AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Enabled = reason == null, Margin = new Padding(0) };
                p.Controls.Add(radios[i]);
                p.Controls.Add(new Label { Text = forDump ? info.DumpDetail : info.FlashDetail, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.FromArgb(90, 99, 110), Margin = new Padding(24, 0, 0, 0) });
                if (reason != null) p.Controls.Add(new Label { Text = reason, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.Firebrick, Margin = new Padding(24, 2, 0, 0) });
                root.Controls.Add(p);
            }
            int pre = (preselect >= 0 && preselect < radios.Length && radios[preselect].Enabled) ? preselect : Array.FindIndex(radios, r => r.Enabled);
            if (pre >= 0) radios[pre].Checked = true;

            cbDefault = new CheckBox { Text = forDump ? "זכור כברירת מחדל לשאיבה" : "זכור כברירת מחדל לצריבה", AutoSize = true, Margin = new Padding(0, 6, 0, 10), Checked = defaultOnly, Enabled = !defaultOnly };
            root.Controls.Add(cbDefault);

            var btns = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            var ok = new Button { Text = defaultOnly ? "שמור" : "המשך", DialogResult = DialogResult.OK, Size = new Size(130, 36), BackColor = Color.FromArgb(21, 101, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "ביטול", DialogResult = DialogResult.Cancel, Size = new Size(100, 36), FlatStyle = FlatStyle.Flat };
            btns.Controls.AddRange(new Control[] { ok, cancel });
            root.Controls.Add(btns);
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(root);
        }
    }

    /// <summary>A titled section whose body (tool options) can be shown or hidden.</summary>
    public sealed class OptionGroup : TableLayoutPanel
    {
        public readonly FlowLayoutPanel Body = new FlowLayoutPanel();
        readonly LinkLabel head = new LinkLabel();
        readonly string title;

        public OptionGroup(string title, string summary)
        {
            this.title = title;
            ColumnCount = 1; RowCount = 3;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) RowStyles.Add(new RowStyle(SizeType.AutoSize));
            AutoSize = true; Dock = DockStyle.Fill; Margin = new Padding(0); Padding = new Padding(0, 8, 0, 8);

            head.AutoSize = true;
            head.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            head.LinkColor = Color.FromArgb(31, 41, 51);
            head.ActiveLinkColor = Color.FromArgb(21, 101, 192);
            head.LinkBehavior = LinkBehavior.HoverUnderline;
            head.LinkClicked += (s, e) => SetOpen(!Body.Visible);
            Controls.Add(head, 0, 0);
            Controls.Add(new Label { Text = summary, AutoSize = true, ForeColor = Color.FromArgb(90, 99, 110), Margin = new Padding(0, 0, 0, 0) }, 0, 1);

            Body.FlowDirection = FlowDirection.TopDown;
            Body.WrapContents = false;
            Body.AutoSize = true;
            Body.Dock = DockStyle.Fill;
            Body.Margin = new Padding(0, 8, 24, 0);
            Controls.Add(Body, 0, 2);
            SetOpen(false);
        }

        public void SetOpen(bool open)
        {
            Body.Visible = open;
            head.Text = (open ? "הסתר: " : "הצג: ") + title;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Color.FromArgb(205, 212, 220))) e.Graphics.DrawLine(p, 0, 0, Width, 0);
        }
    }
}
