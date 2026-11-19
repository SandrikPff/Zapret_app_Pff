using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace ZapretAppPff.Core;

public sealed record PortResult(string Endpoint, bool Passed, double? Milliseconds, string Status);
public sealed record ProfileResult(string ProfileId, PortResult[] Ports, string? Error = null)
{
    public bool Measured => Error is null && Ports.Length > 0;
    public int Passed => Ports.Count(port => port.Passed);

    public double? Median
    {
        get
        {
            var values = Ports.Where(port => port.Passed && port.Milliseconds.HasValue)
                .Select(port => port.Milliseconds!.Value).Order().ToArray();
            if (values.Length == 0)
                return null;
            return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
        }
    }
}

public sealed record Ranking(string ReleaseKey, string FilterKey, string NetworkKey,
    DateTimeOffset TestedAt, ProfileResult[] Results)
{
    public DateTimeOffset FullTestedAt { get; init; } = TestedAt;
    public string[]? RetestedProfileIds { get; init; }

    public bool Matches(string releaseKey, string filterKey, string networkKey) =>
        ReleaseKey == releaseKey && FilterKey == filterKey && NetworkKey == networkKey;

    public bool WasRetested(string profileId) =>
        RetestedProfileIds is null || RetestedProfileIds.Contains(profileId, StringComparer.OrdinalIgnoreCase);
}

public static class Benchmarks
{
    private static readonly Uri[] Endpoints =
    [
        new("https://discord.com/"),
        new("https://gateway.discord.gg/"),
        new("https://cdn.discordapp.com/"),
        new("https://updates.discord.com/"),
        new("https://www.youtube.com/"),
        new("https://youtu.be/"),
        new("https://i.ytimg.com/"),
        new("https://redirector.googlevideo.com/"),
        new("http://www.youtube.com/")
    ];

    public static int PortCount => Endpoints.Length;

    public static async Task<ProfileResult> RunAsync(Profile profile, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxResponseHeadersLength = 32
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var ports = await Task.WhenAll(Endpoints.Select(uri => CheckPortAsync(client, uri, token)));
        return new(profile.Id, ports);
    }

    private static async Task<PortResult> CheckPortAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        var endpoint = $"{uri.Host}:{uri.Port}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(2350));

        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        request.Headers.ConnectionClose = true;
        var start = Stopwatch.GetTimestamp();
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var passed = (int)response.StatusCode is not (407 or 451 or 511) && (int)response.StatusCode < 500;
            return new(endpoint, passed, passed ? Stopwatch.GetElapsedTime(start).TotalMilliseconds : null,
                $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(endpoint, false, null, "Тайм-аут");
        }
        catch (HttpRequestException ex)
        {
            var status = ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "Ошибка DNS",
                HttpRequestError.SecureConnectionError => "Ошибка TLS",
                _ => "Нет ответа"
            };
            return new(endpoint, false, null, status);
        }
    }

    public static IEnumerable<Profile> Sort(IEnumerable<Profile> profiles, IReadOnlyDictionary<string, ProfileResult> results) =>
        profiles.OrderByDescending(profile => results.GetValueOrDefault(profile.Id) is { Measured: true } value ? value.Passed : -1)
            .ThenBy(profile => results.GetValueOrDefault(profile.Id)?.Median ?? double.MaxValue)
            .ThenBy(profile => profile.Order);

    public static Profile[] QuickCandidates(IReadOnlyList<Profile> profiles,
        IReadOnlyDictionary<string, ProfileResult> previousResults) =>
        Sort(profiles, previousResults)
            .Where(profile => previousResults.GetValueOrDefault(profile.Id) is { Measured: true, Passed: > 0 })
            .Take(4)
            .ToArray();

    public static bool MeetsPreviousScore(IEnumerable<ProfileResult> results, int previousScore) =>
        results.Any(result => result.Measured && result.Passed >= previousScore);

    public static string NetworkKey()
    {
        try
        {
            var connections = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(adapter => adapter.Id + ":" + string.Join(",",
                    adapter.GetIPProperties().GatewayAddresses
                        .Select(gateway => gateway.Address.ToString()).Order(StringComparer.Ordinal)))
                .Order(StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", connections))));
        }
        catch (NetworkInformationException)
        {
            return "";
        }
    }
}
