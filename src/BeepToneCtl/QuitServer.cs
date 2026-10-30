// Answers the tray's "Quit Beep Tone" request over a local named pipe. The guard checks the setup
// password itself and then stops restarting the tray for that sign-in, so an agent cannot get rid of
// the beep by ending the tray or editing its files.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace BeepTone
{
    sealed class QuitServer
    {
        readonly WaitHandle stop;
        readonly Dictionary<int, QuitLockout> lockouts = new Dictionary<int, QuitLockout>();
        Thread thread;

        public QuitServer(WaitHandle stop)
        {
            this.stop = stop;
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true };
            thread.Start();
        }

        public void Join(int milliseconds)
        {
            if (thread != null) thread.Join(milliseconds);
        }

        void Run()
        {
            string lastError = null;
            while (!stop.WaitOne(0))
            {
                try
                {
                    ServeOne();
                    lastError = null;
                }
                catch (Exception ex)
                {
                    if (ex.Message != lastError)
                        BeepEvents.Write(BeepEvents.Problem, "Beep Tone guard could not answer the tray's quit request: " + ex.Message, true);
                    lastError = ex.Message;
                    stop.WaitOne(1000);
                }
            }
        }

        void ServeOne()
        {
            using (var pipe = new NamedPipeServerStream(GuardProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, GuardProtocol.MaxMessageBytes, GuardProtocol.MaxMessageBytes, Security()))
            {
                IAsyncResult connected = pipe.BeginWaitForConnection(null, null);
                if (WaitHandle.WaitAny(new[] { stop, connected.AsyncWaitHandle }) == 0) return;
                pipe.EndWaitForConnection(connected);
                string request = GuardProtocol.ReadLine(pipe, 5000);
                // A tray that hangs up without asking, or before the answer, is not a guard problem.
                if (request == null) return;
                string reply = Handle(pipe, request);
                try
                {
                    GuardProtocol.WriteLine(pipe, reply);
                    // Wait for the tray to read the reply and hang up, so closing the pipe cannot drop it.
                    GuardProtocol.ReadLine(pipe, 2000);
                }
                catch (IOException)
                {
                }
            }
        }

        string Handle(NamedPipeServerStream pipe, string request)
        {
            IntPtr handle = pipe.SafePipeHandle.DangerousGetHandle();
            uint sessionId, processId;
            if (!GetNamedPipeClientSessionId(handle, out sessionId) || sessionId == 0)
                return GuardProtocol.Failed + " the request did not come from a signed-in user";
            if (!GetNamedPipeClientProcessId(handle, out processId)) processId = 0;
            string password;
            if (!GuardProtocol.TryParseQuit(request, out password)) return GuardProtocol.Failed + " unknown request";
            if (!BeepPolicy.AllowQuit) return GuardProtocol.NotAllowed;

            int session = (int)sessionId;
            QuitLockout lockout;
            if (!lockouts.TryGetValue(session, out lockout))
            {
                lockout = new QuitLockout();
                lockouts[session] = lockout;
            }
            DateTime now = DateTime.UtcNow;
            int locked = lockout.SecondsLocked(now);
            if (locked > 0) return GuardProtocol.Locked + " " + locked;
            if (!SetupPassword.Verify(password, BeepPolicy.SetupPasswordHash))
            {
                if (lockout.RecordFailure(now))
                {
                    BeepEvents.Write(BeepEvents.QuitLocked, "Wrong setup password entered " + QuitLockout.MaxFailures
                        + " times to quit Beep Tone in session " + session + ". Quitting is refused there for 5 minutes.", true);
                    return GuardProtocol.Locked + " " + lockout.SecondsLocked(now);
                }
                return GuardProtocol.WrongPassword;
            }
            lockout.Reset();

            IntPtr token;
            if (!Native.WTSQueryUserToken(session, out token)) return GuardProtocol.Failed + " the session has no signed-in user";
            try
            {
                QuitMarks.Set(session, new QuitMark
                {
                    Boot = QuitMarks.BootStamp(),
                    LogonId = Native.LogonId(token),
                    QuitUtc = now,
                    ProcessId = (int)processId
                });
            }
            finally
            {
                Native.CloseHandle(token);
            }
            return GuardProtocol.Ok;
        }

        // Signed-in users can connect; nobody can connect over the network, and only SYSTEM can create
        // the pipe, so a user cannot stand in for the guard while it runs.
        static PipeSecurity Security()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeClientSessionId(IntPtr pipe, out uint sessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
    }
}
