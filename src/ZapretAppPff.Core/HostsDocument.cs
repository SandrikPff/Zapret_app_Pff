using System.Net;
using System.Text.RegularExpressions;

namespace ZapretAppPff.Core;

public sealed record HostsEdit(string Text, int Added, int Skipped);

public static class HostsDocument
{
    public const string Begin = "# BEGIN Zapret_app_Pff";
    public const string End = "# END Zapret_app_Pff";

    public static HostsEdit Edit(string currentText, string? upstreamText)
    {
        var current = RemoveBlock(currentText);
        if (upstreamText is null)
            return new(current, 0, 0);

        var existingDomains = ReadLines(current)
            .SelectMany(line => EntryParts(line).Skip(1))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var line in ReadLines(upstreamText))
        {
            var parts = EntryParts(line);
            if (parts.Length == 0)
                continue;
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var address) ||
                address.ToString().Contains('%') || parts.Skip(1).Any(domain => !Filters.IsDomain(domain)))
            {
                throw new InvalidDataException("Список hosts Flowseal имеет неожиданный формат.");
            }
            foreach (var domain in parts.Skip(1))
            {
                if (existingDomains.Contains(domain))
                    skipped++;
                else
                    entries.Add($"{address} {domain}");
            }
        }
        if (entries.Count == 0)
        {
            if (skipped > 0)
                return new(current, 0, skipped);
            throw new InvalidDataException("Список hosts Flowseal пуст.");
        }

        var nl = current.Contains("\r\n") || !current.Contains('\n') ? "\r\n" : "\n";
        var separator = current.Length > 0 && !current.EndsWith('\n') ? nl : "";
        return new(current + separator + Begin + nl + string.Join(nl, entries) + nl + End + nl, entries.Count, skipped);
    }

    private static string RemoveBlock(string text)
    {
        var pattern = @"(?m)^" + Regex.Escape(Begin) + @"\r?$[\s\S]*?^" + Regex.Escape(End) + @"(?:\r?\n|\z)";
        var removed = Regex.Replace(text, pattern, "");
        if (ReadLines(removed).Any(line => line == Begin || line == End))
            throw new InvalidDataException("Разметка блока Zapret_app_Pff в hosts повреждена.");
        return removed;
    }

    private static string[] EntryParts(string line) =>
        line.Split('#', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> ReadLines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r'));
}
