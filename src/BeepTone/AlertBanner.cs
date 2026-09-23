using System;
using System.Drawing;
using System.Windows.Forms;

namespace BeepTone
{
    // Always-on-top strip that does not take focus. A tray notification can be hidden by Do Not
    // Disturb and the tray icon can sit in the overflow area; this cannot be missed.
    sealed class AlertBanner : Form
    {
        static readonly Color Red = Color.FromArgb(190, 40, 40);
        static readonly Color Amber = Color.FromArgb(200, 120, 10);
        readonly Label message;
        readonly LinkLabel hide;
        DateTime hiddenUntil = DateTime.MinValue;
        string shownText;

        public AlertBanner()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Red;
            Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            Padding = new Padding(12, 6, 12, 6);
            message = new Label();
            message.ForeColor = Color.White;
            message.Dock = DockStyle.Fill;
            message.TextAlign = ContentAlignment.MiddleLeft;
            hide = new LinkLabel();
            hide.Text = "Hide for 2 minutes";
            hide.LinkColor = Color.White;
            hide.ActiveLinkColor = Color.White;
            hide.Font = new Font("Segoe UI", 9f);
            hide.Dock = DockStyle.Right;
            hide.AutoSize = true;
            hide.TextAlign = ContentAlignment.MiddleRight;
            hide.LinkClicked += delegate
            {
                hiddenUntil = DateTime.Now.AddMinutes(2);
                Hide();
            };
            Controls.Add(message);
            Controls.Add(hide);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                const int NoActivate = 0x08000000, ToolWindow = 0x00000080, Topmost = 0x00000008;
                cp.ExStyle |= NoActivate | ToolWindow | Topmost;
                return cp;
            }
        }

        public void ShowAlert(string text, bool warning)
        {
            BackColor = warning ? Amber : Red;
            message.Text = text;
            if (DateTime.Now < hiddenUntil && text == shownText) return;
            if (text != shownText) hiddenUntil = DateTime.MinValue;
            shownText = text;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int width = Math.Min(820, area.Width - 32);
            // Wrap long messages onto more lines instead of cutting them off.
            int textWidth = width - Padding.Horizontal - hide.PreferredWidth - 12;
            Size measured = TextRenderer.MeasureText(message.Text, message.Font, new Size(textWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int height = Math.Max(44, measured.Height + Padding.Vertical + 8);
            Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + 8, width, height);
            if (!Visible) Show();
        }

        public void ClearAlert()
        {
            shownText = null;
            hiddenUntil = DateTime.MinValue;
            if (Visible) Hide();
        }
    }
}
