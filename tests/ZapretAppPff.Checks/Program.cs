using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ZapretAppPff.Core;

var folder = Path.Combine(Path.GetTempPath(), "zapret-pff-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
using var source = new FlowsealSource();
try
{
    var registration = Path.Combine(folder, "logon.xml");
    const string sid = "S-1-5-21-1001-1002-1003-1000";
    const string installedDir = @"C:\Program Files\Zapret_app_Pff\app";
    const string installedExe = installedDir + @"\Zapret_app_Pff.exe";
    StartupTaskDefinition.Write(registration, sid, installedExe, installedDir);
    XNamespace taskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    var taskRoot = XDocument.Load(registration).Root!;
    var logon = taskRoot.Element(taskNamespace + "Triggers")!.Element(taskNamespace + "LogonTrigger")!;
    Require(logon.Elements().Select(element => element.Name.LocalName).SequenceEqual(["UserId", "Delay"]),
        "Task Scheduler requires UserId before Delay");
    var principal = taskRoot.Element(taskNamespace + "Principals")!.Element(taskNamespace + "Principal")!;
    var actions = taskRoot.Element(taskNamespace + "Actions")!;
    var exec = actions.Element(taskNamespace + "Exec")!;
    Require(logon.Element(taskNamespace + "UserId")?.Value == sid &&
        principal.Element(taskNamespace + "UserId")?.Value == sid &&
        principal.Element(taskNamespace + "LogonType")?.Value == "InteractiveToken" &&
        actions.Attribute("Context")?.Value == principal.Attribute("id")?.Value,
        "the trigger and action use the same signed-in account");
    Require(exec.Element(taskNamespace + "Command")?.Value == installedExe &&
        exec.Element(taskNamespace + "WorkingDirectory")?.Value == installedDir &&
        exec.Element(taskNamespace + "Arguments")?.Value == "--tray",
        "Task Scheduler launches the installed executable in tray mode");
    Require(File.ReadAllBytes(registration).AsSpan().StartsWith(System.Text.Encoding.Unicode.GetPreamble()),
        "task import uses UTF-16 LE with a byte order mark");

    var store = new ReleaseStore(Path.Combine(folder, "flowseal"), source);
    Require(Filters.NormalizePorts("80 , 1024 \t- 65535") == "80,1024-65535", "port normalization");
    foreach (var invalid in new[] { "0", "01", "65536", "210-100", "22,,80", "443&calc.exe", "1024-65535," })
        Require(Filters.NormalizePorts(invalid) is null, "rejected port range " + invalid);

    using (var zip = ZipFile.Open(Path.Combine(folder, "traversal.zip"), ZipArchiveMode.Create))
        zip.CreateEntry("../crossed");
    try
    {
        ReleaseStore.ExtractArchive(Path.Combine(folder, "traversal.zip"), Path.Combine(folder, "unsafe"), CancellationToken.None);
        throw new Exception("archive traversal accepted");
    }
    catch (InvalidDataException) { }

    const string originalHosts = "# local\r\n127.0.0.1 local.test\r\n127.0.0.5 existing.test\r\n";
    const string upstreamHosts = "1.1.1.1 example.org\r\n2001:db8::1 example.org\r\n8.8.8.8 existing.test\r\n";
    var applied = HostsDocument.Edit(originalHosts, upstreamHosts);
    Require(applied.Added == 2 && applied.Skipped == 1, "hosts merge");
    Require(HostsDocument.Edit(applied.Text, upstreamHosts).Text == applied.Text, "hosts idempotence");
    Require(HostsDocument.Edit(applied.Text, null).Text == originalHosts, "hosts removal");

    Profile first = new("1", "First", 0, []);
    Profile second = new("2", "Second", 1, []);
    Profile third = new("3", "Third", 2, []);
    var results = new Dictionary<string, ProfileResult>
    {
        ["1"] = new("1", [new("example.org:443", true, 20, "HTTP 200")]),
        ["2"] = new("2", [new("example.org:443", true, 100, "HTTP 200"), new("example.net:443", true, 60, "HTTP 200")]),
        ["3"] = new("3", [new("example.org:443", true, 10, "HTTP 200")])
    };
    Require(results["2"].Median == 80, "median");
    Require(Benchmarks.Sort([first, second, third], results).Select(p => p.Id).SequenceEqual(["2", "3", "1"]),
        "ranking by successful ports, then by median latency");

    Profile failed = new("4", "Failed", 3, []);
    Profile measured = new("5", "Measured", 4, []);
    results["4"] = new("4", [], "Winws startup error");
    results["5"] = new("5", [new("example.org:443", false, null, "Timed out")]);
    Require(!results["4"].Measured && results["5"].Measured, "startup errors are separate from zero replies");
    Require(Benchmarks.Sort([failed, measured], results).Select(p => p.Id).SequenceEqual(["5", "4"]),
        "measured zero replies rank ahead of startup errors");

    Profile fourthReachable = new("6", "Fourth reachable", 5, []);
    Profile fifthReachable = new("7", "Fifth reachable", 6, []);
    results["6"] = new("6", [new("example.org:443", true, 60, "HTTP 200")]);
    results["7"] = new("7", [new("example.org:443", true, 75, "HTTP 200")]);
    var shortlist = Benchmarks.QuickCandidates([first, second, third, failed, measured, fourthReachable, fifthReachable], results);
    Require(shortlist.Select(profile => profile.Id).SequenceEqual(["2", "3", "1", "6"]),
        "quick test uses only the four best previously reachable strategies");
    Require(Benchmarks.MeetsPreviousScore([results["2"], results["4"]], 2), "matching port count completes quick test");
    Require(!Benchmarks.MeetsPreviousScore([results["1"], results["4"], results["5"]], 2),
        "weaker or failed strategies require full test");

    var testedAt = DateTimeOffset.UtcNow;
    var legacy = JsonSerializer.Deserialize<Ranking>(JsonSerializer.Serialize(new
    {
        ReleaseKey = "old-release",
        FilterKey = "filters",
        NetworkKey = "adapter",
        TestedAt = testedAt,
        Results = results.Values.ToArray()
    }))!;
    Require(legacy.FullTestedAt == testedAt && legacy.WasRetested("1"), "previous full rankings load without migration");
    Require(legacy.Matches("old-release", "filters", "adapter") && !legacy.Matches("old-release", "filters", "changed"),
        "cache scope matches the current network");
    var partial = legacy with { TestedAt = testedAt.AddMinutes(5), RetestedProfileIds = ["2", "3", "1", "6"] };
    var restored = JsonSerializer.Deserialize<Ranking>(JsonSerializer.Serialize(partial))!;
    Require(restored.FullTestedAt == testedAt && restored.WasRetested("2") && !restored.WasRetested("7"),
        "partial ranking round trip retains the last full test and which results are current");

    var updatesOff = JsonSerializer.Deserialize<Preferences>("""
        {"AutoUpdate":false}
        """)!.Normalize();
    Require(!updatesOff.AutoUpdate && updatesOff.NotifiedFlowsealRelease is null, "existing disabled preference survives upgrade");
    var notified = updatesOff with { NotifiedFlowsealRelease = "1.10.4-001122334455" };
    var restoredNotice = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(notified))!.Normalize();
    Require(!restoredNotice.AutoUpdate && restoredNotice.NotifiedFlowsealRelease == notified.NotifiedFlowsealRelease,
        "update notification can be remembered without changing automatic installation");

    Release? release = null;
    if (args is ["--upstream"])
    {
        var available = await store.CheckUpdateAsync(null, CancellationToken.None);
        Require(available is not null && !Directory.Exists(Path.Combine(folder, "flowseal")),
            "availability check reads metadata without staging the ZIP");

        var oldRelease = new Release("0.0.1", Path.Combine(folder, "flowseal", "previous"), [], []);
        store.SetCurrent(oldRelease, null);
        var pointer = Path.Combine(folder, "flowseal", "current.json");
        var previouslyInstalled = File.ReadAllText(pointer);
        var offered = await store.CheckUpdateAsync(oldRelease, CancellationToken.None);
        Require(offered?.Key == available!.Key && File.ReadAllText(pointer) == previouslyInstalled &&
            Directory.GetDirectories(Path.Combine(folder, "flowseal")).Length == 0,
            "checking an available update preserves the current release and never downloads the archive");

        release = await store.PrepareUpdateAsync(offered!, Console.WriteLine, CancellationToken.None);
        Require(File.ReadAllText(pointer) == previouslyInstalled, "staging does not select the new release");
        store.SetCurrent(release, oldRelease);
        Require(store.ReadInstalled(out _)?.Tag == release.Tag, "atomic installed release pointer");
        Require(await store.CheckUpdateAsync(release, CancellationToken.None) is null,
            "current release produces no update candidate");

        var ipSet = Filters.ParseList(await source.IpSetAsync(CancellationToken.None), networks: true);
        Require(ipSet.Length > 0, "official IPSet validates");
        Require(HostsDocument.Edit(originalHosts, await source.HostsAsync(CancellationToken.None)).Added > 0,
            "official hosts validates");
    }
    else if (args is [var releaseZip])
    {
        var extraction = Path.Combine(folder, "extracted Flowseal");
        ReleaseStore.ExtractArchive(releaseZip, extraction, CancellationToken.None);
        release = ProfileCatalog.Read("fixture", ReleaseStore.LocateRelease(extraction));
    }

    if (release is not null)
    {
        var filters = new Filters { Game = GameMode.Tcp, TcpPorts = "9000-9010", DiscordFake = release.Fakes[0] };
        var overlay = Path.Combine(folder, "User data", "lists");
        Directory.CreateDirectory(overlay);
        foreach (var name in Filters.UserListNames.Append("ipset-all.txt"))
            File.WriteAllText(Path.Combine(overlay, name), "");

        foreach (var profile in release.Profiles)
        {
            var resolved = ProfileCatalog.Resolve(release, profile, filters, overlay);
            foreach (var (template, expanded) in profile.Arguments.Zip(resolved))
            {
                if (!template.Contains("%BIN%") && !template.Contains("%LISTS%"))
                    continue;
                var path = expanded[(expanded.IndexOf('=') + 1)..].TrimStart('@');
                Require(Path.IsPathFullyQualified(path) && File.Exists(path), $"{profile.Name} has an absolute file argument: {expanded}");
            }
            Require(resolved.All(argument => !argument.Contains('%') && !argument.Contains('"')), profile.Name + " argument expansion");
            Require(resolved.Any(argument => argument.StartsWith("--wf-tcp=") && argument.EndsWith(",9000-9010")),
                profile.Name + " TCP ports");
            Require(resolved.Any(argument => argument.StartsWith("--wf-udp=") && argument.EndsWith(",12")), profile.Name + " UDP off");
        }
        if (release.Tag is "1.10.4" or "fixture")
        {
            Require(release.Profiles.SelectMany(p => p.Arguments).Any(argument => argument == "--dpi-desync-fake-tls=!"),
                "CMD caret escape is interpreted without a shell");
            var general = release.Profiles.First(profile => profile.Name == "General");
            var generalArguments = ProfileCatalog.Resolve(release, general, filters, overlay);
            Require(generalArguments.Contains("--ipset-exclude=" + Path.Combine(release.Lists, "ipset-exclude.txt").Replace('\\', '/')),
                "General finds the bundled ipset-exclude file regardless of working directory");
            Require(generalArguments.Contains("--ipset=" + Path.Combine(overlay, "ipset-all.txt").Replace('\\', '/')),
                "General finds the user IPSet in the overlay");
        }
        Console.WriteLine($"Flowseal {release.Tag}: parsed all {release.Profiles.Count} strategies.");
    }
    Console.WriteLine("All checks passed.");
}
finally
{
    Directory.Delete(folder, true);
}

static void Require(bool condition, string check)
{
    if (!condition)
        throw new Exception("Failed: " + check);
}
