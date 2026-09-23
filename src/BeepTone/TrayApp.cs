using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace BeepTone
{
    sealed class TrayApp : ApplicationContext
    {
        sealed class Alert
        {
            public string Text;
            public bool Banner = true;
            public bool Muted;
        }

        readonly NotifyIcon notify;
        readonly ToolStripMenuItem statusItem;
        readonly ToolStripMenuItem cableItem;
        readonly ToolStripMenuItem pauseItem;
        readonly Icon idleIcon;
        readonly Icon beepIcon;
        readonly Icon warningIcon;
        readonly Icon stoppedIcon;
        readonly ToolStripItem beepItem;
        readonly ToolStripMenuItem stopItem;
        readonly Timer timer;
        readonly Timer flashTimer;
        AlertBanner banner;
        BeepMixer mixer;
        DateTime? pauseUntil;
        int ticks;
        int seenBeepCount;
        bool iconWarning;
        bool suppressAlertUntilBeep;
        string alertText;
        DateTime alertShownAt = DateTime.MinValue;
        DateTime defaultsCheckedAt = DateTime.MinValue;
        bool loggedRemoteSkip;
        bool loggedMissingCable;
        string lastTickError;
        bool stoppedByAdmin;
        // A stop from the tray menu is kept in memory only, so it ends at sign-out or restart and
        // cannot be set by editing a file.
        bool stoppedByUser;

        public TrayApp()
        {
            BeepHeartbeat.IdleState = HeartbeatState.Setup;
            BeepHeartbeat.Start();
            pauseUntil = BeepPause.ReadFile();
            idleIcon = MakeIcon(Color.FromArgb(25, 110, 200));
            beepIcon = MakeIcon(Color.FromArgb(214, 132, 16));
            warningIcon = MakeIcon(Color.FromArgb(190, 40, 40));
            stoppedIcon = MakeIcon(Color.FromArgb(128, 128, 128));

            var menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("Starting") { Enabled = false };
            cableItem = new ToolStripMenuItem("") { Enabled = false, Visible = false };
            pauseItem = new ToolStripMenuItem(PauseLabel()) { Visible = BeepPolicy.AllowPause };
            menu.Items.Add(statusItem);
            menu.Items.Add(cableItem);
            beepItem = menu.Items.Add("Beep now", null, delegate { BeepNow(); });
            menu.Items.Add(pauseItem);
            stopItem = new ToolStripMenuItem("Stop beep...") { Visible = BeepPolicy.AllowStop };
            stopItem.Click += delegate { ToggleStop(); };
            menu.Items.Add(stopItem);
            menu.Items.Add("Hear beep on this PC", null, delegate { PlayLocalBeep(); });
            menu.Items.Add("Setup", null, delegate { ShowSetup(); });
            menu.Items.Add("Open readme", null, delegate { OpenReadme(); });
            menu.Items.Add("Open log", null, delegate { OpenLog(); });
            pauseItem.Click += delegate { TogglePause(); };

            notify = new NotifyIcon { Icon = idleIcon, Visible = true, Text = "Beep tone starting", ContextMenuStrip = menu };
            BeepFiles.Log("tray icon is showing");

            flashTimer = new Timer { Interval = 700 };
            flashTimer.Tick += delegate
            {
                flashTimer.Stop();
                SetIcon();
            };
            // Everything except the amber flash runs once a second.
            timer = new Timer { Interval = 1000 };
            timer.Tick += delegate
            {
                try
                {
                    Tick();
                    lastTickError = null;
                }
                catch (Exception ex)
                {
                    if (ex.Message != lastTickError) BeepFiles.Log("tray error: " + ex.Message);
                    lastTickError = ex.Message;
                }
            };
            timer.Start();
            // With the administrator stop set, the tray still runs, grey, and starts the beep as soon as
            // an administrator turns it back on.
            if (AdminFlag.IsDisabled()) EnterStopped(false);
            else StartMixer();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                flashTimer.Dispose();
                notify.Visible = false;
                notify.Dispose();
                ClearBanner();
            }
            base.Dispose(disposing);
        }

        static Icon MakeIcon(Color color)
        {
            var bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(color))
            using (var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.FillEllipse(brush, 1, 1, 30, 30);
                g.DrawString("B", font, Brushes.White, 7, 5);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        static string PauseLabel()
        {
            return "Pause beep for " + BeepPolicy.PauseMinutes + " minutes";
        }

        void Balloon(int ms, string text, ToolTipIcon icon)
        {
            notify.ShowBalloonTip(ms, "Beep tone", text, icon);
        }

        bool Paused { get { return pauseUntil.HasValue && DateTime.UtcNow < pauseUntil.Value; } }

        void StartMixer()
        {
            BeepConfig config = BeepConfig.Load();
            if (config.SetDefaultMicrophone) UpdateDefaults(false);
            if (mixer == null)
            {
                mixer = new BeepMixer();
                ApplySettings(config);
                BeepMixer.Current = mixer;
                mixer.SetBeepPaused(Paused);
                mixer.Start();
                BeepFiles.Log("mixer thread started");
                BeepEvents.Write(BeepEvents.Started, "Beep tone started: " + config.FrequencyHz + " Hz every " + config.IntervalSeconds
                    + " s at " + config.LevelDbfs + " dBFS.", false);
                return;
            }
            ApplySettings(config);
            mixer.RequestRestart("settings changed");
        }

        void ApplySettings(BeepConfig config)
        {
            mixer.CaptureId = config.CaptureDeviceId;
            mixer.CaptureName = config.CaptureDeviceName;
            mixer.RenderId = config.RenderDeviceId;
            mixer.RenderName = config.RenderDeviceName;
            mixer.Settings = config.ToSettings();
            mixer.IgnoredMicApps = BeepPolicy.IgnoredMicApps();
        }

        void BeepNow()
        {
            if (!Paused && mixer != null) mixer.RequestBeep();
        }

        void TogglePause()
        {
            if (Paused)
            {
                EndPause();
                Balloon(4000, "The beep is back on.", ToolTipIcon.Info);
                return;
            }
            DateTime? until = BeepPause.Start();
            if (!until.HasValue) return;
            pauseUntil = until;
            if (mixer != null) mixer.SetBeepPaused(true);
            Balloon(6000, "The beep is paused until " + until.Value.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture)
                + ". It turns back on by itself. The microphone still works.", ToolTipIcon.Warning);
        }

        void EndPause()
        {
            BeepPause.Stop();
            pauseUntil = null;
            // The mixer beeps as soon as the pause ends.
            if (mixer != null) mixer.SetBeepPaused(false);
            suppressAlertUntilBeep = true;
        }

        void ShowSetup()
        {
            using (var setup = new SetupForm(BeepConfig.Load(), UpdateDefaults))
            {
                if (setup.ShowDialog() != DialogResult.OK) return;
            }
            if (stoppedByAdmin || stoppedByUser) return;
            StartMixer();
            Balloon(4000, "Settings saved. Set the softphone microphone to CABLE Output.", ToolTipIcon.Info);
        }

        // Shuts the mixer down and closes the microphone. The heartbeat then reports "stopped", which the
        // guard leaves alone.
        void StopMixer()
        {
            if (mixer != null)
            {
                mixer.RequestStop();
                mixer = null;
                BeepMixer.Current = null;
            }
            BeepHeartbeat.IdleState = HeartbeatState.Stopped;
            alertText = null;
        }

        void ShowStopped(string text)
        {
            notify.Icon = stoppedIcon;
            notify.Text = Clip(text);
            statusItem.Text = text;
            cableItem.Visible = false;
            pauseItem.Visible = false;
            beepItem.Enabled = false;
        }

        void EnterStopped(bool announce)
        {
            stoppedByAdmin = true;
            StopMixer();
            ClearBanner();
            ShowStopped("Beep stopped by an administrator");
            stopItem.Visible = false;
            BeepFiles.Log("beep is off because an administrator stopped it on this PC");
            if (announce) Balloon(6000, "An administrator turned the beep off on this PC.", ToolTipIcon.Info);
        }

        void LeaveStopped()
        {
            stoppedByAdmin = false;
            BeepFiles.Log("an administrator turned the beep back on");
            stopItem.Visible = BeepPolicy.AllowStop || stoppedByUser;
            if (stoppedByUser) return;
            beepItem.Enabled = true;
            seenBeepCount = 0;
            Balloon(4000, "The beep is back on.", ToolTipIcon.Info);
            StartMixer();
        }

        void ToggleStop()
        {
            if (stoppedByUser)
            {
                StartFromMenu();
                return;
            }
            if (!BeepPolicy.AllowStop) return;
            using (var password = new PasswordForm("Enter the setup password to stop the beep."))
            {
                if (password.ShowDialog() != DialogResult.OK) return;
            }
            StopFromMenu();
        }

        void StopFromMenu()
        {
            if (Paused)
            {
                BeepPause.Stop();
                pauseUntil = null;
            }
            stoppedByUser = true;
            StopMixer();
            ShowStopped(UserStoppedText);
            stopItem.Text = "Start beep";
            BeepFiles.Log("beep stopped from the tray menu");
            BeepEvents.Write(BeepEvents.UserStopped, "The beep was stopped from the tray menu with the setup password.", true);
            Balloon(6000, "The beep is stopped until you choose Start beep, sign out, or restart.", ToolTipIcon.Warning);
        }

        void StartFromMenu()
        {
            stoppedByUser = false;
            stopItem.Text = "Stop beep...";
            BeepFiles.Log("beep started from the tray menu");
            BeepEvents.Write(BeepEvents.UserStarted, "The beep was started again from the tray menu.", false);
            ClearBanner();
            if (stoppedByAdmin) return;
            beepItem.Enabled = true;
            seenBeepCount = 0;
            Balloon(4000, "The beep is back on.", ToolTipIcon.Info);
            StartMixer();
        }

        const string UserStoppedText = "Beep stopped. Choose Start beep to turn it on.";

        void OpenReadme()
        {
            string readme = Path.Combine(BeepPaths.InstallDir, "README.md");
            if (!File.Exists(readme))
            {
                MessageBox.Show("README.md was not found next to BeepTone.exe.\r\n" + readme, "Beep Tone");
                return;
            }
            try { Process.Start(readme); }
            catch { Process.Start("notepad.exe", "\"" + readme + "\""); }
        }

        void OpenLog()
        {
            if (!File.Exists(BeepPaths.LogPath)) File.WriteAllText(BeepPaths.LogPath, "");
            Process.Start("notepad.exe", "\"" + BeepPaths.LogPath + "\"");
        }

        void PlayLocalBeep()
        {
            try
            {
                LocalBeep.Play(BeepConfig.Cached().ToSettings());
                Flash();
                BeepFiles.Log("local test beep");
            }
            catch (Exception ex)
            {
                BeepFiles.Log("local test beep failed: " + ex.Message);
                MessageBox.Show(ex.Message, "Beep Tone");
            }
        }

        Alert GetAlert()
        {
            if (Paused || suppressAlertUntilBeep || mixer == null) return null;
            string problem = mixer.Problem;
            if (problem.Length > 0) return new Alert { Text = problem };
            string status = mixer.Status;
            if (status == "Reconnecting")
                return new Alert { Text = "Lost the microphone or virtual cable, so the beep is not going out on calls. Trying again." };
            string muted = mixer.MuteProblem;
            if (muted.Length > 0) return new Alert { Text = muted, Banner = false, Muted = true };
            string bypass = mixer.BypassProblem;
            if (bypass.Length > 0) return new Alert { Text = bypass };
            string verify = mixer.VerifyProblem;
            if (verify.Length > 0) return new Alert { Text = verify };
            if (status != "Running") return null;
            int limit = mixer.Settings.IntervalSeconds + 5;
            double age = mixer.SecondsSinceLastBeep;
            if (age >= 0)
            {
                if (age > limit) return new Alert { Text = "No beep has gone out for " + (int)age + " seconds, so calls may not have it." };
                return null;
            }
            if (mixer.SecondsRunning > limit)
                return new Alert { Text = "No beep has gone out since Beep Tone started, so calls may not have it." };
            return null;
        }

        void UpdateAlert(Alert alert)
        {
            if (alert != null)
            {
                bool repeat = (DateTime.Now - alertShownAt).TotalSeconds >= 120 && !alert.Muted;
                if (alertText != alert.Text || repeat)
                {
                    if (alertText != alert.Text)
                    {
                        BeepFiles.Log("beep problem: " + alert.Text);
                        BeepEvents.Write(BeepEvents.Problem, alert.Text, true);
                    }
                    Balloon(8000, alert.Text, ToolTipIcon.Warning);
                    alertText = alert.Text;
                    alertShownAt = DateTime.Now;
                }
                return;
            }
            if (alertText != null)
            {
                BeepFiles.Log("beep problem cleared");
                BeepEvents.Write(BeepEvents.ProblemCleared, "The beep is going out on the call again.", false);
                Balloon(4000, "The beep is going out on the call again.", ToolTipIcon.Info);
                alertText = null;
            }
        }

        static string Clip(string text)
        {
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        void UpdateStatus()
        {
            string text = "Beep tone starting";
            string menu = "Starting";
            string cable = "";
            if (mixer != null)
            {
                string status = mixer.Status;
                DateTime last = mixer.LastBeepLocal;
                if (status == "Running")
                {
                    string stamp = last > DateTime.MinValue ? " Last beep " + last.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "";
                    text = "Beep tone running." + stamp;
                    menu = "Running." + stamp;
                }
                else if (status == "Reconnecting")
                {
                    text = "Beep tone reconnecting";
                    menu = "Reconnecting";
                }
                else
                {
                    text = "Beep tone " + status;
                    menu = status;
                }
                string users = mixer.CableUsers;
                cable = users.Length > 0 ? "Using the cable: " + users : "No app is using the cable right now";
                string title;
                string balloon = mixer.ConsumeBalloon(out title);
                if (balloon != null) notify.ShowBalloonTip(4000, title ?? "Beep tone", balloon, ToolTipIcon.Info);
            }
            notify.Text = Clip(text);
            statusItem.Text = menu;
            cableItem.Text = cable;
            cableItem.Visible = cable.Length > 0;
        }

        // Amber for a moment after each beep, then back to red or blue.
        void Flash()
        {
            notify.Icon = beepIcon;
            flashTimer.Stop();
            flashTimer.Start();
        }

        void SetIcon()
        {
            if (flashTimer.Enabled) return;
            Icon want = iconWarning ? warningIcon : idleIcon;
            if (notify.Icon != want) notify.Icon = want;
        }

        // The banner window exists only while there is something to show.
        void ShowBanner(string text, bool warning)
        {
            if (banner == null) banner = new AlertBanner();
            banner.ShowAlert(text, warning);
        }

        void ClearBanner()
        {
            if (banner == null) return;
            banner.Dispose();
            banner = null;
        }

        void Tick()
        {
            ticks++;
            bool disabled = AdminFlag.IsDisabled();
            if (disabled && !stoppedByAdmin) EnterStopped(true);
            else if (!disabled && stoppedByAdmin) LeaveStopped();
            if (stoppedByAdmin) return;
            if (stoppedByUser)
            {
                ShowStopped(UserStoppedText);
                ShowBanner("The beep is stopped. Calls are not getting the beep.", true);
                return;
            }

            bool paused = false;
            if (pauseUntil.HasValue)
            {
                if (DateTime.UtcNow >= pauseUntil.Value)
                {
                    EndPause();
                    Balloon(6000, "The beep is back on.", ToolTipIcon.Info);
                }
                else
                {
                    paused = true;
                    if (mixer != null && !mixer.IsBeepPaused) mixer.SetBeepPaused(true);
                }
            }
            else if (mixer != null && mixer.IsBeepPaused)
            {
                mixer.SetBeepPaused(false);
            }
            if (mixer != null)
            {
                int count = mixer.BeepCount;
                if (count != seenBeepCount)
                {
                    seenBeepCount = count;
                    if (count > 0)
                    {
                        if (!paused) Flash();
                        suppressAlertUntilBeep = false;
                    }
                }
            }
            Alert alert = GetAlert();
            iconWarning = paused || alert != null;
            SetIcon();

            UpdateStatus();
            if (paused)
            {
                string pauseText = "Beep paused until " + pauseUntil.Value.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
                notify.Text = Clip(pauseText);
                statusItem.Text = pauseText;
                pauseItem.Text = "Turn beep on";
                ShowBanner(pauseText + ". Calls are not getting the beep.", true);
            }
            else
            {
                pauseItem.Text = PauseLabel();
                pauseItem.Visible = BeepPolicy.AllowPause;
                stopItem.Visible = BeepPolicy.AllowStop;
                if (alert != null && alert.Banner) ShowBanner(alert.Text, false);
                else ClearBanner();
                if (alert != null) notify.Text = Clip(alert.Text);
                UpdateAlert(alert);
            }
            if (ticks % 10 == 0) SyncDeviceIds();
            // .NET waits for its first stage to fill before collecting, and on CPUs with large caches that
            // is tens of MB, so memory looked like it grew all shift. The heap is about 2 MB, so a full
            // collection once a minute takes around a millisecond and keeps memory flat.
            if (ticks % 60 == 0) GC.Collect();
            if (DeviceWatcher.ConsumeDefaultsChanged() || (DateTime.Now - defaultsCheckedAt).TotalSeconds >= 15)
            {
                defaultsCheckedAt = DateTime.Now;
                UpdateDefaults(false);
            }
        }

        // Keeps the cable as the default microphone (when enabled) and keeps any virtual device from
        // being the default speaker. Skipped in Remote Desktop, which has its own audio devices.
        void UpdateDefaults(bool showErrors)
        {
            if (SystemInformation.TerminalServerSession)
            {
                if (!loggedRemoteSkip)
                {
                    loggedRemoteSkip = true;
                    const string message = "Remote Desktop is connected, so the Windows default microphone was left unchanged.";
                    BeepFiles.Log(message);
                    if (showErrors) MessageBox.Show(message, "Beep Tone");
                }
                return;
            }
            loggedRemoteSkip = false;
            try
            {
                BeepConfig config = BeepConfig.Cached();
                string micId = null;
                if (config.SetDefaultMicrophone)
                {
                    AudioEndpoint render = AudioDevices.ResolveRender(config.RenderDeviceId, config.RenderDeviceName);
                    AudioEndpoint cable = AudioDevices.FindPairedCapture(render) ?? AudioDevices.FindCableOutput();
                    if (cable != null)
                    {
                        micId = cable.Id;
                        loggedMissingCable = false;
                    }
                    else if (showErrors || !loggedMissingCable)
                    {
                        loggedMissingCable = true;
                        const string message = "CABLE Output was not found, so the default microphone was not changed.";
                        BeepFiles.Log(message);
                        if (showErrors)
                            MessageBox.Show(message + " Choose it in the softphone, or set the microphone to Follow system setting after the cable is installed.", "Beep Tone");
                    }
                }
                foreach (string change in AudioDevices.EnsureDefaults(micId)) BeepFiles.Log(change);
            }
            catch (Exception ex)
            {
                BeepFiles.Log("could not set default devices: " + ex.Message);
                if (showErrors)
                    MessageBox.Show("The default microphone was not changed. In the softphone, choose CABLE Output or Follow system setting.\r\n" + ex.Message, "Beep Tone");
            }
        }

        // Saves the endpoint ID of the saved devices once found by name, so a later port change still
        // matches. Never saves a fallback microphone.
        void SyncDeviceIds()
        {
            if (mixer == null || !mixer.IsRunning) return;
            BeepConfig config = BeepConfig.Load();
            if (config.ReadFailed) return;
            bool changed = false;
            string captureId = mixer.ResolvedCaptureId;
            if (captureId.Length > 0 && config.CaptureDeviceName.Length > 0 && config.CaptureDeviceId != captureId)
            {
                config.CaptureDeviceId = captureId;
                mixer.CaptureId = captureId;
                changed = true;
            }
            string renderId = mixer.ResolvedRenderId;
            if (renderId.Length > 0 && config.RenderDeviceId != renderId)
            {
                config.RenderDeviceId = renderId;
                mixer.RenderId = renderId;
                changed = true;
            }
            if (changed) config.Save(false);
        }
    }
}
