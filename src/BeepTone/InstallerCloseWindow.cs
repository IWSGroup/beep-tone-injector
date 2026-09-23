using System;
using System.Windows.Forms;

namespace BeepTone
{
    // A hidden top-level window that answers Windows Restart Manager. When an installer needs Beep Tone's
    // files, Restart Manager sends WM_QUERYENDSESSION and WM_ENDSESSION with ENDSESSION_CLOSEAPP to the
    // app's windows. Sign-out sends the same messages without that flag, and is left to Windows.
    sealed class InstallerCloseWindow : NativeWindow
    {
        const int QueryEndSession = 0x0011;
        const int EndSession = 0x0016;
        const long CloseApp = 0x1;
        readonly Action close;

        public InstallerCloseWindow(Action close)
        {
            this.close = close;
            CreateHandle(new CreateParams { Caption = "Beep Tone" });
        }

        protected override void WndProc(ref Message m)
        {
            bool closeApp = (m.LParam.ToInt64() & CloseApp) != 0;
            if (m.Msg == QueryEndSession && closeApp)
            {
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == EndSession && closeApp && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero;
                close();
                return;
            }
            base.WndProc(ref m);
        }
    }
}
