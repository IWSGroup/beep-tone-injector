using System.Drawing;
using System.Windows.Forms;

namespace BeepTone
{
    sealed class PasswordForm : Form
    {
        // The password typed, once it was accepted.
        public string Password { get; private set; }

        public PasswordForm() : this("Enter the setup password.") { }

        public PasswordForm(string prompt)
        {
            Text = "Beep Tone setup";
            Icon = AppIcon.Get();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(360, 140);
            Font = new Font("Segoe UI", 9);

            var label = new Label { Text = prompt, Location = new Point(16, 16), Size = new Size(328, 20) };
            var box = new TextBox { Location = new Point(16, 44), Size = new Size(328, 24), UseSystemPasswordChar = true };
            var ok = new Button { Text = "OK", Location = new Point(168, 88), Size = new Size(80, 28) };
            var cancel = new Button { Text = "Cancel", Location = new Point(256, 88), Size = new Size(88, 28), DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { label, box, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            ok.Click += delegate
            {
                if (SetupPassword.Verify(box.Text, BeepPolicy.SetupPasswordHash))
                {
                    Password = box.Text;
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                BeepFiles.Log("wrong setup password entered");
                MessageBox.Show(this, "That password is not correct.", "Beep Tone");
                box.Clear();
                box.Focus();
            };
        }
    }
}
