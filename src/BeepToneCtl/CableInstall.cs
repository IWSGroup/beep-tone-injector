using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace BeepTone
{
    // Installs VB-Cable from VB-Audio's package, only when its Authenticode signature is valid and from
    // VB-Audio. The package is unpacked in an administrator-only folder, so nothing in it can be swapped
    // before it runs.
    //   install-cable <zip> <sha256>        run elevated by the tray after it downloads the package
    //   install-cable-if-missing [--dry-run] run by the installer as SYSTEM
    static class CableInstall
    {
        // VB-Audio signs as "BUREL VINCENT Entrepreneur individuel". The registration number
        // (French SIREN) identifies the business and survives certificate renewals.
        const string DefaultSignerPattern = @"SERIALNUMBER=423 734 177(,|$)|(^|, )O=BUREL VINCENT";
        const int SetupTimeoutMs = 5 * 60 * 1000;
        const int DownloadTimeoutMs = 60 * 1000;

        static string TargetDir
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BeepTone"), "VBCABLE"); }
        }

        public static int Run(string zip, string expectedHash)
        {
            string target = TargetDir;
            string result = Path.Combine(target, "result.txt");
            try
            {
                Reset(target);
                string copy = Path.Combine(target, "pack.zip");
                File.Copy(zip, copy, true);
                if (!string.Equals(Sha256(copy), expectedHash, StringComparison.OrdinalIgnoreCase))
                    return Done(result, 11, "The downloaded package changed before it could be installed.");
                return InstallPackage(target, copy, result, false);
            }
            catch (Exception ex)
            {
                return Done(result, 10, "The virtual cable was not installed: " + ex.Message);
            }
        }

        // The installer runs this after installing Beep Tone. It never fails the install: problems go to
        // the install log and the event log. --dry-run downloads and checks the package into a temp
        // folder without installing, even when VB-Cable is already present.
        public static int InstallIfMissing(bool dryRun)
        {
            if (!dryRun)
            {
                string found = FindCable();
                if (found != null)
                {
                    Console.WriteLine("VB-Cable is already installed (" + found + "). Nothing to do.");
                    return 0;
                }
            }
            string url = BeepPolicy.GetString("CablePackUrl", BeepPolicy.DefaultCablePackUrl);
            string target = dryRun ? Path.Combine(Path.GetTempPath(), "BeepToneCableCheck") : TargetDir;
            string result = Path.Combine(target, "result.txt");
            int code;
            try
            {
                Reset(target);
                string zip = Path.Combine(target, "pack.zip");
                Console.WriteLine("Downloading VB-Cable from " + url);
                Download(url, zip);
                Console.WriteLine("Downloaded " + new System.IO.FileInfo(zip).Length + " bytes, SHA-256 " + Sha256(zip));
                code = InstallPackage(target, zip, result, dryRun);
            }
            catch (Exception ex)
            {
                code = Done(result, 10, "The virtual cable was not installed: " + ex.Message);
            }
            if (dryRun)
            {
                try { Directory.Delete(target, true); } catch { }
                return code;
            }
            string detail = File.Exists(result) ? File.ReadAllText(result) : "";
            if (code == 0 || code == 3010)
            {
                bool present = FindCable() != null;
                string text = "VB-Cable was installed. " + detail
                    + (present ? "" : " CABLE Input is not visible yet; restart the PC if it does not appear.");
                Console.WriteLine(text);
                BeepEvents.Write(BeepEvents.CableInstalled, text, false);
                return 0;
            }
            BeepEvents.Write(BeepEvents.CableInstallFailed, "VB-Cable could not be installed, so there is no beep on this PC until it is. "
                + detail + " Install it with your deployment tool, or from Beep Tone setup.", true);
            return code;
        }

        // VB-Cable's playback side as an audio device, or the VB-Audio driver itself (present even while the device is disabled).
        public static string FindCable()
        {
            try
            {
                AudioEndpoint cable = AudioDevices.FindCableInput();
                if (cable != null) return cable.Name;
            }
            catch { }
            try
            {
                using (var search = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%VB-Audio Virtual Cable%'"))
                {
                    foreach (ManagementObject device in search.Get())
                        return Convert.ToString(device["Name"]);
                }
            }
            catch { }
            return null;
        }

        static void Download(string url, string path)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = DownloadTimeoutMs;
            request.ReadWriteTimeout = DownloadTimeoutMs;
            request.UserAgent = "BeepTone/" + BeepPaths.Version;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (Stream body = response.GetResponseStream())
            using (FileStream file = File.Create(path))
                body.CopyTo(file);
        }

        static void Reset(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
        }

        static int InstallPackage(string target, string zip, string result, bool dryRun)
        {
            ZipFile.ExtractToDirectory(zip, target);
            string setup = PickSetup(target);
            if (setup == null) return Done(result, 12, "The virtual cable installer was not in the downloaded package.");
            string signer;
            bool valid = Authenticode.IsValid(setup, out signer);
            string pattern = BeepPolicy.GetString("CableSignerPattern", DefaultSignerPattern);
            if (!valid || !Regex.IsMatch(signer ?? "", pattern, RegexOptions.IgnoreCase))
                return Done(result, 13, "The installer signature was not accepted. Valid: " + valid + ". Signer: " + signer);
            if (dryRun)
                return Done(result, 0, "Signature accepted: " + Path.GetFileName(setup) + " signed by " + signer + ". Nothing was installed (dry run).");
            var start = new ProcessStartInfo(setup, "-i -h") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup) };
            int code;
            using (Process p = Process.Start(start))
            {
                // A driver prompt cannot be answered when this runs unattended, so do not wait forever.
                if (!p.WaitForExit(SetupTimeoutMs))
                {
                    try { p.Kill(); } catch { }
                    RemoveAllBut(target, result);
                    return Done(result, 14, "The VB-Cable installer did not finish within 5 minutes and was stopped. Signer: " + signer);
                }
                code = p.ExitCode;
            }
            RemoveAllBut(target, result);
            return Done(result, code, "Installer exit code " + code + ". Signer: " + signer);
        }

        static string PickSetup(string dir)
        {
            string[] all = Directory.GetFiles(dir, "VBCABLE_Setup*.exe", SearchOption.AllDirectories);
            foreach (string file in all)
            {
                string name = Path.GetFileName(file);
                bool x64 = name.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0;
                bool arm = name.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0;
                if (Environment.Is64BitOperatingSystem ? x64 : (!x64 && !arm)) return file;
            }
            return all.Length > 0 ? all[0] : null;
        }

        // Leaves only result.txt, so uninstalling Beep Tone can remove the folder.
        static void RemoveAllBut(string dir, string keep)
        {
            foreach (string sub in Directory.GetDirectories(dir))
            {
                try { Directory.Delete(sub, true); } catch { }
            }
            foreach (string file in Directory.GetFiles(dir))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) try { File.Delete(file); } catch { }
            }
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream file = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        }

        static int Done(string resultFile, int code, string text)
        {
            try { File.WriteAllText(resultFile, text); } catch { }
            Console.WriteLine(text);
            return code;
        }
    }

    static class Authenticode
    {
        static readonly Guid VerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref TrustData data);

        public static bool IsValid(string path, out string signer)
        {
            signer = null;
            try { signer = new X509Certificate(X509Certificate.CreateFromSignedFile(path)).Subject; }
            catch { return false; }
            var file = new FileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(FileInfo)), pcwszFilePath = path };
            IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            try
            {
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new TrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(TrustData)),
                    dwUIChoice = 2,          // no UI
                    fdwRevocationChecks = 0, // none
                    dwUnionChoice = 1,       // file
                    pFile = filePtr,
                    dwProvFlags = 0x00000010 // no revocation check, so an offline PC can still install
                };
                return WinVerifyTrust(IntPtr.Zero, VerifyV2, ref data) == 0;
            }
            finally
            {
                Marshal.DestroyStructure(filePtr, typeof(FileInfo));
                Marshal.FreeHGlobal(filePtr);
            }
        }
    }
}
