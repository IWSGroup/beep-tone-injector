using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace BeepTone
{
    // "install-cable <zip> <sha256>", run elevated by the tray. Copies the package into an
    // administrator-only folder, checks it is the file the user downloaded, and runs the installer
    // only when its Authenticode signature is valid and from VB-Audio.
    static class CableInstall
    {
        const string DefaultSignerPattern = "Vincent Burel|VB-Audio";

        public static int Run(string zip, string expectedHash)
        {
            string target = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BeepTone"), "VBCABLE");
            string result = Path.Combine(target, "result.txt");
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.CreateDirectory(target);
                string copy = Path.Combine(target, "pack.zip");
                File.Copy(zip, copy, true);
                if (!string.Equals(Sha256(copy), expectedHash, StringComparison.OrdinalIgnoreCase))
                    return Done(result, 11, "The downloaded package changed before it could be installed.");
                ZipFile.ExtractToDirectory(copy, target);
                string setup = PickSetup(target);
                if (setup == null) return Done(result, 12, "The virtual cable installer was not in the downloaded package.");
                string signer;
                bool valid = Authenticode.IsValid(setup, out signer);
                string pattern = BeepPolicy.GetString("CableSignerPattern", DefaultSignerPattern);
                if (!valid || !Regex.IsMatch(signer ?? "", pattern, RegexOptions.IgnoreCase))
                    return Done(result, 13, "The installer signature was not accepted. Valid: " + valid + ". Signer: " + signer);
                var start = new ProcessStartInfo(setup, "-i -h") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup) };
                int code;
                using (Process p = Process.Start(start))
                {
                    p.WaitForExit();
                    code = p.ExitCode;
                }
                RemoveAllBut(target, result);
                return Done(result, code, "Installer exit code " + code + ". Signer: " + signer);
            }
            catch (Exception ex)
            {
                return Done(result, 10, "The virtual cable was not installed: " + ex.Message);
            }
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
