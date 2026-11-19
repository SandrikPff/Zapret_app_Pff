using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ZapretAppPff.Installer
{
    internal static class InstallationRegistration
    {
        public const string Product = "Zapret_app_Pff";
        public const string SetupMutex = @"Global\Zapret_app_Pff-setup";
        public const string UninstallerName = "Uninstall.exe";
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Product;

        public static void Register(string root, string executable)
        {
            var version = FileVersionInfo.GetVersionInfo(executable);
            using (var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var entry = registry.CreateSubKey(KeyPath))
            {
                entry.SetValue("DisplayName", Product);
                entry.SetValue("DisplayVersion", new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart).ToString());
                entry.SetValue("Publisher", Product);
                entry.SetValue("InstallLocation", root);
                entry.SetValue("DisplayIcon", '"' + executable + '"');
                entry.SetValue("UninstallString", '"' + Path.Combine(root, UninstallerName) + '"');
                entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
                entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public static void Remove()
        {
            using (var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                registry.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        }
    }
}
