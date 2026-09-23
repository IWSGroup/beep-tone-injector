using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace BeepTone
{
    // Downloads VB-Cable as the signed-in user, then asks for administrator approval to run
    // "BeepToneCtl.exe install-cable". That step lives in Program Files, where the user cannot
    // change it, and installs only the exact file downloaded and only if VB-Audio signed it.
    static class VirtualCable
    {
        const string DefaultUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

        public static bool Install()
        {
            Directory.CreateDirectory(BeepPaths.CableDir);
            string url = BeepPolicy.GetString("CablePackUrl", DefaultUrl);
            string zip = Path.Combine(BeepPaths.CableDir, "VBCABLE_Driver_Pack.zip");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            BeepFiles.Log("downloading virtual cable from " + url);
            using (var client = new WebClient()) client.DownloadFile(url, zip);
            string hash;
            using (var sha = SHA256.Create())
            using (FileStream file = File.OpenRead(zip))
                hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            BeepFiles.Log("downloaded virtual cable package, SHA-256 " + hash);

            string ctl = Path.Combine(BeepPaths.InstallDir, "BeepToneCtl.exe");
            var start = new ProcessStartInfo(ctl, "install-cable \"" + zip + "\" " + hash)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            int code;
            try
            {
                using (Process p = Process.Start(start))
                {
                    while (!p.WaitForExit(200)) Application.DoEvents();
                    code = p.ExitCode;
                }
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException("Administrator approval was cancelled. The virtual cable was not installed.");
            }
            string resultFile = Path.Combine(CableInstallDir, "result.txt");
            string detail = File.Exists(resultFile) ? File.ReadAllText(resultFile).Trim() : "";
            BeepFiles.Log("virtual cable installer finished with exit code " + code + ". " + detail);
            if (code >= 10 && code <= 13) throw new InvalidOperationException(detail.Length > 0 ? detail : "The virtual cable was not installed.");

            for (int i = 0; i < 40; i++)
            {
                foreach (AudioEndpoint e in AudioDevices.List("Render"))
                    if (e.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                Application.DoEvents();
                Thread.Sleep(500);
            }
            return false;
        }

        public static string CableInstallDir
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BeepTone"), "VBCABLE"); }
        }
    }
}
