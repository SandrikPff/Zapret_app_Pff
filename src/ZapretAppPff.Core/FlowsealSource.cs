using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZapretAppPff.Core;

public sealed record ReleaseAsset(string Tag, Uri Uri, long Size, byte[] Sha256)
{
    public string Key => $"{Tag}-{Convert.ToHexString(Sha256.AsSpan(0, 6)).ToLowerInvariant()}";
}

public sealed class FlowsealSource : IDisposable
{
    public const string ReleasePage = "https://github.com/Flowseal/zapret-discord-youtube/releases/latest";
    private const string ReleaseApi = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
    private const string DataRoot = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/";
    private const long MaxArchiveSize = 64 * 1024 * 1024;

    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public FlowsealSource()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Zapret_app_Pff/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ReleaseAsset> LatestAsync(CancellationToken token)
    {
        using var deadline = Deadline(token, 15);
        using var response = await _http.GetAsync(ReleaseApi, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 1_000_000)
            throw new InvalidDataException("Ответ GitHub слишком большой.");

        var info = await response.Content.ReadFromJsonAsync<ApiRelease>(deadline.Token)
            ?? throw new InvalidDataException("GitHub не вернул сведения о релизе.");
        if (info.Tag is null || !ReleaseStore.ValidTag(info.Tag))
            throw new InvalidDataException("GitHub вернул некорректную версию.");

        var assetNames = new[]
        {
            $"zapret-discord-youtube-{info.Tag}.zip",
            $"zapret-discord-youtube-{info.Tag}-windows-x64.zip",
            $"zapret-discord-youtube-{info.Tag}-win-x64.zip"
        };
        var asset = assetNames.Select(name => info.Assets?.FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(entry => entry is not null);
        if (asset?.Digest is null || !Regex.IsMatch(asset.Digest, @"\Asha256:[0-9a-fA-F]{64}\z") ||
            asset.Size is <= 0 or > MaxArchiveSize ||
            !Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) ||
            !IsHttpsHost(uri, "github.com") ||
            !uri.AbsolutePath.Equals($"/Flowseal/zapret-discord-youtube/releases/download/{info.Tag}/{asset.Name}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("В релизе GitHub нет подходящего ZIP с контрольной суммой SHA-256.");
        }

        return new(info.Tag, uri, asset.Size, Convert.FromHexString(asset.Digest[7..]));
    }

    public async Task DownloadAsync(ReleaseAsset asset, string destination, CancellationToken token)
    {
        using var deadline = Deadline(token, 90);
        using var response = await GetAssetAsync(asset.Uri, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != asset.Size)
            throw new InvalidDataException("Размер ZIP отличается от указанного в релизе.");

        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await CopyLimitedAsync(input, output, asset.Size, sha, deadline.Token);
        if (output.Length != asset.Size || !CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), asset.Sha256))
            throw new InvalidDataException("Контрольная сумма ZIP не совпала со значением GitHub.");
    }

    private async Task<HttpResponseMessage> GetAssetAsync(Uri uri, CancellationToken token)
    {
        for (var i = 0; i < 5; i++)
        {
            if (!IsHttpsHost(uri, "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"))
                throw new InvalidDataException("GitHub перенаправил скачивание на неизвестный сервер.");

            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            uri = location is null
                ? throw new InvalidDataException("В перенаправлении GitHub нет адреса.")
                : new Uri(uri, location);
        }

        throw new InvalidDataException("Слишком много перенаправлений GitHub.");
    }

    public Task<string> HostsAsync(CancellationToken token) => GetDataAsync("hosts", 100_000, token);
    public Task<string> IpSetAsync(CancellationToken token) => GetDataAsync("ipset-service.txt", 5_000_000, token);

    private async Task<string> GetDataAsync(string file, int maxBytes, CancellationToken token)
    {
        using var deadline = Deadline(token, 15);
        using var response = await _http.GetAsync(DataRoot + file, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(input, buffer, maxBytes, null, deadline.Token);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, IncrementalHash? hash,
        CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int size;
        while ((size = await input.ReadAsync(buffer, token)) > 0)
        {
            total += size;
            if (total > limit)
                throw new InvalidDataException("Размер загружаемого файла превышает допустимый.");
            hash?.AppendData(buffer, 0, size);
            await output.WriteAsync(buffer.AsMemory(0, size), token);
        }
    }

    private static bool IsHttpsHost(Uri uri, params string[] hosts) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    private static CancellationTokenSource Deadline(CancellationToken token, int seconds)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(TimeSpan.FromSeconds(seconds));
        return source;
    }

    public void Dispose() => _http.Dispose();

    private sealed record ApiRelease(
        [property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("assets")] ApiAsset[]? Assets);
    private sealed record ApiAsset(
        [property: JsonPropertyName("browser_download_url")] string? Url,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);
}

