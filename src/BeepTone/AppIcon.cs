using System.Drawing;

namespace BeepTone
{
    // The program icon (all sizes), for window title bars and the taskbar. The tray keeps its own
    // coloured "B" icons, which show the beep's status.
    static class AppIcon
    {
        static Icon icon;

        public static Icon Get()
        {
            if (icon == null)
            {
                using (var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("BeepTone.ico"))
                    icon = new Icon(stream);
            }
            return icon;
        }
    }
}
