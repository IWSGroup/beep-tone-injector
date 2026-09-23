// Beep Tone Injector: file locations, machine policy, the admin stop flag, per-user settings,
// and the beep pause.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace BeepTone
{
    public static class BeepPaths
    {
        static string dataDir;

        public static string DataDir
        {
            get
            {
                if (dataDir == null)
                {
                    dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeepTone");
#if DEBUG
                    string over = Environment.GetEnvironmentVariable("BEEPTONE_DATA_DIR");
                    if (!string.IsNullOrEmpty(over)) dataDir = over;
#endif
                }
                return dataDir;
            }
        }

        public static string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }
        public static string LogPath { get { return Path.Combine(DataDir, "beep-tone.log"); } }
        public static string HeartbeatPath { get { return Path.Combine(DataDir, "heartbeat.txt"); } }
        public static string PausePath { get { return Path.Combine(DataDir, "pause-until.txt"); } }
        public static string CableDir { get { return Path.Combine(DataDir, "VBCABLE"); } }
        public static string InstallDir { get { return Path.GetDirectoryName(typeof(BeepPaths).Assembly.Location); } }

        public static void Initialize()
        {
            try { Directory.CreateDirectory(DataDir); } catch { }
            BeepFiles.LogPath = LogPath;
            BeepFiles.HeartbeatPath = HeartbeatPath;
        }
    }

    // HKLM\Software\Policies\BeepTone. Values set here override setup and cannot be changed by agents.
    public static class BeepPolicy
    {
        public const string Key = @"Software\Policies\BeepTone";
        internal static bool Ignore;

        public static bool TryGet(string name, out object value)
        {
            value = null;
            if (Ignore) return false;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(Key))
                {
                    if (key == null) return false;
                    value = key.GetValue(name);
                    return value != null;
                }
            }
            catch { return false; }
        }

        public static bool Has(string name)
        {
            object unused;
            return TryGet(name, out unused);
        }

        public static bool TryGetNumber(string name, out double number)
        {
            object value;
            number = 0;
            return TryGet(name, out value) && BeepConfig.TryNumber(value, out number);
        }

        public static double GetNumber(string name, double fallback)
        {
            double number;
            return TryGetNumber(name, out number) ? number : fallback;
        }

        public static string GetString(string name, string fallback)
        {
            object value;
            if (!TryGet(name, out value)) return fallback;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
        }

        public static bool AllowPause { get { return GetNumber("AllowPause", 1) != 0; } }
        public static bool AllowStop { get { return GetNumber("AllowStop", 1) != 0; } }

        public static int PauseMinutes
        {
            get { return (int)BeepLimits.Clamp(GetNumber("PauseMinutes", 15), 1, 60, 15); }
        }

        public static string[] IgnoredMicApps()
        {
            object value;
            if (!TryGet("IgnoredMicApps", out value)) return new string[0];
            var items = new List<string>();
            var many = value as string[];
            string[] entries = many ?? new string[] { Convert.ToString(value, CultureInfo.InvariantCulture) };
            foreach (string entry in entries)
            {
                foreach (string part in (entry ?? "").Split(new char[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Trim().Length > 0) items.Add(part.Trim());
                }
            }
            return items.ToArray();
        }
    }

    // HKLM\Software\BeepTone\Disabled = 1 keeps the beep off until an administrator clears it.
    public static class AdminFlag
    {
        public const string Key = @"Software\BeepTone";

        public static bool IsDisabled()
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("BEEPTONE_TEST_IGNORE_STOP") == "1") return false;
            string stopFile = Environment.GetEnvironmentVariable("BEEPTONE_TEST_STOP_FILE");
            if (!string.IsNullOrEmpty(stopFile)) return File.Exists(stopFile);
