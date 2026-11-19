using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace ZapretAppPff.Installer
{
    internal static class Program
    {
        private const string Product = InstallationRegistration.Product;
        private const string PayloadName = "ZapretAppPff.Installation.zip";

        [STAThread]
        private static int Main()
        {
            try
            {
                if (!Environment.Is64BitOperatingSystem)
                    throw new PlatformNotSupportedException("Требуется Windows 10 или 11 x64.");

                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Product);
                using (var mutex = new Mutex(false, InstallationRegistration.SetupMutex))
                {
                    try
                    {
                        if (!mutex.WaitOne(TimeSpan.FromSeconds(25)))
                            throw new InvalidOperationException("Установщик уже запущен.");
                    }
                    catch (AbandonedMutexException) { }

                    try
                    {
                        Directory.CreateDirectory(root);
                        RejectLink(root);
                        var app = Path.Combine(root, "app");
                        RejectLink(app);
                        Install(root, app);
                        var executable = Path.Combine(app, Product + ".exe");
                        var maintenance = Path.Combine(root, InstallationRegistration.UninstallerName);
                        RejectLink(maintenance);
                        File.Copy(Path.Combine(app, InstallationRegistration.UninstallerName), maintenance, overwrite: true);
                        InstallationRegistration.Register(root, executable);
                        using (Process.Start(new ProcessStartInfo(executable)
                        {
                            WorkingDirectory = app,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }))
                        { }
                    }
                    finally
                    {
                        mutex.ReleaseMutex();
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось установить или запустить приложение.\n\n" + ex.Message,
                    Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static void Install(string root, string app)
        {
            var executable = Path.Combine(app, Product + ".exe");
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var installed = File.Exists(executable);
            if (installed)
            {
                RejectLink(executable);
                var file = FileVersionInfo.GetVersionInfo(executable);
                var installedVersion = new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart, file.FilePrivatePart);
                if (installedVersion > version)
                    return;
                if (AppIsOpen())
                {
                    if (installedVersion < version)
                        throw new InvalidOperationException("Выберите «Выход» в меню значка Zapret_app_Pff в трее, затем запустите новый установщик.");
                    return;
                }
            }

            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName))
            {
                if (payload == null)
                    throw new InvalidDataException("В установщике отсутствуют файлы приложения.");
                string checksum;
                using (var sha = SHA256.Create())
                    checksum = BitConverter.ToString(sha.ComputeHash(payload));
                var fingerprint = Path.Combine(app, "payload.sha256");
                if (installed && File.Exists(fingerprint) && File.ReadAllText(fingerprint) == checksum)
                    return;

                payload.Position = 0;
                var stage = Path.Combine(root, "app-new-" + Guid.NewGuid().ToString("N"));
                var backup = Path.Combine(root, "app-previous");
                Directory.CreateDirectory(stage);
                try
                {
                    RejectLink(backup);
                    Unpack(payload, stage);
                    if (!File.Exists(Path.Combine(stage, Product + ".exe")) ||
                        !File.Exists(Path.Combine(stage, InstallationRegistration.UninstallerName)))
                        throw new InvalidDataException("Установщик повреждён.");
                    File.WriteAllText(Path.Combine(stage, "payload.sha256"), checksum);

                    if (Directory.Exists(backup))
                    {
                        if (!Directory.Exists(app))
                            Directory.Move(backup, app);
                        else
                            Directory.Delete(backup, true);
                    }
                    if (Directory.Exists(app))
                        Directory.Move(app, backup);
                    try { Directory.Move(stage, app); }
                    catch
                    {
                        if (Directory.Exists(backup) && !Directory.Exists(app))
                            Directory.Move(backup, app);
                        throw;
                    }
                    TryDelete(backup);
                }
                finally
                {
                    TryDelete(stage);
                }
            }
        }

        private static bool AppIsOpen()
        {
            Mutex mutex;
            if (!Mutex.TryOpenExisting("Global\\" + Product, out mutex))
                return false;
            using (mutex)
                return true;
        }

        private static void Unpack(Stream payload, string stage)
        {
            var prefix = stage + Path.DirectorySeparatorChar;
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                foreach (var file in archive.Entries)
                {
                    var name = file.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var path = Path.GetFullPath(Path.Combine(stage, name));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.IndexOf(':') >= 0)
                        throw new InvalidDataException("В архиве установщика обнаружен некорректный путь.");
                    if (file.Name.Length == 0)
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var from = file.Open())
                    using (var to = new FileStream(path, FileMode.CreateNew))
                        from.CopyTo(to);
                }
            }
        }

        private static void RejectLink(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("В каталоге установки обнаружена ссылка.");
        }

        private static void TryDelete(string path)
        {
            if (!Directory.Exists(path))
                return;
            try { Directory.Delete(path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
