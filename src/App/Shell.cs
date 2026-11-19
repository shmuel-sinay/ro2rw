using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Ro2rwPc
{
    /// <summary>Flat on/off switch. In an RTL window "on" moves the knob toward the left.</summary>
    public sealed class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Size = new Size(40, 22);
            Cursor = Cursors.Hand;
            Text = "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);
            var track = new Rectangle(0, 2, Width - 1, Height - 4);
            Color on = Enabled ? Color.FromArgb(21, 101, 192) : Color.FromArgb(170, 190, 215);
            Color off = Enabled ? Color.FromArgb(150, 158, 170) : Color.FromArgb(205, 210, 218);
            using (var b = new SolidBrush(Checked ? on : off)) g.FillRectangle(b, track);
            int k = track.Height - 6;
            int x = Checked ? track.Left + 3 : track.Right - k - 3;
            g.FillRectangle(Brushes.White, x, track.Top + 3, k, k);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(-1, 0, Width + 1, Height));
        }
    }

    /// <summary>Vertical navigation rail (placed on the right in an RTL window): numbered steps and plain section headers.</summary>
    public sealed class StepRail : Control
    {
        sealed class Item { public string Title, Status; public int Number, Page; public bool Header; }

        static readonly Color Line = Color.FromArgb(205, 212, 220);
        static readonly Color Accent = Color.FromArgb(21, 101, 192);
        readonly List<Item> items = new List<Item>();
        int selected = -1, hover = -1;
        public event EventHandler SelectedChanged;

        public StepRail()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(232, 236, 242);
            Width = 214;
            TabStop = true;
        }

        public int SelectedPage { get { return selected >= 0 ? items[selected].Page : -1; } }

        public void AddHeader(string text) { items.Add(new Item { Title = text, Header = true }); Invalidate(); }

        public void Add(string title, string status, int number, int page)
        {
            items.Add(new Item { Title = title, Status = status ?? "", Number = number, Page = page });
            if (selected < 0) selected = items.Count - 1;
            Invalidate();
        }

        public void Select(int page)
        {
            for (int i = 0; i < items.Count; i++)
                if (!items[i].Header && items[i].Page == page) { SelectIndex(i); return; }
        }

        public void SetStatus(int page, string status)
        {
            foreach (var it in items) if (!it.Header && it.Page == page) it.Status = status ?? "";
            Invalidate();
        }

        void SelectIndex(int i)
        {
            if (i < 0 || i >= items.Count || items[i].Header || i == selected) return;
            selected = i;
            Invalidate();
            if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
        }

        int HeightOf(Item it) { return it.Header ? 38 : (it.Status.Length > 0 ? 54 : 44); }

        int HitTest(Point p)
        {
            int y = 8;
            for (int i = 0; i < items.Count; i++)
            {
                int h = HeightOf(items[i]);
                if (p.Y >= y && p.Y < y + h) return items[i].Header ? -1 : i;
                y += h;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); SelectIndex(HitTest(e.Location)); base.OnMouseDown(e); }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.Up ? -1 : 0;
            if (step != 0)
            {
                int i = selected + step;
                while (i >= 0 && i < items.Count && items[i].Header) i += step;
                SelectIndex(i);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            using (var p = new Pen(Line)) g.DrawLine(p, 0, 0, 0, Height);
            var fTitle = new Font(Font.FontFamily, 10.5f, FontStyle.Regular);
            var fSel = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
            var fSmall = new Font(Font.FontFamily, 8.5f);
            var fHead = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
            var fl = TextFormatFlags.Right | TextFormatFlags.RightToLeft | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            int y = 8;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                int h = HeightOf(it);
                var r = new Rectangle(1, y, Width - 1, h);
                if (it.Header)
                {
                    TextRenderer.DrawText(g, it.Title, fHead, new Rectangle(8, y + 10, Width - 24, h - 10), Color.FromArgb(105, 115, 130), (fl & ~TextFormatFlags.VerticalCenter) | TextFormatFlags.Bottom);
                }
                else
                {
                    bool sel = i == selected;
                    if (sel) { using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, r); using (var b = new SolidBrush(Accent)) g.FillRectangle(b, Width - 4, y, 4, h); }
                    else if (i == hover) using (var b = new SolidBrush(Color.FromArgb(242, 245, 249))) g.FillRectangle(b, r);
                    var titleRect = new Rectangle(8, y, Width - 54, it.Status.Length > 0 ? h - 18 : h);
                    if (it.Number > 0)
                        TextRenderer.DrawText(g, it.Number.ToString(), fSel, new Rectangle(Width - 40, y, 26, titleRect.Height), sel ? Accent : Color.FromArgb(105, 115, 130), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, it.Title, sel ? fSel : fTitle, titleRect, Color.FromArgb(31, 41, 51), fl);
                    if (it.Status.Length > 0)
                        TextRenderer.DrawText(g, it.Status, fSmall, new Rectangle(8, y + h - 24, Width - 54, 16), Color.FromArgb(105, 115, 130), TextFormatFlags.Right | TextFormatFlags.RightToLeft | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
                y += h;
            }
            fTitle.Dispose(); fSel.Dispose(); fSmall.Dispose(); fHead.Dispose();
        }
    }

    /// <summary>One goal on the edit screen: switch, title, one-line description, and a collapsible options area.</summary>
    public sealed class GoalRow : TableLayoutPanel
    {
        public readonly ToggleSwitch Switch = new ToggleSwitch();
        public readonly Label Title = new Label();
        public readonly Label Desc = new Label();
        public readonly LinkLabel More = new LinkLabel();
        public readonly FlowLayoutPanel Options = new FlowLayoutPanel();

        public GoalRow(string title)
        {
            ColumnCount = 1; RowCount = 2;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            AutoSize = true;
            Dock = DockStyle.Fill;
            Padding = new Padding(0, 10, 0, 10);
            Margin = new Padding(0);

            var head = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Switch.Margin = new Padding(8, 6, 0, 0);
            Switch.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            var text = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
            text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            text.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            text.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Title.Text = title;
            Title.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            Title.AutoSize = true;
            Title.Margin = new Padding(0, 0, 0, 2);
            Desc.AutoSize = false;
            Desc.Height = 20;
            Desc.Dock = DockStyle.Fill;
            Desc.AutoEllipsis = true;
            Desc.ForeColor = Color.FromArgb(90, 99, 110);
            Desc.Margin = new Padding(0);
            text.Controls.Add(Title, 0, 0);
            text.Controls.Add(Desc, 0, 1);

            More.Text = "אפשרויות";
            More.AutoSize = true;
            More.LinkColor = Color.FromArgb(21, 101, 192);
            More.ActiveLinkColor = Color.FromArgb(13, 71, 140);
            More.Margin = new Padding(12, 6, 12, 0);
            More.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            More.LinkClicked += (s, e) => SetExpanded(!Options.Visible);

            head.Controls.Add(Switch, 0, 0);
            head.Controls.Add(text, 1, 0);
            head.Controls.Add(More, 2, 0);

            Options.FlowDirection = FlowDirection.TopDown;
            Options.WrapContents = false;
            Options.AutoSize = true;
            Options.Dock = DockStyle.Fill;
            Options.Margin = new Padding(0, 8, 56, 0);
            Options.Visible = false;

            Controls.Add(head, 0, 0);
            Controls.Add(Options, 0, 1);
        }

        public void SetExpanded(bool open)
        {
            Options.Visible = open;
            More.Text = open ? "הסתר אפשרויות" : "אפשרויות";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Color.FromArgb(205, 212, 220))) e.Graphics.DrawLine(p, 0, 0, Width, 0);
        }
    }
}
