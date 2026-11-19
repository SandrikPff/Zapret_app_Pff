using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ZapretAppPff.Installer;

namespace ZapretAppPff.Uninstaller
{
    internal static class Program
    {
        private const string Product = InstallationRegistration.Product;
        private const string StagePrefix = Product + "-uninstall-";
        private const int DeleteOnRestart = 4;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var root = Path.Combine(programFiles, Product);
                var executable = Assembly.GetExecutingAssembly().Location;

                if (args.Length == 2 && args[0] == "--remove-installed" && int.TryParse(args[1], out var launcherId))
                {
                    var stage = Path.GetDirectoryName(executable);
                    if (!string.Equals(Path.GetDirectoryName(stage), programFiles, StringComparison.OrdinalIgnoreCase) ||
                        !Path.GetFileName(stage).StartsWith(StagePrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("Неверная папка запуска программы удаления.");
                    RemoveInstalled(root, stage, executable, launcherId);
                    return 0;
                }

                if (MessageBox.Show("Удалить Zapret_app_Pff, его настройки и загруженные стратегии?",
                        Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return 0;

                StartWorker(programFiles, executable);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static void StartWorker(string programFiles, string original)
        {
            var stage = Path.Combine(programFiles, StagePrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var worker = Path.Combine(stage, InstallationRegistration.UninstallerName);
            try
            {
                File.Copy(original, worker);
                int launcherId;
                using (var current = Process.GetCurrentProcess())
                    launcherId = current.Id;

                using (var process = Process.Start(new ProcessStartInfo(worker, "--remove-installed " + launcherId)
                {
                    WorkingDirectory = stage,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    if (process == null)
                        throw new InvalidOperationException("Не удалось запустить программу удаления.");
                }
            }
            catch
            {
                Directory.Delete(stage, true);
                throw;
            }
        }

        private static void RemoveInstalled(string root, string stage, string executable, int launcherId)
        {
            try
            {
                try
                {
                    using (var launcher = Process.GetProcessById(launcherId))
                        if (!launcher.WaitForExit(15000))
                            throw new IOException("Не удалось освободить файлы программы удаления.");
                }
                catch (ArgumentException) { }

                using (var mutex = new Mutex(false, InstallationRegistration.SetupMutex))
                {
                    try
                    {
                        if (!mutex.WaitOne(TimeSpan.FromSeconds(25)))
                            throw new InvalidOperationException("Дождитесь завершения другого установщика.");
                    }
                    catch (AbandonedMutexException) { }

                    try
                    {
                        RejectLink(root);
                        var appDirectory = Path.Combine(root, "app");
                        RejectLink(appDirectory);
                        var app = Path.Combine(appDirectory, Product + ".exe");
                        RejectLink(app);
                        if (File.Exists(app))
                            PrepareRemoval(app, stage);

                        RemoveFiles(root);
                        InstallationRegistration.Remove();
                    }
                    finally
                    {
                        mutex.ReleaseMutex();
                    }
                }
                MessageBox.Show("Zapret_app_Pff удалён.", Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                MoveFileEx(executable, null, DeleteOnRestart);
                MoveFileEx(stage, null, DeleteOnRestart);
            }
        }

        private static void PrepareRemoval(string app, string stage)
        {
            using (var process = Process.Start(new ProcessStartInfo(app, "--prepare-uninstall")
            {
                WorkingDirectory = stage,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            }))
            {
                if (process == null || !process.WaitForExit(120000))
                    throw new IOException("Приложение не успело завершить работу. Выйдите через меню в трее и повторите удаление.");

                if (process.ExitCode != 0)
                {
                    var error = process.StandardError.ReadToEnd().Trim();
                    throw new IOException(string.IsNullOrWhiteSpace(error) ? "Не удалось подготовить приложение к удалению." : error);
                }
            }
        }

        private static void RemoveFiles(string root)
        {
            if (!Directory.Exists(root))
                return;

            var uninstaller = Path.Combine(root, InstallationRegistration.UninstallerName);
            foreach (var path in Directory.GetFileSystemEntries(root))
            {
                if (string.Equals(path, uninstaller, StringComparison.OrdinalIgnoreCase))
                    continue;

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                    Directory.Delete(path, (attributes & FileAttributes.ReparsePoint) == 0);
                else
                    File.Delete(path);
            }
            File.Delete(uninstaller);
            Directory.Delete(root);
        }

        private static void RejectLink(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("В папке установки обнаружена ссылка.");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existing, string replacement, int flags);
    }
}
