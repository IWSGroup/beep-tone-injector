using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace BeepTone
{
    // Asks the guard service to let this tray quit (see QuitServer in BeepToneCtl).
    static class GuardClient
    {
        // The guard's reply, or null when no guard is running, so nothing would start the tray again.
        public static string RequestQuit(string password)
        {
            using (var pipe = new NamedPipeClientStream(".", GuardProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try
                {
                    pipe.Connect(3000);
                }
                catch (Exception ex) when (ex is System.TimeoutException || ex is IOException)
                {
                    if (!GuardRunning()) return null;
                    throw new InvalidOperationException("The Beep Tone guard service did not answer. Try again in a moment.");
                }
                // Only the guard runs in session 0, where no signed-in user can start a program, so the
                // password never goes to a program pretending to be it.
                uint serverSession;
                if (!GetNamedPipeServerSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out serverSession) || serverSession != 0)
                    throw new InvalidOperationException("Something other than the Beep Tone guard service answered, so the password was not sent.");
                GuardProtocol.WriteLine(pipe, GuardProtocol.QuitRequest(password));
                string reply = GuardProtocol.ReadLine(pipe, 15000);
                if (reply == null) throw new InvalidOperationException("The Beep Tone guard service did not answer. Try again in a moment.");
                return reply;
            }
        }

        // A reply other than "ok", for people to read.
        public static string Describe(string reply)
        {
            if (reply == GuardProtocol.WrongPassword) return "The Beep Tone guard service did not accept the password.";
            if (reply == GuardProtocol.NotAllowed) return "Quitting Beep Tone is turned off by policy.";
            if (reply.StartsWith(GuardProtocol.Locked + " ", StringComparison.Ordinal))
            {
                int seconds;
                int.TryParse(reply.Substring(GuardProtocol.Locked.Length + 1), out seconds);
                int minutes = Math.Max(1, (seconds + 59) / 60);
                return "Too many wrong passwords. Try again in " + minutes + (minutes == 1 ? " minute." : " minutes.");
            }
            if (reply.StartsWith(GuardProtocol.Failed + " ", StringComparison.Ordinal))
                return "Beep Tone could not quit: " + reply.Substring(GuardProtocol.Failed.Length + 1) + ".";
            return "Beep Tone could not quit.";
        }

        static bool GuardRunning()
        {
            try
            {
                using (var service = new ServiceController("BeepToneGuard"))
                    return service.Status != ServiceControllerStatus.Stopped;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerSessionId(IntPtr pipe, out uint sessionId);
    }
}
