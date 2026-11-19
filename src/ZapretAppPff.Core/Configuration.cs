using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretAppPff.Core;

public enum GameMode { Off, Both, Tcp, Udp }
public enum IpSetMode { None, Loaded, Any }

public sealed record Filters
{
    public const string DefaultRange = "1024-65535";
    public const string EmptyIpSet = "203.0.113.113/32\r\n";
    public static readonly string[] UserListNames = ["list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt"];

    public GameMode Game { get; init; }
    public string TcpPorts { get; init; } = DefaultRange;
    public string UdpPorts { get; init; } = DefaultRange;
    public IpSetMode IpSet { get; init; }
    public string DiscordFake { get; init; } = "";
    public string GameFake { get; init; } = "";
    public string ExtraDomains { get; init; } = "";
    public string ExcludedDomains { get; init; } = "";
    public string ExcludedNetworks { get; init; } = "";

    public string ActiveTcp => Game is GameMode.Both or GameMode.Tcp ? TcpPorts : "12";
    public string ActiveUdp => Game is GameMode.Both or GameMode.Udp ? UdpPorts : "12";
    public string ActiveLegacy => Game == GameMode.Udp ? ActiveUdp : ActiveTcp;

    public Filters Normalize() => this with
    {
        Game = Enum.IsDefined(Game) ? Game : GameMode.Off,
        IpSet = Enum.IsDefined(IpSet) ? IpSet : IpSetMode.None,
        TcpPorts = NormalizePorts(TcpPorts) ?? DefaultRange,
        UdpPorts = NormalizePorts(UdpPorts) ?? DefaultRange,
        DiscordFake = SafeFake(DiscordFake),
        GameFake = SafeFake(GameFake),
        ExtraDomains = ExtraDomains ?? "",
        ExcludedDomains = ExcludedDomains ?? "",
        ExcludedNetworks = ExcludedNetworks ?? ""
    };

    public static string? NormalizePorts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            return null;

        var items = value.Split(',', StringSplitOptions.TrimEntries);
        for (var index = 0; index < items.Length; index++)
        {
            var ends = items[index].Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length > 2 || !IsPort(ends[0], out var start))
                return null;
            if (ends.Length == 2 && (!IsPort(ends[1], out var end) || end < start))
                return null;
            items[index] = string.Join("-", ends);
        }

        return string.Join(",", items);
    }

    private static bool IsPort(string value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
        port is >= 1 and <= 65535 && !value.StartsWith('0');

    public static string SafeFake(string? name) =>
        name is { Length: > 4 and <= 120 } &&
        Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]+\.bin\z", RegexOptions.IgnoreCase) &&
        !name.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase) ? name : "";

    public static string ParseList(string value, bool networks)
    {
        if (value.Length > 5_000_000)
            throw new InvalidDataException("Список слишком большой.");

        var result = new StringBuilder();
        foreach (var line in value.Split('\n'))
        {
            var entry = line.Split('#', 2)[0].Trim();
            if (entry.Length == 0)
                continue;

            if (networks ? !IsNetwork(entry) : !IsDomain(entry))
                throw new InvalidDataException($"Некорректная запись в списке «{entry[..Math.Min(entry.Length, 100)]}».");

            result.Append(entry).Append("\r\n");
        }
        return result.ToString();
    }

    public static bool IsDomain(string value) =>
        value.Length is > 0 and <= 253 &&
        Regex.IsMatch(value, @"\A[A-Za-z0-9.-]+\z") &&
        Uri.CheckHostName(value) == UriHostNameType.Dns;

    private static bool IsNetwork(string value)
    {
        if (value.Contains('%') || !Regex.IsMatch(value, @"\A[0-9a-fA-F:./]+\z"))
            return false;

        var parts = value.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var address))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork &&
            !string.Equals(parts[0], address.ToString(), StringComparison.Ordinal))
            return false;
        var width = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return parts.Length == 1 ||
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) &&
            prefix >= 0 && prefix <= width;
    }
}

public sealed record Preferences
{
    public bool Light { get; init; }
    public string Accent { get; init; } = "#FF475B";
    public bool AutoUpdate { get; init; } = true;
    public string? NotifiedFlowsealRelease { get; init; }
    public bool AutoTest { get; init; } = true;
    public bool StartInTray { get; init; }
    public bool AutoConnect { get; init; }
    public Filters Filters { get; init; } = new();

    public Preferences Normalize() => this with
    {
        Accent = IsAccent(Accent) ? Accent : "#FF475B",
        AutoConnect = StartInTray && AutoConnect,
        Filters = (Filters ?? new()).Normalize()
    };

    public static bool IsAccent(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A#[0-9A-Fa-f]{6}\z");
}

public static class LocalFiles
{
    public static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void WriteJson<T>(string path, T value) =>
        Write(path, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));

    public static void WriteText(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));

    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public static string Fingerprint(Filters filters, string ipSetFingerprint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(filters) + "\n" + ipSetFingerprint + "\nprobe-v3")));
}
