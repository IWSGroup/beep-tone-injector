# Beep Tone Injector
# Mixes a 1400 Hz recording beep into the microphone and sends it to a virtual cable.
# Runs as the signed-in user. Elevates only to install VB-Cable or to set the admin stop flag.

[CmdletBinding()]
param(
    [switch]$ListDevices,
    [switch]$SelfTest,
    [switch]$Watchdog,
    [switch]$Stop,
    [switch]$Start,
    [switch]$NoRelaunch
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:DataDir = Join-Path $env:LOCALAPPDATA 'BeepTone'
$script:ConfigPath = Join-Path $script:DataDir 'config.json'
$script:LogPath = Join-Path $script:DataDir 'beep-tone.log'
$script:HeartbeatPath = Join-Path $script:DataDir 'heartbeat.txt'
$script:CableDir = Join-Path $script:DataDir 'VBCABLE'
$script:TaskName = 'BeepTone'
$script:WatchdogTaskName = 'BeepTone Watchdog'
$script:CablePackUrl = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip'
$script:Mutex = $null
$script:MixerStarted = $false
$script:IconBitmaps = @()
$script:LocalBeepPlayer = $null
$script:LoggedRemoteSkip = $false
$script:LoggedMissingCable = $false
$script:AlertText = $null
$script:AlertShownAt = [datetime]::MinValue
$script:DefaultMicCheckedAt = [datetime]::MinValue
$script:SetupPassword = 'SuperSecretPassword!'
$script:PausePath = Join-Path $script:DataDir 'pause-until.txt'
$script:PauseMinutes = 15
$script:SuppressAlertUntilBeep = $false

$csharp = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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

        public static DateTime LastBeepUtc = DateTime.MinValue;

        public static void Heartbeat()
        {
            try
            {
                if (string.IsNullOrEmpty(HeartbeatPath)) return;
                lock (Gate)
                {
                    string dir = Path.GetDirectoryName(HeartbeatPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    string beep = LastBeepUtc == DateTime.MinValue
                        ? "-"
                        : LastBeepUtc.ToString("o", CultureInfo.InvariantCulture);
                    string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                        + " " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                        + " " + beep;
                    File.WriteAllText(HeartbeatPath, line, Encoding.ASCII);
                }
            }
            catch { }
        }
    }

    public sealed class BeepSettings
    {
        public double FrequencyHz = 1400;
        public int DurationMs = 200;
        public int IntervalSeconds = 13;
        public int RampMs = 50;
        public double MinBeepDbfs = -6;
        public double NoiseGateDbfs = -45;
        public double LevelSmoothingSeconds = 3;
        public double MaxBeepPeak = 0.85;
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

        public static string SelfTest()
        {
            var errors = new List<string>();
            int rate = 48000;
            float[] beep = Create(rate, 1400, 200, 5);
            if (beep.Length != 9600) errors.Add("sample count " + beep.Length + " expected 9600");
            double durationMs = beep.Length * 1000.0 / rate;
            if (durationMs < 170 || durationMs > 250) errors.Add("duration " + durationMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            if (Math.Abs(beep[0]) > 0.05) errors.Add("attack did not start near silence");
            if (Math.Abs(beep[beep.Length - 1]) > 0.08) errors.Add("release did not end near silence");

            int start = (int)Math.Round(rate * 0.005);
            int end = beep.Length - start;
            int crossings = 0;
            for (int i = start + 1; i < end; i++)
            {
                if ((beep[i - 1] <= 0 && beep[i] > 0) || (beep[i - 1] >= 0 && beep[i] < 0))
                    crossings++;
            }
            double seconds = (end - start) / (double)rate;
            double hz = seconds > 0 ? (crossings / 2.0) / seconds : 0;
            if (hz < 1390 || hz > 1410)
                errors.Add("frequency " + hz.ToString("0.0", CultureInfo.InvariantCulture) + " Hz");

            int interval = 13 * rate;
            if (interval != 624000) errors.Add("interval samples " + interval);

            float peak = 0;
            for (int i = start; i < end; i++)
            {
                float a = Math.Abs(beep[i]);
                if (a > peak) peak = a;
            }
            if (peak < 0.95f || peak > 1.01f) errors.Add("peak " + peak.ToString("0.000", CultureInfo.InvariantCulture));

            if (errors.Count == 0)
                return "PASS " + beep.Length + " samples, " + durationMs.ToString("0.0", CultureInfo.InvariantCulture)
                    + " ms, " + hz.ToString("0.0", CultureInfo.InvariantCulture) + " Hz, interval " + interval + " samples";
            return "FAIL " + string.Join("; ", errors.ToArray());
        }
    }

    public sealed class AudioEndpoint
    {
        public string Id;
        public string Name;
        public string Flow;
        public override string ToString() { return Name ?? ""; }
    }

    public static class AudioDevices
    {
        public static AudioEndpoint[] List(string flow)
        {
            int dataFlow = string.Equals(flow, "Render", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            var list = new List<AudioEndpoint>();
            WasapiNative.ComInit();
            IntPtr enumeratorPtr;
            var enumId = typeof(IMMDeviceEnumerator).GUID;
            var clsid = WasapiNative.ClsidEnumerator;
            int hr = WasapiNative.CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref enumId, out enumeratorPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                var enumerator = (IMMDeviceEnumerator)Marshal.GetTypedObjectForIUnknown(enumeratorPtr, typeof(IMMDeviceEnumerator));
                IntPtr collectionPtr;
                hr = enumerator.EnumAudioEndpoints(dataFlow, 1, out collectionPtr);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var collection = (IMMDeviceCollection)Marshal.GetTypedObjectForIUnknown(collectionPtr, typeof(IMMDeviceCollection));
                    int count;
                    hr = collection.GetCount(out count);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr devicePtr;
                        if (collection.Item(i, out devicePtr) < 0 || devicePtr == IntPtr.Zero) continue;
                        try
                        {
                            var device = (IMMDevice)Marshal.GetTypedObjectForIUnknown(devicePtr, typeof(IMMDevice));
                            var item = ReadEndpoint(device, flow);
                            if (item != null && !string.IsNullOrEmpty(item.Name)) list.Add(item);
                            Marshal.ReleaseComObject(device);
                        }
                        finally { Marshal.Release(devicePtr); }
                    }
                    Marshal.ReleaseComObject(collection);
                }
                finally { if (collectionPtr != IntPtr.Zero) Marshal.Release(collectionPtr); }
                Marshal.ReleaseComObject(enumerator);
            }
            finally { if (enumeratorPtr != IntPtr.Zero) Marshal.Release(enumeratorPtr); }
            return list.ToArray();
        }

        public static AudioEndpoint FindCapture(string namePart)
        {
            if (string.IsNullOrEmpty(namePart)) return null;
            AudioEndpoint[] all = List("Capture");
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Name.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (all[i].Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0) return all[i];
            }
            return null;
        }

        public static AudioEndpoint FindRender(string namePart)
        {
            if (string.IsNullOrEmpty(namePart)) return null;
            AudioEndpoint[] all = List("Render");
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0) return all[i];
            }
            return null;
        }

        public static AudioEndpoint FindCableOutput()
        {
            return FindCaptureAllowed("CABLE Output");
        }

        static AudioEndpoint FindCaptureAllowed(string namePart)
        {
            AudioEndpoint[] all = List("Capture");
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0) return all[i];
            }
            return null;
        }

        public static void SetDefaultMicrophone(string deviceId)
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            int[] roles = new int[] { 0, 1, 2 };
            for (int i = 0; i < roles.Length; i++)
            {
                int hr = policy.SetDefaultEndpoint(deviceId, roles[i]);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
        }

        public static AudioEndpoint GetDefaultEndpoint(string flow, int role)
        {
            WasapiNative.ComInit();
            int dataFlow = string.Equals(flow, "Render", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            Guid enumId = typeof(IMMDeviceEnumerator).GUID;
            Guid clsid = WasapiNative.ClsidEnumerator;
            IntPtr enumeratorPtr;
            int hr = WasapiNative.CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref enumId, out enumeratorPtr);
            if (hr < 0) return null;
            try
            {
                var enumerator = (IMMDeviceEnumerator)Marshal.GetTypedObjectForIUnknown(enumeratorPtr, typeof(IMMDeviceEnumerator));
                IntPtr devicePtr;
                hr = enumerator.GetDefaultAudioEndpoint(dataFlow, role, out devicePtr);
                Marshal.ReleaseComObject(enumerator);
                if (hr < 0 || devicePtr == IntPtr.Zero) return null;
                try
                {
                    var device = (IMMDevice)Marshal.GetTypedObjectForIUnknown(devicePtr, typeof(IMMDevice));
                    AudioEndpoint item = ReadEndpoint(device, flow);
                    Marshal.ReleaseComObject(device);
                    return item;
                }
                finally { Marshal.Release(devicePtr); }
            }
            finally { if (enumeratorPtr != IntPtr.Zero) Marshal.Release(enumeratorPtr); }
        }

        static AudioEndpoint ReadEndpoint(IMMDevice device, string flow)
        {
            IntPtr idPtr;
            if (device.GetId(out idPtr) < 0 || idPtr == IntPtr.Zero) return null;
            string id = Marshal.PtrToStringUni(idPtr);
            Marshal.FreeCoTaskMem(idPtr);
            IntPtr storePtr;
            if (device.OpenPropertyStore(0, out storePtr) < 0 || storePtr == IntPtr.Zero)
                return new AudioEndpoint { Id = id, Name = id, Flow = flow };
            try
            {
                var store = (IPropertyStore)Marshal.GetTypedObjectForIUnknown(storePtr, typeof(IPropertyStore));
                var key = WasapiNative.FriendlyNameKey;
                IntPtr pv = Marshal.AllocHGlobal(32);
                try
                {
                    for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                    string name = id;
                    if (store.GetValue(ref key, pv) >= 0 && Marshal.ReadInt16(pv, 0) == 31)
                    {
                        IntPtr str = Marshal.ReadIntPtr(pv, 8);
                        if (str != IntPtr.Zero) name = Marshal.PtrToStringUni(str);
                    }
                    WasapiNative.PropVariantClear(pv);
                    return new AudioEndpoint { Id = id, Name = name, Flow = flow };
                }
                finally { Marshal.FreeHGlobal(pv); Marshal.ReleaseComObject(store); }
            }
            finally { Marshal.Release(storePtr); }
        }
    }

    public sealed class BeepMixer
    {
        public static BeepMixer Current;

        public string CaptureName = "";
        public string RenderName = "CABLE Input";
        public BeepSettings Settings = new BeepSettings();

        int beepRequested;
        int restartRequested;
        int stopRequested;
        readonly object gate = new object();
        string status = "Starting";
        string balloon;
        string balloonTitle;
        DateTime lastBeepLocal = DateTime.MinValue;
        DateTime runningSince = DateTime.MinValue;
        int beepCount;
        string problem;
        bool sawFailure;
        bool running;

        public string Status { get { lock (gate) return status; } }
        public bool IsRunning { get { lock (gate) return running; } }
        public DateTime LastBeepLocal { get { lock (gate) return lastBeepLocal; } }
        public DateTime RunningSince { get { lock (gate) return runningSince; } }
        public int BeepCount { get { lock (gate) return beepCount; } }
        public string Problem { get { lock (gate) return problem ?? ""; } }

        int beepPaused;

        public void SetBeepPaused(bool paused) { Interlocked.Exchange(ref beepPaused, paused ? 1 : 0); }
        public bool IsBeepPaused { get { return Interlocked.CompareExchange(ref beepPaused, 0, 0) != 0; } }

        public void RequestBeep() { Interlocked.Exchange(ref beepRequested, 1); }
        public void RequestRestart() { Interlocked.Exchange(ref restartRequested, 1); }
        public void RequestStop() { Interlocked.Exchange(ref stopRequested, 1); }

        public string ConsumeBalloon(out string title)
        {
            lock (gate)
            {
                title = balloonTitle;
                string text = balloon;
                balloon = null;
                balloonTitle = null;
                return text;
            }
        }

        public void Start()
        {
            if (Current == null) Current = this;
            var thread = new Thread(Background);
            thread.IsBackground = true;
            thread.Name = "BeepToneMixer";
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        void SetStatus(string text)
        {
            lock (gate)
            {
                status = text;
                if (text == "Running") runningSince = DateTime.Now;
            }
        }

        void SetProblem(string text) { lock (gate) problem = text; }

        void SetBalloon(string title, string text)
        {
            lock (gate)
            {
                balloonTitle = title;
                balloon = text;
            }
        }

        void Background()
        {
            WasapiNative.ComInit();
            while (Interlocked.CompareExchange(ref stopRequested, 0, 0) == 0)
            {
                if (IsDisabled())
                {
                    SetStatus("Stopped by administrator");
                    BeepFiles.Log("admin disable flag is set; mixer exiting");
                    lock (gate) { running = false; }
                    return;
                }
                BeepFiles.Heartbeat();
                try
                {
                    RunSession();
                    if (Interlocked.CompareExchange(ref stopRequested, 0, 0) != 0) break;
                    SetStatus("Restarting");
                    BeepFiles.Log("mixer restarting with updated settings");
                }
                catch (Exception ex)
                {
                    if (Interlocked.CompareExchange(ref stopRequested, 0, 0) != 0) break;
                    lock (gate) running = false;
                    BeepFiles.Log("audio interrupted: " + ex.Message);
                    SetStatus("Reconnecting");
                    SetProblem("The beep is not going out on the call. " + ex.Message);
                    if (!sawFailure) sawFailure = true;
                    for (int i = 0; i < 4 && Interlocked.CompareExchange(ref stopRequested, 0, 0) == 0; i++)
                    {
                        Thread.Sleep(500);
                        BeepFiles.Heartbeat();
                    }
                }
            }
            lock (gate) running = false;
        }

        static bool IsDisabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\BeepTone"))
                {
                    if (key == null) return false;
                    object value = key.GetValue("Disabled");
                    if (value == null) return false;
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
                }
            }
            catch { return false; }
        }

        void RunSession()
        {
            string captureName = CaptureName;
            string renderName = RenderName;
            BeepSettings settings = Settings ?? new BeepSettings();
            AudioEndpoint capture = AudioDevices.FindCapture(captureName);
            if (capture == null) throw new InvalidOperationException("Microphone not found: " + captureName);
            AudioEndpoint render = AudioDevices.FindRender(renderName);
            if (render == null) throw new InvalidOperationException("Playback device not found: " + renderName);

            BeepFiles.Log("opening capture '" + capture.Name + "' and playback '" + render.Name + "'");
            using (var session = new MixSession(capture.Id, render.Id, settings))
            {
                session.Open();
                lock (gate) running = true;
                if (sawFailure)
                {
                    sawFailure = false;
                    BeepFiles.Log("audio recovered");
                }
                SetStatus("Running");
                session.Loop(this);
            }
        }

        internal bool ConsumeRestart()
        {
            return Interlocked.Exchange(ref restartRequested, 0) == 1;
        }

        internal bool StopRequested()
        {
            return Interlocked.CompareExchange(ref stopRequested, 0, 0) != 0;
        }

        internal bool ConsumeBeepRequest()
        {
            return Interlocked.Exchange(ref beepRequested, 0) == 1;
        }

        internal void NoteBeep(double speechDb, double toneDb)
        {
            lock (gate)
            {
                lastBeepLocal = DateTime.Now;
                beepCount++;
                problem = null;
            }
            BeepFiles.LastBeepUtc = DateTime.UtcNow;
            BeepFiles.Log("beep speech=" + speechDb.ToString("0.0", CultureInfo.InvariantCulture)
                + " dBFS tone=" + toneDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS");
        }

        internal void TouchDisabledCheck()
        {
            if (IsDisabled()) Interlocked.Exchange(ref stopRequested, 1);
        }
    }

    sealed class LevelTracker
    {
        double smoothed;
        bool hasSpeech;
        public double NoiseGateDbfs = -45;
        public double MinBeepDbfs = -18;
        public double MaxBeepPeak = 1;
        public double SmoothingSeconds = 3;

        public void Observe(float[] samples, int count, int sampleRate)
        {
            if (count <= 0 || sampleRate <= 0) return;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                double s = samples[i];
                sum += s * s;
            }
            double rms = Math.Sqrt(sum / count);
            double gate = Math.Pow(10.0, NoiseGateDbfs / 20.0);
            if (rms < gate) return;
            double dt = count / (double)sampleRate;
            double tau = SmoothingSeconds <= 0.05 ? 0.05 : SmoothingSeconds;
            double alpha = 1.0 - Math.Exp(-dt / tau);
            if (!hasSpeech)
            {
                smoothed = rms;
                hasSpeech = true;
            }
            else smoothed = smoothed + alpha * (rms - smoothed);
        }

        public double CurrentSpeechDb()
        {
            double floor = Math.Pow(10.0, MinBeepDbfs / 20.0);
            double rms = hasSpeech ? Math.Max(smoothed, floor) : floor;
            return 20.0 * Math.Log10(rms);
        }

        public float BeepPeak()
        {
            double floor = Math.Pow(10.0, MinBeepDbfs / 20.0);
            double rms = hasSpeech ? Math.Max(smoothed, floor) : floor;
            double peak = rms * Math.Sqrt(2.0);
            if (MaxBeepPeak > 0 && peak > MaxBeepPeak) peak = MaxBeepPeak;
            if (peak < 0.001) peak = 0.001;
            if (peak > 1f) peak = 1f;
            return (float)peak;
        }
    }

    sealed class SampleRing
    {
        readonly float[] data;
        int read;
        int write;
        int count;
        public SampleRing(int capacity) { data = new float[Math.Max(8, capacity)]; }
        public int Count { get { return count; } }
        public void Push(float sample)
        {
            if (count == data.Length)
            {
                read++;
                if (read == data.Length) read = 0;
                count--;
            }
            data[write] = sample;
            write++;
            if (write == data.Length) write = 0;
            count++;
        }
        public void TrimTo(int target)
        {
            while (count > target)
            {
                read++;
                if (read == data.Length) read = 0;
                count--;
            }
        }
        public bool TryPop(out float sample)
        {
            if (count == 0) { sample = 0; return false; }
            sample = data[read];
            read++;
            if (read == data.Length) read = 0;
            count--;
            return true;
        }
    }

    sealed class MixSession : IDisposable
    {
        readonly string captureId;
        readonly string renderId;
        readonly BeepSettings settings;
        IMMDevice captureDevice;
        IMMDevice renderDevice;
        IAudioClient captureClient;
        IAudioClient renderClient;
        IAudioCaptureClient capture;
        IAudioRenderClient render;
        FormatInfo captureFormat;
        FormatInfo renderFormat;
        int renderBufferFrames;
        bool started;

        public MixSession(string captureId, string renderId, BeepSettings settings)
        {
            this.captureId = captureId;
            this.renderId = renderId;
            this.settings = settings;
        }

        public void Open()
        {
            captureDevice = WasapiNative.OpenDevice(captureId);
            renderDevice = WasapiNative.OpenDevice(renderId);
            captureClient = WasapiNative.ActivateClient(captureDevice);
            renderClient = WasapiNative.ActivateClient(renderDevice);
            captureFormat = WasapiNative.InitializeCapture(captureClient);
            renderFormat = WasapiNative.InitializeRender(renderClient, captureFormat.Rate);
            uint buffer;
            int hr = renderClient.GetBufferSize(out buffer);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            renderBufferFrames = (int)buffer;
            capture = WasapiNative.GetCapture(captureClient);
            render = WasapiNative.GetRender(renderClient);
            WasapiNative.TryDisableDucking(renderDevice, WasapiNative.SessionGuid);
            BeepFiles.Log("capture " + captureFormat + "; render " + renderFormat + "; buffer " + renderBufferFrames + " frames");
        }

        public void Loop(BeepMixer mixer)
        {
            int rate = renderFormat.Rate;
            var beep = BeepSynth.Create(rate, settings.FrequencyHz, settings.DurationMs, settings.RampMs);
            int interval = rate * (settings.IntervalSeconds < 1 ? 1 : settings.IntervalSeconds);
            var levels = new LevelTracker();
            levels.NoiseGateDbfs = settings.NoiseGateDbfs;
            levels.MinBeepDbfs = settings.MinBeepDbfs;
            levels.MaxBeepPeak = settings.MaxBeepPeak;
            levels.SmoothingSeconds = settings.LevelSmoothingSeconds;
            var ring = new SampleRing(Math.Max(rate, 8000));
            int highWater = rate * 200 / 1000;
            int target = rate * 40 / 1000;
            int prime = rate * 30 / 1000;
            float last = 0;
            long rendered = 0;
            long nextBeep = 0;
            bool inBeep = false;
            int beepPos = 0;
            float beepGain = 0.1f;
            var scratch = new float[4096];
            int sinceCheck = 0;

            hrStart(captureClient);
            var primeWatch = Stopwatch.StartNew();
            while (ring.Count < prime && primeWatch.ElapsedMilliseconds < 500)
            {
                PumpCapture(ring, levels, scratch, highWater, target);
                if (ring.Count < prime) Thread.Sleep(5);
                BeepFiles.Heartbeat();
            }

            int prefill = renderBufferFrames;
            WriteRender(prefill, ring, ref last, ref rendered, ref nextBeep, ref inBeep, ref beepPos, ref beepGain, beep, interval, levels, mixer);
            hrStart(renderClient);
            started = true;

            while (true)
            {
                if (mixer.StopRequested()) return;
                if (mixer.ConsumeRestart()) return;
                sinceCheck += 10;
                if (sinceCheck >= 1000)
                {
                    sinceCheck = 0;
                    BeepFiles.Heartbeat();
                    mixer.TouchDisabledCheck();
                    if (mixer.StopRequested()) return;
                }
                PumpCapture(ring, levels, scratch, highWater, target);
                uint padding;
                int hr = renderClient.GetCurrentPadding(out padding);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                int want = renderBufferFrames - (int)padding;
                if (want > 0)
                {
                    if (want > scratch.Length) want = scratch.Length;
                    WriteRender(want, ring, ref last, ref rendered, ref nextBeep, ref inBeep, ref beepPos, ref beepGain, beep, interval, levels, mixer);
                }
                else Thread.Sleep(5);
            }
        }

        static void hrStart(IAudioClient client)
        {
            int hr = client.Start();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        void PumpCapture(SampleRing ring, LevelTracker levels, float[] scratch, int highWater, int target)
        {
            while (true)
            {
                int packet;
                int hr = capture.GetNextPacketSize(out packet);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                if (packet <= 0) break;
                IntPtr data;
                int frames;
                int flags;
                long devPos, qpcPos;
                hr = capture.GetBuffer(out data, out frames, out flags, out devPos, out qpcPos);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    bool silent = (flags & 2) != 0 || data == IntPtr.Zero;
                    int n = frames;
                    if (n > scratch.Length) n = scratch.Length;
                    if (silent)
                    {
                        for (int i = 0; i < frames; i++) ring.Push(0);
                    }
                    else
                    {
                        int offset = 0;
                        while (offset < frames)
                        {
                            int chunk = frames - offset;
                            if (chunk > scratch.Length) chunk = scratch.Length;
                            WasapiNative.Downmix(data, captureFormat, offset, chunk, scratch);
                            for (int i = 0; i < chunk; i++) ring.Push(scratch[i]);
                            levels.Observe(scratch, chunk, captureFormat.Rate);
                            offset += chunk;
                        }
                    }
                    if (ring.Count > highWater) ring.TrimTo(target);
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
            }
        }

        void WriteRender(int frames, SampleRing ring, ref float last, ref long rendered, ref long nextBeep,
            ref bool inBeep, ref int beepPos, ref float beepGain, float[] beep, int interval, LevelTracker levels, BeepMixer mixer)
        {
            IntPtr data;
            int hr = render.GetBuffer(frames, out data);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                int channels = renderFormat.Channels;
                int bytesPerSample = renderFormat.Bits / 8;
                int block = renderFormat.BlockAlign;
                var raw = new byte[frames * block];
                for (int i = 0; i < frames; i++)
                {
                    float voice;
                    if (!ring.TryPop(out voice)) voice = last;
                    else last = voice;

                    bool force = mixer.ConsumeBeepRequest();
                    if (mixer.IsBeepPaused)
                    {
                        if (inBeep) inBeep = false;
                        if (rendered >= nextBeep) nextBeep = rendered + interval;
                    }
                    else if (force || (!inBeep && rendered >= nextBeep))
                    {
                        inBeep = true;
                        beepPos = 0;
                        beepGain = levels.BeepPeak();
                        nextBeep = rendered + interval;
                        double speechDb = levels.CurrentSpeechDb();
                        double toneRms = beepGain / Math.Sqrt(2.0);
                        double toneDb = 20.0 * Math.Log10(toneRms <= 0 ? 0.00001 : toneRms);
                        mixer.NoteBeep(speechDb, toneDb);
                    }

                    float sample = voice;
                    if (inBeep)
                    {
                        float tone = 0;
                        if (beepPos < beep.Length) tone = beep[beepPos] * beepGain;
                        sample = SoftLimit(voice + tone);
                        beepPos++;
                        if (beepPos >= beep.Length) inBeep = false;
                    }

                    if (sample > 1f) sample = 1f;
                    else if (sample < -1f) sample = -1f;
                    WriteFrame(raw, i * block, channels, bytesPerSample, renderFormat.IsFloat, sample);
                    rendered++;
                }
                Marshal.Copy(raw, 0, data, raw.Length);
            }
            finally
            {
                render.ReleaseBuffer(frames, 0);
            }
        }

        static float SoftLimit(float x)
        {
            float ax = Math.Abs(x);
            const float knee = 0.8f;
            if (ax <= knee) return x;
            float sign = x < 0 ? -1f : 1f;
            float excess = ax - knee;
            float y = knee + (1f - knee) * (float)Math.Tanh(excess / (1f - knee));
            if (y > 0.99f) y = 0.99f;
            return sign * y;
        }

        static void WriteFrame(byte[] raw, int offset, int channels, int bytesPerSample, bool isFloat, float sample)
        {
            for (int c = 0; c < channels; c++)
            {
                int at = offset + c * bytesPerSample;
                if (isFloat)
                {
                    byte[] b = BitConverter.GetBytes(sample);
                    raw[at] = b[0]; raw[at + 1] = b[1]; raw[at + 2] = b[2]; raw[at + 3] = b[3];
                }
                else if (bytesPerSample == 2)
                {
                    short v = (short)Math.Round(sample * 32767.0);
                    raw[at] = (byte)(v & 0xFF);
                    raw[at + 1] = (byte)((v >> 8) & 0xFF);
                }
                else if (bytesPerSample == 4)
                {
                    int v = (int)Math.Round(sample * 2147483647.0);
                    raw[at] = (byte)(v & 0xFF);
                    raw[at + 1] = (byte)((v >> 8) & 0xFF);
                    raw[at + 2] = (byte)((v >> 16) & 0xFF);
                    raw[at + 3] = (byte)((v >> 24) & 0xFF);
                }
            }
        }

        public void Dispose()
        {
            try { if (started && captureClient != null) captureClient.Stop(); } catch { }
            try { if (started && renderClient != null) renderClient.Stop(); } catch { }
            Release(capture);
            Release(render);
            Release(captureClient);
            Release(renderClient);
            Release(captureDevice);
            Release(renderDevice);
        }

        static void Release(object com)
        {
            try { if (com != null) Marshal.ReleaseComObject(com); } catch { }
        }
    }

    sealed class FormatInfo
    {
        public int Rate;
        public int Channels;
        public int Bits;
        public bool IsFloat;
        public int BlockAlign;
        public override string ToString()
        {
            return Rate + " Hz " + Channels + " ch " + Bits + " bit " + (IsFloat ? "float" : "pcm");
        }
    }

    static class WasapiNative
    {
        public static readonly Guid ClsidEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid SessionGuid = new Guid("BEE7BEE7-1400-4000-8000-0000000BE001");
        public static readonly Guid IeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        public static PropertyKey FriendlyNameKey = new PropertyKey
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        const uint StreamFlagsCapture = 0x00080000;
        const uint StreamFlagsRender = 0x00080000 | 0x80000000 | 0x08000000;

        [DllImport("ole32.dll")]
        public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsctx, ref Guid iid, out IntPtr instance);

        [DllImport("ole32.dll")]
        static extern int CoInitializeEx(IntPtr reserved, int coinit);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(IntPtr pvar);

        public static void ComInit()
        {
            int hr = CoInitializeEx(IntPtr.Zero, 0);
            if (hr < 0 && hr != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(hr);
        }

        public static IMMDevice OpenDevice(string id)
        {
            Guid enumId = typeof(IMMDeviceEnumerator).GUID;
            Guid clsid = ClsidEnumerator;
            IntPtr enumeratorPtr;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref enumId, out enumeratorPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                var enumerator = (IMMDeviceEnumerator)Marshal.GetTypedObjectForIUnknown(enumeratorPtr, typeof(IMMDeviceEnumerator));
                IntPtr devicePtr;
                hr = enumerator.GetDevice(id, out devicePtr);
                Marshal.ReleaseComObject(enumerator);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                var device = (IMMDevice)Marshal.GetTypedObjectForIUnknown(devicePtr, typeof(IMMDevice));
                Marshal.Release(devicePtr);
                return device;
            }
            finally { Marshal.Release(enumeratorPtr); }
        }

        public static IAudioClient ActivateClient(IMMDevice device)
        {
            Guid iid = typeof(IAudioClient).GUID;
            IntPtr ptr;
            int hr = device.Activate(ref iid, 23, IntPtr.Zero, out ptr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            var client = (IAudioClient)Marshal.GetTypedObjectForIUnknown(ptr, typeof(IAudioClient));
            Marshal.Release(ptr);
            return client;
        }

        public static FormatInfo InitializeCapture(IAudioClient client)
        {
            IntPtr mix;
            int hr = client.GetMixFormat(out mix);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                FormatInfo info = Inspect(mix);
                long dur = 200000;
                IntPtr session = AllocGuid(SessionGuid);
                try
                {
                    hr = client.Initialize(0, StreamFlagsCapture, dur, 0, mix, session);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                }
                finally { Marshal.FreeHGlobal(session); }
                return info;
            }
            finally { Marshal.FreeCoTaskMem(mix); }
        }

        public static void MarkAsCall(IAudioClient client)
        {
            IntPtr unknown = IntPtr.Zero;
            IntPtr client2Ptr = IntPtr.Zero;
            IntPtr props = IntPtr.Zero;
            try
            {
                unknown = Marshal.GetIUnknownForObject(client);
                Guid iid = typeof(IAudioClient2).GUID;
                int hr = Marshal.QueryInterface(unknown, ref iid, out client2Ptr);
                if (hr < 0)
                {
                    BeepFiles.Log("call category unavailable 0x" + hr.ToString("X8"));
                    return;
                }
                var client2 = (IAudioClient2)Marshal.GetTypedObjectForIUnknown(client2Ptr, typeof(IAudioClient2));
                var value = new AudioClientProperties();
                value.cbSize = (uint)Marshal.SizeOf(typeof(AudioClientProperties));
                value.bIsOffload = 0;
                value.eCategory = 3;
                value.Options = 0;
                props = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AudioClientProperties)));
                Marshal.StructureToPtr(value, props, false);
                hr = client2.SetClientProperties(props);
                if (hr < 0) BeepFiles.Log("call category rejected 0x" + hr.ToString("X8"));
                else BeepFiles.Log("render stream marked as a call so Windows will not duck it");
            }
            catch (Exception ex)
            {
                BeepFiles.Log("call category failed: " + ex.Message);
            }
            finally
            {
                if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
                if (client2Ptr != IntPtr.Zero) Marshal.Release(client2Ptr);
                if (unknown != IntPtr.Zero) Marshal.Release(unknown);
            }
        }

        public static FormatInfo InitializeRender(IAudioClient client, int sampleRate)
        {
            MarkAsCall(client);
            int[] channels = new int[] { 1, 2 };
            for (int i = 0; i < channels.Length; i++)
            {
                IntPtr format = AllocFloatFormat(sampleRate, channels[i]);
                try
                {
                    IntPtr session = AllocGuid(SessionGuid);
                    try
                    {
                        int hr = client.Initialize(0, StreamFlagsRender, 200000, 0, format, session);
                        if (hr >= 0) return Inspect(format);
                        BeepFiles.Log("render format " + sampleRate + " Hz " + channels[i] + " ch rejected 0x" + hr.ToString("X8"));
                    }
                    finally { Marshal.FreeHGlobal(session); }
                }
                finally { Marshal.FreeHGlobal(format); }
            }

            IntPtr mix;
            int mixHr = client.GetMixFormat(out mix);
            if (mixHr < 0) Marshal.ThrowExceptionForHR(mixHr);
            try
            {
                IntPtr session = AllocGuid(SessionGuid);
                try
                {
                    int hr = client.Initialize(0, StreamFlagsCapture, 200000, 0, mix, session);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                    return Inspect(mix);
                }
                finally { Marshal.FreeHGlobal(session); }
            }
            finally { Marshal.FreeCoTaskMem(mix); }
        }

        public static IAudioCaptureClient GetCapture(IAudioClient client)
        {
            Guid iid = typeof(IAudioCaptureClient).GUID;
            IntPtr ptr;
            int hr = client.GetService(ref iid, out ptr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            var svc = (IAudioCaptureClient)Marshal.GetTypedObjectForIUnknown(ptr, typeof(IAudioCaptureClient));
            Marshal.Release(ptr);
            return svc;
        }

        public static IAudioRenderClient GetRender(IAudioClient client)
        {
            Guid iid = typeof(IAudioRenderClient).GUID;
            IntPtr ptr;
            int hr = client.GetService(ref iid, out ptr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            var svc = (IAudioRenderClient)Marshal.GetTypedObjectForIUnknown(ptr, typeof(IAudioRenderClient));
            Marshal.Release(ptr);
            return svc;
        }

        public static void TryDisableDucking(IMMDevice device, Guid sessionGuid)
        {
            try
            {
                Guid iid = typeof(IAudioSessionManager2).GUID;
                IntPtr mgrPtr;
                int hr = device.Activate(ref iid, 23, IntPtr.Zero, out mgrPtr);
                if (hr < 0) { BeepFiles.Log("ducking opt-out unavailable 0x" + hr.ToString("X8")); return; }
                try
                {
                    var mgr = (IAudioSessionManager2)Marshal.GetTypedObjectForIUnknown(mgrPtr, typeof(IAudioSessionManager2));
                    IntPtr ctlPtr;
                    hr = mgr.GetAudioSessionControl(ref sessionGuid, 0, out ctlPtr);
                    Marshal.ReleaseComObject(mgr);
                    if (hr < 0 || ctlPtr == IntPtr.Zero)
                    {
                        BeepFiles.Log("session control unavailable 0x" + hr.ToString("X8"));
                        return;
                    }
                    try
                    {
                        var ctl = (IAudioSessionControl2)Marshal.GetTypedObjectForIUnknown(ctlPtr, typeof(IAudioSessionControl2));
                        hr = ctl.SetDuckingPreference(true);
                        Marshal.ReleaseComObject(ctl);
                        if (hr < 0) BeepFiles.Log("SetDuckingPreference failed 0x" + hr.ToString("X8"));
                        else BeepFiles.Log("communications ducking opted out");
                    }
                    finally { Marshal.Release(ctlPtr); }
                }
                finally { Marshal.Release(mgrPtr); }
            }
            catch (Exception ex)
            {
                BeepFiles.Log("ducking opt-out skipped: " + ex.Message);
            }
        }

        public static void Downmix(IntPtr data, FormatInfo format, int frameOffset, int frames, float[] dest)
        {
            int channels = format.Channels < 1 ? 1 : format.Channels;
            IntPtr start = IntPtr.Add(data, frameOffset * format.BlockAlign);
            if (format.IsFloat)
            {
                var tmp = new float[frames * channels];
                Marshal.Copy(start, tmp, 0, tmp.Length);
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    int at = i * channels;
                    for (int c = 0; c < channels; c++) sum += tmp[at + c];
                    dest[i] = sum / channels;
                }
                return;
            }
            if (format.Bits == 16)
            {
                var tmp = new short[frames * channels];
                Marshal.Copy(start, tmp, 0, tmp.Length);
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    int at = i * channels;
                    for (int c = 0; c < channels; c++) sum += tmp[at + c] / 32768f;
                    dest[i] = sum / channels;
                }
                return;
            }
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    IntPtr p = IntPtr.Add(start, (i * channels + c) * (format.Bits / 8));
                    if (format.Bits == 32) sum += Marshal.ReadInt32(p) / 2147483648f;
                }
                dest[i] = sum / channels;
            }
        }

        static FormatInfo Inspect(IntPtr p)
        {
            short tag = Marshal.ReadInt16(p, 0);
            short channels = Marshal.ReadInt16(p, 2);
            int rate = Marshal.ReadInt32(p, 4);
            short block = Marshal.ReadInt16(p, 12);
            short bits = Marshal.ReadInt16(p, 14);
            bool isFloat = tag == 3;
            if (tag == -2)
            {
                var bytes = new byte[16];
                Marshal.Copy(IntPtr.Add(p, 24), bytes, 0, 16);
                isFloat = new Guid(bytes) == IeeeFloat;
            }
            if (channels < 1) channels = 1;
            if (bits < 16) bits = 16;
            if (block < 1) block = (short)(channels * (bits / 8));
            return new FormatInfo { Rate = rate, Channels = channels, Bits = bits, IsFloat = isFloat, BlockAlign = block };
        }

        static IntPtr AllocFloatFormat(int rate, int channels)
        {
            int bits = 32;
            int block = channels * 4;
            IntPtr p = Marshal.AllocHGlobal(18);
            for (int i = 0; i < 18; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt16(p, 0, 3);
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * block);
            Marshal.WriteInt16(p, 12, (short)block);
            Marshal.WriteInt16(p, 14, (short)bits);
            Marshal.WriteInt16(p, 16, 0);
            return p;
        }

        static IntPtr AllocGuid(Guid guid)
        {
            IntPtr p = Marshal.AllocHGlobal(16);
            Marshal.StructureToPtr(guid, p, false);
            return p;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr cb);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr cb);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IntPtr device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AudioClientProperties
    {
        public uint cbSize;
        public int bIsOffload;
        public int eCategory;
        public int Options;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr service);
    }

    [ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient2
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr service);
        [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minDuration, out long maxDuration);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int numFrames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int numFrames);
        [PreserveSig] int GetNextPacketSize(out int numFrames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(int numFramesRequested, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(int numFramesWritten, int flags);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, int streamFlags, out IntPtr volume);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out int processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string a);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string a, int b);
    }

    public static class NativeConsole
    {
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
'@

Add-Type -TypeDefinition $csharp -Language CSharp -ReferencedAssemblies @('System.Windows.Forms', 'System.Drawing') | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Initialize-BeepDataDir {
    if (-not (Test-Path -LiteralPath $script:DataDir)) {
        New-Item -ItemType Directory -Path $script:DataDir -Force | Out-Null
    }
    [BeepTone.BeepFiles]::LogPath = $script:LogPath
    [BeepTone.BeepFiles]::HeartbeatPath = $script:HeartbeatPath
}

function Write-BeepLog {
    param([string]$Message)
    Initialize-BeepDataDir
    [BeepTone.BeepFiles]::Log($Message)
}

function Get-DefaultBeepConfig {
    return [pscustomobject]@{
        captureDeviceName      = ''
        renderDeviceName       = 'CABLE Input'
        frequencyHz            = 1400
        durationMs             = 200
        intervalSeconds        = 13
        rampMs                 = 50
        minBeepDbfs            = -6
        noiseGateDbfs          = -45
        levelSmoothingSeconds  = 3
        maxBeepPeak            = 1
        setCommunicationsDevice = $true
    }
}

function Read-BeepConfig {
    $defaults = Get-DefaultBeepConfig
    if (-not (Test-Path -LiteralPath $script:ConfigPath)) { return $defaults }
    try {
        $raw = Get-Content -LiteralPath $script:ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        Write-BeepLog "config could not be read: $($_.Exception.Message)"
        return $defaults
    }
    foreach ($prop in $defaults.PSObject.Properties) {
        if ($null -eq $raw.PSObject.Properties[$prop.Name]) { continue }
        $defaults.$($prop.Name) = $raw.$($prop.Name)
    }
    return $defaults
}

function Save-BeepConfig {
    param($Config)
    Initialize-BeepDataDir
    $Config | ConvertTo-Json | Set-Content -LiteralPath $script:ConfigPath -Encoding UTF8
}

function ConvertTo-BeepSettings {
    param($Config)
    $settings = New-Object BeepTone.BeepSettings
    $settings.FrequencyHz = [double]$Config.frequencyHz
    $settings.DurationMs = [int]$Config.durationMs
    $settings.IntervalSeconds = [int]$Config.intervalSeconds
    $settings.RampMs = [int]$Config.rampMs
    $settings.MinBeepDbfs = [double]$Config.minBeepDbfs
    $settings.NoiseGateDbfs = [double]$Config.noiseGateDbfs
    $settings.LevelSmoothingSeconds = [double]$Config.levelSmoothingSeconds
    $settings.MaxBeepPeak = [double]$Config.maxBeepPeak
    return $settings
}

function Test-BeepDisabled {
    try {
        $item = Get-ItemProperty -Path 'HKLM:\Software\BeepTone' -Name 'Disabled' -ErrorAction Stop
        return ([int]$item.Disabled -eq 1)
    } catch {
        return $false
    }
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PowerShellExe {
    return Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
}

function Update-HiddenLauncher {
    Initialize-BeepDataDir
    $launcher = Join-Path $script:DataDir 'launch-hidden.vbs'
    $scriptPath = $PSCommandPath.Replace('"', '')
    @"
Set shell = CreateObject("Wscript.Shell")
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File ""$scriptPath"" " & WScript.Arguments(0)
shell.Run cmd, 0, False
"@ | Set-Content -LiteralPath $launcher -Encoding ASCII
    return $launcher
}

function Start-MixerProcess {
    $launcher = Update-HiddenLauncher
    $wscript = Join-Path $env:SystemRoot 'System32\wscript.exe'
    Start-Process -FilePath $wscript -ArgumentList "//B //Nologo `"$launcher`" -NoRelaunch" | Out-Null
}

function Get-BeepMixerProcesses {
    Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object {
        $_.CommandLine -and
        $_.CommandLine -match 'BeepTone\.ps1' -and
        $_.CommandLine -notmatch '-Watchdog' -and
        $_.CommandLine -notmatch '-SelfTest' -and
        $_.CommandLine -notmatch '-ListDevices' -and
        $_.CommandLine -notmatch '-Stop' -and
        $_.CommandLine -notmatch '-Start' -and
        $_.ProcessId -ne $PID
    }
}

function Stop-MixerProcesses {
    $procs = @(Get-BeepMixerProcesses)
    foreach ($proc in $procs) {
        try { Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
    }
    return $procs.Count
}

function Invoke-ElevatedSwitch {
    param([string]$SwitchName)
    $ps = Get-PowerShellExe
    try {
        $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" $SwitchName"
        Start-Process -FilePath $ps -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList $arg | Out-Null
    } catch {
        $message = "Administrator approval is required. The beep was not changed. $($_.Exception.Message)"
        Write-BeepLog $message
        Write-Host $message
        exit 1
    }
}

function Set-AdminDisabledFlag {
    param([int]$Value)
    if (-not (Test-Path 'HKLM:\Software\BeepTone')) {
        New-Item -Path 'HKLM:\Software\BeepTone' -Force | Out-Null
    }
    New-ItemProperty -Path 'HKLM:\Software\BeepTone' -Name 'Disabled' -Value $Value -PropertyType DWord -Force | Out-Null
}

function Register-BeepTasks {
    $launcher = Update-HiddenLauncher
    $wscript = Join-Path $env:SystemRoot 'System32\wscript.exe'
    $who = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $who -LogonType Interactive -RunLevel Limited
    $mixerSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -Hidden -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    $mixerAction = New-ScheduledTaskAction -Execute $wscript -Argument "//B //Nologo `"$launcher`" -NoRelaunch"
    $logon = New-ScheduledTaskTrigger -AtLogOn -User $who
    Register-ScheduledTask -TaskName $script:TaskName -Action $mixerAction -Trigger $logon -Settings $mixerSettings -Principal $principal -Force | Out-Null

    $watchSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -Hidden -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
    $watchAction = New-ScheduledTaskAction -Execute $wscript -Argument "//B //Nologo `"$launcher`" -Watchdog"
    $watchLogon = New-ScheduledTaskTrigger -AtLogOn -User $who
    $watchRepeat = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1)) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
    Register-ScheduledTask -TaskName $script:WatchdogTaskName -Action $watchAction -Trigger @($watchLogon, $watchRepeat) -Settings $watchSettings -Principal $principal -Force | Out-Null
}

function Read-Heartbeat {
    if (-not (Test-Path -LiteralPath $script:HeartbeatPath)) { return $null }
    try {
        $text = [IO.File]::ReadAllText($script:HeartbeatPath).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        $parts = $text.Split(' ')
        if ($parts.Count -lt 2) { return $null }
        $when = [DateTime]::Parse($parts[0], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        $lastBeep = $null
        if ($parts.Count -ge 3 -and $parts[2] -ne '-') {
            $lastBeep = [DateTime]::Parse($parts[2], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        }
        return [pscustomobject]@{ Timestamp = $when; ProcessId = [int]$parts[1]; LastBeep = $lastBeep }
    } catch {
        return $null
    }
}

function Get-ProcessAgeSeconds {
    param($Started)
    if ($null -eq $Started) { return [double]::PositiveInfinity }
    $when = $Started
    if ($Started -is [string]) {
        $when = [Management.ManagementDateTimeConverter]::ToDateTime($Started)
    }
    return ((Get-Date) - [datetime]$when).TotalSeconds
}

function Get-MixerProcessById {
    param([int]$ProcessId)
    if ($ProcessId -le 0) { return $null }
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId"
    if (-not $proc) { return $null }
    if (-not $proc.CommandLine) { return $null }
    if ($proc.CommandLine -notmatch 'BeepTone\.ps1') { return $null }
    if ($proc.CommandLine -match '-Watchdog|-Stop|-Start|-SelfTest|-ListDevices') { return $null }
    return $proc
}

function Invoke-Watchdog {
    Initialize-BeepDataDir
    if (Test-BeepDisabled) { exit 0 }
    $heartbeat = Read-Heartbeat
    $proc = $null
    $ageSeconds = [double]::PositiveInfinity
    if ($heartbeat) {
        $ageSeconds = ([DateTime]::UtcNow - $heartbeat.Timestamp).TotalSeconds
        $proc = Get-MixerProcessById -ProcessId $heartbeat.ProcessId
    }
    if ($proc -and $ageSeconds -lt 90 -and (Test-BeepPauseActive)) { exit 0 }

    $restartForBeep = $false
    if ($proc -and $ageSeconds -lt 90) {
        $started = Get-ProcessAgeSeconds $proc.CreationDate
        if ($started -ge 90) {
            $config = Read-BeepConfig
            $limit = [Math]::Max(30, [int]$config.intervalSeconds * 2)
            $beepLate = $false
            if ($null -eq $heartbeat.LastBeep) {
                $beepLate = $true
            } else {
                $beepAge = ([DateTime]::UtcNow - $heartbeat.LastBeep).TotalSeconds
                if ($beepAge -gt $limit) { $beepLate = $true }
            }
            if ($beepLate) {
                $restartForBeep = $true
                Write-BeepLog 'watchdog restarting because no beep has been sent'
            }
        }
        if (-not $restartForBeep) { exit 0 }
    }

    if (-not $proc) {
        $running = @(Get-BeepMixerProcesses)
        if ($running.Count -gt 0) {
            $young = $false
            foreach ($candidate in $running) {
                $started = $candidate.CreationDate
                if ($started -and (Get-ProcessAgeSeconds $started) -lt 90) {
                    $young = $true
                }
            }
            if ($young) { exit 0 }
            $proc = $running[0]
        }
    }

    if ($proc -and ($restartForBeep -or -not ($ageSeconds -lt 90))) {
        Write-BeepLog "watchdog stopping hung mixer pid $($proc.ProcessId)"
        try { Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline) {
            if (-not (Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 200
        }
    }

    if (Test-BeepDisabled) { exit 0 }
    Write-BeepLog 'watchdog starting mixer'
    Start-MixerProcess
    exit 0
}

function Test-CableInstalled {
    $render = @([BeepTone.AudioDevices]::List('Render'))
    foreach ($device in $render) {
        if ($device.Name -like '*CABLE Input*') { return $true }
    }
    return $false
}

function Install-VirtualCable {
    Initialize-BeepDataDir
    if (-not (Test-Path -LiteralPath $script:CableDir)) {
        New-Item -ItemType Directory -Path $script:CableDir -Force | Out-Null
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $zip = Join-Path $script:CableDir 'VBCABLE_Driver_Pack45.zip'
    Write-BeepLog "downloading virtual cable from $($script:CablePackUrl)"
    Invoke-WebRequest -Uri $script:CablePackUrl -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath $script:CableDir -Force
    $setup = Get-ChildItem -LiteralPath $script:CableDir -Filter 'VBCABLE_Setup*.exe' -Recurse | Where-Object {
        if ([Environment]::Is64BitOperatingSystem) { $_.Name -match 'x64' } else { $_.Name -notmatch 'x64|arm' }
    } | Select-Object -First 1
    if (-not $setup) {
        $setup = Get-ChildItem -LiteralPath $script:CableDir -Filter 'VBCABLE_Setup*.exe' -Recurse | Select-Object -First 1
    }
    if (-not $setup) { throw 'The virtual cable installer was not in the downloaded package.' }
    $runner = Join-Path $script:CableDir 'install-cable.ps1'
    @'
param([string]$Setup)
Set-Location -LiteralPath (Split-Path -Parent $Setup)
$proc = Start-Process -FilePath $Setup -ArgumentList '-i','-h' -Wait -PassThru
if ($null -eq $proc.ExitCode) { exit 0 }
exit $proc.ExitCode
'@ | Set-Content -LiteralPath $runner -Encoding ASCII
    $ps = Get-PowerShellExe
    Write-BeepLog "launching elevated installer $($setup.FullName)"
    try {
        $installArg = "-NoProfile -ExecutionPolicy Bypass -File `"$runner`" -Setup `"$($setup.FullName)`""
        $elevated = Start-Process -FilePath $ps -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $installArg
    } catch {
        throw 'Administrator approval was cancelled. The virtual cable was not installed.'
    }
    if ($elevated.ExitCode -ne 0) {
        Write-BeepLog "installer exit code $($elevated.ExitCode)"
    }
    for ($i = 0; $i -lt 40; $i++) {
        [System.Windows.Forms.Application]::DoEvents()
        if (Test-CableInstalled) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function New-SetupNumber {
    param($Min, $Max, $Value, $Increment, $Decimals, $X, $Y, $Width)
    $box = New-Object System.Windows.Forms.NumericUpDown
    $box.Location = New-Object System.Drawing.Point($X, $Y)
    $box.Size = New-Object System.Drawing.Size($Width, 24)
    $box.DecimalPlaces = $Decimals
    $box.Increment = [decimal]$Increment
    $box.Minimum = [decimal]$Min
    $box.Maximum = [decimal]$Max
    $number = [decimal]$Value
    if ($number -lt $box.Minimum) { $number = $box.Minimum }
    if ($number -gt $box.Maximum) { $number = $box.Maximum }
    $box.Value = $number
    return $box
}

function Test-SetupPassword {
    $prompt = New-Object System.Windows.Forms.Form
    $prompt.Text = 'Beep Tone setup'
    $prompt.FormBorderStyle = 'FixedDialog'
    $prompt.MaximizeBox = $false
    $prompt.MinimizeBox = $false
    $prompt.StartPosition = 'CenterScreen'
    $prompt.ClientSize = New-Object System.Drawing.Size(360, 140)
    $prompt.Font = New-Object System.Drawing.Font('Segoe UI', 9)

    $label = New-Object System.Windows.Forms.Label
    $label.Text = 'Enter the setup password.'
    $label.Location = New-Object System.Drawing.Point(16, 16)
    $label.Size = New-Object System.Drawing.Size(328, 20)
    $prompt.Controls.Add($label)

    $box = New-Object System.Windows.Forms.TextBox
    $box.Location = New-Object System.Drawing.Point(16, 44)
    $box.Size = New-Object System.Drawing.Size(328, 24)
    $box.UseSystemPasswordChar = $true
    $prompt.Controls.Add($box)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = 'OK'
    $ok.Location = New-Object System.Drawing.Point(168, 88)
    $ok.Size = New-Object System.Drawing.Size(80, 28)
    $prompt.Controls.Add($ok)

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Cancel'
    $cancel.Location = New-Object System.Drawing.Point(256, 88)
    $cancel.Size = New-Object System.Drawing.Size(88, 28)
    $prompt.Controls.Add($cancel)

    $script:setupPasswordOk = $false
    $prompt.AcceptButton = $ok
    $prompt.CancelButton = $cancel
    $ok.Add_Click({
        if ($box.Text -ceq $script:SetupPassword) {
            $script:setupPasswordOk = $true
            $prompt.DialogResult = [System.Windows.Forms.DialogResult]::OK
            $prompt.Close()
            return
        }
        [System.Windows.Forms.MessageBox]::Show('That password is not correct.', 'Beep Tone') | Out-Null
        $box.Clear()
        $box.Focus() | Out-Null
    })
    $cancel.Add_Click({
        $prompt.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
        $prompt.Close()
    })
    [void]$prompt.ShowDialog()
    $prompt.Dispose()
    return [bool]$script:setupPasswordOk
}

function Show-BeepSetup {
    param($Config)
    if (-not (Test-SetupPassword)) { return $false }
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Beep Tone setup'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.StartPosition = 'CenterScreen'
    $form.ClientSize = New-Object System.Drawing.Size(560, 620)
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

    $cableLabel = New-Object System.Windows.Forms.Label
    $cableLabel.Location = New-Object System.Drawing.Point(16, 16)
    $cableLabel.Size = New-Object System.Drawing.Size(360, 40)
    $form.Controls.Add($cableLabel)

    $installButton = New-Object System.Windows.Forms.Button
    $installButton.Text = 'Install Virtual Cable'
    $installButton.Location = New-Object System.Drawing.Point(380, 16)
    $installButton.Size = New-Object System.Drawing.Size(124, 32)
    $form.Controls.Add($installButton)

    $micLabel = New-Object System.Windows.Forms.Label
    $micLabel.Text = 'Microphone'
    $micLabel.Location = New-Object System.Drawing.Point(16, 68)
    $micLabel.Size = New-Object System.Drawing.Size(480, 18)
    $form.Controls.Add($micLabel)

    $micBox = New-Object System.Windows.Forms.ComboBox
    $micBox.DropDownStyle = 'DropDownList'
    $micBox.Location = New-Object System.Drawing.Point(16, 88)
    $micBox.Size = New-Object System.Drawing.Size(488, 24)
    $form.Controls.Add($micBox)

    $outLabel = New-Object System.Windows.Forms.Label
    $outLabel.Text = 'Send the mixed microphone to this playback device'
    $outLabel.Location = New-Object System.Drawing.Point(16, 124)
    $outLabel.Size = New-Object System.Drawing.Size(480, 18)
    $form.Controls.Add($outLabel)

    $outBox = New-Object System.Windows.Forms.ComboBox
    $outBox.DropDownStyle = 'DropDownList'
    $outBox.Location = New-Object System.Drawing.Point(16, 144)
    $outBox.Size = New-Object System.Drawing.Size(488, 24)
    $form.Controls.Add($outBox)

    $commCheck = New-Object System.Windows.Forms.CheckBox
    $commCheck.Text = 'Set CABLE Output as the Windows default microphone'
    $commCheck.Location = New-Object System.Drawing.Point(16, 180)
    $commCheck.Size = New-Object System.Drawing.Size(488, 24)
    $commCheck.Checked = [bool]$Config.setCommunicationsDevice
    $form.Controls.Add($commCheck)

    $toneLabel = New-Object System.Windows.Forms.Label
    $toneLabel.Text = 'Tone'
    $toneLabel.Location = New-Object System.Drawing.Point(16, 212)
    $toneLabel.Size = New-Object System.Drawing.Size(528, 18)
    $form.Controls.Add($toneLabel)

    $freqLabel = New-Object System.Windows.Forms.Label
    $freqLabel.Text = 'Frequency (Hz)'
    $freqLabel.Location = New-Object System.Drawing.Point(16, 234)
    $freqLabel.Size = New-Object System.Drawing.Size(120, 18)
    $form.Controls.Add($freqLabel)

    $durationLabel = New-Object System.Windows.Forms.Label
    $durationLabel.Text = 'Length (ms)'
    $durationLabel.Location = New-Object System.Drawing.Point(150, 234)
    $durationLabel.Size = New-Object System.Drawing.Size(120, 18)
    $form.Controls.Add($durationLabel)

    $intervalLabel = New-Object System.Windows.Forms.Label
    $intervalLabel.Text = 'Every (seconds)'
    $intervalLabel.Location = New-Object System.Drawing.Point(284, 234)
    $intervalLabel.Size = New-Object System.Drawing.Size(120, 18)
    $form.Controls.Add($intervalLabel)

    $fadeLabel = New-Object System.Windows.Forms.Label
    $fadeLabel.Text = 'Fade (ms)'
    $fadeLabel.Location = New-Object System.Drawing.Point(418, 234)
    $fadeLabel.Size = New-Object System.Drawing.Size(120, 18)
    $form.Controls.Add($fadeLabel)

    $freqBox = New-SetupNumber -Min 1260 -Max 1540 -Value $Config.frequencyHz -Increment 10 -Decimals 0 -X 16 -Y 254 -Width 120
    $durationBox = New-SetupNumber -Min 170 -Max 250 -Value $Config.durationMs -Increment 10 -Decimals 0 -X 150 -Y 254 -Width 120
    $intervalBox = New-SetupNumber -Min 12 -Max 15 -Value $Config.intervalSeconds -Increment 1 -Decimals 0 -X 284 -Y 254 -Width 120
    $fadeBox = New-SetupNumber -Min 5 -Max 80 -Value $Config.rampMs -Increment 5 -Decimals 0 -X 418 -Y 254 -Width 120
    $form.Controls.Add($freqBox)
    $form.Controls.Add($durationBox)
    $form.Controls.Add($intervalBox)
    $form.Controls.Add($fadeBox)

    $rangeLabel = New-Object System.Windows.Forms.Label
    $rangeLabel.Text = 'Frequency 1260-1540 Hz, length 170-250 ms, repeat every 12-15 seconds.'
    $rangeLabel.Location = New-Object System.Drawing.Point(16, 284)
    $rangeLabel.Size = New-Object System.Drawing.Size(528, 18)
    $form.Controls.Add($rangeLabel)

    $levelLabel = New-Object System.Windows.Forms.Label
    $levelLabel.Text = 'Level (dBFS)'
    $levelLabel.Location = New-Object System.Drawing.Point(16, 310)
    $levelLabel.Size = New-Object System.Drawing.Size(120, 18)
    $form.Controls.Add($levelLabel)

    $levelBox = New-SetupNumber -Min -48 -Max -3 -Value $Config.minBeepDbfs -Increment 1 -Decimals 0 -X 16 -Y 330 -Width 120
    $form.Controls.Add($levelBox)

    $levelHint = New-Object System.Windows.Forms.Label
    $levelHint.Text = 'Closer to 0 is louder. This level stays fixed.'
    $levelHint.Location = New-Object System.Drawing.Point(148, 332)
    $levelHint.Size = New-Object System.Drawing.Size(390, 20)
    $form.Controls.Add($levelHint)

    $help = New-Object System.Windows.Forms.TextBox
    $help.Multiline = $true
    $help.ReadOnly = $true
    $help.BorderStyle = 'None'
    $help.BackColor = $form.BackColor
    $help.Location = New-Object System.Drawing.Point(16, 364)
    $help.Size = New-Object System.Drawing.Size(528, 188)
    $help.Text = "In the softphone, set the microphone to CABLE Output (VB-Audio Virtual Cable), or to Follow system setting. The checkbox above makes CABLE Output the Windows default input, which is what Follow system setting uses. This app opens the physical microphone. The softphone must not use that same device.`r`n`r`nTurn off microphone processing in the phone software. Noise removal, noise suppression, and automatic volume treat the beep as noise and make it louder, quieter, or drop it out as other call audio changes. In Webex, set Settings, Audio, Smart audio, Microphone audio to Music mode, and turn off automatic microphone volume. Other softphones need that same kind of processing turned off.`r`n`r`nMuting that mic in the softphone, Windows, or the headset also mutes this beep. Headphones avoid speaker-to-mic echo. The mixer adds about 20 ms.`r`n`r`nClose this window to finish later from the tray icon. There is no Quit on the tray."
    $form.Controls.Add($help)

    $saveButton = New-Object System.Windows.Forms.Button
    $saveButton.Text = 'Save and start'
    $saveButton.Location = New-Object System.Drawing.Point(320, 564)
    $saveButton.Size = New-Object System.Drawing.Size(120, 32)
    $form.Controls.Add($saveButton)

    $refreshButton = New-Object System.Windows.Forms.Button
    $refreshButton.Text = 'Refresh'
    $refreshButton.Location = New-Object System.Drawing.Point(448, 564)
    $refreshButton.Size = New-Object System.Drawing.Size(96, 32)
    $form.Controls.Add($refreshButton)

    $script:setupSaved = $false

    $reload = {
        $micBox.Items.Clear()
        $outBox.Items.Clear()
        $capture = @([BeepTone.AudioDevices]::List('Capture'))
        foreach ($device in $capture) {
            if ($device.Name -like '*CABLE Output*') { continue }
            [void]$micBox.Items.Add($device)
        }
        $render = @([BeepTone.AudioDevices]::List('Render'))
        foreach ($device in $render) { [void]$outBox.Items.Add($device) }
        $cable = Test-CableInstalled
        if ($cable) { $cableLabel.Text = 'Virtual cable is installed.' }
        else { $cableLabel.Text = 'Virtual cable is not installed.' }
        for ($i = 0; $i -lt $micBox.Items.Count; $i++) {
            if ($Config.captureDeviceName -and $micBox.Items[$i].Name -like "*$($Config.captureDeviceName)*") { $micBox.SelectedIndex = $i; break }
        }
        if ($micBox.SelectedIndex -lt 0 -and $micBox.Items.Count -gt 0) { $micBox.SelectedIndex = 0 }
        $pickedOut = -1
        for ($i = 0; $i -lt $outBox.Items.Count; $i++) {
            $name = $outBox.Items[$i].Name
            if ($Config.renderDeviceName -and $name -like "*$($Config.renderDeviceName)*") { $pickedOut = $i; break }
        }
        if ($pickedOut -lt 0) {
            for ($i = 0; $i -lt $outBox.Items.Count; $i++) {
                if ($outBox.Items[$i].Name -like '*CABLE Input*') { $pickedOut = $i; break }
            }
        }
        if ($pickedOut -ge 0) { $outBox.SelectedIndex = $pickedOut }
        elseif ($outBox.Items.Count -gt 0) { $outBox.SelectedIndex = 0 }
        $renderName = ''
        if ($outBox.SelectedIndex -ge 0) { $renderName = [string]$outBox.SelectedItem.Name }
        $virtual = $renderName -match '(?i)cable|voicemeeter|vb-audio|virtual'
        $saveButton.Enabled = ($micBox.SelectedIndex -ge 0 -and $virtual)
    }
    & $reload
    $syncSave = {
        $renderName = ''
        if ($outBox.SelectedIndex -ge 0) { $renderName = [string]$outBox.SelectedItem.Name }
        $virtual = $renderName -match '(?i)cable|voicemeeter|vb-audio|virtual'
        $saveButton.Enabled = ($micBox.SelectedIndex -ge 0 -and $virtual)
    }
    $micBox.Add_SelectedIndexChanged($syncSave)
    $outBox.Add_SelectedIndexChanged($syncSave)

    $installButton.Add_Click({
        $installButton.Enabled = $false
        $saveButton.Enabled = $false
        $cableLabel.Text = 'Downloading and installing the virtual cable...'
        $form.Refresh()
        [System.Windows.Forms.Application]::DoEvents()
        try {
            $ready = Install-VirtualCable
            if ($ready) {
                $cableLabel.Text = 'Virtual cable is installed.'
                Write-BeepLog 'virtual cable is available'
            } else {
                [System.Windows.Forms.MessageBox]::Show(
                    'The installer finished, but CABLE Input is not available yet. Sign out or reboot, then open Beep Tone again.',
                    'Beep Tone') | Out-Null
            }
        } catch {
            Write-BeepLog "virtual cable install failed: $($_.Exception.Message)"
            [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Beep Tone') | Out-Null
        } finally {
            $installButton.Enabled = $true
            & $reload
        }
    })

    $refreshButton.Add_Click({ & $reload })

    $saveButton.Add_Click({
        if ($micBox.SelectedIndex -lt 0 -or $outBox.SelectedIndex -lt 0) { return }
        if ($outBox.SelectedItem.Name -notmatch '(?i)cable|voicemeeter|vb-audio|virtual') { return }
        $Config.captureDeviceName = $micBox.SelectedItem.Name
        $Config.renderDeviceName = $outBox.SelectedItem.Name
        $Config.setCommunicationsDevice = [bool]$commCheck.Checked
        $Config.frequencyHz = [double]$freqBox.Value
        $Config.durationMs = [int]$durationBox.Value
        $Config.intervalSeconds = [int]$intervalBox.Value
        $Config.rampMs = [int]$fadeBox.Value
        $level = [double]$levelBox.Value
        $Config.minBeepDbfs = $level
        $peak = [math]::Pow(10.0, $level / 20.0) * [math]::Sqrt(2.0)
        if ($peak -lt 0.001) { $peak = 0.001 }
        if ($peak -gt 1) { $peak = 1 }
        $capped = $peak * 1.001
        if ($capped -gt 1) { $capped = 1 }
        $Config.maxBeepPeak = [math]::Round($capped, 5)
        Save-BeepConfig -Config $Config
        if ($Config.setCommunicationsDevice) {
            Update-CableDefaultMicrophone -Notify
        }
        try {
            Register-BeepTasks
            Write-BeepLog 'keep-alive tasks registered'
        } catch {
            Write-BeepLog "could not register keep-alive tasks: $($_.Exception.Message)"
            [System.Windows.Forms.MessageBox]::Show(
                "The beep will run now, but it could not register the tasks that restart it.`r`n$($_.Exception.Message)",
                'Beep Tone') | Out-Null
        }
        $script:setupSaved = $true
        $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $form.Close()
    })

    [void]$form.ShowDialog()
    $form.Dispose()
    return [bool]$script:setupSaved
}

function New-BeepIcon {
    param([System.Drawing.Color]$Color)
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $graphics = [System.Drawing.Graphics]::FromImage($bmp)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $brush = New-Object System.Drawing.SolidBrush $Color
    $graphics.FillEllipse($brush, 1, 1, 30, 30)
    $font = New-Object System.Drawing.Font('Segoe UI', 16, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $graphics.DrawString('B', $font, [System.Drawing.Brushes]::White, 7, 5)
    $graphics.Dispose()
    $brush.Dispose()
    $font.Dispose()
    $script:IconBitmaps += $bmp
    return [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}

function Get-BeepReadmePath {
    return (Join-Path (Split-Path -Parent $PSCommandPath) 'README.md')
}

function Open-BeepReadme {
    $readme = Get-BeepReadmePath
    if (-not (Test-Path -LiteralPath $readme)) {
        [System.Windows.Forms.MessageBox]::Show(
            "README.md was not found next to BeepTone.ps1.`r`n$readme",
            'Beep Tone') | Out-Null
        return
    }
    try {
        Start-Process -FilePath $readme | Out-Null
    } catch {
        Start-Process -FilePath 'notepad.exe' -ArgumentList "`"$readme`"" | Out-Null
    }
}

function Get-LocalBeepWav {
    param($Config)
    $rate = 48000
    $samples = [BeepTone.BeepSynth]::Create($rate, [double]$Config.frequencyHz, [int]$Config.durationMs, [int]$Config.rampMs)
    $peak = [math]::Pow(10.0, [double]$Config.minBeepDbfs / 20.0) * [math]::Sqrt(2.0)
    $cap = [double]$Config.maxBeepPeak
    if ($cap -gt 0 -and $peak -gt $cap) { $peak = $cap }
    if ($peak -gt 1) { $peak = 1 }
    if ($peak -lt 0.001) { $peak = 0.001 }
    $dataBytes = $samples.Length * 2
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
    $writer.Write([int](36 + $dataBytes))
    $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
    $writer.Write([Text.Encoding]::ASCII.GetBytes('fmt '))
    $writer.Write([int]16)
    $writer.Write([int16]1)
    $writer.Write([int16]1)
    $writer.Write([int]$rate)
    $writer.Write([int]($rate * 2))
    $writer.Write([int16]2)
    $writer.Write([int16]16)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
    $writer.Write([int]$dataBytes)
    foreach ($sample in $samples) {
        $value = [double]$sample * $peak
        if ($value -gt 1) { $value = 1 }
        if ($value -lt -1) { $value = -1 }
        $scaled = [int][math]::Round($value * 32767.0)
        if ($scaled -gt 32767) { $scaled = 32767 }
        if ($scaled -lt -32768) { $scaled = -32768 }
        $writer.Write([int16]$scaled)
    }
    $writer.Flush()
    $bytes = $stream.ToArray()
    $play = New-Object System.IO.MemoryStream (,$bytes)
    $play.Position = 0
    return $play
}

function Play-LocalTestBeep {
    try {
        $config = Read-BeepConfig
        if ($script:LocalBeepPlayer) {
            try { $script:LocalBeepPlayer.Stop() } catch { }
        }
        $stream = Get-LocalBeepWav -Config $config
        $script:LocalBeepStream = $stream
        $player = New-Object System.Media.SoundPlayer $stream
        $player.Load()
        $script:LocalBeepPlayer = $player
        $player.Play()
        Write-BeepLog 'local test beep'
    } catch {
        Write-BeepLog "local test beep failed: $($_.Exception.Message)"
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Beep Tone') | Out-Null
    }
}

function Get-BeepPauseUntil {
    if (-not (Test-Path -LiteralPath $script:PausePath)) { return $null }
    try {
        $text = [IO.File]::ReadAllText($script:PausePath).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return [DateTime]::Parse($text, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    } catch {
        return $null
    }
}

function Test-BeepPauseActive {
    $until = Get-BeepPauseUntil
    if (-not $until) { return $false }
    return ([DateTime]::UtcNow -lt $until)
}

function Start-BeepPause {
    Initialize-BeepDataDir
    $until = [DateTime]::UtcNow.AddMinutes($script:PauseMinutes)
    [IO.File]::WriteAllText($script:PausePath, $until.ToString('o', [Globalization.CultureInfo]::InvariantCulture))
    $mixer = [BeepTone.BeepMixer]::Current
    if ($mixer) { $mixer.SetBeepPaused($true) }
    Write-BeepLog ("beep paused until " + $until.ToString('o', [Globalization.CultureInfo]::InvariantCulture))
    return $until
}

function Stop-BeepPause {
    if (Test-Path -LiteralPath $script:PausePath) {
        Remove-Item -LiteralPath $script:PausePath -Force -ErrorAction SilentlyContinue
    }
    $mixer = [BeepTone.BeepMixer]::Current
    if ($mixer) {
        $mixer.SetBeepPaused($false)
        $mixer.RequestBeep()
    }
    $script:SuppressAlertUntilBeep = $true
    Write-BeepLog 'beep pause ended'
}

function Get-BeepAlert {
    param($Mixer)
    if (Test-BeepPauseActive) { return $null }
    if ($script:SuppressAlertUntilBeep) { return $null }
    if (-not $Mixer) {
        if ($script:MixerStarted) { return 'The beep is not running.' }
        return $null
    }
    $problem = $Mixer.Problem
    if ($problem) { return $problem }
    $status = $Mixer.Status
    if ($status -eq 'Reconnecting') {
        return 'The microphone or virtual cable was lost. The beep is not going out on the call.'
    }
    if ($status -ne 'Running') { return $null }
    $interval = 13
    if ($Mixer.Settings -and [int]$Mixer.Settings.IntervalSeconds -ge 1) {
        $interval = [int]$Mixer.Settings.IntervalSeconds
    }
    $limit = $interval + 5
    $last = $Mixer.LastBeepLocal
    if ($last -gt [datetime]::MinValue) {
        $age = ((Get-Date) - $last).TotalSeconds
        if ($age -gt $limit) {
            return "No beep has been sent for $([int]$age) seconds. The recording may not include the beep."
        }
        return $null
    }
    $since = $Mixer.RunningSince
    if ($since -gt [datetime]::MinValue -and ((Get-Date) - $since).TotalSeconds -gt $limit) {
        return 'No beep has been sent since the mixer started. The recording may not include the beep.'
    }
    return $null
}

function Update-BeepAlert {
    param($Notify, $Mixer)
    $alert = Get-BeepAlert $Mixer
    if ($alert) {
        $repeat = ((Get-Date) - $script:AlertShownAt).TotalSeconds -ge 120
        if ($script:AlertText -ne $alert -or $repeat) {
            if ($script:AlertText -ne $alert) { Write-BeepLog "beep problem: $alert" }
            $Notify.ShowBalloonTip(8000, 'Beep tone', $alert, [System.Windows.Forms.ToolTipIcon]::Warning)
            $script:AlertText = $alert
            $script:AlertShownAt = Get-Date
        }
        return $true
    }
    if ($script:AlertText) {
        Write-BeepLog 'beep problem cleared'
        $Notify.ShowBalloonTip(4000, 'Beep tone', 'The beep is going out on the call again.', [System.Windows.Forms.ToolTipIcon]::Info)
        $script:AlertText = $null
    }
    return $false
}

function Update-BeepTray {
    param($Icon, $StatusItem)
    $mixer = [BeepTone.BeepMixer]::Current
    $text = 'Beep tone needs setup'
    $menu = 'Setup needed'
    if ($mixer) {
        $status = $mixer.Status
        $last = $mixer.LastBeepLocal
        if ($status -eq 'Running') {
            if ($last -gt [DateTime]::MinValue) {
                $stamp = $last.ToString('HH:mm:ss')
                $text = "Beep tone running. Last beep $stamp"
                $menu = "Running. Last beep $stamp"
            } else {
                $text = 'Beep tone running'
                $menu = 'Running'
            }
        } elseif ($status -eq 'Reconnecting') {
            $text = 'Beep tone reconnecting'
            $menu = 'Reconnecting'
        } elseif ($status) {
            $text = "Beep tone $status"
            $menu = $status
        }
        $title = $null
        $balloon = $mixer.ConsumeBalloon([ref]$title)
        if ($balloon) {
            if (-not $title) { $title = 'Beep tone' }
            $Icon.ShowBalloonTip(4000, $title, $balloon, [System.Windows.Forms.ToolTipIcon]::Info)
        }
    }
    if ($text.Length -gt 63) { $text = $text.Substring(0, 63) }
    $Icon.Text = $text
    $StatusItem.Text = $menu
}

function Test-RemoteDesktopSession {
    return [System.Windows.Forms.SystemInformation]::TerminalServerSession
}

function Update-CableDefaultMicrophone {
    param([switch]$Notify)
    if (Test-RemoteDesktopSession) {
        if (-not $script:LoggedRemoteSkip) {
            $script:LoggedRemoteSkip = $true
            $message = 'Remote Desktop is connected, so the Windows default microphone was left unchanged.'
            Write-BeepLog $message
            if ($Notify) {
                [System.Windows.Forms.MessageBox]::Show($message, 'Beep Tone') | Out-Null
            }
        }
        return
    }
    $script:LoggedRemoteSkip = $false
    try {
        $cableOut = [BeepTone.AudioDevices]::FindCableOutput()
        if (-not $cableOut) {
            if ($Notify -or -not $script:LoggedMissingCable) {
                $script:LoggedMissingCable = $true
                $message = 'CABLE Output was not found, so the default microphone was not changed.'
                Write-BeepLog $message
                if ($Notify) {
                    [System.Windows.Forms.MessageBox]::Show(
                        "$message Choose it in the softphone, or set the microphone to Follow system setting after the cable is installed.",
                        'Beep Tone') | Out-Null
                }
            }
            return
        }
        $script:LoggedMissingCable = $false
        $current = [BeepTone.AudioDevices]::GetDefaultEndpoint('Capture', 0)
        if ($current -and $current.Id -eq $cableOut.Id) { return }
        $was = 'none'
        if ($current -and $current.Name) { $was = $current.Name }
        [BeepTone.AudioDevices]::SetDefaultMicrophone($cableOut.Id)
        Write-BeepLog "default microphone was $was, set back to $($cableOut.Name)"
    } catch {
        Write-BeepLog "could not set default microphone: $($_.Exception.Message)"
        if ($Notify) {
            [System.Windows.Forms.MessageBox]::Show(
                "The default microphone was not changed. In the softphone, choose CABLE Output or Follow system setting.`r`n$($_.Exception.Message)",
                'Beep Tone') | Out-Null
        }
    }
}

function Start-MixerIfConfigured {
    $config = Read-BeepConfig
    if ([string]::IsNullOrWhiteSpace($config.captureDeviceName)) { return }
    if ([string]::IsNullOrWhiteSpace($config.renderDeviceName)) { return }
    if ($config.setCommunicationsDevice) {
        Update-CableDefaultMicrophone
    }
    if (-not $script:MixerStarted) {
        $mixer = New-Object BeepTone.BeepMixer
        $mixer.CaptureName = [string]$config.captureDeviceName
        $mixer.RenderName = [string]$config.renderDeviceName
        $mixer.Settings = ConvertTo-BeepSettings -Config $config
        [BeepTone.BeepMixer]::Current = $mixer
        $mixer.Start()
        $mixer.SetBeepPaused((Test-BeepPauseActive))
        $script:MixerStarted = $true
        Write-BeepLog 'mixer thread started'
        return
    }
    $mixer = [BeepTone.BeepMixer]::Current
    $mixer.CaptureName = [string]$config.captureDeviceName
    $mixer.RenderName = [string]$config.renderDeviceName
    $mixer.Settings = ConvertTo-BeepSettings -Config $config
    $mixer.RequestRestart()
}

function Start-TrayApp {
    $created = $false
    $script:Mutex = New-Object System.Threading.Mutex($false, 'Local\BeepToneInjector', [ref]$created)
    $owned = $false
    try {
        $owned = $script:Mutex.WaitOne(0, $false)
    } catch [System.Threading.AbandonedMutexException] {
        $owned = $true
    }
    if (-not $owned) { exit 0 }
    if (Test-BeepDisabled) {
        Write-BeepLog 'not starting because an administrator stopped the beep'
        exit 0
    }

    Initialize-BeepDataDir
    $script:IdleIcon = New-BeepIcon ([System.Drawing.Color]::FromArgb(255, 25, 110, 200))
    $script:BeepIcon = New-BeepIcon ([System.Drawing.Color]::FromArgb(255, 214, 132, 16))
    $script:WarningIcon = New-BeepIcon ([System.Drawing.Color]::FromArgb(255, 190, 40, 40))
    $script:SeenBeepCount = 0
    $script:IconFlashUntil = [datetime]::MinValue
    $notify = New-Object System.Windows.Forms.NotifyIcon
    $notify.Icon = $script:IdleIcon
    $notify.Visible = $true
    $notify.Text = 'Beep tone starting'
    Write-BeepLog 'tray icon is showing'

    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $statusItem = New-Object System.Windows.Forms.ToolStripMenuItem('Starting')
    $statusItem.Enabled = $false
    $beepItem = New-Object System.Windows.Forms.ToolStripMenuItem('Beep now')
    $pauseItem = New-Object System.Windows.Forms.ToolStripMenuItem('Pause beep for 15 minutes')
    $hearItem = New-Object System.Windows.Forms.ToolStripMenuItem('Hear beep on this PC')
    $setupItem = New-Object System.Windows.Forms.ToolStripMenuItem('Setup')
    $readmeItem = New-Object System.Windows.Forms.ToolStripMenuItem('Open readme')
    $logItem = New-Object System.Windows.Forms.ToolStripMenuItem('Open log')
    [void]$menu.Items.Add($statusItem)
    [void]$menu.Items.Add($beepItem)
    [void]$menu.Items.Add($pauseItem)
    [void]$menu.Items.Add($hearItem)
    [void]$menu.Items.Add($setupItem)
    [void]$menu.Items.Add($readmeItem)
    [void]$menu.Items.Add($logItem)
    $notify.ContextMenuStrip = $menu

    $beepItem.Add_Click({
        if (Test-BeepPauseActive) { return }
        $mixer = [BeepTone.BeepMixer]::Current
        if ($mixer) { $mixer.RequestBeep() }
    })
    $pauseItem.Add_Click({
        if (Test-BeepPauseActive) {
            Stop-BeepPause
            $notify.ShowBalloonTip(4000, 'Beep tone', 'The beep is back on.', [System.Windows.Forms.ToolTipIcon]::Info)
            return
        }
        $until = Start-BeepPause
        $local = $until.ToLocalTime().ToString('h:mm tt')
        $notify.ShowBalloonTip(6000, 'Beep tone', "The beep is paused until $local. It turns back on by itself. The microphone still works.", [System.Windows.Forms.ToolTipIcon]::Warning)
    })
    $hearItem.Add_Click({
        Play-LocalTestBeep
        $script:IconFlashUntil = [datetime]::Now.AddMilliseconds(700)
        $notify.Icon = $script:BeepIcon
    })
    $readmeItem.Add_Click({ Open-BeepReadme })
    $setupItem.Add_Click({
        $config = Read-BeepConfig
        $saved = Show-BeepSetup -Config $config
        if ($saved) {
            Start-MixerIfConfigured
            $notify.ShowBalloonTip(4000, 'Beep tone', 'Beep tone is running. Set the softphone microphone to CABLE Output.', [System.Windows.Forms.ToolTipIcon]::Info)
        }
    })
    $logItem.Add_Click({
        Initialize-BeepDataDir
        if (-not (Test-Path -LiteralPath $script:LogPath)) {
            [IO.File]::WriteAllText($script:LogPath, '')
        }
        Start-Process -FilePath 'notepad.exe' -ArgumentList $script:LogPath | Out-Null
    })

    $script:TrayTicks = 0
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 200
    $timer.Add_Tick({
        $script:TrayTicks++
        $mixer = [BeepTone.BeepMixer]::Current
        $pauseUntil = Get-BeepPauseUntil
        $paused = $false
        if ($pauseUntil -and [DateTime]::UtcNow -ge $pauseUntil) {
            Stop-BeepPause
            $notify.ShowBalloonTip(6000, 'Beep tone', 'The beep is back on.', [System.Windows.Forms.ToolTipIcon]::Info)
        } elseif ($pauseUntil) {
            $paused = $true
            if ($mixer) { $mixer.SetBeepPaused($true) }
        } elseif ($mixer -and $mixer.IsBeepPaused) {
            $mixer.SetBeepPaused($false)
        }
        $alertNow = Get-BeepAlert $mixer
        if ($mixer) {
            $count = $mixer.BeepCount
            if ($count -ne $script:SeenBeepCount) {
                $script:SeenBeepCount = $count
                if ($count -gt 0) {
                    $script:IconFlashUntil = [datetime]::Now.AddMilliseconds(700)
                    $script:SuppressAlertUntilBeep = $false
                }
            }
        }
        if ([datetime]::Now -lt $script:IconFlashUntil -and -not $paused) {
            if ($notify.Icon -ne $script:BeepIcon) { $notify.Icon = $script:BeepIcon }
        } elseif ($paused -or $alertNow) {
            if ($notify.Icon -ne $script:WarningIcon) { $notify.Icon = $script:WarningIcon }
        } elseif ($notify.Icon -ne $script:IdleIcon) {
            $notify.Icon = $script:IdleIcon
        }
        if ($script:TrayTicks % 5 -ne 0) { return }
        if (Test-BeepDisabled) {
            Write-BeepLog 'stopping because an administrator set the disable flag'
            [System.Windows.Forms.Application]::Exit()
            return
        }
        if (-not $mixer -or -not $mixer.IsRunning) {
            [BeepTone.BeepFiles]::Heartbeat()
        }
        Update-BeepTray -Icon $notify -StatusItem $statusItem
        if ($paused -and $pauseUntil) {
            $pauseText = 'Beep paused until ' + $pauseUntil.ToLocalTime().ToString('h:mm tt')
            if ($pauseText.Length -gt 63) { $pauseText = $pauseText.Substring(0, 63) }
            $notify.Text = $pauseText
            $statusItem.Text = $pauseText
            $pauseItem.Text = 'Turn beep on'
        } else {
            $pauseItem.Text = 'Pause beep for 15 minutes'
        }
        if ($paused) { return }
        if ($alertNow) {
            $tip = [string]$alertNow
            if ($tip.Length -gt 63) { $tip = $tip.Substring(0, 63) }
            $notify.Text = $tip
        }
        Update-BeepAlert -Notify $notify -Mixer $mixer
        if (((Get-Date) - $script:DefaultMicCheckedAt).TotalSeconds -ge 15) {
            $script:DefaultMicCheckedAt = Get-Date
            $micConfig = Read-BeepConfig
            if ($micConfig.setCommunicationsDevice) { Update-CableDefaultMicrophone }
        }
    })
    $timer.Start()

    $config = Read-BeepConfig
    $needsSetup = [string]::IsNullOrWhiteSpace($config.captureDeviceName) -or -not (Test-Path -LiteralPath $script:ConfigPath)
    if (-not $needsSetup -and [string]::IsNullOrWhiteSpace($config.renderDeviceName)) { $needsSetup = $true }
    if ($needsSetup) {
        $saved = Show-BeepSetup -Config $config
        if ($saved) {
            $notify.ShowBalloonTip(4000, 'Beep tone', 'Beep tone is running. Set the softphone microphone to CABLE Output.', [System.Windows.Forms.ToolTipIcon]::Info)
        }
    }
    Start-MixerIfConfigured
    [System.Windows.Forms.Application]::Run()
    $timer.Stop()
    $notify.Visible = $false
    $notify.Dispose()
    if ($script:Mutex) {
        try { $script:Mutex.ReleaseMutex() } catch { }
        $script:Mutex.Dispose()
    }
}

function Show-DeviceList {
    $capture = @([BeepTone.AudioDevices]::List('Capture'))
    $render = @([BeepTone.AudioDevices]::List('Render'))
    Write-Output 'Capture devices:'
    foreach ($device in $capture) {
        $note = ''
        if ($device.Name -like '*CABLE Output*') { $note = ' (hidden in setup)' }
        Write-Output ("  {0}{1}" -f $device.Name, $note)
    }
    Write-Output 'Playback devices:'
    foreach ($device in $render) {
        Write-Output ("  {0}" -f $device.Name)
    }
}

function Invoke-AdminStop {
    if (-not (Test-IsAdmin)) {
        Invoke-ElevatedSwitch -SwitchName '-Stop'
        exit 0
    }
    Set-AdminDisabledFlag -Value 1
    $count = Stop-MixerProcesses
    Write-BeepLog "administrator stop; ended $count mixer process(es)"
    Write-Host 'Beep tone is stopped. It will not restart until BeepTone.ps1 -Start is run by an administrator.'
}

function Invoke-AdminStart {
    if (-not (Test-IsAdmin)) {
        Invoke-ElevatedSwitch -SwitchName '-Start'
        exit 0
    }
    Set-AdminDisabledFlag -Value 0
    Write-BeepLog 'administrator start'
    try {
        Start-ScheduledTask -TaskName $script:TaskName
        Write-Host 'Beep tone start requested through the user logon task.'
    } catch {
        Write-Host "The disable flag was cleared, but the logon task could not be started. The beep starts at the next sign-in, or run BeepTone.ps1 as the signed-in user. $($_.Exception.Message)"
        exit 1
    }
}

function Enter-HiddenSession {
    $consoleOnly = $ListDevices -or $SelfTest -or $Watchdog -or $Stop -or $Start
    if ($consoleOnly) { return }
    $hwnd = [BeepTone.NativeConsole]::GetConsoleWindow()
    $visible = $hwnd -ne [IntPtr]::Zero -and [BeepTone.NativeConsole]::IsWindowVisible($hwnd)
    $sta = [Threading.Thread]::CurrentThread.ApartmentState -eq 'STA'
    if (($visible -or -not $sta) -and -not $NoRelaunch) {
        Start-MixerProcess
        exit 0
    }
    if ($hwnd -ne [IntPtr]::Zero) {
        [void][BeepTone.NativeConsole]::ShowWindow($hwnd, 0)
    }
}

function Main {
    if ($SelfTest) {
        $result = [BeepTone.BeepSynth]::SelfTest()
        Write-Output $result
        if ($result.StartsWith('PASS')) { exit 0 }
        exit 1
    }
    if ($ListDevices) {
        Show-DeviceList
        exit 0
    }
    if ($Watchdog) {
        try { Invoke-Watchdog } catch {
            try { Write-BeepLog "watchdog error: $($_.Exception.Message)" } catch { }
            exit 0
        }
    }
    if ($Stop) {
        Invoke-AdminStop
        exit 0
    }
    if ($Start) {
        Invoke-AdminStart
        exit 0
    }

    Enter-HiddenSession
    try {
        Start-TrayApp
    } catch {
        try { Write-BeepLog "fatal: $($_.Exception.Message)" } catch { }
        try { Start-MixerProcess } catch { }
        exit 1
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Main
}
