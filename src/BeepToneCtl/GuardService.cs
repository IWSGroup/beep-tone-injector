// Windows service that keeps the beep running for every signed-in user.
// It runs as LocalSystem, so a standard user cannot stop it, and checks every 5 seconds.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace BeepTone
{
    sealed class GuardService : ServiceBase
    {
        public const string Name = "BeepToneGuard";
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread worker;

        public GuardService()
        {
            ServiceName = Name;
            CanStop = true;
            CanHandleSessionChangeEvent = true;
        }

        protected override void OnStart(string[] args)
        {
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Start();
        }

        protected override void OnStop()
        {
            stop.Set();
            if (worker != null) worker.Join(5000);
        }

        protected override void OnSessionChange(SessionChangeDescription change)
        {
            if (change.Reason == SessionChangeReason.SessionLogon || change.Reason == SessionChangeReason.SessionUnlock
                || change.Reason == SessionChangeReason.ConsoleConnect || change.Reason == SessionChangeReason.RemoteConnect)
                wake.Set();
        }

        void Run()
        {
            var guard = new Guard(Guard.DefaultTrayPath(), false);
            BeepEvents.Write(BeepEvents.Started, "Beep Tone guard started. Tray app: " + guard.TrayPath, false);
            int passes = 0;
            while (!stop.WaitOne(0))
            {
                // Keep memory flat; see the same call in the tray app.
                if (++passes % 12 == 0) GC.Collect();
                try { guard.Pass(); }
                catch (Exception ex) { BeepEvents.Write(BeepEvents.Problem, "Beep Tone guard error: " + ex.Message, true); }
                WaitHandle.WaitAny(new WaitHandle[] { stop, wake }, 5000);
            }
        }
    }

    sealed class Guard
    {
        sealed class SessionState
        {
            public DateTime LastLaunchUtc = DateTime.MinValue;
            public readonly List<DateTime> Launches = new List<DateTime>();
            public DateTime BackoffUntilUtc = DateTime.MinValue;
            public DateTime SuspectSinceUtc = DateTime.MinValue;
        }

        public readonly string TrayPath;
        readonly bool dryRun;
        readonly Dictionary<int, SessionState> sessions = new Dictionary<int, SessionState>();

        public Guard(string trayPath, bool dryRun)
        {
            TrayPath = trayPath;
            this.dryRun = dryRun;
        }

        public static string DefaultTrayPath()
        {
            return Path.Combine(BeepPaths.InstallDir, "BeepTone.exe");
        }

        public List<string> Pass()
        {
            var notes = new List<string>();
            // The tray still runs while an administrator has the beep stopped, showing a grey icon,
            // so it can start the beep again the moment the stop is cleared.
            if (AdminFlag.IsDisabled()) notes.Add("an administrator stopped the beep; the tray shows that and does not beep");
            int[] ids = dryRun ? new int[] { Process.GetCurrentProcess().SessionId } : Native.ActiveSessions();
            foreach (int sessionId in ids)
            {
                if (sessionId == 0) continue;
                IntPtr token = IntPtr.Zero;
                if (!dryRun && !Native.WTSQueryUserToken(sessionId, out token)) continue;
                try { notes.Add(CheckSession(sessionId, token)); }
                catch (Exception ex) { notes.Add("session " + sessionId + ": " + ex.Message); }
                finally { if (token != IntPtr.Zero) Native.CloseHandle(token); }
            }
            return notes;
        }

        string CheckSession(int sessionId, IntPtr token)
        {
            SessionState state;
            if (!sessions.TryGetValue(sessionId, out state))
            {
                state = new SessionState();
                sessions[sessionId] = state;
            }
            string local = Native.LocalAppData(token);
            if (string.IsNullOrEmpty(local)) return "session " + sessionId + ": profile is not loaded yet";
            string heartbeatPath = Path.Combine(Path.Combine(local, "BeepTone"), "heartbeat.txt");
            HeartbeatInfo heartbeat = HeartbeatInfo.Read(heartbeatPath);
            Process mixer = heartbeat == null ? null : FindMixer(heartbeat.ProcessId, sessionId);
            if (mixer == null) mixer = FindAnyMixer(sessionId);
            double age = 0;
            if (mixer != null)
            {
                try { age = (DateTime.Now - mixer.StartTime).TotalSeconds; } catch { age = WatchdogPolicy.GraceSeconds; }
                // A tray that is not the one named in the heartbeat is not writing it.
                if (heartbeat != null && heartbeat.ProcessId != mixer.Id) heartbeat = null;
            }
            DateTime now = DateTime.UtcNow;
            bool recent = (now - state.LastLaunchUtc).TotalSeconds < WatchdogPolicy.GraceSeconds;
            string reason;
            WatchdogAction action = WatchdogPolicy.Decide(heartbeat, mixer != null, age, now,
                BeepLimits.MaxIntervalSeconds, recent, out reason);
            string prefix = "session " + sessionId + ": ";

            // A stale heartbeat can be a machine waking from sleep. Confirm on the next pass.
            if (action == WatchdogAction.Restart && !dryRun)
            {
                if (state.SuspectSinceUtc == DateTime.MinValue) state.SuspectSinceUtc = now;
                if ((now - state.SuspectSinceUtc).TotalSeconds < 4) return prefix + reason + "; confirming";
            }
            else
            {
                state.SuspectSinceUtc = DateTime.MinValue;
            }
            if (action == WatchdogAction.None) return prefix + reason;
            if (dryRun) return prefix + reason + "; would " + (action == WatchdogAction.Start ? "start" : "restart") + " the tray app";

            if (now < state.BackoffUntilUtc) return prefix + reason + "; waiting after repeated failures";
            state.Launches.RemoveAll(delegate (DateTime t) { return (now - t).TotalMinutes > 10; });
            if (state.Launches.Count >= 5)
            {
                state.BackoffUntilUtc = now.AddMinutes(10);
                BeepEvents.Write(BeepEvents.GuardBackoff, "The beep tray app in session " + sessionId
                    + " was started 5 times in 10 minutes. Waiting 10 minutes before trying again. Last reason: " + reason, true);
                return prefix + "backing off";
            }
            if (action == WatchdogAction.Restart && mixer != null)
            {
                try { mixer.Kill(); mixer.WaitForExit(5000); } catch { }
                BeepEvents.Write(BeepEvents.GuardRestarted, "Restarted the beep tray app in session " + sessionId + ": " + reason, true);
            }
            Native.Launch(token, sessionId, TrayPath);
            state.LastLaunchUtc = now;
            state.Launches.Add(now);
            state.SuspectSinceUtc = DateTime.MinValue;
            BeepEvents.Write(BeepEvents.GuardLaunched, "Started the beep tray app in session " + sessionId + ": " + reason, false);
            return prefix + reason + "; started the tray app";
        }

        // Only BeepTone.exe in the same session counts, so a heartbeat file the user edited
        // cannot make this service end some other process.
        static Process FindMixer(int pid, int sessionId)
        {
            if (pid <= 0) return null;
            try
            {
                Process p = Process.GetProcessById(pid);
                if (p.SessionId != sessionId) return null;
                if (!string.Equals(p.ProcessName, "BeepTone", StringComparison.OrdinalIgnoreCase)) return null;
                return p;
            }
            catch { return null; }
        }

        static Process FindAnyMixer(int sessionId)
        {
            foreach (Process p in Process.GetProcessesByName("BeepTone"))
            {
                try { if (p.SessionId == sessionId) return p; } catch { }
            }
            return null;
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WtsSessionInfo
        {
            public int SessionId;
            public string WinStationName;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll")]
        static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessAsUserW(IntPtr token, string application, string commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInformation info);

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);

        static readonly Guid LocalAppDataFolder = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

        public static int[] ActiveSessions()
        {
            var ids = new List<int>();
            IntPtr info;
            int count;
            if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out info, out count)) return ids.ToArray();
            try
            {
                int size = Marshal.SizeOf(typeof(WtsSessionInfo));
                for (int i = 0; i < count; i++)
                {
                    var s = (WtsSessionInfo)Marshal.PtrToStructure(IntPtr.Add(info, i * size), typeof(WtsSessionInfo));
                    if (s.State == 0) ids.Add(s.SessionId);
                }
            }
            finally { WTSFreeMemory(info); }
            return ids.ToArray();
        }

        public static string LocalAppData(IntPtr token)
        {
            Guid folder = LocalAppDataFolder;
            IntPtr path;
            if (SHGetKnownFolderPath(ref folder, 0, token, out path) < 0) return null;
            try { return Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }

        public static void Launch(IntPtr token, int sessionId, string trayPath)
        {
            IntPtr environment;
            if (!CreateEnvironmentBlock(out environment, token, false)) environment = IntPtr.Zero;
            try
            {
                var startup = new StartupInfo();
                startup.cb = Marshal.SizeOf(typeof(StartupInfo));
                startup.lpDesktop = @"winsta0\default";
                ProcessInformation info;
                const uint CreateUnicodeEnvironment = 0x00000400;
                if (!CreateProcessAsUserW(token, trayPath, "\"" + trayPath + "\"", IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, environment, Path.GetDirectoryName(trayPath), ref startup, out info))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "could not start the tray app in session " + sessionId);
                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
            }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            }
        }
    }
}
