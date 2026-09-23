// Beep Tone Injector: platform-neutral pieces.
// Everything here runs without audio hardware, so -SelfTest can check it on any machine.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BeepTone
{
    public static class BeepFiles
    {
        public static string LogPath;
        public static string HeartbeatPath;
        static readonly object Gate = new object();

        public static void Log(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(LogPath)) return;
                string dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                    + " " + message + Environment.NewLine;
                lock (Gate)
                {
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                    var info = new FileInfo(LogPath);
                    if (info.Length <= 512 * 1024) return;
                    string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    string archive = Path.Combine(dir, "beep-tone-" + stamp + ".log");
                    try { File.Move(LogPath, archive); }
                    catch { return; }
                    string note = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                        + " archived previous log to " + Path.GetFileName(archive) + Environment.NewLine;
                    File.WriteAllText(LogPath, note, Encoding.UTF8);
                    FileInfo[] archives = new DirectoryInfo(dir).GetFiles("beep-tone-*.log");
                    Array.Sort(archives, delegate (FileInfo a, FileInfo b)
                    {
                        return string.Compare(b.Name, a.Name, StringComparison.Ordinal);
                    });
                    for (int i = 7; i < archives.Length; i++)
                    {
                        try { archives[i].Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        // Writes to a temp file and swaps it in, so a reader never sees a half-written file.
        public static void WriteAtomic(string path, string text, Encoding encoding)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, encoding);
            if (!File.Exists(path))
            {
                try { File.Move(temp, path); return; }
                catch (IOException) { }
            }
            try
            {
                File.Replace(temp, path, null, true);
            }
            catch (IOException)
            {
                File.Copy(temp, path, true);
                try { File.Delete(temp); } catch { }
            }
            catch (UnauthorizedAccessException)
            {
                File.Copy(temp, path, true);
                try { File.Delete(temp); } catch { }
            }
        }

        public static void WriteHeartbeat(HeartbeatInfo info)
        {
            try
            {
                if (string.IsNullOrEmpty(HeartbeatPath)) return;
                lock (Gate) WriteAtomic(HeartbeatPath, info.Format(), Encoding.ASCII);
            }
            catch { }
        }
    }

    public static class HeartbeatState
    {
        public const string Running = "running";
        public const string Paused = "paused";
        public const string Reconnecting = "reconnecting";
        public const string Setup = "setup";
        public const string Stopped = "stopped";
    }

    public sealed class HeartbeatInfo
    {
        public DateTime TimestampUtc;
        public int ProcessId;
        public DateTime LastBeepUtc = DateTime.MinValue;
        public string State = HeartbeatState.Running;

        public string Format()
        {
            string beep = LastBeepUtc == DateTime.MinValue ? "-" : LastBeepUtc.ToString("o", CultureInfo.InvariantCulture);
            return TimestampUtc.ToString("o", CultureInfo.InvariantCulture) + " "
                + ProcessId.ToString(CultureInfo.InvariantCulture) + " " + beep + " " + (State ?? HeartbeatState.Running);
        }

        public static HeartbeatInfo Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] parts = text.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            var info = new HeartbeatInfo();
            DateTime when;
            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when)) return null;
            info.TimestampUtc = when.ToUniversalTime();
            int pid;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)) return null;
            info.ProcessId = pid;
            if (parts.Length >= 3 && parts[2] != "-")
            {
                DateTime beep;
                if (DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out beep))
                    info.LastBeepUtc = beep.ToUniversalTime();
            }
            info.State = parts.Length >= 4 ? parts[3] : HeartbeatState.Running;
            return info;
        }

        // Retries because antivirus or the writer can hold the file for a moment.
        public static HeartbeatInfo Read(string path)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    HeartbeatInfo info = Parse(File.ReadAllText(path));
                    if (info != null) return info;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                System.Threading.Thread.Sleep(50);
            }
            return null;
        }
    }

    public enum WatchdogAction { None, Start, Restart }

    // Shared by the per-user watchdog task and the machine guard service.
    public static class WatchdogPolicy
    {
        public const double StaleSeconds = 30;
        public const double GraceSeconds = 90;

        public static double BeepLateSeconds(int intervalSeconds)
        {
            return Math.Max(30, intervalSeconds * 2);
        }

        public static WatchdogAction Decide(HeartbeatInfo heartbeat, bool processAlive, double processAgeSeconds,
            DateTime nowUtc, int intervalSeconds, bool recentlyLaunched, out string reason)
        {
            if (!processAlive)
            {
                if (recentlyLaunched)
                {
                    reason = "mixer was started recently";
                    return WatchdogAction.None;
                }
                reason = "mixer is not running";
                return WatchdogAction.Start;
            }
            if (heartbeat == null)
            {
                if (processAgeSeconds < GraceSeconds)
                {
                    reason = "mixer is starting";
                    return WatchdogAction.None;
                }
                reason = "mixer has no heartbeat";
                return WatchdogAction.Restart;
            }
            double age = (nowUtc - heartbeat.TimestampUtc).TotalSeconds;
            if (age > StaleSeconds)
            {
                reason = "heartbeat is " + ((int)age).ToString(CultureInfo.InvariantCulture) + " seconds old";
                return WatchdogAction.Restart;
            }
            if (heartbeat.State != HeartbeatState.Running)
            {
                // Paused, reconnecting to a missing device, or waiting for setup: a restart cannot help.
                reason = "mixer reports " + heartbeat.State;
                return WatchdogAction.None;
            }
            if (processAgeSeconds < GraceSeconds)
            {
                reason = "mixer is starting";
                return WatchdogAction.None;
            }
            double limit = BeepLateSeconds(intervalSeconds);
            if (heartbeat.LastBeepUtc == DateTime.MinValue || (nowUtc - heartbeat.LastBeepUtc).TotalSeconds > limit)
            {
                reason = "no beep has been sent while running";
                return WatchdogAction.Restart;
            }
            reason = "healthy";
            return WatchdogAction.None;
        }
    }

    public static class BeepLimits
    {
        public const double MinFrequencyHz = 1260;
        public const double MaxFrequencyHz = 1540;
        public const int MinDurationMs = 170;
        public const int MaxDurationMs = 250;
        public const int MinIntervalSeconds = 12;
        public const int MaxIntervalSeconds = 15;
        public const int MinRampMs = 5;
        public const int MaxRampMs = 80;
        public const double MinLevelDbfs = -90;
        public const double MaxLevelDbfs = -3;

        public static double Clamp(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    public sealed class BeepSettings
    {
        public double FrequencyHz = 1400;
        public int DurationMs = 200;
        public int IntervalSeconds = 13;
        public int RampMs = 50;
        public double LevelDbfs = -6;

        public BeepSettings Clamped()
        {
            var s = new BeepSettings();
            s.FrequencyHz = BeepLimits.Clamp(FrequencyHz, BeepLimits.MinFrequencyHz, BeepLimits.MaxFrequencyHz, 1400);
            s.DurationMs = BeepLimits.Clamp(DurationMs, BeepLimits.MinDurationMs, BeepLimits.MaxDurationMs);
            s.IntervalSeconds = BeepLimits.Clamp(IntervalSeconds, BeepLimits.MinIntervalSeconds, BeepLimits.MaxIntervalSeconds);
            s.RampMs = BeepLimits.Clamp(RampMs, BeepLimits.MinRampMs, BeepLimits.MaxRampMs);
            s.LevelDbfs = BeepLimits.Clamp(LevelDbfs, BeepLimits.MinLevelDbfs, BeepLimits.MaxLevelDbfs, -6);
            return s;
        }

        // Level is the RMS of the steady part of the tone.
        public float PeakGain()
        {
            double peak = Math.Pow(10.0, LevelDbfs / 20.0) * Math.Sqrt(2.0);
            if (peak > 1) peak = 1;
            if (peak < 1e-6) peak = 1e-6;
            return (float)peak;
        }
    }

    public static class BeepSynth
    {
        public static float[] Create(int sampleRate, double frequencyHz, int durationMs, int rampMs)
        {
            int total = (int)Math.Round(sampleRate * (durationMs / 1000.0));
            if (total < 1) total = 1;
            int ramp = (int)Math.Round(sampleRate * (rampMs / 1000.0));
            if (ramp < 1) ramp = 1;
            if (ramp * 2 > total) ramp = total / 2;
            var samples = new float[total];
            double step = 2.0 * Math.PI * frequencyHz / sampleRate;
            for (int i = 0; i < total; i++)
            {
                double env = 1.0;
                if (i < ramp)
                    env = 0.5 * (1.0 - Math.Cos(Math.PI * i / ramp));
                else if (i >= total - ramp)
                    env = 0.5 * (1.0 + Math.Cos(Math.PI * (i - (total - ramp)) / ramp));
                samples[i] = (float)(env * Math.Sin(step * i));
            }
            return samples;
        }

        public static float[] Create(int sampleRate, BeepSettings settings)
        {
            BeepSettings s = settings.Clamped();
            float[] beep = Create(sampleRate, s.FrequencyHz, s.DurationMs, s.RampMs);
            float gain = s.PeakGain();
            for (int i = 0; i < beep.Length; i++) beep[i] *= gain;
            return beep;
        }
    }

    // Decides when the tone plays. A beep that has started always finishes, so a pause
    // or "Beep now" never cuts the tone off mid-wave.
    public sealed class BeepScheduler
    {
        readonly float[] beep;
        readonly long interval;
        long position;
        long nextBeep;
        int beepPos = -1;
        bool wasPaused;

        public BeepScheduler(float[] scaledBeep, long intervalFrames)
        {
            beep = scaledBeep;
            interval = Math.Max(1, intervalFrames);
        }

        public long Position { get { return position; } }
        public bool InBeep { get { return beepPos >= 0; } }

        // Adds the tone into buffer[0..count). Returns the index where a beep started, or -1.
        public int Mix(float[] buffer, int count, bool paused, bool force)
        {
            int started = -1;
            if (paused)
            {
                wasPaused = true;
            }
            else
            {
                if (wasPaused)
                {
                    wasPaused = false;
                    nextBeep = position;
                }
                if (force && beepPos < 0) nextBeep = position;
            }
            for (int i = 0; i < count; i++)
            {
                if (beepPos < 0 && !paused && position >= nextBeep)
                {
                    beepPos = 0;
                    nextBeep = position + interval;
                    if (started < 0) started = i;
                }
                if (beepPos >= 0)
                {
                    buffer[i] += beep[beepPos];
                    beepPos++;
                    if (beepPos >= beep.Length) beepPos = -1;
                }
                position++;
            }
            return started;
        }
    }

    // Holds microphone samples between two devices whose clocks run at slightly different
    // speeds. A small resampling correction keeps the backlog near the target instead of
    // letting it grow until speech is dropped, or drain until the output clicks.
    public sealed class MixCore
    {
        readonly float[] ring;
        readonly long mask;
        readonly double nominalStep;
        readonly int captureRate;
        readonly int targetFill;
        readonly int maxFill;
        long writeIndex = 1;
        long readBase = 1;
        double frac;
        double avgFill;
        double integral;
        double correction;
        bool primed;
        float tail;

        public long Underruns;
        public long Drops;
        public double MinFillSeen = double.MaxValue;
        public double MaxFillSeen;

        const double Kp = 0.2;
        const double Ki = 0.011;
        const double MaxCorrection = 0.005;
        const double AverageSeconds = 1.0;

        public MixCore(int captureRate, int renderRate, int targetMs, int maxMs)
        {
            this.captureRate = captureRate;
            nominalStep = captureRate / (double)renderRate;
            targetFill = Math.Max(16, captureRate * targetMs / 1000);
            maxFill = Math.Max(targetFill * 2, captureRate * maxMs / 1000);
            int capacity = 1;
            while (capacity < captureRate * 2 + maxFill) capacity <<= 1;
            ring = new float[capacity];
            mask = capacity - 1;
            avgFill = targetFill;
        }

        public int TargetFill { get { return targetFill; } }
        public double Fill { get { return writeIndex - readBase - frac; } }
        public double CorrectionPpm { get { return correction * 1e6; } }
        public bool Primed { get { return primed; } }

        public void Push(float[] source, int count)
        {
            MakeRoom(count);
            for (int i = 0; i < count; i++)
            {
                ring[writeIndex & mask] = source[i];
                writeIndex++;
            }
        }

        public void PushSilence(int count)
        {
            MakeRoom(count);
            for (int i = 0; i < count; i++)
            {
                ring[writeIndex & mask] = 0;
                writeIndex++;
            }
        }

        void MakeRoom(int count)
        {
            // Output stopped pulling. Keep the newest audio rather than overwrite unread samples.
            if (writeIndex + count - (readBase - 1) >= ring.Length - 4)
            {
                readBase = writeIndex + count - targetFill;
                frac = 0;
                Drops++;
            }
        }

        public void Read(float[] dest, int count)
        {
            double fill = Fill;
            if (fill < MinFillSeen && primed) MinFillSeen = fill;
            if (fill > MaxFillSeen) MaxFillSeen = fill;

            if (primed && fill > maxFill)
            {
                readBase = writeIndex - targetFill;
                frac = 0;
                fill = targetFill;
                Drops++;
            }

            double dt = count / (double)captureRate / nominalStep;
            double alpha = 1.0 - Math.Exp(-dt / AverageSeconds);
            avgFill += (fill - avgFill) * alpha;
            if (primed)
            {
                double error = (avgFill - targetFill) / captureRate;
                integral += error * dt;
                double c = Kp * error + Ki * integral;
                if (c > MaxCorrection) { c = MaxCorrection; integral -= error * dt; }
                else if (c < -MaxCorrection) { c = -MaxCorrection; integral -= error * dt; }
                correction = c;
            }
            double step = nominalStep * (1.0 + correction);

            for (int i = 0; i < count; i++)
            {
                if (!primed)
                {
                    // Start only once there is the normal backlog plus what this call hands out, so the
                    // backlog is still there afterwards. Anything older is skipped, so a stall does not
                    // leave extra delay behind.
                    long keep = targetFill + (long)Math.Ceiling((count - i) * step) + 3;
                    if (writeIndex - readBase >= keep)
                    {
                        readBase = writeIndex - keep;
                        frac = 0;
                        primed = true;
                        avgFill = targetFill;
                    }
                    else
                    {
                        tail *= 0.97f;
                        dest[i] = tail;
                        continue;
                    }
                }
                if (writeIndex - readBase < 3)
                {
                    primed = false;
                    Underruns++;
                    tail *= 0.97f;
                    dest[i] = tail;
                    continue;
                }
                float xm1 = ring[(readBase - 1) & mask];
                float x0 = ring[readBase & mask];
                float x1 = ring[(readBase + 1) & mask];
                float x2 = ring[(readBase + 2) & mask];
                float t = (float)frac;
                float c1 = 0.5f * (x1 - xm1);
                float c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
                float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
                float y = ((c3 * t + c2) * t + c1) * t + x0;
                dest[i] = y;
                tail = y;
                frac += step;
                int advance = (int)frac;
                readBase += advance;
                frac -= advance;
            }
        }

        public void ResetStats()
        {
            Underruns = 0;
            Drops = 0;
            MinFillSeen = double.MaxValue;
            MaxFillSeen = 0;
        }
    }

    public static class SoftLimiter
    {
        public static void Apply(float[] buffer, int count)
        {
            const float knee = 0.8f;
            for (int i = 0; i < count; i++)
            {
                float x = buffer[i];
                float ax = x < 0 ? -x : x;
                if (ax <= knee) continue;
                float excess = ax - knee;
                float y = knee + (1f - knee) * (float)Math.Tanh(excess / (1f - knee));
                if (y > 0.99f) y = 0.99f;
                buffer[i] = x < 0 ? -y : y;
            }
        }
    }

    // Hann-windowed Goertzel filters at the tone frequency and at 100 Hz and 200 Hz either side.
    // A beep is narrow, so it stands well above every neighbour. Voiced speech is a comb of
    // harmonics 100-250 Hz apart, so at least one neighbour usually sits on a harmonic too.
    public sealed class ToneDetector
    {
        static readonly double[] Offsets = new double[] { -200, -100, 100, 200 };
        readonly int blockSize;
        readonly double[] window;
        readonly double coeff;
        readonly double[] neighbourCoeff = new double[4];
        double s1, s2;
        readonly double[] n1 = new double[4];
        readonly double[] n2 = new double[4];
        int n;

        public double LastAmplitude;
        public double LastNeighbourRatio;
        public readonly double BlockSeconds;

        public ToneDetector(int sampleRate, double frequencyHz, int blockMs)
        {
            blockSize = Math.Max(32, sampleRate * blockMs / 1000);
            BlockSeconds = blockSize / (double)sampleRate;
            window = new double[blockSize];
            for (int i = 0; i < blockSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (blockSize - 1));
            coeff = 2.0 * Math.Cos(2.0 * Math.PI * frequencyHz / sampleRate);
            for (int k = 0; k < 4; k++)
                neighbourCoeff[k] = 2.0 * Math.Cos(2.0 * Math.PI * (frequencyHz + Offsets[k]) / sampleRate);
        }

        // Returns true when a block has completed.
        public bool Feed(float sample)
        {
            double x = sample * window[n];
            double s0 = x + coeff * s1 - s2; s2 = s1; s1 = s0;
            for (int k = 0; k < 4; k++)
            {
                double v = x + neighbourCoeff[k] * n1[k] - n2[k];
                n2[k] = n1[k];
                n1[k] = v;
            }
            n++;
            if (n < blockSize) return false;
            double p = Power(s1, s2, coeff);
            double worst = 0;
            for (int k = 0; k < 4; k++)
            {
                double pk = Power(n1[k], n2[k], neighbourCoeff[k]);
                if (pk > worst) worst = pk;
                n1[k] = n2[k] = 0;
            }
            // The Hann window has a coherent gain of one half.
            LastAmplitude = 4.0 * Math.Sqrt(Math.Max(0, p)) / blockSize;
            LastNeighbourRatio = worst <= 1e-24 ? (p > 1e-24 ? 1e6 : 0) : p / worst;
            s1 = s2 = 0;
            n = 0;
            return true;
        }

        static double Power(double a, double b, double c)
        {
            return a * a + b * b - c * a * b;
        }
    }

    // Groups detector blocks into tone bursts the length of a beep.
    public sealed class BeepBurstFinder
    {
        readonly int minBlocks;
        readonly int maxBlocks;
        int run;
        double runStart;
        double runPeak;

        public BeepBurstFinder(double blockSeconds, double minSeconds, double maxSeconds)
        {
            minBlocks = Math.Max(1, (int)Math.Round(minSeconds / blockSeconds));
            maxBlocks = Math.Max(minBlocks, (int)Math.Round(maxSeconds / blockSeconds));
        }

        // Feed one block. Returns true when a finished burst qualifies as a beep.
        public bool Block(bool tone, double timeSeconds, double amplitude, out double beepStart, out double beepPeak)
        {
            beepStart = 0;
            beepPeak = 0;
            if (tone)
            {
                if (run == 0) { runStart = timeSeconds; runPeak = 0; }
                run++;
                if (amplitude > runPeak) runPeak = amplitude;
                return false;
            }
            bool qualifies = run >= minBlocks && run <= maxBlocks;
            if (qualifies)
            {
                beepStart = runStart;
                beepPeak = runPeak;
            }
            run = 0;
            return qualifies;
        }
    }

    public sealed class RecordingResult
    {
        public string Path;
        public double DurationSeconds;
        public int BeepCount;
        public double FirstBeepSeconds = -1;
        public double MaxGapSeconds;
        public double MaxGapAtSeconds;
        public bool Pass;
        public string Error;
    }

    // Checks a WAV recording for a beep at least every maxGapSeconds, including the stretch
    // before the first beep and after the last one.
    public static class RecordingChecker
    {
        const double MinToneDbfs = -70;
        const double MinNeighbourRatio = 10;

        public static RecordingResult CheckFile(string path, double frequencyHz, double maxGapSeconds)
        {
            return CheckFile(path, frequencyHz, maxGapSeconds, delegate (string p) { return new WavReader(p); });
        }

        // open lets callers add formats, such as MP3 through Media Foundation.
        public static RecordingResult CheckFile(string path, double frequencyHz, double maxGapSeconds, Func<string, IFrameSource> open)
        {
            var result = new RecordingResult();
            result.Path = path;
            try
            {
                using (IFrameSource reader = open(path))
                {
                    int channels = reader.Channels;
                    var detectors = new ToneDetector[channels];
                    var finders = new BeepBurstFinder[channels];
                    var blockCounts = new long[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        detectors[c] = new ToneDetector(reader.SampleRate, frequencyHz, 20);
                        finders[c] = new BeepBurstFinder(detectors[c].BlockSeconds, 0.1, 0.4);
                    }
                    double minAmplitude = Math.Pow(10.0, MinToneDbfs / 20.0) * Math.Sqrt(2.0);
                    var beeps = new List<double>();
                    var frame = new float[channels];
                    long frames = 0;
                    while (reader.ReadFrame(frame))
                    {
                        for (int c = 0; c < channels; c++)
                        {
                            ToneDetector d = detectors[c];
                            if (!d.Feed(frame[c])) continue;
                            bool tone = d.LastAmplitude >= minAmplitude && d.LastNeighbourRatio >= MinNeighbourRatio;
                            double start, peak;
                            double time = blockCounts[c] * d.BlockSeconds;
                            blockCounts[c]++;
                            if (finders[c].Block(tone, time, d.LastAmplitude, out start, out peak)) beeps.Add(start);
                        }
                        frames++;
                    }
                    for (int c = 0; c < channels; c++)
                    {
                        double start, peak;
                        if (finders[c].Block(false, blockCounts[c] * detectors[c].BlockSeconds, 0, out start, out peak)) beeps.Add(start);
                    }
                    result.DurationSeconds = frames / (double)reader.SampleRate;
                    beeps.Sort();
                    var merged = new List<double>();
                    foreach (double b in beeps)
                    {
                        if (merged.Count == 0 || b - merged[merged.Count - 1] > 1.0) merged.Add(b);
                    }
                    result.BeepCount = merged.Count;
                    if (merged.Count > 0) result.FirstBeepSeconds = merged[0];
                    double last = 0;
                    foreach (double b in merged)
                    {
                        if (b - last > result.MaxGapSeconds) { result.MaxGapSeconds = b - last; result.MaxGapAtSeconds = last; }
                        last = b;
                    }
                    if (result.DurationSeconds - last > result.MaxGapSeconds)
                    {
                        result.MaxGapSeconds = result.DurationSeconds - last;
                        result.MaxGapAtSeconds = last;
                    }
                    result.Pass = result.MaxGapSeconds <= maxGapSeconds;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Pass = false;
            }
            return result;
        }
    }

    // Decoded audio, one frame (one sample per channel) at a time.
    public interface IFrameSource : IDisposable
    {
        int SampleRate { get; }
        int Channels { get; }
        bool ReadFrame(float[] frame);
    }

    // Reads PCM 8/16/24/32-bit, IEEE float, A-law and mu-law WAV files, one frame at a time.
    public sealed class WavReader : IFrameSource
    {
        readonly FileStream stream;
        readonly BinaryReader reader;
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        readonly int bits;
        readonly int format;
        readonly int blockAlign;
        long remaining;
        readonly byte[] block;

        public WavReader(string path)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            reader = new BinaryReader(stream);
            if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV file");
            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAV file");
            bool haveFormat = false;
            while (stream.Position + 8 <= stream.Length)
            {
                string id = new string(reader.ReadChars(4));
                long size = reader.ReadUInt32();
                long next = stream.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    format = reader.ReadInt16();
                    Channels = reader.ReadInt16();
                    SampleRate = reader.ReadInt32();
                    reader.ReadInt32();
                    blockAlign = reader.ReadInt16();
                    bits = reader.ReadInt16();
                    if (format == unchecked((short)0xFFFE) || format == -2)
                    {
                        reader.ReadInt16();
                        reader.ReadInt16();
                        reader.ReadInt32();
                        format = reader.ReadInt16();
                    }
                    haveFormat = true;
                }
                else if (id == "data")
                {
                    if (!haveFormat) throw new InvalidDataException("data before format");
                    remaining = Math.Min(size, stream.Length - stream.Position);
                    break;
                }
                stream.Position = next;
            }
            if (!haveFormat) throw new InvalidDataException("no format chunk");
            if (Channels < 1 || SampleRate < 1000 || blockAlign < 1) throw new InvalidDataException("bad format chunk");
            if (format != 1 && format != 3 && format != 6 && format != 7)
                throw new InvalidDataException("unsupported WAV encoding " + format + "; convert the recording to PCM WAV first");
            block = new byte[blockAlign];
        }

        public bool ReadFrame(float[] frame)
        {
            if (remaining < blockAlign) return false;
            int got = reader.Read(block, 0, blockAlign);
            if (got < blockAlign) return false;
            remaining -= blockAlign;
            int bytes = Math.Max(1, bits / 8);
            for (int c = 0; c < Channels; c++)
            {
                int at = c * bytes;
                float v = 0;
                if (format == 7) v = MuLaw(block[at]);
                else if (format == 6) v = ALaw(block[at]);
                else if (format == 3 && bits == 32) v = BitConverter.ToSingle(block, at);
                else if (format == 3 && bits == 64) v = (float)BitConverter.ToDouble(block, at);
                else if (bits == 8) v = (block[at] - 128) / 128f;
                else if (bits == 16) v = BitConverter.ToInt16(block, at) / 32768f;
                else if (bits == 24) v = ((block[at] << 8 | block[at + 1] << 16 | block[at + 2] << 24) >> 8) / 8388608f;
                else if (bits == 32) v = BitConverter.ToInt32(block, at) / 2147483648f;
                frame[c] = v;
            }
            return true;
        }

        public static float MuLaw(byte value)
        {
            int u = ~value & 0xFF;
            int sign = u & 0x80;
            int exponent = (u >> 4) & 0x07;
            int mantissa = u & 0x0F;
            int sample = ((mantissa << 3) + 0x84) << exponent;
            sample -= 0x84;
            return (sign != 0 ? -sample : sample) / 32768f;
        }

        public static float ALaw(byte value)
        {
            int a = value ^ 0x55;
            int sign = a & 0x80;
            int exponent = (a >> 4) & 0x07;
            int mantissa = a & 0x0F;
            int sample = exponent == 0 ? (mantissa << 4) + 8 : ((mantissa << 4) + 0x108) << (exponent - 1);
            return (sign != 0 ? sample : -sample) / 32768f;
        }

        public void Dispose()
        {
            reader.Dispose();
            stream.Dispose();
        }
    }

    public static class SetupPassword
    {
        const int Iterations = 100000;

        public static string Hash(string password)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            byte[] hash = Derive(password, salt, Iterations);
            return "pbkdf2-sha256$" + Iterations.ToString(CultureInfo.InvariantCulture) + "$"
                + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
        }

        public static bool Verify(string password, string stored)
        {
            if (password == null || string.IsNullOrEmpty(stored)) return false;
            try
            {
                string[] parts = stored.Split('$');
                if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
                int iterations = int.Parse(parts[1], CultureInfo.InvariantCulture);
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);
                byte[] actual = Derive(password, salt, iterations);
                if (actual.Length != expected.Length) return false;
                int diff = 0;
                for (int i = 0; i < actual.Length; i++) diff |= actual[i] ^ expected[i];
                return diff == 0;
            }
            catch { return false; }
        }

        static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(32);
        }
    }

    public static class BeepEvents
    {
        public const string Source = "BeepTone";
        public const int Started = 1000;
        public const int AdminStopped = 1001;
        public const int AdminStarted = 1002;
        public const int Problem = 1100;
        public const int ProblemCleared = 1101;
        public const int Paused = 1200;
        public const int Resumed = 1201;
        public const int PauseTampered = 1202;
        public const int UserStopped = 1203;
        public const int UserStarted = 1204;
        public const int MicBypass = 1300;
        public const int MicBypassCleared = 1301;
        public const int SettingsSaved = 1400;
        public const int GuardLaunched = 1500;
        public const int GuardRestarted = 1501;
        public const int GuardBackoff = 1502;
        public const int CableInstalled = 1600;
        public const int CableInstallFailed = 1601;

        static int available = -1;

        public static bool Available
        {
            get
            {
                if (available < 0)
                {
                    try
                    {
                        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                            @"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + Source))
                            available = key != null ? 1 : 0;
                    }
                    catch { available = 0; }
                }
                return available == 1;
            }
        }

        public static void Write(int id, string message, bool warning)
        {
            if (!Available) return;
            try
            {
                string who = Environment.UserDomainName + "\\" + Environment.UserName;
                EventLog.WriteEntry(Source, message + Environment.NewLine + "User: " + who, warning
                    ? EventLogEntryType.Warning : EventLogEntryType.Information, id);
            }
            catch { }
        }
    }

    public static class BeepSelfTest
    {
        public static string[] RunAll()
        {
            var lines = new List<string>();
            Run(lines, "tone shape", TestSynth);
            Run(lines, "tone schedule", TestScheduler);
            Run(lines, "clock drift", TestDrift);
            Run(lines, "microphone stall", TestStall);
            Run(lines, "tone detection", TestDetector);
            Run(lines, "recording check", TestRecordingChecker);
            Run(lines, "heartbeat", TestHeartbeat);
            Run(lines, "watchdog", TestWatchdog);
            Run(lines, "settings limits", TestLimits);
            Run(lines, "password", TestPassword);
            return lines.ToArray();
        }

        delegate string TestFn();

        static void Run(List<string> lines, string name, TestFn fn)
        {
            try
            {
                string detail = fn();
                lines.Add((detail.StartsWith("FAIL") ? "" : "PASS ") + name + ": " + detail);
            }
            catch (Exception ex)
            {
                lines.Add("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message);
            }
        }

        static string Fail(List<string> errors)
        {
            return "FAIL " + string.Join("; ", errors.ToArray());
        }

        static string TestSynth()
        {
            var errors = new List<string>();
            int rate = 48000;
            float[] beep = BeepSynth.Create(rate, 1400, 200, 5);
            if (beep.Length != 9600) errors.Add("sample count " + beep.Length + " expected 9600");
            if (Math.Abs(beep[0]) > 0.05) errors.Add("attack did not start near silence");
            if (Math.Abs(beep[beep.Length - 1]) > 0.08) errors.Add("release did not end near silence");
            int start = (int)Math.Round(rate * 0.005);
            int end = beep.Length - start;
            int crossings = 0;
            for (int i = start + 1; i < end; i++)
            {
                if ((beep[i - 1] <= 0 && beep[i] > 0) || (beep[i - 1] >= 0 && beep[i] < 0)) crossings++;
            }
            double hz = (crossings / 2.0) / ((end - start) / (double)rate);
            if (hz < 1390 || hz > 1410) errors.Add("frequency " + hz.ToString("0.0", CultureInfo.InvariantCulture) + " Hz");
            var settings = new BeepSettings();
            settings.LevelDbfs = -20;
            float[] scaled = BeepSynth.Create(rate, settings);
            double sum = 0;
            int mid = scaled.Length / 2;
            for (int i = mid - 480; i < mid + 480; i++) sum += scaled[i] * scaled[i];
            double rmsDb = 10 * Math.Log10(sum / 960);
            if (Math.Abs(rmsDb + 20) > 0.3) errors.Add("level " + rmsDb.ToString("0.00", CultureInfo.InvariantCulture) + " dBFS, expected -20");
            if (errors.Count > 0) return Fail(errors);
            return beep.Length + " samples, 200.0 ms, " + hz.ToString("0.0", CultureInfo.InvariantCulture) + " Hz, level -20 dBFS";
        }

        static List<long> RunSchedule(BeepScheduler s, int rate, int seconds, int pauseFrom, int pauseTo, int forceAt)
        {
            var starts = new List<long>();
            var buffer = new float[480];
            long total = (long)rate * seconds;
            bool forced = false;
            while (s.Position < total)
            {
                double t = s.Position / (double)rate;
                bool paused = t >= pauseFrom && t < pauseTo;
                bool force = !forced && forceAt >= 0 && t >= forceAt;
                if (force) forced = true;
                Array.Clear(buffer, 0, buffer.Length);
                long before = s.Position;
                int at = s.Mix(buffer, buffer.Length, paused, force);
                if (at >= 0) starts.Add(before + at);
            }
            return starts;
        }

        static string TestScheduler()
        {
            var errors = new List<string>();
            int rate = 48000;
            float[] beep = BeepSynth.Create(rate, 1400, 200, 50);
            var plain = RunSchedule(new BeepScheduler(beep, 13L * rate), rate, 60, -1, -1, -1);
            string got = Seconds(plain, rate);
            if (got != "0,13,26,39,52") errors.Add("plain schedule " + got);
            var paused = RunSchedule(new BeepScheduler(beep, 13L * rate), rate, 60, 20, 30, -1);
            got = Seconds(paused, rate);
            if (got != "0,13,30,43,56") errors.Add("pause schedule " + got);
            var forced = RunSchedule(new BeepScheduler(beep, 13L * rate), rate, 30, -1, -1, 5);
            got = Seconds(forced, rate);
            if (got != "0,5,18") errors.Add("beep now schedule " + got);
            // "Beep now" during a beep must not restart it.
            var s = new BeepScheduler(beep, 13L * rate);
            var buf = new float[480];
            s.Mix(buf, buf.Length, false, false);
            int again = s.Mix(buf, buf.Length, false, true);
            if (again >= 0) errors.Add("beep now restarted a beep in progress");
            if (errors.Count > 0) return Fail(errors);
            return "every 13 s, pause resumes with a beep, beep now resets the schedule";
        }

        static string Seconds(List<long> starts, int rate)
        {
            var parts = new List<string>();
            foreach (long p in starts) parts.Add(((int)Math.Round(p / (double)rate)).ToString(CultureInfo.InvariantCulture));
            return string.Join(",", parts.ToArray());
        }

        // Simulates a microphone and an output device whose clocks differ by ppm.
        static string SimulateDrift(double ppm, double minutes, out bool ok)
        {
            int rate = 48000;
            var core = new MixCore(rate, rate, 30, 150);
            var rng = new Random(7);
            var packet = new float[480];
            var output = new float[2048];
            double capAccum = 0;
            double renderQueue = 0;
            int renderTarget = rate * 20 / 1000;
            double nextRenderEvent = 0;
            double warmup = 90;
            double totalMs = minutes * 60000;
            long underrunsAtWarmup = -1, dropsAtWarmup = -1;
            double correctionSum = 0;
            long correctionSamples = 0;
            for (int ms = 0; ms < totalMs; ms++)
            {
                double t = ms / 1000.0;
                capAccum += rate * (1 + ppm * 1e-6) / 1000.0;
                while (capAccum >= 480)
                {
                    for (int i = 0; i < 480; i++) packet[i] = (float)(0.1 * Math.Sin(i * 0.05));
                    core.Push(packet, 480);
                    capAccum -= 480;
                }
                renderQueue -= rate / 1000.0;
                if (renderQueue < 0) renderQueue = 0;
                if (ms >= nextRenderEvent)
                {
                    int want = renderTarget - (int)renderQueue;
                    if (want > 0)
                    {
                        core.Read(output, want);
                        renderQueue += want;
                    }
                    nextRenderEvent = ms + 10 + rng.Next(0, 3);
                    if (t >= warmup) { correctionSum += core.CorrectionPpm; correctionSamples++; }
                }
                if (underrunsAtWarmup < 0 && t >= warmup)
                {
                    underrunsAtWarmup = core.Underruns;
                    dropsAtWarmup = core.Drops;
                    core.MinFillSeen = double.MaxValue;
                    core.MaxFillSeen = 0;
                }
            }
            double minMs = core.MinFillSeen * 1000.0 / rate;
            double maxMs = core.MaxFillSeen * 1000.0 / rate;
            long lateUnderruns = core.Underruns - underrunsAtWarmup;
            long lateDrops = core.Drops - dropsAtWarmup;
            double meanCorrection = correctionSamples > 0 ? correctionSum / correctionSamples : 0;
            double correctionError = Math.Abs(meanCorrection - ppm);
            ok = lateUnderruns == 0 && lateDrops == 0 && minMs > 10 && maxMs < 55 && correctionError < 25;
            return ppm.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " ppm: backlog "
                + minMs.ToString("0", CultureInfo.InvariantCulture) + "-" + maxMs.ToString("0", CultureInfo.InvariantCulture)
                + " ms, correction " + meanCorrection.ToString("0", CultureInfo.InvariantCulture) + " ppm, "
                + lateUnderruns + " underruns, " + lateDrops + " drops";
        }

        static string TestDrift()
        {
            var results = new List<string>();
            bool all = true;
            foreach (double ppm in new double[] { -400, -100, 0, 100, 400, 1500 })
            {
                bool ok;
                results.Add(SimulateDrift(ppm, 20, out ok));
                if (!ok) all = false;
            }
            string text = string.Join("; ", results.ToArray());
            return all ? text : "FAIL " + text;
        }

        static string TestStall()
        {
            int rate = 48000;
            var core = new MixCore(rate, rate, 30, 150);
            var packet = new float[480];
            var output = new float[480];
            for (int i = 0; i < 100; i++) { core.Push(packet, 480); core.Read(output, 480); }
            for (int i = 0; i < 30; i++) core.Read(output, 480);
            if (core.Underruns == 0) return "FAIL a 300 ms stall did not register as an underrun";
            for (int i = 0; i < 40; i++) core.Push(packet, 480);
            core.Read(output, 480);
            double afterBurstMs = core.Fill * 1000.0 / rate;
            if (afterBurstMs > 40) return "FAIL a 400 ms burst left " + afterBurstMs.ToString("0", CultureInfo.InvariantCulture) + " ms of delay";
            for (int i = 0; i < 1000; i++) { core.Push(packet, 480); core.Read(output, 480); }
            double fillMs = core.Fill * 1000.0 / rate;
            if (!core.Primed || fillMs > 60) return "FAIL did not recover, backlog " + fillMs.ToString("0", CultureInfo.InvariantCulture) + " ms";
            return "fades out on a stall and recovers to " + fillMs.ToString("0", CultureInfo.InvariantCulture) + " ms backlog";
        }

        static float[] SpeechLike(int rate, double seconds, double levelDbfs, int seed)
        {
            var rng = new Random(seed);
            int n = (int)(rate * seconds);
            var x = new float[n];
            double amp = Math.Pow(10, levelDbfs / 20) * Math.Sqrt(2) / 4;
            double f0 = 140, phase = 0;
            for (int i = 0; i < n; i++)
            {
                if (i % (rate / 20) == 0) f0 = 100 + rng.NextDouble() * 150;
                phase += 2 * Math.PI * f0 / rate;
                double v = 0;
                for (int h = 1; h <= 12; h++) v += Math.Sin(phase * h) / h;
                double env = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * i / (double)rate);
                v = v * env * amp + (rng.NextDouble() - 0.5) * amp * 0.2;
                x[i] = (float)v;
            }
            return x;
        }

        static string TestDetector()
        {
            var errors = new List<string>();
            int rate = 48000;
            var settings = new BeepSettings();
            settings.LevelDbfs = -40;
            float[] beep = BeepSynth.Create(rate, settings);
            float[] voice = SpeechLike(rate, 30, -20, 3);
            var sched = new BeepScheduler(beep, 13L * rate);
            sched.Mix(voice, voice.Length, false, false);
            var det = new ToneDetector(rate, 1400, 20);
            var finder = new BeepBurstFinder(det.BlockSeconds, 0.08, 0.5);
            double threshold = settings.PeakGain() * Math.Pow(10, -15 / 20.0);
            int found = 0;
            long blocks = 0;
            double peak = 0;
            for (int i = 0; i < voice.Length; i++)
            {
                if (!det.Feed(voice[i])) continue;
                double s, p;
                if (finder.Block(det.LastAmplitude >= threshold && det.LastNeighbourRatio >= 4, blocks * det.BlockSeconds, det.LastAmplitude, out s, out p))
                {
                    found++;
                    peak = p;
                }
                blocks++;
            }
            if (found != 3) errors.Add("found " + found + " beeps under -20 dBFS speech, expected 3");
            double heardDb = 20 * Math.Log10(peak / Math.Sqrt(2));
            if (Math.Abs(heardDb + 40) > 1.5) errors.Add("measured " + heardDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS, expected -40");
            float[] quiet = SpeechLike(rate, 30, -20, 4);
            det = new ToneDetector(rate, 1400, 20);
            finder = new BeepBurstFinder(det.BlockSeconds, 0.08, 0.5);
            int falseHits = 0;
            blocks = 0;
            for (int i = 0; i < quiet.Length; i++)
            {
                if (!det.Feed(quiet[i])) continue;
                double s, p;
                if (finder.Block(det.LastAmplitude >= threshold && det.LastNeighbourRatio >= 4, blocks * det.BlockSeconds, det.LastAmplitude, out s, out p)) falseHits++;
                blocks++;
            }
            if (falseHits > 0) errors.Add(falseHits + " false beeps in speech without a tone");
            if (errors.Count > 0) return Fail(errors);
            return "3 of 3 beeps at -40 dBFS under -20 dBFS speech, measured " + heardDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS, no false hits";
        }

        static string TestRecordingChecker()
        {
            var errors = new List<string>();
            string dir = Path.Combine(Path.GetTempPath(), "BeepToneSelfTest-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            try
            {
                string good = Path.Combine(dir, "good.wav");
                string gap = Path.Combine(dir, "gap.wav");
                WriteTestRecording(good, 8000, 70, 13, -1, true);
                WriteTestRecording(gap, 16000, 70, 13, 26, false);
                RecordingResult r1 = RecordingChecker.CheckFile(good, 1400, 18);
                RecordingResult r2 = RecordingChecker.CheckFile(gap, 1400, 18);
                if (r1.Error != null) errors.Add("mu-law: " + r1.Error);
                else if (!r1.Pass || r1.BeepCount != 6) errors.Add("mu-law file: " + r1.BeepCount + " beeps, max gap " + r1.MaxGapSeconds.ToString("0.0", CultureInfo.InvariantCulture));
                if (r2.Error != null) errors.Add("pcm: " + r2.Error);
                else if (r2.Pass || Math.Abs(r2.MaxGapSeconds - 26) > 0.5) errors.Add("gap file: pass=" + r2.Pass + ", max gap " + r2.MaxGapSeconds.ToString("0.0", CultureInfo.InvariantCulture));
                if (errors.Count > 0) return Fail(errors);
                return "8 kHz mu-law passes with 6 beeps; 16 kHz PCM with a missing beep fails with a "
                    + r2.MaxGapSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s gap";
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        internal static void WriteTestRecording(string path, int rate, int seconds, int interval, int skipBeepAt, bool muLaw)
        {
            float[] voice = SpeechLike(rate, seconds, -18, 11);
            var settings = new BeepSettings();
            settings.LevelDbfs = -30;
            float[] beep = BeepSynth.Create(rate, settings);
            for (int t = 0; t < seconds; t += interval)
            {
                if (t == skipBeepAt) continue;
                int at = t * rate;
                for (int i = 0; i < beep.Length && at + i < voice.Length; i++) voice[at + i] += beep[i];
            }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int bytesPerSample = muLaw ? 1 : 2;
                int dataBytes = voice.Length * 2 * bytesPerSample;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)(muLaw ? 7 : 1));
                w.Write((short)2);
                w.Write(rate);
                w.Write(rate * 2 * bytesPerSample);
                w.Write((short)(2 * bytesPerSample));
                w.Write((short)(8 * bytesPerSample));
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                for (int i = 0; i < voice.Length; i++)
                {
                    // Agent on the left with the beep, customer noise on the right.
                    float left = voice[i];
                    float right = (float)(0.01 * Math.Sin(i * 0.3));
                    if (muLaw) { w.Write(EncodeMuLaw(left)); w.Write(EncodeMuLaw(right)); }
                    else { w.Write(ToPcm16(left)); w.Write(ToPcm16(right)); }
                }
            }
        }

        static short ToPcm16(float v)
        {
            double s = Math.Round(v * 32767.0);
            if (s > 32767) s = 32767;
            if (s < -32768) s = -32768;
            return (short)s;
        }

        static byte EncodeMuLaw(float v)
        {
            int sample = ToPcm16(v);
            int sign = (sample >> 8) & 0x80;
            if (sign != 0) sample = -sample;
            if (sample > 32635) sample = 32635;
            sample += 0x84;
            int exponent = 7;
            for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
            int mantissa = (sample >> (exponent + 3)) & 0x0F;
            return (byte)~(sign | (exponent << 4) | mantissa);
        }

        static string TestHeartbeat()
        {
            var info = new HeartbeatInfo();
            info.TimestampUtc = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
            info.ProcessId = 1234;
            info.LastBeepUtc = info.TimestampUtc.AddSeconds(-5);
            info.State = HeartbeatState.Paused;
            HeartbeatInfo back = HeartbeatInfo.Parse(info.Format());
            if (back == null || back.ProcessId != 1234 || back.State != HeartbeatState.Paused || back.LastBeepUtc != info.LastBeepUtc)
                return "FAIL round trip of " + info.Format();
            HeartbeatInfo old = HeartbeatInfo.Parse("2026-09-22T12:00:00.0000000Z 55 -");
            if (old == null || old.ProcessId != 55 || old.State != HeartbeatState.Running) return "FAIL old three-field heartbeat";
            if (HeartbeatInfo.Parse("") != null || HeartbeatInfo.Parse("2026-09-22T12:00") != null) return "FAIL partial file was accepted";
            return "round trip, old format, and partial file";
        }

        static string TestWatchdog()
        {
            var errors = new List<string>();
            DateTime now = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
            Func<double, string, double, HeartbeatInfo> hb = delegate (double ageSeconds, string state, double beepAge)
            {
                var h = new HeartbeatInfo();
                h.TimestampUtc = now.AddSeconds(-ageSeconds);
                h.ProcessId = 1;
                h.State = state;
                h.LastBeepUtc = beepAge < 0 ? DateTime.MinValue : now.AddSeconds(-beepAge);
                return h;
            };
            string reason;
            Check(errors, "dead process", WatchdogPolicy.Decide(null, false, 0, now, 13, false, out reason), WatchdogAction.Start);
            Check(errors, "dead but just launched", WatchdogPolicy.Decide(null, false, 0, now, 13, true, out reason), WatchdogAction.None);
            Check(errors, "healthy", WatchdogPolicy.Decide(hb(1, "running", 5), true, 600, now, 13, false, out reason), WatchdogAction.None);
            Check(errors, "hung", WatchdogPolicy.Decide(hb(45, "running", 50), true, 600, now, 13, false, out reason), WatchdogAction.Restart);
            Check(errors, "no beep", WatchdogPolicy.Decide(hb(1, "running", 60), true, 600, now, 13, false, out reason), WatchdogAction.Restart);
            Check(errors, "no beep yet, young", WatchdogPolicy.Decide(hb(1, "running", -1), true, 20, now, 13, false, out reason), WatchdogAction.None);
            Check(errors, "missing microphone", WatchdogPolicy.Decide(hb(1, "reconnecting", -1), true, 600, now, 13, false, out reason), WatchdogAction.None);
            Check(errors, "paused", WatchdogPolicy.Decide(hb(1, "paused", 400), true, 600, now, 13, false, out reason), WatchdogAction.None);
            Check(errors, "waiting for setup", WatchdogPolicy.Decide(hb(1, "setup", -1), true, 600, now, 13, false, out reason), WatchdogAction.None);
            Check(errors, "paused but hung", WatchdogPolicy.Decide(hb(45, "paused", 400), true, 600, now, 13, false, out reason), WatchdogAction.Restart);
            if (errors.Count > 0) return Fail(errors);
            return "10 cases";
        }

        static void Check(List<string> errors, string name, WatchdogAction got, WatchdogAction want)
        {
            if (got != want) errors.Add(name + " gave " + got + ", expected " + want);
        }

        static string TestLimits()
        {
            var s = new BeepSettings();
            s.FrequencyHz = 20000;
            s.DurationMs = 5000;
            s.IntervalSeconds = 3600;
            s.RampMs = 0;
            s.LevelDbfs = -200;
            BeepSettings c = s.Clamped();
            if (c.FrequencyHz != 1540 || c.DurationMs != 250 || c.IntervalSeconds != 15 || c.RampMs != 5 || c.LevelDbfs != -90)
                return "FAIL out-of-range values were not limited";
            s.FrequencyHz = double.NaN;
            s.LevelDbfs = double.PositiveInfinity;
            c = s.Clamped();
            if (c.FrequencyHz != 1400 || c.LevelDbfs != -6) return "FAIL NaN or infinity was not replaced";
            return "out-of-range, NaN and infinity values are limited";
        }

        static string TestPassword()
        {
            string stored = SetupPassword.Hash("correct horse");
            if (!SetupPassword.Verify("correct horse", stored)) return "FAIL right password rejected";
            if (SetupPassword.Verify("Correct horse", stored)) return "FAIL wrong password accepted";
            if (SetupPassword.Verify("x", "garbage")) return "FAIL malformed hash accepted";
            return "hash and verify";
        }
    }
}
