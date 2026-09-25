using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace BeepTone
{
    static class Program
    {
        const string Usage = @"BeepToneCtl.exe <command>

  selftest                       Check the tone, timing, detection and safety rules. No audio devices needed.
  list-devices                   List microphones and playback devices.
  test-mix [--seconds N]         Run the mixer for N seconds (default 40) and check each beep reaches the cable.
  check-recordings <file|folder> [--recurse] [--max-gap S] [--frequency HZ] [--csv FILE]
                                 Check WAV, MP3, M4A or WMA recordings for a beep at least every S
                                 seconds (default 18), anywhere from 1260 to 1540 Hz. --frequency
                                 counts only beeps within 40 Hz of HZ.
  stop                           Administrator: turn the beep off on this PC until 'start'.
  start                          Administrator: turn the beep back on.
  guard-check                    Show what the guard service would do for this session, without doing it.
  cleanup-legacy                 Administrator: remove leftovers of the PowerShell version.
  post-install                   Run by the installer: cleanup-legacy, then report an administrator stop.
  install-cable-if-missing [--dry-run]
                                 Administrator: download and install VB-Cable if it is not installed.
                                 --dry-run downloads and checks the signature only.
  remove-cable [--dry-run]       Administrator: remove VB-Cable, as uninstalling Beep Tone does.
                                 Exits with 3010 when a restart finishes the removal.
                                 --dry-run lists what would be removed.
  new-password-hash              Make a setup password hash for the SetupPasswordHash policy value.
";

        static int Main(string[] args)
        {
            BeepPaths.Initialize();
            if (args.Length == 0)
            {
                if (!Environment.UserInteractive)
                {
                    ServiceBase.Run(new GuardService());
                    return 0;
                }
                Console.WriteLine("Beep Tone " + BeepPaths.Version);
                Console.Write(Usage);
                return 0;
            }
            string command = args[0].ToLowerInvariant();
            try
            {
                switch (command)
                {
                    case "service": ServiceBase.Run(new GuardService()); return 0;
                    case "selftest": return SelfTest();
                    case "list-devices": return ListDevices();
                    case "test-mix": return TestMix((int)Option(args, "--seconds", 40));
                    case "check-recordings": return CheckRecordings(args);
                    case "stop": return AdminStop();
                    case "start": return AdminStart();
                    case "guard-check": return GuardCheck();
                    case "cleanup-legacy": return Cleanup();
                    case "post-install": return PostInstall();
                    case "new-password-hash": return NewPasswordHash();
                    case "install-cable-if-missing":
                        if (!Flag(args, "--dry-run") && !IsAdmin()) return Fail("Run this from an administrator command prompt.");
                        return CableInstall.InstallIfMissing(Flag(args, "--dry-run"));
                    case "remove-cable":
                        if (!Flag(args, "--dry-run") && !IsAdmin()) return Fail("Run this from an administrator command prompt.");
                        return CableInstall.Remove(Flag(args, "--dry-run"));
                    case "install-cable":
                        if (args.Length < 3) return Fail("install-cable needs the package path and its SHA-256.");
                        return CableInstall.Run(args[1], args[2]);
                    case "help":
                    case "-h":
                    case "--help":
                    case "/?":
                    case "version":
                    case "--version":
                        Console.WriteLine("Beep Tone " + BeepPaths.Version);
                        if (command == "version" || command == "--version") return 0;
                        Console.Write(Usage);
                        return 0;
                }
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
            return Fail("Unknown command '" + args[0] + "'.\r\n\r\n" + Usage);
        }

        static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 1;
        }

        static double Option(string[] args, string name, double fallback)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                double value;
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
            }
            return fallback;
        }

        static string TextOption(string[] args, string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static bool Flag(string[] args, string name)
        {
            return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }

        static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // Re-runs this command elevated and waits. A cancelled approval changes nothing.
        static int Elevate(string command)
        {
            var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, command)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (Process p = Process.Start(start))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Win32Exception)
            {
                return Fail("Administrator approval is required. Nothing was changed.");
            }
        }

        static int SelfTest()
        {
            var lines = new List<string>(BeepSelfTest.RunAll());
            lines.AddRange(BeepConfigSelfTest.Run());
            lines.Add(MediaFoundationSelfTest());
            foreach (string line in lines) Console.WriteLine(line);
            int failed = lines.Count(l => l.StartsWith("FAIL", StringComparison.Ordinal));
            if (failed > 0)
            {
                Console.WriteLine(failed + " test(s) failed.");
                return 1;
            }
            Console.WriteLine("All " + lines.Count + " tests passed.");
            return 0;
        }

        static int ListDevices()
        {
            Console.WriteLine("Capture devices:");
            foreach (AudioEndpoint e in AudioDevices.List("Capture"))
                Console.WriteLine("  " + e.Name + (e.IsVirtual ? " (virtual, not offered as a microphone)" : ""));
            Console.WriteLine("Playback devices:");
            foreach (AudioEndpoint e in AudioDevices.List("Render")) Console.WriteLine("  " + e.Name);
            return 0;
        }

        static int TestMix(int seconds)
        {
            if (AdminFlag.IsDisabled())
                return Fail("An administrator has stopped the beep on this PC. Run 'BeepToneCtl.exe start' first.");
            // The test mixer must not overwrite a running tray's heartbeat.
            BeepFiles.HeartbeatPath = null;
            if (Process.GetProcessesByName("BeepTone").Any(p => p.SessionId == Process.GetCurrentProcess().SessionId))
                Console.WriteLine("Note: the tray app is also running, so its beeps are in the cable too.");
            BeepConfig config = BeepConfig.Load();
            var mixer = new BeepMixer
            {
                CaptureId = config.CaptureDeviceId,
                CaptureName = config.CaptureDeviceName,
                RenderId = config.RenderDeviceId,
                RenderName = config.RenderDeviceName,
                Settings = config.ToSettings(),
                IgnoredMicApps = BeepPolicy.IgnoredMicApps()
            };
            Console.WriteLine("Testing for " + seconds + " seconds: " + config.FrequencyHz + " Hz every " + config.IntervalSeconds
                + " s at " + config.LevelDbfs + " dBFS.");
            BeepFiles.Log("test mix for " + seconds + " seconds");
            mixer.Start();
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            string last = null;
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(500);
                string line = mixer.Status + ": mic '" + mixer.ActiveCaptureName + "' -> '" + mixer.ActiveRenderName + "', beeps "
                    + mixer.BeepCount + ", heard " + mixer.HeardCount + ", missed " + mixer.MissedCount;
                foreach (string problem in new[] { mixer.Problem, mixer.MuteProblem, mixer.BypassProblem, mixer.VerifyProblem })
                    if (problem.Length > 0) line += " | " + problem;
                if (line != last) Console.WriteLine(line);
                last = line;
            }
            mixer.RequestStop();
            Thread.Sleep(300);
            if (!mixer.CanVerify)
            {
                Console.WriteLine("RESULT: the beep was sent, but there is no matching recording device to check it on.");
                return 2;
            }
            if (mixer.HeardCount >= 1 && mixer.MissedCount == 0)
            {
                Console.WriteLine("PASS: all " + mixer.HeardCount + " beeps were heard on the cable at the set level.");
                return 0;
            }
            Console.WriteLine("FAIL: " + mixer.HeardCount + " beeps heard, " + mixer.MissedCount + " missed. See " + BeepPaths.LogPath + ".");
            return 1;
        }

        static int CheckRecordings(string[] args)
        {
            if (args.Length < 2) return Fail("check-recordings needs a recording or a folder.");
            string path = args[1];
            double maxGap = Option(args, "--max-gap", 18);
            // Without --frequency, any beep in the allowed 1260-1540 Hz counts, so beeps from a hardware
            // beep device or another app are found too.
            double frequency = Option(args, "--frequency", 0);
            string csv = TextOption(args, "--csv");
            if (csv != null) csv = Path.GetFullPath(csv);
            string[] files;
            if (Directory.Exists(path))
                files = Directory.GetFiles(path, "*.*", Flag(args, "--recurse") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(RecordingFiles.IsRecording).ToArray();
            else if (File.Exists(path))
                files = new[] { path };
            else
                return Fail("Not found: " + path);

            var rows = new List<string[]>();
            int failed = 0;
            foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                RecordingResult r = RecordingChecker.CheckFile(file, frequency, maxGap, RecordingFiles.Open);
                string verdict = r.Error != null ? "ERROR" : (r.Pass ? "PASS" : "FAIL");
                if (verdict != "PASS") failed++;
                rows.Add(new[]
                {
                    file, verdict, Round(r.DurationSeconds), r.BeepCount.ToString(CultureInfo.InvariantCulture),
                    r.BeepCount > 0 ? Math.Round(r.BeepFrequencyHz).ToString(CultureInfo.InvariantCulture) : "",
                    Round(r.FirstBeepSeconds), Round(r.MaxGapSeconds), Round(r.MaxGapAtSeconds), r.Error ?? ""
                });
            }
            var header = new[] { "File", "Result", "Seconds", "Beeps", "BeepHz", "FirstBeepAt", "LongestGap", "LongestGapFrom", "Error" };
            PrintTable(header, rows);
            if (csv != null)
            {
                var text = new StringBuilder();
                text.AppendLine(string.Join(",", header));
                foreach (string[] row in rows) text.AppendLine(string.Join(",", row.Select(Csv)));
                File.WriteAllText(csv, text.ToString(), new UTF8Encoding(true));
            }
            string band = frequency > 0
                ? "within 40 Hz of " + frequency.ToString(CultureInfo.InvariantCulture) + " Hz"
                : "anywhere from " + BeepLimits.MinFrequencyHz + " to " + BeepLimits.MaxFrequencyHz + " Hz";
            Console.WriteLine(rows.Count + " recording(s) checked for beeps " + band + ", longest allowed gap "
                + maxGap.ToString(CultureInfo.InvariantCulture) + " s. " + failed + " did not pass.");
            if (csv != null) Console.WriteLine("Report written to " + csv);
            return failed > 0 ? 1 : 0;
        }

        // Checks that Windows can decode recordings through Media Foundation, the path MP3, M4A and WMA
        // files take, by reading a synthetic WAV both ways and comparing the results.
        static string MediaFoundationSelfTest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "BeepToneMfTest-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            try
            {
                string gap = Path.Combine(dir, "gap.wav");
                BeepSelfTest.WriteTestRecording(gap, 16000, 70, 13, 26, false);
                RecordingResult plain = RecordingChecker.CheckFile(gap, 0, 18);
                RecordingResult mf = RecordingChecker.CheckFile(gap, 0, 18, delegate (string p) { return new MediaFoundationReader(p); });
                if (mf.Error != null) return "FAIL media foundation decoding: " + mf.Error;
                if (mf.BeepCount != plain.BeepCount || Math.Abs(mf.MaxGapSeconds - plain.MaxGapSeconds) > 0.1)
                    return "FAIL media foundation decoding: " + mf.BeepCount + " beeps, gap " + Round(mf.MaxGapSeconds)
                        + "; expected " + plain.BeepCount + ", " + Round(plain.MaxGapSeconds);
                return "PASS media foundation decoding: Windows decodes recordings the same way, so MP3, M4A and WMA can be checked";
            }
            catch (Exception ex)
            {
                return "FAIL media foundation decoding: " + ex.Message;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static string Round(double value)
        {
            return Math.Round(value, 1).ToString("0.0", CultureInfo.InvariantCulture);
        }

        static string Csv(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        static void PrintTable(string[] header, List<string[]> rows)
        {
            int columns = header.Length - 1;
            var widths = new int[columns];
            for (int c = 0; c < columns; c++)
                widths[c] = Math.Max(header[c].Length, rows.Count == 0 ? 0 : rows.Max(r => r[c].Length));
            Action<string[]> print = row =>
            {
                var line = new StringBuilder();
                for (int c = 0; c < columns; c++) line.Append(row[c].PadRight(widths[c] + 2));
                if (row[columns].Length > 0) line.Append(row[columns]);
                Console.WriteLine(line.ToString().TrimEnd());
            };
            print(header);
            foreach (string[] row in rows) print(row);
        }

        static int AdminStop()
        {
            if (!IsAdmin())
            {
                int code = Elevate("stop");
                if (code == 0) Console.WriteLine("Beep tone is stopped on this PC. It stays off until an administrator runs 'BeepToneCtl.exe start'.");
                return code;
            }
            // Each tray app sees this within a second, stops the beep and turns grey.
            AdminFlag.Set(true);
            BeepFiles.Log("administrator stop");
            BeepEvents.Write(BeepEvents.AdminStopped, "An administrator stopped the beep on this computer.", true);
            Console.WriteLine("Beep tone is stopped on this PC. It stays off until an administrator runs 'BeepToneCtl.exe start'.");
            return 0;
        }

        static int AdminStart()
        {
            if (!IsAdmin())
            {
                int code = Elevate("start");
                if (code == 0) Console.WriteLine("Beep tone is on. It starts for each signed-in user within a few seconds.");
                return code;
            }
            AdminFlag.Set(false);
            BeepFiles.Log("administrator start");
            BeepEvents.Write(BeepEvents.AdminStarted, "An administrator started the beep on this computer.", false);
            Console.WriteLine("Beep tone is on. It starts for each signed-in user within a few seconds.");
            return 0;
        }

        // The installer captures this output in its log (msiexec /l*v).
        static int PostInstall()
        {
            foreach (string note in LegacyCleanup.Run()) Console.WriteLine(note);
            if (AdminFlag.IsDisabled())
            {
                const string warning = @"WARNING: an administrator stop is set (HKLM\Software\BeepTone\Disabled = 1), so the beep stays off. "
                    + "The tray shows a grey icon. Run 'BeepToneCtl.exe start' as an administrator to turn it on.";
                Console.WriteLine(warning);
                BeepEvents.Write(BeepEvents.AdminStopped, "Beep Tone was installed, but " + warning.Substring(9), true);
            }
            return 0;
        }

        static int GuardCheck()
        {
            var guard = new Guard(Guard.DefaultTrayPath(), true);
            Console.WriteLine("Dry run for this session. Tray app: " + guard.TrayPath);
            foreach (string note in guard.Pass()) Console.WriteLine(note);
            return 0;
        }

        static int Cleanup()
        {
            if (!IsAdmin()) return Fail("Run this from an administrator command prompt, so it can remove other users' leftovers and show what it did.");
            foreach (string note in LegacyCleanup.Run()) Console.WriteLine(note);
            return 0;
        }

        static int NewPasswordHash()
        {
            string first = ReadSecret("New setup password: ");
            string second = ReadSecret("Type it again: ");
            if (first.Length == 0 || first != second) return Fail("The passwords did not match.");
            Console.WriteLine(SetupPassword.Hash(first));
            Console.WriteLine(@"Set this as the string value SetupPasswordHash under HKLM\Software\Policies\BeepTone.");
            return 0;
        }

        static string ReadSecret(string prompt)
        {
            Console.Write(prompt);
            var text = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (text.Length > 0) text.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
            }
            Console.WriteLine();
            return text.ToString();
        }
    }
}