public sealed class ReleaseStore(string root, FlowsealSource source)
{
    public static bool ValidTag(string? tag) =>
        tag is not null && Regex.IsMatch(tag, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,62}[A-Za-z0-9]\z|\A[A-Za-z0-9]\z");

    private string Pointer => Path.Combine(root, "current.json");
    private sealed record StoredRelease(string Tag, string Folder);
    private sealed record ReleaseState(StoredRelease Current, StoredRelease? Previous);

    public Release? ReadInstalled(out string? error)
    {
        error = null;
        var state = LocalFiles.ReadJson<ReleaseState>(Pointer);
        if (state is null)
            return null;

        foreach (var entry in new[] { state.Current, state.Previous })
        {
            if (entry?.Folder is not { } folder || !ValidTag(entry.Tag) ||
                !Regex.IsMatch(folder, @"\Arelease-[A-Za-z0-9_.-]+-[0-9a-f]{12}\z"))
                continue;
            try
            {
                return ProfileCatalog.Read(entry.Tag, Path.Combine(root, folder));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
        }
        return null;
    }

    public void SetCurrent(Release release, Release? previous) =>
        LocalFiles.WriteJson(Pointer, new ReleaseState(ToStored(release), previous is null ? null : ToStored(previous)));

    private static StoredRelease ToStored(Release release) => new(release.Tag, Path.GetFileName(release.Root));

    public async Task<ReleaseAsset?> CheckUpdateAsync(Release? current, CancellationToken token)
    {
        var asset = await source.LatestAsync(token).ConfigureAwait(false);
        return string.Equals(current?.Root, Destination(asset), StringComparison.OrdinalIgnoreCase) ? null : asset;
    }

    private string Destination(ReleaseAsset asset) => Path.Combine(root, $"release-{asset.Key}");

    public async Task<Release> PrepareUpdateAsync(ReleaseAsset asset, Action<string> report, CancellationToken token)
    {
        var destination = Destination(asset);
        report($"Скачивание Flowseal {asset.Tag}…");
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zip = Path.Combine(staging, "release.zip");
            await source.DownloadAsync(asset, zip, token).ConfigureAwait(false);
            report($"Подготовка Flowseal {asset.Tag}…");
            var extraction = Path.Combine(staging, "files");
            ExtractArchive(zip, extraction, token);
            var releaseRoot = LocateRelease(extraction);
            ProfileCatalog.Read(asset.Tag, releaseRoot);
            token.ThrowIfCancellationRequested();

            if (Directory.Exists(destination))
            {
                try { return ProfileCatalog.Read(asset.Tag, destination); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException)
                {
                    var previous = Path.Combine(staging, "damaged");
                    Directory.Move(destination, previous);
                    try { Directory.Move(releaseRoot, destination); }
                    catch
                    {
                        Directory.Move(previous, destination);
                        throw;
                    }
                    return ProfileCatalog.Read(asset.Tag, destination);
                }
            }

            Directory.Move(releaseRoot, destination);
            return ProfileCatalog.Read(asset.Tag, destination);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void ExtractArchive(string zip, string destination, CancellationToken token)
    {
        const int maxEntries = 2048;
        const long maxUncompressed = 192 * 1024 * 1024;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > maxEntries)
            throw new InvalidDataException("В ZIP слишком много файлов.");

        Directory.CreateDirectory(destination);
        var fullDestination = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Any(part => !SafeZipPart(part)) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) is 0xA000)
            {
                throw new InvalidDataException("ZIP содержит недопустимый путь или символическую ссылку.");
            }

            var target = Path.GetFullPath(Path.Combine(destination, Path.Combine(parts)));
            if (!target.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл ZIP находится за пределами каталога релиза.");

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.Length < 0 || entry.Length > maxUncompressed - declared)
                throw new InvalidDataException("Распакованный релиз превышает допустимый размер.");
            declared += entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew);
            var remaining = entry.Length;
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                remaining -= read;
                if (remaining < 0)
                    throw new InvalidDataException("Размер файла не совпадает с каталогом ZIP.");
                output.Write(buffer, 0, read);
            }
            if (remaining != 0)
                throw new InvalidDataException("ZIP неполный.");
        }
    }

    private static bool SafeZipPart(string part) =>
        part.Length is > 0 and <= 180 && !part.EndsWith('.') && !part.EndsWith(' ') &&
        !part.Any(character => character < ' ' || "<>:\"|?*".Contains(character)) &&
        !Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|\z)", RegexOptions.IgnoreCase);

    public static string LocateRelease(string destination)
    {
        if (File.Exists(Path.Combine(destination, "bin", "winws.exe")))
            return destination;

        var candidates = Directory.EnumerateDirectories(destination)
            .Where(directory => File.Exists(Path.Combine(directory, "bin", "winws.exe")))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new InvalidDataException("В ZIP не найден каталог Flowseal.");
    }
}
