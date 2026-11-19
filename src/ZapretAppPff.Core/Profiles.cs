using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretAppPff.Core;

public sealed record Profile(string Id, string Name, int Order, IReadOnlyList<string> Arguments);
public sealed record Release(string Tag, string Root, IReadOnlyList<Profile> Profiles, IReadOnlyList<string> Fakes)
{
    public string Bin => Path.Combine(Root, "bin");
    public string Lists => Path.Combine(Root, "lists");
}

public static partial class ProfileCatalog
{
    public static Release Read(string tag, string root)
    {
        var bin = Path.Combine(root, "bin");
        foreach (var file in new[] { "winws.exe", "WinDivert64.sys", "WinDivert.dll", "cygwin1.dll" })
        {
            var path = Path.Combine(bin, file);
            if (!File.Exists(path))
                throw new InvalidDataException($"В релизе отсутствует {file}.");
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 ||
                pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus)
            {
                throw new InvalidDataException($"{file} не предназначен для Windows x64.");
            }
        }

        var profiles = new List<Profile>();
        var scripts = Directory.EnumerateFiles(root, "*.bat")
            .Where(file => !Path.GetFileName(file).StartsWith("service", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => Path.GetFileName(file).Equals("general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(file => NaturalNumbers().Replace(Path.GetFileName(file), m => m.Value.PadLeft(8, '0')),
                StringComparer.OrdinalIgnoreCase);

        foreach (var script in scripts)
        {
            if (new FileInfo(script).Length > 200_000)
                throw new InvalidDataException("Сценарий стратегии слишком большой.");

            var arguments = Parse(File.ReadAllLines(script));
            if (arguments is null)
                continue;

            foreach (var argument in arguments)
                VerifyReference(root, argument);
            var id = Path.GetFileName(script);
            var name = Path.GetFileNameWithoutExtension(script);
            if (name.StartsWith("general (", StringComparison.OrdinalIgnoreCase) && name.EndsWith(')'))
                name = name[9..^1];
            if (name.Equals("general", StringComparison.OrdinalIgnoreCase))
                name = "General";

            profiles.Add(new(id, name, profiles.Count, arguments));
        }

        if (profiles.Count == 0)
            throw new InvalidDataException("В релизе нет поддерживаемых стратегий winws.");

        string[] fakes = Directory.GetFiles(bin, "*.bin")
            .Select(file => Path.GetFileName(file)!)
            .Where(file => Filters.SafeFake(file).Length > 0)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(tag, root, profiles, fakes);
    }

    public static IReadOnlyList<string>? Parse(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            var match = StartCommand().Match(line);
            if (!match.Success)
                continue;

            var arguments = new StringBuilder();
            line = match.Groups["args"].Value;
            while (line.TrimEnd().EndsWith('^'))
            {
                arguments.Append(line.TrimEnd()[..^1]).Append(' ');
                if (++index == lines.Count)
                    throw new InvalidDataException("Незаконченная строка аргументов winws.");
                line = lines[index].Trim();
            }
            arguments.Append(line);
            var tokens = Tokenize(arguments.ToString());
            if (tokens.Count == 0 || tokens.Any(arg => !arg.StartsWith("--", StringComparison.Ordinal)))
                throw new InvalidDataException("Неподдерживаемая команда запуска winws.");
            return tokens;
        }

        if (lines.Any(line => line.Contains("winws.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("В релизе изменился формат запуска winws.");
        return null;
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < command.Length; i++)
        {
            var character = command[i];
            if (character == '"')
                quoted = !quoted;
            else if (!quoted && character == '^' && i + 1 < command.Length)
                word.Append(command[++i]);
            else if (!quoted && char.IsWhiteSpace(character))
                FinishWord();
            else
                word.Append(character);
        }
        if (quoted)
            throw new InvalidDataException("В аргументах winws отсутствует закрывающая кавычка.");
        FinishWord();
        return tokens;

        void FinishWord()
        {
            if (word.Length == 0)
                return;
            tokens.Add(word.ToString());
            word.Clear();
        }
    }

    private static void VerifyReference(string root, string argument)
    {
        var stripped = argument
            .Replace("%GameFilterTCP%", "", StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterUDP%", "", StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilter%", "", StringComparison.OrdinalIgnoreCase);

        var match = FileReference().Match(stripped);
        if (match.Success)
        {
            var name = match.Groups["name"].Value;
            var folder = match.Groups["folder"].Value.Equals("BIN", StringComparison.OrdinalIgnoreCase) ? "bin" : "lists";
            if (folder == "lists" && Filters.UserListNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                return;
            if (!File.Exists(Path.Combine(root, folder, name)))
                throw new InvalidDataException($"В релизе отсутствует файл стратегии {name}.");
        }
        else if (stripped.Contains('%'))
        {
            throw new InvalidDataException($"Неизвестная переменная в аргументе стратегии {argument}.");
        }
    }

    public static IReadOnlyList<string> Resolve(Release release, Profile profile, Filters filters, string overlay)
    {
        return profile.Arguments.Select(ResolveArgument).ToArray();

        string ResolveArgument(string arg)
        {
            var match = FileReference().Match(arg);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                var isBin = match.Groups["folder"].Value.Equals("BIN", StringComparison.OrdinalIgnoreCase);
                var directory = isBin ? release.Bin : release.Lists;
                if (isBin)
                {
                    var selected = name.ToUpperInvariant() switch
                    {
                        "ACTIVE_DISCORD_UDP.BIN" => filters.DiscordFake,
                        "ACTIVE_GAME_UDP.BIN" => filters.GameFake,
                        _ => ""
                    };
                    name = release.Fakes.FirstOrDefault(fake => fake.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? name;
                }
                else if (name.Equals("ipset-all.txt", StringComparison.OrdinalIgnoreCase) ||
                    Filters.UserListNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    directory = overlay;
                    name = name.ToLowerInvariant();
                }
                // Winws can resolve file options from a different working directory.
                var path = Path.GetFullPath(Path.Combine(directory, name)).Replace('\\', '/');
                return arg[..match.Index] + path;
            }

            return arg.Replace("%GameFilterTCP%", filters.ActiveTcp, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilterUDP%", filters.ActiveUdp, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilter%", filters.ActiveLegacy, StringComparison.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex("""^start\s+(?:"[^"]*"\s+)?(?:/[a-z]+\s+)*"%BIN%winws\.exe"\s+(?<args>.*)$""", RegexOptions.IgnoreCase)]
    private static partial Regex StartCommand();

    [GeneratedRegex(@"%(?<folder>BIN|LISTS)%(?<name>[0-9A-Za-z_.-]+)\z", RegexOptions.IgnoreCase)]
    private static partial Regex FileReference();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NaturalNumbers();
}
