using System;
using System.Threading;
using System.Windows.Forms;

namespace BeepTone
{
    static class Program
    {
        [STAThread]
        static int Main()
        {
            BeepPaths.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs e)
            {
                BeepFiles.Log("tray error: " + e.Exception.Message);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                BeepFiles.Log("fatal: " + e.ExceptionObject);
            };

            bool created;
            string mutexName = @"Local\BeepToneInjector";
#if DEBUG
            string testMutex = Environment.GetEnvironmentVariable("BEEPTONE_TEST_MUTEX");
            if (!string.IsNullOrEmpty(testMutex)) mutexName = @"Local\" + testMutex;
#endif
            using (var mutex = new Mutex(false, mutexName, out created))
            {
                bool owned;
                try { owned = mutex.WaitOne(0, false); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var tray = new TrayApp()) Application.Run(tray);
                mutex.ReleaseMutex();
            }
            return 0;
        }
    }
}
