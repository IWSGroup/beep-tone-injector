using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace BeepTone
{
    // Removes what the earlier PowerShell version (BeepTone.ps1) left behind: its scheduled tasks,
    // any running copy, and its compiled files. Settings and logs are kept, because this version
    // reads the same config.json. The installer runs this; administrators can also run it by hand.
    static class LegacyCleanup
    {
        public static List<string> Run()
        {
            var notes = new List<string>();
            RemoveTasks(notes);
            StopScripts(notes);
            foreach (string dir in ProfileDataDirs()) CleanDataDir(dir, notes);
            if (notes.Count == 0) notes.Add("nothing from the PowerShell version was found");
            foreach (string note in notes) BeepFiles.Log("cleanup: " + note);
            return notes;
        }

        static void RemoveTasks(List<string> notes)
        {
            try
            {
                Type type = Type.GetTypeFromProgID("Schedule.Service");
                dynamic scheduler = Activator.CreateInstance(type);
                scheduler.Connect();
                dynamic folder = scheduler.GetFolder("\\");
                var names = new List<string>();
                foreach (dynamic task in folder.GetTasks(1))
                {
                    string name = task.Name;
                    if (name.StartsWith("BeepTone", StringComparison.OrdinalIgnoreCase)) names.Add(name);
                }
                foreach (string name in names)
                {
                    folder.DeleteTask(name, 0);
                    notes.Add("removed scheduled task '" + name + "'");
                }
            }
            catch (Exception ex)
            {
                notes.Add("could not check scheduled tasks: " + ex.Message);
            }
        }

        static void StopScripts(List<string> notes)
        {
            try
            {
                using (var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe'"))
                {
                    foreach (ManagementObject p in search.Get())
                    {
                        string command = p["CommandLine"] as string;
                        if (command == null || command.IndexOf("BeepTone.ps1", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        int pid = Convert.ToInt32(p["ProcessId"]);
                        try
                        {
                            Process.GetProcessById(pid).Kill();
                            notes.Add("ended BeepTone.ps1 process " + pid);
                        }
                        catch (Exception ex) { notes.Add("could not end process " + pid + ": " + ex.Message); }
                    }
                }
            }
            catch (Exception ex)
            {
                notes.Add("could not check running processes: " + ex.Message);
            }
        }

        static IEnumerable<string> ProfileDataDirs()
        {
            var dirs = new List<string>();
            try
            {
                using (RegistryKey list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                {
                    foreach (string sid in list.GetSubKeyNames())
                    {
                        using (RegistryKey profile = list.OpenSubKey(sid))
                        {
                            string path = profile == null ? null : profile.GetValue("ProfileImagePath") as string;
                            if (string.IsNullOrEmpty(path)) continue;
                            string dir = Path.Combine(Environment.ExpandEnvironmentVariables(path), @"AppData\Local\BeepTone");
                            if (Directory.Exists(dir)) dirs.Add(dir);
                        }
                    }
                }
            }
            catch { }
            if (!dirs.Contains(BeepPaths.DataDir) && Directory.Exists(BeepPaths.DataDir)) dirs.Add(BeepPaths.DataDir);
            return dirs;
        }

        static void CleanDataDir(string dir, List<string> notes)
        {
            foreach (string name in new string[] { "bin", "VBCABLE" })
            {
                string sub = Path.Combine(dir, name);
                if (!Directory.Exists(sub)) continue;
                try
                {
                    Directory.Delete(sub, true);
                    notes.Add("removed " + sub);
                }
                catch (Exception ex) { notes.Add("could not remove " + sub + ": " + ex.Message); }
            }
            foreach (string pattern in new string[] { "launch-hidden.vbs", "*.tmp" })
            {
                foreach (string file in Directory.GetFiles(dir, pattern))
                {
                    try
                    {
                        File.Delete(file);
                        notes.Add("removed " + file);
                    }
                    catch (Exception ex) { notes.Add("could not remove " + file + ": " + ex.Message); }
                }
            }
        }
    }
}
