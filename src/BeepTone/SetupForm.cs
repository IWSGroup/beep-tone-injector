using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace BeepTone
{
    // Anyone can open setup and look. The setup password is asked for only when saving a change.
    // The layout sizes itself, so nothing is cut off at other font sizes or display scaling.
    sealed class SetupForm : Form
    {
        const int ContentWidth = 520;

        // A slider for quick changes and a number box for exact values, kept in step.
        sealed class SliderField
        {
            public readonly TrackBar Slider;
            public readonly NumericUpDown Box;
            bool syncing;

            public SliderField(int min, int max, int value, int step, int tick)
            {
                Slider = new TrackBar
                {
                    Minimum = min,
                    Maximum = max,
                    SmallChange = step,
                    LargeChange = step * 5,
                    TickFrequency = tick,
                    TickStyle = TickStyle.BottomRight,
                    Width = 300,
                    Anchor = AnchorStyles.Left | AnchorStyles.Right
                };
                Box = new NumericUpDown { Minimum = min, Maximum = max, Increment = step, Width = 70, Anchor = AnchorStyles.Left };
                int v = Math.Max(min, Math.Min(max, value));
                Slider.Value = v;
                Box.Value = v;
                Slider.ValueChanged += delegate
                {
                    if (syncing) return;
                    syncing = true;
                    // The slider snaps to its step; the number box takes any value.
                    int snapped = (int)(Math.Round((Slider.Value - min) / (double)step) * step) + min;
                    snapped = Math.Max(min, Math.Min(max, snapped));
                    if (Slider.Value != snapped) Slider.Value = snapped;
                    Box.Value = snapped;
                    syncing = false;
                };
                Box.ValueChanged += delegate
                {
                    if (syncing) return;
                    syncing = true;
                    Slider.Value = (int)Box.Value;
                    syncing = false;
                };
            }

            public int Value { get { return (int)Box.Value; } }

            public bool Enabled
            {
                set { Slider.Enabled = value; Box.Enabled = value; }
            }

            public event EventHandler Changed
            {
                add { Box.ValueChanged += value; }
                remove { Box.ValueChanged -= value; }
            }
        }

        readonly BeepConfig config;
        readonly Action<bool> updateDefaults;
        readonly Label cableLabel;
        readonly Button installButton;
        readonly ComboBox micBox;
        readonly ComboBox outBox;
        readonly CheckBox defaultCheck;
        readonly SliderField frequency, duration, fade, level;
        readonly ComboBox intervalBox;
        readonly Button saveButton;
        readonly string original;

        public SetupForm(BeepConfig config, Action<bool> updateDefaults)
        {
            this.config = config;
            this.updateDefaults = updateDefaults;
            Text = "Beep Tone " + BeepPaths.Version + " setup";
            Icon = AppIcon.Get();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var page = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            Controls.Add(page);

            // Virtual cable
            var cableRow = Row();
            cableLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) };
            installButton = new Button { Text = "Install Virtual Cable", AutoSize = true };
            cableRow.Controls.Add(cableLabel);
            cableRow.Controls.Add(installButton);
            page.Controls.Add(cableRow);

            // Devices
            page.Controls.Add(Heading("Microphone"));
            micBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = ContentWidth };
            page.Controls.Add(micBox);
            page.Controls.Add(Heading("Send the mixed microphone to this playback device"));
            outBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = ContentWidth };
            page.Controls.Add(outBox);
            defaultCheck = new CheckBox
            {
                Text = "Set CABLE Output as the Windows default microphone",
                AutoSize = true,
                Checked = config.SetDefaultMicrophone,
                Enabled = !BeepPolicy.Has("SetDefaultMicrophone"),
                Margin = new Padding(0, 8, 0, 4)
            };
            page.Controls.Add(defaultCheck);

            // Tone
            var tone = new GroupBox { Text = "Tone", AutoSize = true, Width = ContentWidth, Padding = new Padding(8), Margin = new Padding(0, 8, 0, 4) };
            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tone.Controls.Add(grid);
            frequency = AddSlider(grid, "Frequency (Hz)", (int)BeepLimits.MinFrequencyHz, (int)BeepLimits.MaxFrequencyHz, (int)Math.Round(config.FrequencyHz), 10, 20, "FrequencyHz");
            duration = AddSlider(grid, "Length (ms)", BeepLimits.MinDurationMs, BeepLimits.MaxDurationMs, config.DurationMs, 10, 10, "DurationMs");
            fade = AddSlider(grid, "Fade (ms)", BeepLimits.MinRampMs, BeepLimits.MaxRampMs, config.RampMs, 5, 5, "RampMs");
            level = AddSlider(grid, "Level (dBFS)", (int)BeepLimits.MinLevelDbfs, (int)BeepLimits.MaxLevelDbfs, (int)Math.Round(config.LevelDbfs), 1, 10, "LevelDbfs");

            grid.Controls.Add(FieldLabel("Every"));
            intervalBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) };
            for (int s = BeepLimits.MinIntervalSeconds; s <= BeepLimits.MaxIntervalSeconds; s++) intervalBox.Items.Add(s + " seconds");
            intervalBox.SelectedIndex = Math.Max(0, Math.Min(intervalBox.Items.Count - 1, config.IntervalSeconds - BeepLimits.MinIntervalSeconds));
            intervalBox.Enabled = !BeepPolicy.Has("IntervalSeconds");
            grid.Controls.Add(intervalBox);
            var hear = new Button { Text = "Hear it", AutoSize = true, Anchor = AnchorStyles.Right };
            grid.Controls.Add(hear);

            var toneNote = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(ContentWidth - 30, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Level: closer to 0 is louder. \"Hear it\" plays these settings on this PC only, not into a call."
                    + (AnyToneLocked() ? " Greyed-out settings are set by your administrator." : ""),
                Margin = new Padding(3, 6, 3, 0)
            };
            grid.Controls.Add(toneNote);
            grid.SetColumnSpan(toneNote, 3);
            page.Controls.Add(tone);

            // Help
            var help = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(ContentWidth, 0),
                Margin = new Padding(0, 8, 0, 4),
                Text = "In the softphone, set the microphone to CABLE Output (VB-Audio Virtual Cable) or to Follow system setting. "
                    + "Never pick the headset there; the tray turns red if an app does.\r\n\r\n"
                    + "Turn off noise removal, noise suppression and automatic microphone volume in the softphone. In Webex, set "
                    + "Settings, Audio, Smart audio, Microphone audio to Music mode. Muting in the softphone also mutes the beep."
            };
            page.Controls.Add(help);
            var readme = new LinkLabel { Text = "More in the readme", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            readme.LinkClicked += delegate { OpenReadme(); };
            page.Controls.Add(readme);

            // Buttons
            var buttons = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Width = ContentWidth, Anchor = AnchorStyles.Left | AnchorStyles.Right };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var refresh = new Button { Text = "Refresh devices", AutoSize = true };
            saveButton = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(88, 0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(refresh, 0, 0);
            buttons.Controls.Add(saveButton, 2, 0);
            buttons.Controls.Add(cancel, 3, 0);
            page.Controls.Add(buttons);
            AcceptButton = saveButton;
            CancelButton = cancel;

            micBox.SelectedIndexChanged += delegate { SyncSave(); };
            outBox.SelectedIndexChanged += delegate { SyncSave(); };
            defaultCheck.CheckedChanged += delegate { SyncSave(); };
            intervalBox.SelectedIndexChanged += delegate { SyncSave(); };
            foreach (SliderField f in new[] { frequency, duration, fade, level }) f.Changed += delegate { SyncSave(); };
            refresh.Click += delegate { Reload(); };
            installButton.Click += delegate { InstallCable(); };
            hear.Click += delegate { Hear(); };
            saveButton.Click += delegate { Save(); };

            Reload();
            original = Snapshot();
            SyncSave();
        }

        static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        }

        static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 8, 0, 2) };
        }

        static Label FieldLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) };
        }

        static SliderField AddSlider(TableLayoutPanel grid, string label, int min, int max, int value, int step, int tick, string policyName)
        {
            var field = new SliderField(min, max, value, step, tick);
            field.Enabled = !BeepPolicy.Has(policyName);
            grid.Controls.Add(FieldLabel(label));
            grid.Controls.Add(field.Slider);
            grid.Controls.Add(field.Box);
            return field;
        }

        static bool AnyToneLocked()
        {
            foreach (string name in new[] { "FrequencyHz", "DurationMs", "IntervalSeconds", "RampMs", "LevelDbfs" })
                if (BeepPolicy.Has(name)) return true;
            return false;
        }

        int IntervalSeconds { get { return BeepLimits.MinIntervalSeconds + Math.Max(0, intervalBox.SelectedIndex); } }

        BeepSettings CurrentSettings()
        {
            var s = new BeepSettings
            {
                FrequencyHz = frequency.Value,
                DurationMs = duration.Value,
                IntervalSeconds = IntervalSeconds,
                RampMs = fade.Value,
                LevelDbfs = level.Value
            };
            return s.Clamped();
        }

        // Everything a save would write, so "changed" means exactly "a save would change something".
        string Snapshot()
        {
            var mic = micBox.SelectedItem as AudioEndpoint;
            var output = outBox.SelectedItem as AudioEndpoint;
            return string.Join("|", new[]
            {
                mic == null ? "" : mic.Id, output == null ? "" : output.Id, defaultCheck.Checked.ToString(),
                frequency.Value.ToString(CultureInfo.InvariantCulture), duration.Value.ToString(CultureInfo.InvariantCulture),
                IntervalSeconds.ToString(CultureInfo.InvariantCulture), fade.Value.ToString(CultureInfo.InvariantCulture),
                level.Value.ToString(CultureInfo.InvariantCulture)
            });
        }

        void Reload()
        {
            var keepMic = micBox.SelectedItem as AudioEndpoint;
            var keepOut = outBox.SelectedItem as AudioEndpoint;
            micBox.Items.Clear();
            outBox.Items.Clear();
            AudioEndpoint[] mics = AudioDevices.ListMicrophones();
            AudioEndpoint[] outputs = AudioDevices.List("Render");
            micBox.Items.AddRange(mics);
            outBox.Items.AddRange(outputs);
            bool cable = false;
            foreach (AudioEndpoint e in outputs)
                if (e.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) cable = true;
            cableLabel.Text = cable ? "Virtual cable is installed." : "Virtual cable is not installed.";
            installButton.Visible = !cable;
            Select(micBox, mics, keepMic != null
                ? AudioDevices.Match(mics, keepMic.Id, keepMic.Name)
                : AudioDevices.Match(mics, config.CaptureDeviceId, config.CaptureDeviceName));
            Select(outBox, outputs, keepOut != null
                ? AudioDevices.Match(outputs, keepOut.Id, keepOut.Name)
                : AudioDevices.Match(outputs, config.RenderDeviceId, config.RenderDeviceName) ?? AudioDevices.Match(outputs, null, "CABLE Input"));
            SyncSave();
        }

        static void Select(ComboBox box, AudioEndpoint[] items, AudioEndpoint match)
        {
            if (items.Length == 0) return;
            int index = match == null ? 0 : Array.IndexOf(items, match);
            box.SelectedIndex = index < 0 ? 0 : index;
        }

        void SyncSave()
        {
            if (saveButton == null) return;
            var output = outBox.SelectedItem as AudioEndpoint;
            bool valid = micBox.SelectedIndex >= 0 && output != null && output.IsVirtual;
            saveButton.Enabled = valid && original != null && Snapshot() != original;
        }

        void Hear()
        {
            try { LocalBeep.Play(CurrentSettings()); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Beep Tone"); }
        }

        void OpenReadme()
        {
            string readme = Path.Combine(BeepPaths.InstallDir, "README.md");
            try { Process.Start(readme); }
            catch { try { Process.Start("notepad.exe", "\"" + readme + "\""); } catch { } }
        }

        void InstallCable()
        {
            installButton.Enabled = false;
            cableLabel.Text = "Downloading and installing the virtual cable...";
            Refresh();
            UseWaitCursor = true;
            try
            {
                if (VirtualCable.Install()) BeepFiles.Log("virtual cable is available");
                else MessageBox.Show(this, "The installer finished, but CABLE Input is not available yet. Sign out or reboot, then open setup again.", "Beep Tone");
            }
            catch (Exception ex)
            {
                BeepFiles.Log("virtual cable install failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Beep Tone");
            }
            finally
            {
                UseWaitCursor = false;
                installButton.Enabled = true;
                Reload();
            }
        }

        void Save()
        {
            var mic = micBox.SelectedItem as AudioEndpoint;
            var output = outBox.SelectedItem as AudioEndpoint;
            if (mic == null || output == null || !output.IsVirtual) return;
            if (Snapshot() == original)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            using (var password = new PasswordForm())
            {
                if (password.ShowDialog(this) != DialogResult.OK) return;
            }
            BeepSettings s = CurrentSettings();
            config.CaptureDeviceId = mic.Id;
            config.CaptureDeviceName = mic.Name;
            config.RenderDeviceId = output.Id;
            config.RenderDeviceName = output.Name;
            config.SetDefaultMicrophone = defaultCheck.Checked;
            config.FrequencyHz = s.FrequencyHz;
            config.DurationMs = s.DurationMs;
            config.IntervalSeconds = s.IntervalSeconds;
            config.RampMs = s.RampMs;
            config.LevelDbfs = s.LevelDbfs;
            config.ApplyPolicyAndLimits();
            config.Save(true);
            string summary = "Settings saved: " + config.FrequencyHz + " Hz, " + config.DurationMs + " ms, every " + config.IntervalSeconds
                + " s, " + config.LevelDbfs + " dBFS, microphone " + config.CaptureDeviceName;
            BeepFiles.Log(summary);
            BeepEvents.Write(BeepEvents.SettingsSaved, summary, false);
            if (config.SetDefaultMicrophone) updateDefaults(true);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