#endif
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(Key))
                {
                    object value = key == null ? null : key.GetValue("Disabled");
                    return value != null && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
                }
            }
            catch { return false; }
        }

        public static void Set(bool disabled)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(Key))
                key.SetValue("Disabled", disabled ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    public sealed class BeepConfig
    {
        public string CaptureDeviceId = "";
        public string CaptureDeviceName = "";
        public string RenderDeviceId = "";
        public string RenderDeviceName = "CABLE Input";
        public double FrequencyHz = 1400;
        public int DurationMs = 200;
        public int IntervalSeconds = 13;
        public int RampMs = 50;
        public double LevelDbfs = -6;
        public bool SetDefaultMicrophone = true;

        // True when the file exists but could not be read. Such a config is never saved
        // automatically, so a locked or damaged file cannot be replaced with defaults.
        public bool ReadFailed;

        static readonly object Gate = new object();
        static BeepConfig lastGood;
        static BeepConfig cached;
        static long cachedStamp = -1;
        static DateTime cachedAt = DateTime.MinValue;

        public BeepConfig Copy()
        {
            return (BeepConfig)MemberwiseClone();
        }

        public BeepSettings ToSettings()
        {
            var s = new BeepSettings();
            s.FrequencyHz = FrequencyHz;
            s.DurationMs = DurationMs;
            s.IntervalSeconds = IntervalSeconds;
            s.RampMs = RampMs;
            s.LevelDbfs = LevelDbfs;
            return s.Clamped();
        }

        public static BeepConfig Load()
        {
            return Load(BeepPaths.ConfigPath);
        }

        public static BeepConfig Load(string path)
        {
            var config = new BeepConfig();
            bool failed = false;
            if (File.Exists(path))
            {
                try
                {
                    var raw = FlatJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
                    if (raw == null) throw new InvalidDataException("the file is not a JSON object");
                    config.CaptureDeviceId = Text(raw, "captureDeviceId", config.CaptureDeviceId);
                    config.CaptureDeviceName = Text(raw, "captureDeviceName", config.CaptureDeviceName);
                    config.RenderDeviceId = Text(raw, "renderDeviceId", config.RenderDeviceId);
                    config.RenderDeviceName = Text(raw, "renderDeviceName", config.RenderDeviceName);
                    config.FrequencyHz = Number(raw, "frequencyHz", config.FrequencyHz);
                    config.DurationMs = (int)Number(raw, "durationMs", config.DurationMs);
                    config.IntervalSeconds = (int)Number(raw, "intervalSeconds", config.IntervalSeconds);
                    config.RampMs = (int)Number(raw, "rampMs", config.RampMs);
                    // Older versions called the level minBeepDbfs.
                    config.LevelDbfs = Number(raw, "levelDbfs", Number(raw, "minBeepDbfs", config.LevelDbfs));
                    object flag;
                    if (raw.TryGetValue("setCommunicationsDevice", out flag) && flag is bool) config.SetDefaultMicrophone = (bool)flag;
                }
                catch (Exception ex)
                {
                    failed = true;
                    BeepFiles.Log("config could not be read: " + ex.Message);
                    lock (Gate) if (lastGood != null) config = lastGood.Copy();
                }
            }
            config.ApplyPolicyAndLimits();
            config.ReadFailed = failed;
            if (!failed) lock (Gate) lastGood = config.Copy();
            return config;
        }

        // Re-reads the file only when it changes, and at least every 30 seconds for policy changes.
        public static BeepConfig Cached()
        {
            long stamp = 0;
            try { if (File.Exists(BeepPaths.ConfigPath)) stamp = File.GetLastWriteTimeUtc(BeepPaths.ConfigPath).Ticks; } catch { }
            lock (Gate)
            {
                if (cached != null && cachedStamp == stamp && (DateTime.UtcNow - cachedAt).TotalSeconds < 30) return cached;
            }
            BeepConfig fresh = Load();
            lock (Gate)
            {
                cached = fresh;
                cachedStamp = stamp;
                cachedAt = DateTime.UtcNow;
            }
            return fresh;
        }

        public void Save(bool force)
        {
            Save(BeepPaths.ConfigPath, force);
        }

        public void Save(string path, bool force)
        {
            if (ReadFailed && !force)
            {
                BeepFiles.Log("config not saved, because the current file could not be read");
                return;
            }
            var text = new StringBuilder();
            text.AppendLine("{");
            text.AppendLine("    \"captureDeviceId\": " + FlatJson.Quote(CaptureDeviceId) + ",");
            text.AppendLine("    \"captureDeviceName\": " + FlatJson.Quote(CaptureDeviceName) + ",");
            text.AppendLine("    \"renderDeviceId\": " + FlatJson.Quote(RenderDeviceId) + ",");
            text.AppendLine("    \"renderDeviceName\": " + FlatJson.Quote(RenderDeviceName) + ",");
            text.AppendLine("    \"frequencyHz\": " + FrequencyHz.ToString(CultureInfo.InvariantCulture) + ",");
            text.AppendLine("    \"durationMs\": " + DurationMs.ToString(CultureInfo.InvariantCulture) + ",");
            text.AppendLine("    \"intervalSeconds\": " + IntervalSeconds.ToString(CultureInfo.InvariantCulture) + ",");
            text.AppendLine("    \"rampMs\": " + RampMs.ToString(CultureInfo.InvariantCulture) + ",");
            text.AppendLine("    \"levelDbfs\": " + LevelDbfs.ToString(CultureInfo.InvariantCulture) + ",");
            text.AppendLine("    \"setCommunicationsDevice\": " + (SetDefaultMicrophone ? "true" : "false"));
            text.AppendLine("}");
            BeepFiles.WriteAtomic(path, text.ToString(), new UTF8Encoding(false));
            ReadFailed = false;
            lock (Gate)
            {
                lastGood = Copy();
                cached = null;
            }
        }

        // Settings outside the allowed ranges are pulled back in, whether they came from setup,
        // a hand-edited config.json, or policy.
        public void ApplyPolicyAndLimits()
        {
            double number;
            if (BeepPolicy.TryGetNumber("FrequencyHz", out number)) FrequencyHz = number;
            if (BeepPolicy.TryGetNumber("DurationMs", out number)) DurationMs = (int)number;
            if (BeepPolicy.TryGetNumber("IntervalSeconds", out number)) IntervalSeconds = (int)number;
            if (BeepPolicy.TryGetNumber("RampMs", out number)) RampMs = (int)number;
            if (BeepPolicy.TryGetNumber("LevelDbfs", out number)) LevelDbfs = number;
            if (BeepPolicy.TryGetNumber("SetDefaultMicrophone", out number)) SetDefaultMicrophone = number != 0;
            BeepSettings s = ToSettings();
            FrequencyHz = s.FrequencyHz;
            DurationMs = s.DurationMs;
            IntervalSeconds = s.IntervalSeconds;
            RampMs = s.RampMs;
            LevelDbfs = s.LevelDbfs;
            CaptureDeviceId = CaptureDeviceId ?? "";
            CaptureDeviceName = CaptureDeviceName ?? "";
            RenderDeviceId = RenderDeviceId ?? "";
            RenderDeviceName = RenderDeviceName ?? "";
        }

        static string Text(Dictionary<string, object> raw, string name, string fallback)
        {
            object value;
            if (!raw.TryGetValue(name, out value) || value == null) return fallback;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        static double Number(Dictionary<string, object> raw, string name, double fallback)
        {
            object value;
            double number;
            if (raw.TryGetValue(name, out value) && TryNumber(value, out number)) return number;
            return fallback;
        }

        public static bool TryNumber(object value, out double number)
        {
            number = 0;
            if (value == null || value is bool) return false;
            try
            {
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return !double.IsNaN(number) && !double.IsInfinity(number);
            }
            catch { return false; }
        }
    }

    // The pause file is in a folder the user can write, so a time further out than one pause is
    // treated as tampering: it is removed, logged, and reported.
    public static class BeepPause
    {
        public static DateTime? ReadFile()
        {
            return ReadFile(BeepPaths.PausePath);
        }

        public static DateTime? ReadFile(string path)
        {
            if (!File.Exists(path)) return null;
            DateTime until = DateTime.MinValue;
            bool parsed = false;
            try
            {
                parsed = DateTime.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out until);
                until = until.ToUniversalTime();
            }
            catch { }
            string problem = null;
            if (!parsed) problem = "pause file could not be read; removed it";
            else if (!BeepPolicy.AllowPause) problem = "pausing is turned off by policy; removed the pause file";
            else if (until > DateTime.UtcNow.AddMinutes(BeepPolicy.PauseMinutes + 1))
            {
                string stamp = until.ToString("o", CultureInfo.InvariantCulture);
                problem = "pause file asked for a pause until " + stamp + ", longer than one pause; removed it";
                BeepEvents.Write(BeepEvents.PauseTampered, "The beep pause file was edited to pause until " + stamp + ". It was ignored.", true);
            }
            if (problem != null || DateTime.UtcNow >= until)
            {
                if (problem != null) BeepFiles.Log(problem);
                try { File.Delete(path); } catch { }
                return null;
            }
            return until;
        }

        public static DateTime? Start()
        {
            if (!BeepPolicy.AllowPause) return null;
            DateTime until = DateTime.UtcNow.AddMinutes(BeepPolicy.PauseMinutes);
            string stamp = until.ToString("o", CultureInfo.InvariantCulture);
            BeepFiles.WriteAtomic(BeepPaths.PausePath, stamp, Encoding.ASCII);
            BeepFiles.Log("beep paused until " + stamp);
            BeepEvents.Write(BeepEvents.Paused, "The beep was paused until " + stamp + ".", true);
            return until;
        }

        public static void Stop()
        {
            try { File.Delete(BeepPaths.PausePath); } catch { }
            BeepFiles.Log("beep pause ended");
            BeepEvents.Write(BeepEvents.Resumed, "The beep pause ended.", false);
        }
    }

    public static class BeepConfigSelfTest
    {
        public static string[] Run()
        {
            var lines = new List<string>();
            string dir = Path.Combine(Path.GetTempPath(), "BeepToneConfigTest-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            string savedLog = BeepFiles.LogPath;
            BeepPolicy.Ignore = true;
            BeepFiles.LogPath = Path.Combine(dir, "test.log");
            try
            {
                string config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, "{\"frequencyHz\": 20000, \"intervalSeconds\": 3600, \"minBeepDbfs\": -200, \"durationMs\": \"abc\", \"captureDeviceName\": \"Headset (2- Jabra)\"}", new UTF8Encoding(true));
                BeepConfig c = BeepConfig.Load(config);
                if (c.FrequencyHz == 1540 && c.IntervalSeconds == 15 && c.LevelDbfs == -90 && c.DurationMs == 200 && !c.ReadFailed)
                    lines.Add("PASS config limits: hand-edited out-of-range values are pulled back in, old minBeepDbfs is migrated");
                else
                    lines.Add("FAIL config limits: " + c.FrequencyHz + " Hz, " + c.IntervalSeconds + " s, " + c.LevelDbfs + " dBFS, " + c.DurationMs + " ms");

                c.LevelDbfs = -30;
                c.Save(config, false);
                File.WriteAllText(config, "{ not json");
                BeepConfig bad = BeepConfig.Load(config);
                bad.Save(config, false);
                string after = File.ReadAllText(config);
                if (bad.ReadFailed && bad.LevelDbfs == -30 && after == "{ not json")
                    lines.Add("PASS unreadable config: keeps the last good settings and is not overwritten");
                else
                    lines.Add("FAIL unreadable config: readFailed=" + bad.ReadFailed + " level=" + bad.LevelDbfs + " file=" + after);

                var tricky = new BeepConfig();
                tricky.CaptureDeviceName = "Headset \"Pro\" C:\\mic\t\u00e9\u4e2d";
                tricky.Save(config, true);
                BeepConfig back = BeepConfig.Load(config);
                lines.Add(back.CaptureDeviceName == tricky.CaptureDeviceName && !back.ReadFailed
                    ? "PASS config text: quotes, backslashes, tabs and non-English device names survive a save and load"
                    : "FAIL config text: saved '" + tricky.CaptureDeviceName + "', read '" + back.CaptureDeviceName + "'");

                string pause = Path.Combine(dir, "pause-until.txt");
                File.WriteAllText(pause, DateTime.UtcNow.AddYears(5).ToString("o", CultureInfo.InvariantCulture));
                bool farIgnored = BeepPause.ReadFile(pause) == null && !File.Exists(pause);
                File.WriteAllText(pause, DateTime.UtcNow.AddMinutes(10).ToString("o", CultureInfo.InvariantCulture));
                bool nearKept = BeepPause.ReadFile(pause) != null;
                lines.Add(farIgnored && nearKept
                    ? "PASS pause file: a normal pause is kept, a years-long pause is removed"
                    : "FAIL pause file: far ignored=" + farIgnored + ", near kept=" + nearKept);
            }
            catch (Exception ex)
            {
                lines.Add("FAIL config tests: " + ex.Message);
            }
            finally
            {
                BeepPolicy.Ignore = false;
                BeepFiles.LogPath = savedLog;
                try { Directory.Delete(dir, true); } catch { }
            }
            return lines.ToArray();
        }
    }
}
