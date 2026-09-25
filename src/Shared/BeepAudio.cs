// Beep Tone Injector: Windows audio (WASAPI), devices, and the mixer thread.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace BeepTone
{
    public sealed class AudioEndpoint
    {
        public string Id;
        public string Name;
        public string Flow;
        public int FormFactor = -1;
        public bool IsVirtual { get { return AudioDevices.IsVirtualName(Name); } }
        public override string ToString() { return Name ?? ""; }
    }

    public static class AudioDevices
    {
        // Endpoint form factors from mmdeviceapi.h.
        const int Speakers = 1, Headphones = 3, Microphone = 4, Headset = 5, Handset = 6;

        static readonly Regex PortNumber = new Regex(@"\(\d+-\s*");
        static string lastGoodSpeakerId;

        public static bool IsVirtualName(string name)
        {
            return VirtualDevices.IsVirtualName(name);
        }

        // "CABLE Output (VB-Audio Virtual Cable)" -> "CABLE Output", for messages people read.
        public static string ShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int at = name.IndexOf(" (", StringComparison.Ordinal);
            return at > 0 ? name.Substring(0, at) : name;
        }

        // Windows adds "2- " when the same model is plugged into another port.
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return PortNumber.Replace(name, "(").Trim();
        }

        public static AudioEndpoint[] List(string flow)
        {
            int dataFlow = IsRender(flow) ? 0 : 1;
            var list = new List<AudioEndpoint>();
            WasapiNative.ComInit();
            IMMDeviceEnumerator enumerator = WasapiNative.CreateEnumerator();
            try
            {
                IntPtr collectionPtr;
                WasapiNative.Check(enumerator.EnumAudioEndpoints(dataFlow, 1, out collectionPtr), "EnumAudioEndpoints");
                try
                {
                    var collection = (IMMDeviceCollection)Marshal.GetTypedObjectForIUnknown(collectionPtr, typeof(IMMDeviceCollection));
                    int count;
                    WasapiNative.Check(collection.GetCount(out count), "GetCount");
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr devicePtr;
                        if (collection.Item(i, out devicePtr) < 0 || devicePtr == IntPtr.Zero) continue;
                        try
                        {
                            var device = (IMMDevice)Marshal.GetTypedObjectForIUnknown(devicePtr, typeof(IMMDevice));
                            AudioEndpoint item = ReadEndpoint(device, flow);
                            if (item != null && !string.IsNullOrEmpty(item.Name)) list.Add(item);
                            Marshal.ReleaseComObject(device);
                        }
                        finally { Marshal.Release(devicePtr); }
                    }
                    Marshal.ReleaseComObject(collection);
                }
                finally { if (collectionPtr != IntPtr.Zero) Marshal.Release(collectionPtr); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
            return list.ToArray();
        }

        static bool IsRender(string flow)
        {
            return string.Equals(flow, "Render", StringComparison.OrdinalIgnoreCase);
        }

        // Matches by endpoint ID first, then by name without the port number.
        public static AudioEndpoint Match(AudioEndpoint[] all, string id, string name)
        {
            if (!string.IsNullOrEmpty(id))
            {
                foreach (AudioEndpoint e in all) if (e.Id == id) return e;
            }
            if (string.IsNullOrEmpty(name)) return null;
            string wanted = NormalizeName(name);
            foreach (AudioEndpoint e in all)
                if (string.Equals(NormalizeName(e.Name), wanted, StringComparison.OrdinalIgnoreCase)) return e;
            foreach (AudioEndpoint e in all)
                if (NormalizeName(e.Name).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) return e;
            return null;
        }

        public static AudioEndpoint[] ListMicrophones()
        {
            var mics = new List<AudioEndpoint>();
            foreach (AudioEndpoint e in List("Capture")) if (!e.IsVirtual) mics.Add(e);
            return mics.ToArray();
        }

        public static AudioEndpoint FindMicrophone(string id, string name)
        {
            return Match(ListMicrophones(), id, name);
        }

        // The saved microphone when it is connected. Otherwise a headset, then the Windows
        // default, then anything else. A virtual cable is never used as the microphone.
        public static AudioEndpoint ResolveCapture(string preferredId, string preferredName, out bool preferred)
        {
            AudioEndpoint[] mics = ListMicrophones();
            bool hasPreference = !string.IsNullOrEmpty(preferredId) || !string.IsNullOrEmpty(preferredName);
            if (hasPreference)
            {
                AudioEndpoint match = Match(mics, preferredId, preferredName);
                if (match != null) { preferred = true; return match; }
            }
            preferred = !hasPreference;
            foreach (AudioEndpoint e in mics) if (e.FormFactor == Headset || e.FormFactor == Handset) return e;
            foreach (int role in new int[] { 2, 0 })
            {
                AudioEndpoint def = GetDefaultEndpoint("Capture", role);
                if (def != null && !def.IsVirtual) return def;
            }
            foreach (AudioEndpoint e in mics) if (e.FormFactor == Microphone) return e;
            return mics.Length > 0 ? mics[0] : null;
        }

        public static AudioEndpoint ResolveRender(string preferredId, string preferredName)
        {
            AudioEndpoint[] all = List("Render");
            AudioEndpoint match = Match(all, preferredId, preferredName);
            if (match != null) return match;
            return Match(all, null, "CABLE Input");
        }

        // The recording side of a virtual cable: "CABLE Input (...)" pairs with "CABLE Output (...)".
        public static AudioEndpoint FindPairedCapture(AudioEndpoint render)
        {
            if (render == null || string.IsNullOrEmpty(render.Name)) return null;
            AudioEndpoint[] captures = List("Capture");
            int at = render.Name.IndexOf("Input", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                string wanted = NormalizeName(render.Name.Substring(0, at) + "Output" + render.Name.Substring(at + 5));
                foreach (AudioEndpoint e in captures)
                    if (string.Equals(NormalizeName(e.Name), wanted, StringComparison.OrdinalIgnoreCase)) return e;
            }
            if (render.Name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                return Match(captures, null, "CABLE Output");
            return null;
        }

        public static AudioEndpoint FindCableOutput()
        {
            return Match(List("Capture"), null, "CABLE Output");
        }

        public static void SetDefaultEndpoint(string deviceId, int role)
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try { WasapiNative.Check(policy.SetDefaultEndpoint(deviceId, role), "SetDefaultEndpoint"); }
            finally { Marshal.ReleaseComObject(policy); }
        }

        public static void SetDefaultMicrophone(string deviceId)
        {
            for (int role = 0; role <= 2; role++) SetDefaultEndpoint(deviceId, role);
        }

        static readonly string[] RoleNames = new string[] { "default", "multimedia", "communications" };

        // Makes the cable the default microphone for every role (when micId is given), and makes sure
        // the cable Beep Tone plays into (cableRenderId) is not the default speaker, which would send
        // the other party's voice into the call. Any other speaker the user picks, including other
        // virtual devices such as VoiceMeeter, is left alone. Returns one line per change.
        public static string[] EnsureDefaults(string micId, string cableRenderId)
        {
            var changes = new List<string>();
            if (!string.IsNullOrEmpty(micId))
            {
                for (int role = 0; role <= 2; role++)
                {
                    AudioEndpoint current = GetDefaultEndpoint("Capture", role);
                    if (current != null && current.Id == micId) continue;
                    SetDefaultEndpoint(micId, role);
                    changes.Add(RoleNames[role] + " microphone was " + (current == null ? "none" : current.Name) + ", set to the cable");
                }
            }
            AudioEndpoint replacement = null;
            for (int role = 0; role <= 2; role++)
            {
                AudioEndpoint current = GetDefaultEndpoint("Render", role);
                if (current == null) continue;
                if (string.IsNullOrEmpty(cableRenderId) || current.Id != cableRenderId)
                {
                    if (!current.IsVirtual) lastGoodSpeakerId = current.Id;
                    continue;
                }
                if (replacement == null) replacement = PickSpeaker();
                if (replacement == null) break;
                SetDefaultEndpoint(replacement.Id, role);
                changes.Add(RoleNames[role] + " speaker was " + current.Name + ", set to " + replacement.Name);
            }
            return changes.ToArray();
        }

        static AudioEndpoint PickSpeaker()
        {
            var speakers = new List<AudioEndpoint>();
            foreach (AudioEndpoint e in List("Render")) if (!e.IsVirtual) speakers.Add(e);
            foreach (AudioEndpoint e in speakers) if (e.Id == lastGoodSpeakerId) return e;
            foreach (AudioEndpoint e in speakers) if (e.FormFactor == Headset || e.FormFactor == Headphones) return e;
            foreach (AudioEndpoint e in speakers) if (e.FormFactor == Speakers) return e;
            return speakers.Count > 0 ? speakers[0] : null;
        }

        public static AudioEndpoint GetDefaultEndpoint(string flow, int role)
        {
            WasapiNative.ComInit();
            IMMDeviceEnumerator enumerator = WasapiNative.CreateEnumerator();
            try
            {
                IntPtr devicePtr;
                int hr = enumerator.GetDefaultAudioEndpoint(IsRender(flow) ? 0 : 1, role, out devicePtr);
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
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        public static bool IsMuted(string deviceId)
        {
            IMMDevice device = WasapiNative.OpenDevice(deviceId);
            try
            {
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                IntPtr ptr;
                WasapiNative.Check(device.Activate(ref iid, 23, IntPtr.Zero, out ptr), "Activate endpoint volume");
                try
                {
                    var volume = (IAudioEndpointVolume)Marshal.GetTypedObjectForIUnknown(ptr, typeof(IAudioEndpointVolume));
                    int muted;
                    int hr = volume.GetMute(out muted);
                    Marshal.ReleaseComObject(volume);
                    return hr >= 0 && muted != 0;
                }
                finally { Marshal.Release(ptr); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }

        // Process IDs with an active stream on the device.
        public static int[] ActiveSessionProcessIds(string deviceId)
        {
            var pids = new List<int>();
            IMMDevice device = WasapiNative.OpenDevice(deviceId);
            try
            {
                Guid iid = typeof(IAudioSessionManager2).GUID;
                IntPtr mgrPtr;
                WasapiNative.Check(device.Activate(ref iid, 23, IntPtr.Zero, out mgrPtr), "Activate session manager");
                try
                {
                    var mgr = (IAudioSessionManager2)Marshal.GetTypedObjectForIUnknown(mgrPtr, typeof(IAudioSessionManager2));
                    IntPtr enumPtr;
                    int hr = mgr.GetSessionEnumerator(out enumPtr);
                    Marshal.ReleaseComObject(mgr);
                    if (hr < 0 || enumPtr == IntPtr.Zero) return pids.ToArray();
                    try
                    {
                        var sessions = (IAudioSessionEnumerator)Marshal.GetTypedObjectForIUnknown(enumPtr, typeof(IAudioSessionEnumerator));
                        int count;
                        if (sessions.GetCount(out count) >= 0)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                IntPtr ctlPtr;
                                if (sessions.GetSession(i, out ctlPtr) < 0 || ctlPtr == IntPtr.Zero) continue;
                                try
                                {
                                    var ctl = (IAudioSessionControl2)Marshal.GetTypedObjectForIUnknown(ctlPtr, typeof(IAudioSessionControl2));
                                    int state, pid;
                                    if (ctl.GetState(out state) >= 0 && state == 1 && ctl.GetProcessId(out pid) >= 0 && pid != 0 && !pids.Contains(pid))
                                        pids.Add(pid);
                                    Marshal.ReleaseComObject(ctl);
                                }
                                finally { Marshal.Release(ctlPtr); }
                            }
                        }
                        Marshal.ReleaseComObject(sessions);
                    }
                    finally { Marshal.Release(enumPtr); }
                }
                finally { Marshal.Release(mgrPtr); }
            }
            finally { Marshal.ReleaseComObject(device); }
            return pids.ToArray();
        }

        static AudioEndpoint ReadEndpoint(IMMDevice device, string flow)
        {
            IntPtr idPtr;
            if (device.GetId(out idPtr) < 0 || idPtr == IntPtr.Zero) return null;
            string id = Marshal.PtrToStringUni(idPtr);
            Marshal.FreeCoTaskMem(idPtr);
            var endpoint = new AudioEndpoint { Id = id, Name = id, Flow = flow };
            IntPtr storePtr;
            if (device.OpenPropertyStore(0, out storePtr) < 0 || storePtr == IntPtr.Zero) return endpoint;
            try
            {
                var store = (IPropertyStore)Marshal.GetTypedObjectForIUnknown(storePtr, typeof(IPropertyStore));
                try
                {
                    string name = ReadString(store, WasapiNative.FriendlyNameKey);
                    if (!string.IsNullOrEmpty(name)) endpoint.Name = name;
                    endpoint.FormFactor = ReadUInt(store, WasapiNative.FormFactorKey);
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            finally { Marshal.Release(storePtr); }
            return endpoint;
        }

        static string ReadString(IPropertyStore store, PropertyKey key)
        {
            IntPtr pv = Marshal.AllocHGlobal(32);
            try
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                string value = null;
                if (store.GetValue(ref key, pv) >= 0 && Marshal.ReadInt16(pv, 0) == 31)
                {
                    IntPtr str = Marshal.ReadIntPtr(pv, 8);
                    if (str != IntPtr.Zero) value = Marshal.PtrToStringUni(str);
                }
                WasapiNative.PropVariantClear(pv);
                return value;
            }
            finally { Marshal.FreeHGlobal(pv); }
        }

        static int ReadUInt(IPropertyStore store, PropertyKey key)
        {
            IntPtr pv = Marshal.AllocHGlobal(32);
            try
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                int value = -1;
                if (store.GetValue(ref key, pv) >= 0 && Marshal.ReadInt16(pv, 0) == 19) value = Marshal.ReadInt32(pv, 8);
                WasapiNative.PropVariantClear(pv);
                return value;
            }
            finally { Marshal.FreeHGlobal(pv); }
        }
    }

    // Wakes the mixer as soon as a device is plugged in, removed, or a default changes,
    // instead of waiting for the next retry.
    public sealed class DeviceWatcher : IMMNotificationClient
    {
        static DeviceWatcher instance;
        static IMMDeviceEnumerator enumerator;
        static int defaultsChanged;
        static int generation;
        public static readonly AutoResetEvent DevicesChanged = new AutoResetEvent(false);

        public static int Generation { get { return Interlocked.CompareExchange(ref generation, 0, 0); } }

        public static bool ConsumeDefaultsChanged()
        {
            return Interlocked.Exchange(ref defaultsChanged, 0) == 1;
        }

        public static void Ensure()
        {
            if (instance != null) return;
            WasapiNative.ComInit();
            IMMDeviceEnumerator e = WasapiNative.CreateEnumerator();
            var watcher = new DeviceWatcher();
            int hr = e.RegisterEndpointNotificationCallback(watcher);
            if (hr < 0)
            {
                Marshal.ReleaseComObject(e);
                throw new COMException("RegisterEndpointNotificationCallback failed 0x" + hr.ToString("X8", CultureInfo.InvariantCulture), hr);
            }
            enumerator = e;
            instance = watcher;
        }

        static void Changed()
        {
            Interlocked.Increment(ref generation);
            DevicesChanged.Set();
        }

        public void OnDeviceStateChanged(string deviceId, int newState) { Changed(); }
        public void OnDeviceAdded(string deviceId) { Changed(); }
        public void OnDeviceRemoved(string deviceId) { Changed(); }
        public void OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId)
        {
            Interlocked.Exchange(ref defaultsChanged, 1);
        }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }

    public static class BeepHeartbeat
    {
        static Timer timer;
        static int exiting;
        public static volatile string IdleState = HeartbeatState.Setup;

        public static void Start()
        {
            if (timer != null) return;
            timer = new Timer(Tick, null, 0, 1000);
        }

        static void Tick(object unused)
        {
            try
            {
                BeepMixer mixer = BeepMixer.Current;
                var info = new HeartbeatInfo();
                info.TimestampUtc = DateTime.UtcNow;
                info.ProcessId = BeepMixer.ProcessId;
                info.LastBeepUtc = BeepMixer.LastBeepUtc;
                info.State = mixer == null ? IdleState : mixer.HeartbeatState;
                if (mixer != null)
                {
                    double stalled = mixer.StalledSeconds;
                    if (stalled > 5)
                    {
                        // Let the heartbeat go stale so a watchdog sees the hang, then exit so it can restart us.
                        if (stalled > 20 && Interlocked.Exchange(ref exiting, 1) == 0)
                        {
                            BeepFiles.Log("audio thread has not responded for " + ((int)stalled).ToString(CultureInfo.InvariantCulture) + " seconds; exiting so the watchdog restarts the mixer");
                            BeepEvents.Write(BeepEvents.Problem, "The beep mixer stopped responding and is restarting.", true);
                            Environment.Exit(3);
                        }
                        return;
                    }
                }
                BeepFiles.WriteHeartbeat(info);
            }
            catch { }
        }
    }

    public sealed class BeepMixer
    {
        public static BeepMixer Current;
        public static readonly int ProcessId = Process.GetCurrentProcess().Id;
        static long lastBeepUtcTicks;

        public static DateTime LastBeepUtc
        {
            get
            {
                long t = Interlocked.Read(ref lastBeepUtcTicks);
                return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
            }
        }

        // Preferences from setup. The mixer falls back to another microphone while the preferred
        // one is missing, and goes back to it when it returns. Fallbacks are never saved.
        public string CaptureId = "";
        public string CaptureName = "";
        public string RenderId = "";
        public string RenderName = "CABLE Input";
        public BeepSettings Settings = new BeepSettings();
        public string[] IgnoredMicApps = new string[0];

        int beepRequested;
        int restartRequested;
        int stopRequested;
        int beepPaused;
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        readonly object gate = new object();
        readonly Stopwatch clock = Stopwatch.StartNew();
        string status = "Starting";
        string balloon;
        string balloonTitle;
        string problem;
        string muteProblem;
        string bypassProblem;
        string verifyProblem;
        string cableUsers = "";
        string restartReason;
        long lastBeepTicks = -1;
        long runningSinceTicks = -1;
        long sessionTicks = -1;
        int beepCount;
        int heardCount;
        int missedCount;
        int missedInARow;
        bool running;
        bool inSession;
        bool disabled;
        AudioEndpoint activeCapture;
        AudioEndpoint activeRender;
        AudioEndpoint activeTap;
        bool usingPreferred = true;
        string resolvedCaptureId;
        string resolvedRenderId;
        string lastFallbackId;
        readonly Dictionary<int, string> processNames = new Dictionary<int, string>();
        readonly Dictionary<int, string> appNames = new Dictionary<int, string>();

        public string Status { get { lock (gate) return status; } }
        public bool IsRunning { get { lock (gate) return running; } }
        public int BeepCount { get { lock (gate) return beepCount; } }
        public int HeardCount { get { lock (gate) return heardCount; } }
        public int MissedCount { get { lock (gate) return missedCount; } }
        public string Problem { get { lock (gate) return problem ?? ""; } }
        public string MuteProblem { get { lock (gate) return muteProblem ?? ""; } }
        public string BypassProblem { get { lock (gate) return bypassProblem ?? ""; } }
        public string VerifyProblem { get { lock (gate) return verifyProblem ?? ""; } }
        public string CableUsers { get { lock (gate) return cableUsers; } }
        public bool UsingPreferredCapture { get { lock (gate) return usingPreferred; } }
        public string ActiveCaptureName { get { lock (gate) return activeCapture == null ? "" : activeCapture.Name; } }
        public string ActiveRenderName { get { lock (gate) return activeRender == null ? "" : activeRender.Name; } }
        public string ResolvedCaptureId { get { lock (gate) return resolvedCaptureId ?? ""; } }
        public string ResolvedRenderId { get { lock (gate) return resolvedRenderId ?? ""; } }
        public bool CanVerify { get { lock (gate) return activeTap != null; } }

        // Monotonic, so clock changes and daylight saving cannot fake or hide a missed beep.
        public double SecondsSinceLastBeep
        {
            get { lock (gate) return lastBeepTicks < 0 ? -1 : (clock.ElapsedTicks - lastBeepTicks) / (double)Stopwatch.Frequency; }
        }

        public double SecondsRunning
        {
            get { lock (gate) return runningSinceTicks < 0 ? -1 : (clock.ElapsedTicks - runningSinceTicks) / (double)Stopwatch.Frequency; }
        }

        public DateTime LastBeepLocal
        {
            get
            {
                DateTime utc = LastBeepUtc;
                return utc == DateTime.MinValue ? DateTime.MinValue : utc.ToLocalTime();
            }
        }

        public string HeartbeatState
        {
            get
            {
                lock (gate)
                {
                    if (disabled) return BeepTone.HeartbeatState.Stopped;
                    if (IsBeepPaused) return BeepTone.HeartbeatState.Paused;
                    return running ? BeepTone.HeartbeatState.Running : BeepTone.HeartbeatState.Reconnecting;
                }
            }
        }

        internal double StalledSeconds
        {
            get
            {
                lock (gate)
                {
                    if (!inSession || sessionTicks < 0) return 0;
                    return (clock.ElapsedTicks - sessionTicks) / (double)Stopwatch.Frequency;
                }
            }
        }

        public void SetBeepPaused(bool paused) { Interlocked.Exchange(ref beepPaused, paused ? 1 : 0); }
        public bool IsBeepPaused { get { return Interlocked.CompareExchange(ref beepPaused, 0, 0) != 0; } }
        public void RequestBeep() { Interlocked.Exchange(ref beepRequested, 1); }

        public void RequestRestart() { RequestRestart("settings changed"); }

        public void RequestRestart(string reason)
        {
            lock (gate) restartReason = reason;
            Interlocked.Exchange(ref restartRequested, 1);
        }

        public void RequestStop()
        {
            Interlocked.Exchange(ref stopRequested, 1);
            stopEvent.Set();
        }

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
            BeepHeartbeat.Start();
            var thread = new Thread(Background);
            thread.IsBackground = true;
            thread.Name = "BeepToneMixer";
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
            var monitor = new Thread(Monitor);
            monitor.IsBackground = true;
            monitor.Name = "BeepToneMonitor";
            monitor.SetApartmentState(ApartmentState.MTA);
            monitor.Start();
        }

        void SetStatus(string text)
        {
            lock (gate)
            {
                status = text;
                if (text == "Running") runningSinceTicks = clock.ElapsedTicks;
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

        internal bool StopRequested() { return Interlocked.CompareExchange(ref stopRequested, 0, 0) != 0; }
        internal bool ConsumeRestart() { return Interlocked.Exchange(ref restartRequested, 0) == 1; }
        internal bool ConsumeBeepRequest() { return Interlocked.Exchange(ref beepRequested, 0) == 1; }
        internal void Tick() { lock (gate) sessionTicks = clock.ElapsedTicks; }
        internal long Now { get { return clock.ElapsedTicks; } }

        void Background()
        {
            WasapiNative.ComInit();
            AudioPerf.OptOutOfPowerThrottling();
            try { DeviceWatcher.Ensure(); }
            catch (Exception ex) { BeepFiles.Log("device notifications unavailable: " + ex.Message); }

            int failures = 0;
            string lastError = null;
            long lastErrorLogged = 0;
            while (!StopRequested())
            {
                if (AdminFlag.IsDisabled())
                {
                    MarkDisabled();
                    return;
                }
                try
                {
                    RunSession(failures);
                    failures = 0;
                    lastError = null;
                    if (StopRequested()) break;
                    string reason;
                    lock (gate) { reason = restartReason ?? "settings changed"; restartReason = null; }
                    SetStatus("Restarting");
                    BeepFiles.Log("mixer restarting: " + reason);
                }
                catch (Exception ex)
                {
                    lock (gate) { running = false; inSession = false; }
                    if (StopRequested()) break;
                    failures++;
                    string message = WasapiNative.Describe(ex);
                    long now = clock.ElapsedTicks;
                    bool longAgo = (now - lastErrorLogged) / (double)Stopwatch.Frequency > 300;
                    if (message != lastError || longAgo)
                    {
                        BeepFiles.Log("audio interrupted: " + message + (message == lastError ? " (still failing after " + failures + " attempts)" : ""));
                        lastError = message;
                        lastErrorLogged = now;
                    }
                    SetStatus("Reconnecting");
                    SetProblem("The beep is not going out on calls. " + WasapiNative.Friendly(ex) + " Trying again.");
                    int delay = Math.Min(10000, 500 << Math.Min(failures, 5));
                    WaitHandle.WaitAny(new WaitHandle[] { stopEvent, DeviceWatcher.DevicesChanged }, delay);
                }
            }
            lock (gate) { running = false; inSession = false; }
        }

        void MarkDisabled()
        {
            lock (gate) { running = false; inSession = false; disabled = true; }
            SetStatus("Stopped by administrator");
            BeepFiles.Log("admin disable flag is set; mixer exiting");
            RequestStop();
        }

        void RunSession(int previousFailures)
        {
            lock (gate) { inSession = true; sessionTicks = clock.ElapsedTicks; }
            bool preferred;
            AudioEndpoint capture = AudioDevices.ResolveCapture(CaptureId, CaptureName, out preferred);
            if (capture == null) throw new InvalidOperationException("No microphone is connected.");
            AudioEndpoint render = AudioDevices.ResolveRender(RenderId, RenderName);
            if (render == null)
                throw new InvalidOperationException("The virtual cable is not installed or is turned off.");
            AudioEndpoint tap = AudioDevices.FindPairedCapture(render);

            if (!preferred)
            {
                if (capture.Id != lastFallbackId)
                {
                    BeepFiles.Log("microphone '" + CaptureName + "' is not connected; using '" + capture.Name + "' until it is back");
                    SetBalloon("Beep tone", "Using " + capture.Name + " until " + CaptureName + " is connected again.");
                    lastFallbackId = capture.Id;
                }
            }
            else if (lastFallbackId != null)
            {
                BeepFiles.Log("back on the saved microphone '" + capture.Name + "'");
                SetBalloon("Beep tone", "Back on " + capture.Name + ".");
                lastFallbackId = null;
            }
            lock (gate)
            {
                activeCapture = capture;
                activeRender = render;
                activeTap = null;
                usingPreferred = preferred;
                if (preferred && (!string.IsNullOrEmpty(CaptureId) || !string.IsNullOrEmpty(CaptureName))) resolvedCaptureId = capture.Id;
                resolvedRenderId = render.Id;
                missedInARow = 0;
                verifyProblem = null;
            }

            BeepFiles.Log("opening capture '" + capture.Name + "' and playback '" + render.Name + "'"
                + (tap == null ? "; no matching recording device to check the beep" : "; checking the beep on '" + tap.Name + "'"));
            using (var session = new MixSession(capture, render, tap, Settings.Clamped(), this))
            {
                session.Open();
                lock (gate)
                {
                    running = true;
                    activeTap = session.TapActive ? tap : null;
                }
                if (previousFailures > 0) BeepFiles.Log("audio recovered");
                SetProblem(null);
                SetStatus("Running");
                session.Loop();
            }
            lock (gate) { running = false; inSession = false; }
        }

        internal void NoteBeep(double toneDb, bool willVerify)
        {
            lock (gate)
            {
                lastBeepTicks = clock.ElapsedTicks;
                beepCount++;
            }
            Interlocked.Exchange(ref lastBeepUtcTicks, DateTime.UtcNow.Ticks);
            if (!willVerify)
                BeepFiles.Log("beep tone=" + toneDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS");
        }

        internal void NoteHeard(double toneDb, double heardDb, string where)
        {
            bool cleared;
            lock (gate)
            {
                cleared = verifyProblem != null;
                heardCount++;
                missedInARow = 0;
                verifyProblem = null;
            }
            BeepFiles.Log("beep tone=" + toneDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS heard="
                + heardDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS on " + where);
            if (cleared) BeepFiles.Log("beep is reaching " + where + " again");
        }

        internal void NoteMissed(double toneDb, string where)
        {
            bool raise = false;
            lock (gate)
            {
                missedCount++;
                missedInARow++;
                if (missedInARow >= 2 && verifyProblem == null)
                {
                    verifyProblem = "The beep is not reaching the softphone at full level. In Windows Sound settings, set CABLE Input and CABLE Output to 100.";
                    raise = true;
                }
            }
            BeepFiles.Log("beep tone=" + toneDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS NOT heard on " + where);
            if (raise) BeepFiles.Log("beep problem: two beeps in a row did not reach " + where);
        }

        string lastMonitorError;

        void Monitor()
        {
            WasapiNative.ComInit();
            while (!stopEvent.WaitOne(2000))
            {
                if (AdminFlag.IsDisabled())
                {
                    MarkDisabled();
                    return;
                }
                MonitorStep("app check", CheckApps);
                MonitorStep("mute check", CheckMutes);
                MonitorStep("microphone check", CheckPreferredReturned);
            }
        }

        // Devices come and go between listing and opening them, so one failed check is normal.
        // Log each distinct failure once.
        void MonitorStep(string name, ThreadStart step)
        {
            try { step(); }
            catch (Exception ex)
            {
                string text = name + ": " + WasapiNative.Describe(ex);
                if (text != lastMonitorError) BeepFiles.Log(text);
                lastMonitorError = text;
            }
        }

        // An app recording straight from the headset skips the cable and has no beep.
        void CheckApps()
        {
            AudioEndpoint tap;
            lock (gate) tap = activeTap;
            var apps = new List<string>();
            var details = new List<string>();
            bool headset = false;
            foreach (AudioEndpoint mic in AudioDevices.ListMicrophones())
            {
                int[] pids;
                try { pids = AudioDevices.ActiveSessionProcessIds(mic.Id); }
                catch { continue; }
                foreach (int pid in pids)
                {
                    if (pid == ProcessId) continue;
                    string exe = ProcessName(pid);
                    if (IsIgnored(exe)) continue;
                    string app = AppName(pid, exe);
                    if (!apps.Contains(app)) apps.Add(app);
                    details.Add(exe + " (process " + pid.ToString(CultureInfo.InvariantCulture) + ") on '" + mic.Name + "'");
                    if (mic.FormFactor == 5 || mic.FormFactor == 6) headset = true;
                }
            }
            var users = new List<string>();
            AudioEndpoint cable = tap ?? AudioDevices.FindCableOutput();
            if (cable != null)
            {
                foreach (int pid in AudioDevices.ActiveSessionProcessIds(cable.Id))
                {
                    if (pid == ProcessId) continue;
                    string app = AppName(pid, ProcessName(pid));
                    if (!users.Contains(app)) users.Add(app);
                }
            }
            string text = null;
            if (apps.Count > 0)
            {
                string mic = headset ? "your headset" : "the microphone";
                text = apps.Count == 1
                    ? apps[0] + " is using " + mic + " directly, so its calls have no beep. In " + apps[0] + ", set the microphone to CABLE Output."
                    : JoinNames(apps) + " are using " + mic + " directly, so their calls have no beep. Set their microphone to CABLE Output.";
            }
            string previous;
            lock (gate)
            {
                previous = bypassProblem;
                bypassProblem = text;
                cableUsers = string.Join(", ", users.ToArray());
            }
            if (text != previous)
            {
                if (text != null)
                {
                    string detail = string.Join("; ", details.ToArray());
                    BeepFiles.Log("beep bypass: " + detail);
                    BeepEvents.Write(BeepEvents.MicBypass, text + Environment.NewLine + "Details: " + detail, true);
                }
                else
                {
                    BeepFiles.Log("no app is using the microphone directly");
                    BeepEvents.Write(BeepEvents.MicBypassCleared, "No app is using the microphone directly.", false);
                }
            }
        }

        static string JoinNames(List<string> names)
        {
            if (names.Count == 1) return names[0];
            return string.Join(", ", names.GetRange(0, names.Count - 1).ToArray()) + " and " + names[names.Count - 1];
        }

        // The name people know an app by ("Cisco Webex", "Microsoft Teams"), from its file description.
        string AppName(int pid, string exe)
        {
            string name;
            if (appNames.TryGetValue(pid, out name)) return name;
            name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
            try
            {
                string description = Process.GetProcessById(pid).MainModule.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
            }
            catch { }
            if (appNames.Count > 200) appNames.Clear();
            appNames[pid] = name;
            return name;
        }

        string ProcessName(int pid)
        {
            string name;
            if (processNames.TryGetValue(pid, out name)) return name;
            try { name = Process.GetProcessById(pid).ProcessName + ".exe"; }
            catch { name = "process " + pid.ToString(CultureInfo.InvariantCulture); }
            if (processNames.Count > 200) processNames.Clear();
            processNames[pid] = name;
            return name;
        }

        // The Sound control panel (rundll32) and Settings open every microphone for their level meters.
        // Another copy of Beep Tone (a second signed-in user, or a test mix) is mixing the beep itself.
        static readonly string[] AlwaysIgnored = new string[] { "rundll32.exe", "SystemSettings.exe", "BeepTone.exe", "BeepToneCtl.exe" };

        bool IsIgnored(string processName)
        {
            foreach (string item in AlwaysIgnored)
                if (string.Equals(item, processName, StringComparison.OrdinalIgnoreCase)) return true;
            string[] ignored = IgnoredMicApps ?? new string[0];
            foreach (string item in ignored)
            {
                if (string.IsNullOrEmpty(item)) continue;
                string a = item.Trim();
                if (!a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a += ".exe";
                if (string.Equals(a, processName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        void CheckMutes()
        {
            AudioEndpoint render, tap;
            lock (gate) { render = activeRender; tap = activeTap; }
            string text = null;
            if (render != null && AudioDevices.IsMuted(render.Id))
                text = AudioDevices.ShortName(render.Name) + " is muted in Windows, so calls have no microphone and no beep. Unmute it in Windows Sound settings.";
            else if (tap != null && AudioDevices.IsMuted(tap.Id))
                text = AudioDevices.ShortName(tap.Name) + " is muted in Windows, so calls have no microphone and no beep. Unmute it in Windows Sound settings.";
            string previous;
            lock (gate) { previous = muteProblem; muteProblem = text; }
            if (text != previous) BeepFiles.Log(text ?? "virtual cable unmuted");
        }

        void CheckPreferredReturned()
        {
            bool check;
            lock (gate) check = running && !usingPreferred;
            if (!check) return;
            AudioEndpoint mic = AudioDevices.FindMicrophone(CaptureId, CaptureName);
            if (mic != null) RequestRestart("saved microphone '" + mic.Name + "' is connected again");
        }
    }

    sealed class MixSession : IDisposable
    {
        const long CaptureBufferHns = 1000000;
        const int BacklogTargetMs = 30;
        const int BacklogMaxMs = 150;
        const double StallSeconds = 1.0;

        readonly AudioEndpoint captureEndpoint;
        readonly AudioEndpoint renderEndpoint;
        readonly AudioEndpoint tapEndpoint;
        readonly BeepSettings settings;
        readonly BeepMixer mixer;
        IMMDevice captureDevice, renderDevice, tapDevice;
        IAudioClient captureClient, renderClient, tapClient;
        IAudioCaptureClient capture, tap;
        IAudioRenderClient render;
        FormatInfo captureFormat, renderFormat, tapFormat;
        readonly AutoResetEvent captureEvent = new AutoResetEvent(false);
        readonly AutoResetEvent renderEvent = new AutoResetEvent(false);
        readonly AutoResetEvent tapEvent = new AutoResetEvent(false);
        SampleConverter captureConverter, tapConverter;
        SampleWriter writer;
        float[] captureMono, tapMono, output;
        int renderBufferFrames, renderTargetFrames;
        bool captureStarted, renderStarted, tapStarted;
        long captureGlitches;

        public MixSession(AudioEndpoint capture, AudioEndpoint render, AudioEndpoint tap, BeepSettings settings, BeepMixer mixer)
        {
            captureEndpoint = capture;
            renderEndpoint = render;
            tapEndpoint = tap;
            this.settings = settings;
            this.mixer = mixer;
        }

        public bool TapActive { get { return tap != null; } }

        public void Open()
        {
            captureDevice = WasapiNative.OpenDevice(captureEndpoint.Id);
            renderDevice = WasapiNative.OpenDevice(renderEndpoint.Id);
            captureClient = WasapiNative.InitializeCapture(captureDevice, captureEvent, CaptureBufferHns, out captureFormat);
            renderClient = WasapiNative.InitializeRender(renderDevice, renderEvent, captureFormat.Rate, out renderFormat);
            uint frames;
            WasapiNative.Check(renderClient.GetBufferSize(out frames), "render GetBufferSize");
            renderBufferFrames = (int)frames;
            long defaultPeriod, minimumPeriod;
            int periodFrames = renderFormat.Rate / 100;
            if (renderClient.GetDevicePeriod(out defaultPeriod, out minimumPeriod) >= 0 && defaultPeriod > 0)
                periodFrames = (int)(defaultPeriod * renderFormat.Rate / 10000000);
            renderTargetFrames = Math.Max(periodFrames * 2, renderFormat.Rate * 20 / 1000);
            if (renderTargetFrames > renderBufferFrames - periodFrames) renderTargetFrames = Math.Max(periodFrames, renderBufferFrames - periodFrames);
            capture = WasapiNative.GetCapture(captureClient);
            render = WasapiNative.GetRender(renderClient);
            WasapiNative.TryDisableDucking(renderDevice, WasapiNative.SessionGuid);

            WasapiNative.Check(captureClient.GetBufferSize(out frames), "capture GetBufferSize");
            captureConverter = new SampleConverter(captureFormat, (int)frames);
            captureMono = new float[frames];
            writer = new SampleWriter(renderFormat, renderBufferFrames);
            output = new float[renderBufferFrames];

            if (tapEndpoint != null) OpenTap();
            BeepFiles.Log("capture " + captureFormat + "; render " + renderFormat + "; render buffer "
                + renderBufferFrames + " frames, keeping " + renderTargetFrames + " queued"
                + (tapFormat != null ? "; check " + tapFormat : ""));
        }

        void OpenTap()
        {
            try
            {
                tapDevice = WasapiNative.OpenDevice(tapEndpoint.Id);
                tapClient = WasapiNative.InitializeCapture(tapDevice, tapEvent, CaptureBufferHns, out tapFormat);
                uint frames;
                WasapiNative.Check(tapClient.GetBufferSize(out frames), "check GetBufferSize");
                tap = WasapiNative.GetCapture(tapClient);
                tapConverter = new SampleConverter(tapFormat, (int)frames);
                tapMono = new float[frames];
            }
            catch (Exception ex)
            {
                BeepFiles.Log("cannot check the beep on '" + tapEndpoint.Name + "': " + WasapiNative.Describe(ex));
                Release(tap); tap = null;
                Release(tapClient); tapClient = null;
                Release(tapDevice); tapDevice = null;
                tapFormat = null;
            }
        }

        public void Loop()
        {
            IntPtr mmcss = AudioPerf.EnterAudioPriority();
            try { Run(); }
            finally { AudioPerf.LeaveAudioPriority(mmcss); }
        }

        void Run()
        {
            int renderRate = renderFormat.Rate;
            float[] beep = BeepSynth.Create(renderRate, settings);
            var scheduler = new BeepScheduler(beep, (long)renderRate * settings.IntervalSeconds);
            var core = new MixCore(captureFormat.Rate, renderRate, BacklogTargetMs, BacklogMaxMs);
            double toneDb = settings.LevelDbfs;
            double freq = Stopwatch.Frequency;

            ToneDetector detector = null;
            BeepBurstFinder finder = null;
            double threshold = 0;
            long tapBlocks = 0;
            var pending = new List<long>();
            if (tap != null)
            {
                detector = new ToneDetector(tapFormat.Rate, settings.FrequencyHz, 20);
                double seconds = settings.DurationMs / 1000.0;
                finder = new BeepBurstFinder(detector.BlockSeconds, seconds * 0.4, seconds * 2 + 0.1);
                threshold = settings.PeakGain() * Math.Pow(10, -15 / 20.0);
            }

            WasapiNative.Check(captureClient.Start(), "capture Start");
            captureStarted = true;
            long lastCapture = mixer.Now;
            long primeUntil = mixer.Now + (long)(0.5 * freq);
            // The cable takes its first queue of audio at once when it starts, so collect that much on
            // top of the normal backlog before starting, or the first read runs dry.
            double prime = core.TargetFill + renderTargetFrames * (double)captureFormat.Rate / renderRate;
            while (core.Fill < prime && mixer.Now < primeUntil)
            {
                captureEvent.WaitOne(50);
                mixer.Tick();
                if (DrainCapture(core)) lastCapture = mixer.Now;
            }
            FillRender(core, scheduler, toneDb, pending);
            WasapiNative.Check(renderClient.Start(), "render Start");
            renderStarted = true;
            if (tap != null)
            {
                if (tapClient.Start() >= 0) tapStarted = true;
            }

            // Raw handles, because WaitHandle.WaitAny copies its array on every call and the
            // audio loop must not create garbage.
            IntPtr[] handles = tap != null
                ? new IntPtr[] { Raw(captureEvent), Raw(renderEvent), Raw(tapEvent) }
                : new IntPtr[] { Raw(captureEvent), Raw(renderEvent) };
            long lastRenderProgress = mixer.Now;
            long nextStats = mixer.Now + (long)(600 * freq);
            // Windows often flags the first packet after Start as a discontinuity; that is not a glitch.
            core.ResetStats();
            captureGlitches = 0;
            long seenGaps = 0;
            long nextGapLog = 0;

            while (true)
            {
                if (mixer.StopRequested() || mixer.ConsumeRestart()) return;
                AudioPerf.WaitAny(handles, 100);
                mixer.Tick();
                long now = mixer.Now;

                if (DrainCapture(core)) lastCapture = now;
                if (FillRender(core, scheduler, toneDb, pending)) lastRenderProgress = now;

                if (tap != null)
                {
                    DrainTap(detector, finder, threshold, ref tapBlocks, pending, toneDb, freq);
                    for (int i = pending.Count - 1; i >= 0; i--)
                    {
                        if ((now - pending[i]) / freq > 1.5)
                        {
                            pending.RemoveAt(i);
                            mixer.NoteMissed(toneDb, tapEndpoint.Name);
                        }
                    }
                }

                if ((now - lastCapture) / freq > StallSeconds)
                    throw new InvalidOperationException("The microphone stopped sending audio.");
                if ((now - lastRenderProgress) / freq > StallSeconds)
                    throw new InvalidOperationException("The virtual cable stopped taking audio.");

                // Log when a gap happens, at most once a minute, so it can be matched to what else was going on.
                long gaps = core.Underruns + core.Drops + captureGlitches;
                if (gaps != seenGaps && now >= nextGapLog)
                {
                    BeepFiles.Log("audio gap: " + core.Underruns + " underruns, " + core.Drops + " trims, " + captureGlitches
                        + " capture glitches since the last stats; backlog now " + (core.Fill * 1000.0 / captureFormat.Rate).ToString("0", CultureInfo.InvariantCulture) + " ms");
                    seenGaps = gaps;
                    nextGapLog = now + (long)(60 * freq);
                }

                if (now >= nextStats)
                {
                    BeepFiles.Log("audio stats: backlog " + (core.MinFillSeen == double.MaxValue ? 0 : core.MinFillSeen * 1000.0 / captureFormat.Rate).ToString("0", CultureInfo.InvariantCulture)
                        + "-" + (core.MaxFillSeen * 1000.0 / captureFormat.Rate).ToString("0", CultureInfo.InvariantCulture) + " ms, clock correction "
                        + core.CorrectionPpm.ToString("0", CultureInfo.InvariantCulture) + " ppm, " + core.Underruns + " underruns, "
                        + core.Drops + " trims, " + captureGlitches + " capture glitches");
                    core.ResetStats();
                    captureGlitches = 0;
                    seenGaps = 0;
                    nextStats = now + (long)(600 * freq);
                }
            }
        }

        static IntPtr Raw(WaitHandle handle)
        {
            return handle.SafeWaitHandle.DangerousGetHandle();
        }

        bool DrainCapture(MixCore core)
        {
            bool any = false;
            while (true)
            {
                int packet;
                WasapiNative.Check(capture.GetNextPacketSize(out packet), "capture GetNextPacketSize");
                if (packet <= 0) break;
                IntPtr data;
                int frames, flags;
                long devicePosition, qpcPosition;
                WasapiNative.Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition), "capture GetBuffer");
                try
                {
                    if (frames <= 0) continue;
                    any = true;
                    if ((flags & 1) != 0) captureGlitches++;
                    if ((flags & 2) != 0 || data == IntPtr.Zero) core.PushSilence(frames);
                    else
                    {
                        captureConverter.ToMono(data, frames, captureMono);
                        core.Push(captureMono, frames);
                    }
                }
                finally { capture.ReleaseBuffer(frames); }
            }
            return any;
        }

        bool FillRender(MixCore core, BeepScheduler scheduler, double toneDb, List<long> pending)
        {
            uint padding;
            WasapiNative.Check(renderClient.GetCurrentPadding(out padding), "render GetCurrentPadding");
            int want = renderTargetFrames - (int)padding;
            if (want <= 0) return false;
            if (want > output.Length) want = output.Length;
            core.Read(output, want);
            int started = scheduler.Mix(output, want, mixer.IsBeepPaused, mixer.ConsumeBeepRequest());
            SoftLimiter.Apply(output, want);
            IntPtr data;
            WasapiNative.Check(render.GetBuffer(want, out data), "render GetBuffer");
            try { writer.Write(output, want, data); }
            finally { render.ReleaseBuffer(want, 0); }
            if (started >= 0)
            {
                bool verify = tap != null;
                mixer.NoteBeep(toneDb, verify);
                if (verify)
                {
                    double delay = (padding + started) / (double)renderFormat.Rate;
                    pending.Add(mixer.Now + (long)(delay * Stopwatch.Frequency));
                }
            }
            return true;
        }

        void DrainTap(ToneDetector detector, BeepBurstFinder finder, double threshold, ref long blocks,
            List<long> pending, double toneDb, double freq)
        {
            while (true)
            {
                int packet;
                if (tap.GetNextPacketSize(out packet) < 0 || packet <= 0) break;
                IntPtr data;
                int frames, flags;
                long devicePosition, qpcPosition;
                if (tap.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition) < 0) break;
                try
                {
                    if (frames <= 0) continue;
                    bool silent = (flags & 2) != 0 || data == IntPtr.Zero;
                    if (!silent) tapConverter.ToMono(data, frames, tapMono);
                    for (int i = 0; i < frames; i++)
                    {
                        if (!detector.Feed(silent ? 0f : tapMono[i])) continue;
                        blocks++;
                        double streamTime = blocks * detector.BlockSeconds;
                        bool tone = detector.LastAmplitude >= threshold && detector.LastNeighbourRatio >= 4;
                        double start, peak;
                        if (!finder.Block(tone, streamTime, detector.LastAmplitude, out start, out peak)) continue;
                        // Convert the burst start from stream time to the monotonic clock.
                        long heardAt = mixer.Now - (long)((streamTime - start + (frames - i) / (double)tapFormat.Rate) * freq);
                        int match = -1;
                        for (int k = 0; k < pending.Count; k++)
                        {
                            double offset = (heardAt - pending[k]) / freq;
                            if (offset > -0.3 && offset < 1.0) { match = k; break; }
                        }
                        if (match < 0) continue;
                        pending.RemoveAt(match);
                        double heardDb = 20 * Math.Log10(Math.Max(1e-9, peak / Math.Sqrt(2)));
                        mixer.NoteHeard(toneDb, heardDb, tapEndpoint.Name);
                    }
                }
                finally { tap.ReleaseBuffer(frames); }
            }
        }

        public void Dispose()
        {
            try { if (captureStarted) captureClient.Stop(); } catch { }
            try { if (renderStarted) renderClient.Stop(); } catch { }
            try { if (tapStarted) tapClient.Stop(); } catch { }
            Release(capture);
            Release(render);
            Release(tap);
            Release(captureClient);
            Release(renderClient);
            Release(tapClient);
            Release(captureDevice);
            Release(renderDevice);
            Release(tapDevice);
            captureEvent.Close();
            renderEvent.Close();
            tapEvent.Close();
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

    // Converts a device buffer to mono floats without allocating per packet.
    sealed class SampleConverter
    {
        readonly FormatInfo format;
        readonly float[] floats;
        readonly short[] shorts;
        readonly int[] ints;
        readonly byte[] bytes;

        public SampleConverter(FormatInfo format, int maxFrames)
        {
            this.format = format;
            int samples = maxFrames * Math.Max(1, format.Channels);
            if (format.IsFloat) floats = new float[samples];
            else if (format.Bits == 16) shorts = new short[samples];
            else if (format.Bits == 32) ints = new int[samples];
            else bytes = new byte[maxFrames * format.BlockAlign];
        }

        public void ToMono(IntPtr data, int frames, float[] dest)
        {
            int ch = Math.Max(1, format.Channels);
            int samples = frames * ch;
            if (format.IsFloat)
            {
                if (ch == 1) { Marshal.Copy(data, dest, 0, frames); return; }
                Marshal.Copy(data, floats, 0, samples);
                for (int i = 0, at = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++) sum += floats[at++];
                    dest[i] = sum / ch;
                }
                return;
            }
            if (format.Bits == 16)
            {
                Marshal.Copy(data, shorts, 0, samples);
                for (int i = 0, at = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++) sum += shorts[at++] / 32768f;
                    dest[i] = sum / ch;
                }
                return;
            }
            if (format.Bits == 32)
            {
                Marshal.Copy(data, ints, 0, samples);
                for (int i = 0, at = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++) sum += ints[at++] / 2147483648f;
                    dest[i] = sum / ch;
                }
                return;
            }
            // 24-bit packed.
            Marshal.Copy(data, bytes, 0, frames * format.BlockAlign);
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                int at = i * format.BlockAlign;
                for (int c = 0; c < ch; c++, at += 3)
                    sum += ((bytes[at] << 8 | bytes[at + 1] << 16 | bytes[at + 2] << 24) >> 8) / 8388608f;
                dest[i] = sum / ch;
            }
        }
    }

    // Writes mono floats into a device buffer of any channel count.
    sealed class SampleWriter
    {
        readonly FormatInfo format;
        readonly float[] floats;
        readonly short[] shorts;
        readonly int[] ints;
        readonly byte[] bytes;

        public SampleWriter(FormatInfo format, int maxFrames)
        {
            this.format = format;
            int samples = maxFrames * Math.Max(1, format.Channels);
            if (format.IsFloat) floats = new float[samples];
            else if (format.Bits == 16) shorts = new short[samples];
            else if (format.Bits == 32) ints = new int[samples];
            else bytes = new byte[maxFrames * format.BlockAlign];
        }

        public void Write(float[] mono, int frames, IntPtr dest)
        {
            int ch = Math.Max(1, format.Channels);
            if (format.IsFloat)
            {
                if (ch == 1) { Marshal.Copy(mono, 0, dest, frames); return; }
                for (int i = 0, at = 0; i < frames; i++)
                    for (int c = 0; c < ch; c++) floats[at++] = mono[i];
                Marshal.Copy(floats, 0, dest, frames * ch);
                return;
            }
            if (format.Bits == 16)
            {
                for (int i = 0, at = 0; i < frames; i++)
                {
                    short v = (short)Math.Round(Clip(mono[i]) * 32767.0);
                    for (int c = 0; c < ch; c++) shorts[at++] = v;
                }
                Marshal.Copy(shorts, 0, dest, frames * ch);
                return;
            }
            if (format.Bits == 32)
            {
                for (int i = 0, at = 0; i < frames; i++)
                {
                    int v = (int)Math.Round(Clip(mono[i]) * 2147483647.0);
                    for (int c = 0; c < ch; c++) ints[at++] = v;
                }
                Marshal.Copy(ints, 0, dest, frames * ch);
                return;
            }
            for (int i = 0; i < frames; i++)
            {
                int v = (int)Math.Round(Clip(mono[i]) * 8388607.0);
                int at = i * format.BlockAlign;
                for (int c = 0; c < ch; c++, at += 3)
                {
                    bytes[at] = (byte)v;
                    bytes[at + 1] = (byte)(v >> 8);
                    bytes[at + 2] = (byte)(v >> 16);
                }
            }
            Marshal.Copy(bytes, 0, dest, frames * format.BlockAlign);
        }

        static float Clip(float x)
        {
            return x > 1f ? 1f : (x < -1f ? -1f : x);
        }
    }

    static class AudioPerf
    {
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref int taskIndex);

        [DllImport("avrt.dll")]
        static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState state, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll")]
        static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);

        public static void WaitAny(IntPtr[] handles, int milliseconds)
        {
            WaitForMultipleObjects((uint)handles.Length, handles, false, (uint)milliseconds);
        }

        static int optedOut;

        // Registers the thread with the multimedia scheduler, which keeps audio on time under load.
        public static IntPtr EnterAudioPriority()
        {
            try
            {
                int index = 0;
                IntPtr handle = AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
                if (handle == IntPtr.Zero) Thread.CurrentThread.Priority = ThreadPriority.Highest;
                return handle;
            }
            catch
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                return IntPtr.Zero;
            }
        }

        public static void LeaveAudioPriority(IntPtr handle)
        {
            try { if (handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(handle); } catch { }
        }

        // Windows slows hidden background processes on battery (EcoQoS). Audio cannot wait.
        public static void OptOutOfPowerThrottling()
        {
            if (Interlocked.Exchange(ref optedOut, 1) == 1) return;
            try
            {
                var state = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 0 };
                if (!SetProcessInformation(GetCurrentProcess(), 4, ref state, Marshal.SizeOf(typeof(PowerThrottlingState))))
                    BeepFiles.Log("could not opt out of power throttling: error " + Marshal.GetLastWin32Error());
            }
            catch (EntryPointNotFoundException) { }
            catch (Exception ex) { BeepFiles.Log("could not opt out of power throttling: " + ex.Message); }
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
        public static PropertyKey FormFactorKey = new PropertyKey
        {
            fmtid = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"),
            pid = 0
        };

        const uint EventCallback = 0x00040000;
        const uint NoPersist = 0x00080000;
        const uint AutoConvertPcm = 0x80000000;
        const uint SrcDefaultQuality = 0x08000000;

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

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed: " + DescribeHr(hr), hr);
        }

        public static string DescribeHr(int hr)
        {
            switch ((uint)hr)
            {
                case 0x88890004: return "the device was unplugged or disabled (0x88890004)";
                case 0x8889000A: return "another app is using the device in exclusive mode (0x8889000A)";
                case 0x88890008: return "the device does not accept this audio format (0x88890008)";
                case 0x88890010: return "the Windows Audio service is not running (0x88890010)";
                case 0x88890026: return "Windows reset the audio device (0x88890026)";
                case 0x80070005: return "Windows privacy settings block microphone access for desktop apps (0x80070005)";
                case 0x80070490: return "the device was not found (0x80070490)";
            }
            return "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
        }

        // What to tell an agent. Describe() keeps the technical detail for the log.
        public static string Friendly(Exception ex)
        {
            if (ex is InvalidOperationException) return ex.Message;
            var com = ex as ExternalException;
            if (com == null) return "The microphone or virtual cable is not responding.";
            switch ((uint)com.ErrorCode)
            {
                case 0x88890004:
                case 0x80070490: return "The headset or virtual cable was unplugged or turned off.";
                case 0x8889000A: return "Another app has taken over the microphone.";
                case 0x80070005: return "Windows privacy settings are blocking the microphone. In Settings, Privacy, Microphone, turn on access for desktop apps.";
                case 0x88890010: return "The Windows Audio service is not running.";
            }
            return "The microphone or virtual cable is not responding.";
        }

        public static string Describe(Exception ex)
        {
            if (ex is COMException || ex is InvalidOperationException) return ex.Message;
            var com = ex as ExternalException;
            if (com != null) return DescribeHr(com.ErrorCode);
            return ex.Message;
        }

        public static IMMDeviceEnumerator CreateEnumerator()
        {
            Guid enumId = typeof(IMMDeviceEnumerator).GUID;
            Guid clsid = ClsidEnumerator;
            IntPtr ptr;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref enumId, out ptr), "create device enumerator");
            try { return (IMMDeviceEnumerator)Marshal.GetTypedObjectForIUnknown(ptr, typeof(IMMDeviceEnumerator)); }
            finally { Marshal.Release(ptr); }
        }

        public static IMMDevice OpenDevice(string id)
        {
            IMMDeviceEnumerator enumerator = CreateEnumerator();
            try
            {
                IntPtr devicePtr;
                Check(enumerator.GetDevice(id, out devicePtr), "open device");
                try { return (IMMDevice)Marshal.GetTypedObjectForIUnknown(devicePtr, typeof(IMMDevice)); }
                finally { Marshal.Release(devicePtr); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        public static IAudioClient ActivateClient(IMMDevice device)
        {
            Guid iid = typeof(IAudioClient).GUID;
            IntPtr ptr;
            Check(device.Activate(ref iid, 23, IntPtr.Zero, out ptr), "activate audio client");
            try { return (IAudioClient)Marshal.GetTypedObjectForIUnknown(ptr, typeof(IAudioClient)); }
            finally { Marshal.Release(ptr); }
        }

        // Asks Windows for mono float at the device rate and lets it convert. Falls back to the
        // device's own format when conversion is not available.
        public static IAudioClient InitializeCapture(IMMDevice device, AutoResetEvent ready, long bufferHns, out FormatInfo format)
        {
            int rate = MixRate(device);
            IntPtr mono = AllocFloatFormat(rate, 1);
            try
            {
                IAudioClient client = ActivateClient(device);
                int hr = InitializeWith(client, EventCallback | NoPersist | AutoConvertPcm | SrcDefaultQuality, bufferHns, mono);
                if (hr >= 0)
                {
                    Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "capture SetEventHandle");
                    format = Inspect(mono);
                    return client;
                }
                Marshal.ReleaseComObject(client);
                BeepFiles.Log("capture mono float rejected: " + DescribeHr(hr));
            }
            finally { Marshal.FreeHGlobal(mono); }

            IAudioClient native = ActivateClient(device);
            IntPtr mix;
            Check(native.GetMixFormat(out mix), "capture GetMixFormat");
            try
            {
                Check(InitializeWith(native, EventCallback | NoPersist, bufferHns, mix), "capture Initialize");
                Check(native.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "capture SetEventHandle");
                format = Inspect(mix);
                return native;
            }
            finally { Marshal.FreeCoTaskMem(mix); }
        }

        public static IAudioClient InitializeRender(IMMDevice device, AutoResetEvent ready, int sampleRate, out FormatInfo format)
        {
            long[] buffers = new long[] { 600000, 0 };
            foreach (int channels in new int[] { 1, 2 })
            {
                foreach (long buffer in buffers)
                {
                    IntPtr wanted = AllocFloatFormat(sampleRate, channels);
                    try
                    {
                        IAudioClient client = ActivateClient(device);
                        MarkAsCall(client);
                        int hr = InitializeWith(client, EventCallback | NoPersist | AutoConvertPcm | SrcDefaultQuality, buffer, wanted);
                        if (hr >= 0)
                        {
                            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "render SetEventHandle");
                            format = Inspect(wanted);
                            return client;
                        }
                        Marshal.ReleaseComObject(client);
                        BeepFiles.Log("render " + sampleRate + " Hz " + channels + " ch, buffer " + (buffer / 10000) + " ms rejected: " + DescribeHr(hr));
                    }
                    finally { Marshal.FreeHGlobal(wanted); }
                }
            }

            IAudioClient native = ActivateClient(device);
            MarkAsCall(native);
            IntPtr mix;
            Check(native.GetMixFormat(out mix), "render GetMixFormat");
            try
            {
                Check(InitializeWith(native, EventCallback | NoPersist, 0, mix), "render Initialize");
                Check(native.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "render SetEventHandle");
                format = Inspect(mix);
                return native;
            }
            finally { Marshal.FreeCoTaskMem(mix); }
        }

        static int MixRate(IMMDevice device)
        {
            IAudioClient probe = ActivateClient(device);
            try
            {
                IntPtr mix;
                Check(probe.GetMixFormat(out mix), "GetMixFormat");
                try { return Marshal.ReadInt32(mix, 4); }
                finally { Marshal.FreeCoTaskMem(mix); }
            }
            finally { Marshal.ReleaseComObject(probe); }
        }

        static int InitializeWith(IAudioClient client, uint flags, long bufferHns, IntPtr format)
        {
            IntPtr session = AllocGuid(SessionGuid);
            try { return client.Initialize(0, flags, bufferHns, 0, format, session); }
            finally { Marshal.FreeHGlobal(session); }
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
                if (hr < 0) return;
                var client2 = (IAudioClient2)Marshal.GetTypedObjectForIUnknown(client2Ptr, typeof(IAudioClient2));
                var value = new AudioClientProperties();
                value.cbSize = (uint)Marshal.SizeOf(typeof(AudioClientProperties));
                value.bIsOffload = 0;
                value.eCategory = 3;
                value.Options = 0;
                props = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AudioClientProperties)));
                Marshal.StructureToPtr(value, props, false);
                hr = client2.SetClientProperties(props);
                if (hr < 0) BeepFiles.Log("call category rejected " + DescribeHr(hr));
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

        public static IAudioCaptureClient GetCapture(IAudioClient client)
        {
            Guid iid = typeof(IAudioCaptureClient).GUID;
            IntPtr ptr;
            Check(client.GetService(ref iid, out ptr), "GetService capture");
            try { return (IAudioCaptureClient)Marshal.GetTypedObjectForIUnknown(ptr, typeof(IAudioCaptureClient)); }
            finally { Marshal.Release(ptr); }
        }

        public static IAudioRenderClient GetRender(IAudioClient client)
        {
            Guid iid = typeof(IAudioRenderClient).GUID;
            IntPtr ptr;
            Check(client.GetService(ref iid, out ptr), "GetService render");
            try { return (IAudioRenderClient)Marshal.GetTypedObjectForIUnknown(ptr, typeof(IAudioRenderClient)); }
            finally { Marshal.Release(ptr); }
        }

        public static void TryDisableDucking(IMMDevice device, Guid sessionGuid)
        {
            try
            {
                Guid iid = typeof(IAudioSessionManager2).GUID;
                IntPtr mgrPtr;
                int hr = device.Activate(ref iid, 23, IntPtr.Zero, out mgrPtr);
                if (hr < 0) { BeepFiles.Log("ducking opt-out unavailable " + DescribeHr(hr)); return; }
                try
                {
                    var mgr = (IAudioSessionManager2)Marshal.GetTypedObjectForIUnknown(mgrPtr, typeof(IAudioSessionManager2));
                    IntPtr ctlPtr;
                    hr = mgr.GetAudioSessionControl(ref sessionGuid, 0, out ctlPtr);
                    Marshal.ReleaseComObject(mgr);
                    if (hr < 0 || ctlPtr == IntPtr.Zero) return;
                    try
                    {
                        var ctl = (IAudioSessionControl2)Marshal.GetTypedObjectForIUnknown(ctlPtr, typeof(IAudioSessionControl2));
                        hr = ctl.SetDuckingPreference(true);
                        Marshal.ReleaseComObject(ctl);
                        if (hr < 0) BeepFiles.Log("SetDuckingPreference failed " + DescribeHr(hr));
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
            int block = channels * 4;
            IntPtr p = Marshal.AllocHGlobal(18);
            for (int i = 0; i < 18; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt16(p, 0, 3);
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * block);
            Marshal.WriteInt16(p, 12, (short)block);
            Marshal.WriteInt16(p, 14, 32);
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

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
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
        [PreserveSig] int GetSessionEnumerator(out IntPtr sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IntPtr session);
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

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int mute, ref Guid context);
        [PreserveSig] int GetMute(out int mute);
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
}
