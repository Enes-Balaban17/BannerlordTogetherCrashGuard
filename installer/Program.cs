using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BannerlordTogetherCrashGuard.Setup
{
    internal static class Program
    {
        private const string Module = "BannerlordTogetherCrashGuard";
        private const string Game = "Mount & Blade II Bannerlord";
        private const string ClientBin = "bin/Win64_Shipping_Client";
        private const string Title = "Bannerlord Together Crash Guard";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            using (var picker = new FolderBrowserDialog
            {
                Description = "Mount & Blade II Bannerlord oyun klasörünü seç.",
                ShowNewFolderButton = false,
                SelectedPath = FindGameFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            })
            {
                while (picker.ShowDialog() == DialogResult.OK)
                {
                    if (!IsGameFolder(picker.SelectedPath))
                    {
                        MessageBox.Show("Bannerlord.exe ve Modules klasörünü içeren oyun klasörünü seç.",
                            Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }

                    if (Process.GetProcessesByName("Bannerlord").Any()
                        || Process.GetProcessesByName("Bannerlord.Native").Any()
                        || Process.GetProcessesByName("TaleWorlds.MountAndBlade.Launcher").Any())
                    {
                        MessageBox.Show("Kurulumdan önce oyunu ve başlatıcıyı kapat.",
                            Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    try
                    {
                        Install(picker.SelectedPath);
                        MessageBox.Show("Kurulum tamamlandı. Modun DLL engeli otomatik kaldırıldı. Başlatıcıda modu BannerlordTogether'ın altında etkinleştir.",
                            Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        MessageBox.Show("Oyun klasörüne yazma izni gerekli. Kurucuya sağ tıklayıp Yönetici olarak çalıştır seçeneğiyle yeniden aç.",
                            Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    catch (IOException error)
                    {
                        MessageBox.Show(error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return;
                }
            }
        }

        private static string FindGameFolder()
        {
            string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrWhiteSpace(steam))
                steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");

            string defaultGame = Path.Combine(steam, "steamapps", "common", Game);
            if (IsGameFolder(defaultGame))
                return defaultGame;

            try
            {
                string libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraryFile))
                {
                    foreach (Match match in Regex.Matches(File.ReadAllText(libraryFile),
                        @"(?m)^\s*""path""\s*""([^""]+)"""))
                    {
                        string library = match.Groups[1].Value.Replace(@"\\", @"\");
                        string candidate = Path.Combine(library, "steamapps", "common", Game);
                        if (IsGameFolder(candidate))
                            return candidate;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        private static bool IsGameFolder(string path)
        {
            return File.Exists(Path.Combine(path, ClientBin, "Bannerlord.exe"))
                && File.Exists(Path.Combine(path, "Modules", "Native", "SubModule.xml"));
        }

        internal static void Install(string gameFolder)
        {
            string gameRoot = Path.GetFullPath(gameFolder);
            if (!IsGameFolder(gameRoot))
                throw new IOException("Bannerlord oyun klasörü doğrulanamadı.");

            string modules = Path.Combine(gameRoot, "Modules");
            string destination = Path.Combine(modules, Module);
            if ((File.GetAttributes(modules) & FileAttributes.ReparsePoint) != 0
                || (Directory.Exists(destination)
                    && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Kurulum yolu bir klasör bağlantısı. Oyun klasöründeki gerçek mod dizinini kullan.");

            // Use the same volume for the staged module and its optional backup.
            string transaction = Path.Combine(gameRoot, "." + Module + "-" + Guid.NewGuid().ToString("N"));
            string staged = Path.Combine(transaction, "new");
            string backup = Path.Combine(transaction, "previous");
            string stagedBin = Path.Combine(staged, ClientBin);
            try
            {
                Directory.CreateDirectory(stagedBin);
                WriteResource("SubModule.xml", staged);
                WriteResource(Module + ".dll", stagedBin);
                UnblockFile(Path.Combine(staged, "SubModule.xml"));
                UnblockFile(Path.Combine(stagedBin, Module + ".dll"));
                if (Directory.Exists(destination))
                    Directory.Move(destination, backup);
                try
                {
                    Directory.Move(staged, destination);
                }
                catch
                {
                    if (Directory.Exists(backup) && !Directory.Exists(destination))
                        Directory.Move(backup, destination);
                    throw;
                }
            }
            finally
            {
                // Delete only files created here. Retain any previous installation as a backup.
                try
                {
                    foreach (string file in new[] { Path.Combine(staged, "SubModule.xml"), Path.Combine(stagedBin, Module + ".dll") })
                    {
                        if (File.Exists(file))
                            File.Delete(file);
                    }
                    foreach (string directory in new[] { stagedBin, Path.Combine(staged, "bin"), staged, transaction })
                    {
                        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                            Directory.Delete(directory);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void WriteResource(string name, string directory)
        {
            using (Stream data = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new IOException("Kurulum dosyası eksik: " + name))
            using (FileStream target = File.Open(Path.Combine(directory, name), FileMode.CreateNew))
                data.CopyTo(target);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFile(string path);

        internal static void UnblockFile(string path)
        {
            if (DeleteFile(Path.GetFullPath(path) + ":Zone.Identifier"))
                return;
            int error = Marshal.GetLastWin32Error();
            // Most payloads have no download-zone stream. Other errors must be visible
            // rather than reporting successful unblocking after an access failure.
            if (error != 2 && error != 3)
                throw new IOException("Dosyanın indirme engeli kaldırılamadı: " + Path.GetFileName(path),
                    new System.ComponentModel.Win32Exception(error));
        }
    }
}
