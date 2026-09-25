using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WixToolset.Dtf.WindowsInstaller;

namespace BeepTone.Setup
{
    public static class CustomActions
    {
        // A deferred action cannot reliably ask for a restart itself, so it leaves this global atom and
        // CheckVBCableRestart asks after InstallFinalize, as the WiX toolset's own actions do.
        const string RestartAtom = "BeepToneVBCableRestart";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern ushort GlobalAddAtom(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern ushort GlobalFindAtom(string name);

        [DllImport("kernel32.dll")]
        static extern ushort GlobalDeleteAtom(ushort atom);

        // Turns the password typed on the setup password page (or passed as SETUPPASSWORD) into the
        // hash the tray checks. Only the hash is written to the registry; the password is cleared.
        [CustomAction]
        public static ActionResult HashSetupPassword(Session session)
        {
            string password = session["SETUPPASSWORD"];
            if (string.IsNullOrEmpty(password)) return ActionResult.Success;
            session["SETUPPASSWORDHASH"] = SetupPassword.Hash(password);
            session["SETUPPASSWORD"] = "";
            session["SETUPPASSWORDCONFIRM"] = "";
            session.Log("A new setup password was set.");
            return ActionResult.Success;
        }

        // On uninstall, asks whether to remove VB-Cable too, when it is installed and someone is watching.
        // The answer goes into REMOVEVBCABLE; the default button follows the choice made at install.
        // Silent uninstalls are not asked and follow REMOVEVBCABLE.
        [CustomAction]
        public static ActionResult AskRemoveVBCable(Session session)
        {
            if (VirtualDevices.CableDeviceIds().Count == 0)
            {
                session.Log("VB-Cable is not installed, so there is nothing to ask.");
                return ActionResult.Success;
            }
            bool remove = session["REMOVEVBCABLE"] != "0";
            using (var record = new Record(0))
            {
                record.FormatString = "Also remove VB-Cable, the virtual audio cable Beep Tone plays the beep into?\n\n"
                    + "Choose No if other software on this PC uses CABLE Input or CABLE Output.";
                // Windows message box flags: MB_YESNO, MB_ICONQUESTION, and MB_DEFBUTTON2 when the default is No.
                const int YesNo = 0x4, Question = 0x20, DefaultNo = 0x100;
                MessageResult answer = session.Message(InstallMessage.User | (InstallMessage)(YesNo | Question | (remove ? 0 : DefaultNo)), record);
                if (answer == MessageResult.Yes) session["REMOVEVBCABLE"] = "1";
                else if (answer == MessageResult.No) session["REMOVEVBCABLE"] = "0";
                session.Log("Remove VB-Cable: " + answer + ", REMOVEVBCABLE=" + session["REMOVEVBCABLE"]);
            }
            return ActionResult.Success;
        }

        // Runs "BeepToneCtl.exe remove-cable" as SYSTEM on uninstall and asks Windows for a restart when
        // the removal needs one. It never fails the uninstall. A silent uninstall only restarts when
        // /norestart is not given if someone is watching; otherwise it returns 3010 with /norestart and
        // does not restart at all without it, so agents are never restarted mid-call.
        // CustomActionData: path to BeepToneCtl.exe | UILevel | REBOOT
        [CustomAction]
        public static ActionResult RemoveVBCable(Session session)
        {
            string[] data = (session["CustomActionData"] ?? "").Split('|');
            string ctl = data[0];
            int uiLevel;
            int.TryParse(data.Length > 1 ? data[1] : "", out uiLevel);
            string reboot = data.Length > 2 ? data[2] : "";
            try
            {
                var start = new ProcessStartInfo(ctl, "remove-cable")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(start))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    var errors = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(10 * 60 * 1000))
                    {
                        try { p.Kill(); } catch { }
                        session.Log("Removing VB-Cable did not finish within 10 minutes.");
                        return ActionResult.Success;
                    }
                    foreach (string line in (output.Result + errors.Result).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        session.Log(line);
                    session.Log("remove-cable exit code " + p.ExitCode);
                    if (p.ExitCode == 3010)
                    {
                        bool watched = uiLevel > 2;
                        bool suppressed = string.Equals(reboot, "ReallySuppress", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(reboot, "R", StringComparison.OrdinalIgnoreCase);
                        if (watched || suppressed)
                        {
                            GlobalAddAtom(RestartAtom);
                            session.Log("Windows finishes removing VB-Cable after a restart, so a restart will be requested.");
                        }
                        else
                        {
                            session.Log("Windows finishes removing VB-Cable after the next restart. Not restarting a silent uninstall; pass /norestart to get exit code 3010.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                session.Log("Could not remove VB-Cable: " + ex.Message);
            }
            return ActionResult.Success;
        }

        // Runs after InstallFinalize: asks for the restart RemoveVBCable found was needed.
        [CustomAction]
        public static ActionResult CheckVBCableRestart(Session session)
        {
            ushort atom = GlobalFindAtom(RestartAtom);
            if (atom == 0) return ActionResult.Success;
            for (int i = 0; i < 16 && atom != 0; i++)
            {
                GlobalDeleteAtom(atom);
                atom = GlobalFindAtom(RestartAtom);
            }
            session.SetMode(InstallRunMode.RebootAtEnd, true);
            session.Log("Restart requested to finish removing VB-Cable.");
            return ActionResult.Success;
        }
    }
}
