using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace ZapretAppPff;

public sealed class WindowsTools
{
    public static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zapret_app_Pff");
    public const string ProductName = "Zapret_app_Pff";
    private static string HostsPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    public string Sid { get; } = WindowsIdentity.GetCurrent().User!.Value;
    public string Data => Path.Combine(Home, "data", Sid);
    public string Overlay => Path.Combine(Data, "lists");
    public string DownloadedIpSet => Path.Combine(Data, "ipset-flowseal.txt");
    public string SettingsPath => Path.Combine(Data, "settings.json");
    public string RankingPath => Path.Combine(Data, "ranking.json");
    private string TaskName => $@"\Zapret_app_Pff-{Sid}";

    public static ProcessStartInfo Hidden(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<int> RunAsync(string file, CancellationToken token, params string[] arguments) =>
        (await RunCommandAsync(file, captureOutput: false, token, arguments)).ExitCode;

    private static async Task<(int ExitCode, string Error)> RunCommandAsync(
        string file, bool captureOutput, CancellationToken token, params string[] arguments)
    {
        var info = Hidden(Path.Combine(Environment.SystemDirectory, file), arguments);
        info.RedirectStandardOutput = captureOutput;
        info.RedirectStandardError = captureOutput;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Не удалось запустить {file}.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var output = captureOutput ? process.StandardOutput.ReadToEndAsync(deadline.Token) : null;
        var errors = captureOutput ? process.StandardError.ReadToEndAsync(deadline.Token) : null;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var standardOutput = output is null ? "" : (await output).Trim();
            var standardError = errors is null ? "" : (await errors).Trim();
            return (process.ExitCode, standardError.Length > 0 ? standardError : standardOutput);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (Win32Exception) when (process.HasExited) { }
            }
            throw;
        }
    }

    private static async Task ChangeStartupTaskAsync(string error, CancellationToken token, params string[] arguments)
    {
        var result = await RunCommandAsync("schtasks.exe", captureOutput: true, token, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{error} Код Windows 0x{result.ExitCode:X8}. {result.Error}".Trim());
    }

    public async Task ConfigureTaskAsync(bool enabled, CancellationToken token)
    {
        if (!enabled)
        {
            await RemoveStartupTaskAsync(TaskName, token);
            return;
        }

        var appDirectory = Path.Combine(Home, "app");
        var executable = Path.Combine(appDirectory, "Zapret_app_Pff.exe");
        if (!File.Exists(executable))
            throw new InvalidOperationException("Для автозапуска сначала запустите установочный EXE.");

        Directory.CreateDirectory(Data);
        var xml = Path.Combine(Data, "logon-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            StartupTaskDefinition.Write(xml, Sid, executable, appDirectory);
            await ChangeStartupTaskAsync("Планировщик не смог включить автозапуск.", token, "/Create", "/TN", TaskName, "/XML", xml, "/F");
        }
        finally
        {
            File.Delete(xml);
        }
    }

    internal async Task RemoveInstallationChangesAsync()
    {
        var data = Path.Combine(Home, "data");
        var users = Directory.Exists(data)
            ? Directory.EnumerateDirectories(data).Select(path => Path.GetFileName(path)!)
            : Enumerable.Empty<string>();

        foreach (var user in users.Append(Sid).Distinct(StringComparer.Ordinal))
        {
            SecurityIdentifier identity;
            try { identity = new SecurityIdentifier(user); }
            catch (ArgumentException) { continue; }

            await RemoveStartupTaskAsync($@"\Zapret_app_Pff-{identity.Value}", CancellationToken.None);
        }
        await EditHostsAsync(null, CancellationToken.None);
    }

    private static async Task RemoveStartupTaskAsync(string name, CancellationToken token)
    {
        if (await RunAsync("schtasks.exe", token, "/Query", "/TN", name) != 0)
            return;
        await ChangeStartupTaskAsync("Планировщик не смог отключить автозапуск.", token, "/Delete", "/TN", name, "/F");
    }

    public string IpSetKey => File.Exists(DownloadedIpSet) ? File.GetLastWriteTimeUtc(DownloadedIpSet).Ticks.ToString() : "";

    public void PrepareLists(Release release, Filters filters)
    {
        var general = Filters.ParseList(filters.ExtraDomains, networks: false);
        var excluded = Filters.ParseList(filters.ExcludedDomains, networks: false);
        var networks = Filters.ParseList(filters.ExcludedNetworks, networks: true);
        var ipSet = filters.IpSet switch
        {
            IpSetMode.Any => "",
            IpSetMode.Loaded => ReadIpSet(release),
            _ => Filters.EmptyIpSet
        };

        LocalFiles.WriteText(Path.Combine(Overlay, "list-general-user.txt"), NonEmpty(general, "domain.example.abc\r\n"));
        LocalFiles.WriteText(Path.Combine(Overlay, "list-exclude-user.txt"), NonEmpty(excluded, "domain.example.abc\r\n"));
        LocalFiles.WriteText(Path.Combine(Overlay, "ipset-exclude-user.txt"), NonEmpty(networks, Filters.EmptyIpSet));
        LocalFiles.WriteText(Path.Combine(Overlay, "ipset-all.txt"), ipSet);
    }

    private ComputerService ReadLegacy() => new(ServiceState("zapret"), HasFlowsealMarker());
    private sealed record ComputerService(ServiceControllerStatus? State, bool IsFlowseal);

    private string ReadIpSet(Release release)
    {
        if (File.Exists(DownloadedIpSet))
            return File.ReadAllText(DownloadedIpSet);
        var packaged = Path.Combine(release.Lists, "ipset-all.txt.backup");
        if (File.Exists(packaged) && new FileInfo(packaged).Length > 0)
            return File.ReadAllText(packaged);
        packaged = Path.Combine(release.Lists, "ipset-all.txt");
        var deployed = File.Exists(packaged) ? File.ReadAllText(packaged) : "";
        return !string.IsNullOrWhiteSpace(deployed) && deployed.Trim() != Filters.EmptyIpSet.Trim()
            ? deployed : throw new InvalidDataException("Скачайте список IPSet или выберите другой режим.");
    }

    private static string NonEmpty(string value, string fallback) => value.Length == 0 ? fallback : value;

    public bool IsOwnLegacyFlowseal => ReadLegacy() is { State: not null, IsFlowseal: true };

    public void EnsureAvailable(int? ownProcess)
    {
        if (ServiceState("BFE") != ServiceControllerStatus.Running)
            throw new InvalidOperationException("Служба Windows «Базовый механизм фильтрации» (BFE) должна работать.");
        if (ServiceState("zapret") is { } state && state != ServiceControllerStatus.Stopped)
            throw new InvalidOperationException("Запущена служба zapret. Откройте «Настройки → Диагностика».");
        var other = FindOtherWinws(ownProcess);
        if (other.Count > 0)
            throw new InvalidOperationException($"Запущен другой winws ({other[0]}). Закройте запустившее его приложение и повторите проверку.");
    }

    public async Task RemoveLegacyAsync(CancellationToken token)
    {
        var service = ReadLegacy();
        if (service.State is null)
            return;
        if (!service.IsFlowseal)
            throw new InvalidOperationException("В службе zapret отсутствует метка Flowseal.");
        if (service.State != ServiceControllerStatus.Stopped)
        {
            await RunAsync("sc.exe", token, "stop", "zapret");
            var stopDeadline = DateTime.UtcNow.AddSeconds(7);
            while (ServiceState("zapret") is { } status && status != ServiceControllerStatus.Stopped)
            {
                if (DateTime.UtcNow >= stopDeadline)
                    throw new InvalidOperationException("Служба Flowseal не остановилась.");
                await Task.Delay(250, token);
            }
        }
        if (ServiceState("zapret").HasValue && await RunAsync("sc.exe", token, "delete", "zapret") != 0)
            throw new InvalidOperationException("Windows не удалила прежнюю службу Flowseal.");
    }

    private static bool HasFlowsealMarker()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
        return key?.GetValue("zapret-discord-youtube") is string;
    }

    private static IReadOnlyList<string> FindOtherWinws(int? ownProcess)
    {
        var found = new List<string>();
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                if (process.Id == ownProcess)
                    continue;

                var label = $"PID {process.Id}";
                try
                {
                    if (process.HasExited)
                        continue;
                    if (process.MainModule?.FileName is { } path)
                        label += $" · {path}";
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { continue; }

                found.Add(label);
            }
        }
        return found;
    }

    private static ServiceControllerStatus? ServiceState(string name)
    {
        using var service = new ServiceController(name);
        try { return service.Status; }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            return null;
        }
    }

    public string Diagnose(Release? release, int? ownProcess)
    {
        var lines = new List<string>
        {
            release is null ? "Flowseal ещё не установлен." : $"Flowseal {release.Tag} · {release.Profiles.Count} стратегий",
            ServiceState("BFE") == ServiceControllerStatus.Running ? "BFE работает." : "BFE остановлена.",
            ServiceState("WinDivert") is { } driver ? $"WinDivert: {ServiceStatusText(driver)}." : "Драйвер WinDivert запускается вместе с winws.",
            ReadLegacy().State is { } legacy ? $"Прежняя служба zapret: {ServiceStatusText(legacy)}." : "Прежняя служба zapret отсутствует.",
        };
        var other = FindOtherWinws(ownProcess);
        if (other.Count > 0)
            lines.AddRange(other.Select(value => $"Другой winws: {value}."));
        using var internetSettings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (internetSettings?.GetValue("ProxyEnable") is 1)
            lines.Add("Системный прокси включён. Проверки портов идут напрямую.");

        var services = ServiceController.GetServices();
        try
        {
            var names = services.Where(service => service.Status == ServiceControllerStatus.Running)
                .Where(service => new[] { "GoodbyeDPI", "discordfix_zapret", "Adguard", "winws1", "winws2", "Killer",
                    "HappService", "VPN", "SmartByte", "TracSrvWrapper", "EPWD", "Intel Connectivity" }
                    .Any(term => $"{service.ServiceName} {service.DisplayName}".Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Select(service => service.DisplayName).Order().ToArray();
            if (names.Length > 0)
                lines.Add("Возможные конфликты: " + string.Join(", ", names) + ".");
        }
        finally
        {
            foreach (var service in services)
                service.Dispose();
        }

        if (File.Exists(HostsPath))
        {
            var hosts = File.ReadAllText(HostsPath);
            if (hosts.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                hosts.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                lines.Add("В hosts есть записи YouTube, которые могут влиять на подключение.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ServiceStatusText(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => "работает",
        ServiceControllerStatus.Stopped => "остановлена",
        ServiceControllerStatus.StartPending => "запускается",
        ServiceControllerStatus.StopPending => "останавливается",
        _ => "приостановлена"
    };

    public async Task EnableTimestampsAsync(CancellationToken token)
    {
        if (await RunAsync("netsh.exe", token, "interface", "tcp", "set", "global", "timestamps=enabled") != 0)
            throw new InvalidOperationException("Windows не смогла включить TCP timestamps.");
    }

    public async Task<HostsEdit> EditHostsAsync(string? upstream, CancellationToken token)
    {
        var exists = File.Exists(HostsPath);
        if (exists && File.GetAttributes(HostsPath).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Файл hosts является ссылкой. Изменение отменено.");
        var raw = exists ? File.ReadAllBytes(HostsPath) : [];
        var encoding = raw.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()) ? Encoding.Unicode :
            raw.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()) ? Encoding.BigEndianUnicode : Encoding.Latin1;
        var preamble = encoding == Encoding.Latin1 ? 0 : encoding.GetPreamble().Length;
        // Decode single-byte data reversibly. This preserves existing UTF-8 and ANSI comments.
        var text = encoding.GetString(raw.AsSpan(preamble));
        var hadUtf8Bom = encoding == Encoding.Latin1 && raw.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        var edit = HostsDocument.Edit(hadUtf8Bom ? text[3..] : text, upstream);
        var editedText = hadUtf8Bom ? text[..3] + edit.Text : edit.Text;
        if (text == editedText)
            return edit;

        var newBytes = encoding.GetPreamble().Concat(encoding.GetBytes(editedText)).ToArray();
        token.ThrowIfCancellationRequested();
        LocalFiles.Write(Path.Combine(Data, "hosts.before"), raw);
        var temp = $"{HostsPath}.zapret-pilot-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew))
            {
                stream.Write(newBytes);
                stream.Flush(flushToDisk: true);
            }
            if (exists)
                File.Replace(temp, HostsPath, destinationBackupFileName: null);
            else
                File.Move(temp, HostsPath);
        }
        finally
        {
            File.Delete(temp);
        }
        await RunAsync("ipconfig.exe", token, "/flushdns");
        return edit;
    }

    public void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Data);
            var log = Path.Combine(Data, "pilot.log");
            if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024)
                File.Delete(log);
            File.AppendAllText(log, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
