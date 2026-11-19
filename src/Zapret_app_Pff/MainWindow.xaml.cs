using System.ComponentModel;
using System.Drawing;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ZapretAppPff;

public partial class MainWindow : Window, IDisposable
{
    private static readonly TimeSpan RankingLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan FullRankingLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    private readonly WindowsTools _windows = new();
    private readonly FlowsealSource _source = new();
    private readonly WinwsEngine _engine = new();
    private readonly ReleaseStore _store;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _accentSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Forms.NotifyIcon _tray;
    private readonly Icon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayPower;
    private readonly Forms.ToolStripMenuItem _trayTest;

    private Preferences _prefs;
    private Release? _release;
    private Ranking? _ranking;
    private Dictionary<string, ProfileResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private Activity? _activity;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private string _status = "Подготовка…";
    private string? _pendingAccent;
    private string? _availableFlowseal;
    private UpdateNotice? _updateNotice;
    private bool _painted;
    private bool _exiting;
    private bool _disposed;
    private bool _sweep;
    private bool _sweepHadConnection;
    private bool _followBest;
    private bool _autoPaused;
    private bool _stopRequested;
    private bool _autoConnectPending;
    private int _tested;
    private int _sweepTotal;

    public MainWindow(bool trayOnly)
    {
        _store = new(Path.Combine(WindowsTools.Home, "flowseal"), _source);
        _prefs = (LocalFiles.ReadJson<Preferences>(_windows.SettingsPath) ?? new()).Normalize();
        _autoConnectPending = trayOnly && _prefs.AutoConnect;

        InitializeComponent();
        Theme.Apply(_prefs);
        LightThemeToggle.IsChecked = _prefs.Light;
        LoadAccentPicker();
        _accentSaveTimer.Tick += (_, _) => CommitAccent();
        SyncStartupFields();
        SyncFilterFields();
        _painted = true;

        var resource = Application.GetResourceStream(new Uri("Assets/Pilot.ico", UriKind.Relative))!;
        using (resource.Stream)
        using (var icon = new Icon(resource.Stream))
            _trayIcon = (Icon)icon.Clone();

        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Открыть Zapret_app_Pff", null, (_, _) => ShowFromTray());
        _trayPower = new Forms.ToolStripMenuItem("Включить", null, async (_, _) => await TogglePowerAsync());
        _trayTest = new Forms.ToolStripMenuItem("Тест лучшего", null, async (_, _) => await ManualSweepAsync());
        trayMenu.Items.Add(_trayPower);
        trayMenu.Items.Add(_trayTest);
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = WindowsTools.ProductName, ContextMenuStrip = trayMenu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        _engine.ProcessExited += process => Dispatcher.InvokeAsync(() =>
        {
            if (!_exiting && _activity is null && _engine.SetExited(process) is { } error)
            {
                _windows.Log(error);
                SetStatus(error);
            }
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await AutomaticTickAsync();
        Refresh();
    }

    public Task InitializeAsync() => RunAsync(async token =>
    {
        try
        {
            _release = _store.ReadInstalled(out var readError);
            if (readError is not null)
                _windows.Log(readError);
            RestoreRanking();
            SyncFakeFields();
            Refresh();
            string? updateWarning = null;
            if (_release is null)
                _lastAttempt = DateTimeOffset.UtcNow;
            try { await UpdateAsync(token); }
            catch (Exception ex) when (!token.IsCancellationRequested && _release is not null)
            {
                updateWarning = $"Проверка обновления Flowseal не завершилась. Используется {_release.Tag}.";
                _windows.Log(ex.ToString());
            }
            if (_release is not null)
            {
                if (_autoConnectPending && RankingIsFresh() && BestProfile() is { } cached)
                    await ConnectAsync(cached, automatic: true, token);
                else if ((_autoConnectPending || _prefs.AutoTest) && (!RankingIsFresh() || _autoConnectPending))
                    await SweepAsync(_autoConnectPending, token);
                else if (_status == "Подготовка…")
                    SetStatus("Готово к подключению.");
            }
            if (updateWarning is not null && !_stopRequested)
                SetStatus(updateWarning);
        }
        finally
        {
            if (!_exiting)
                _timer.Start();
        }
    });

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_activity is not null || _exiting)
            return Task.CompletedTask;

        var activity = new Activity();
        _activity = activity;
        _stopRequested = false;
        Refresh();
        activity.Completion = ExecuteAsync(activity, action);
        return activity.Completion;
    }

    private async Task ExecuteAsync(Activity activity, Func<CancellationToken, Task> action)
    {
        try
        {
            await action(activity.Cancellation.Token);
        }
        catch (OperationCanceledException) when (activity.Cancellation.IsCancellationRequested)
        {
            if (!_stopRequested && !_exiting)
                SetStatus("Операция отменена.");
        }
        catch (Exception ex)
        {
            _windows.Log(ex.ToString());
            if (!_exiting)
                SetStatus(UserError(ex));
        }
        finally
        {
            activity.Cancellation.Dispose();
            _activity = null;
            if (!_exiting)
            {
                if (_engine.ReapExited() is { } error)
                {
                    _windows.Log(error);
                    SetStatus(error);
                }
                Refresh();
            }
        }
    }

    private static string UserError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests } =>
            "GitHub ограничил запросы. Проверку релиза можно повторить позже в настройках.",
        HttpRequestException => "Скачивание не удалось. Проверьте доступ к GitHub и повторите операцию.",
        OperationCanceledException => "Операция не завершилась за отведённое время.",
        UnauthorizedAccessException => "Windows отказала в доступе. Проверьте права администратора.",
        _ => ex.Message.Length > 240 ? ex.Message[..240] + "…" : ex.Message
    };

    private async Task UpdateAsync(CancellationToken token, bool installRequested = false)
    {
        if (installRequested || _release is null)
            SetStatus("Проверка релизов Flowseal…");

        var previousRelease = _release;
        var asset = await _store.CheckUpdateAsync(previousRelease, token);
        _availableFlowseal = asset?.Tag;
        if (asset is null)
        {
            if (installRequested)
                SetStatus($"Flowseal {previousRelease!.Tag} уже актуален.");
            return;
        }

        if (previousRelease is not null && !installRequested && !_prefs.AutoUpdate)
        {
            OfferUpdate(asset, previousRelease.Tag);
            return;
        }

        Release replacement;
        try
        {
            replacement = await _store.PrepareUpdateAsync(asset,
                status => Dispatcher.InvokeAsync(() => { if (!_exiting && !token.IsCancellationRequested) SetStatus(status); }), token);

            var previous = _engine.Active;
            var activeRelease = _engine.ActiveRelease;
            try
            {
                if (previous is not null)
                {
                    await _engine.StopAsync();
                    var linked = replacement.Profiles.FirstOrDefault(profile => profile.Id == previous.Id)
                        ?? replacement.Profiles[0];
                    await StartAsync(replacement, linked, _prefs.Filters, token);
                }
                else
                {
                    _windows.PrepareLists(replacement, _prefs.Filters);
                }

                token.ThrowIfCancellationRequested();
                _store.SetCurrent(replacement, previousRelease);
            }
            catch
            {
                await RestoreConnectionAsync(activeRelease, previous);
                throw;
            }
        }
        catch (Exception) when (previousRelease is not null && !token.IsCancellationRequested)
        {
            OfferUpdate(asset, previousRelease.Tag);
            throw;
        }

        _release = replacement;
        _availableFlowseal = null;
        ResetRanking();
        SyncFakeFields();
        SetStatus($"Установлен Flowseal {replacement.Tag}.");
        if (previousRelease is not null)
            ShowUpdateNotice("Flowseal обновился", $"Установлена версия {replacement.Tag}.", update: null);
    }

    private void OfferUpdate(ReleaseAsset asset, string installed)
    {
        if (_exiting || _prefs.NotifiedFlowsealRelease == asset.Key)
            return;

        ShowUpdateNotice("Вышел новый релиз Flowseal", $"Доступна версия {asset.Tag}. Сейчас установлена {installed}.", InstallFromNoticeAsync);
        try { Save(_prefs with { NotifiedFlowsealRelease = asset.Key }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _windows.Log(ex.Message);
        }
    }

    private void ShowUpdateNotice(string title, string message, Func<Task>? update)
    {
        if (_exiting)
            return;

        _updateNotice?.Close();
        var notice = new UpdateNotice(title, message, update);
        _updateNotice = notice;
        notice.Closed += (_, _) =>
        {
            if (ReferenceEquals(_updateNotice, notice))
                _updateNotice = null;
        };
        notice.Show();
    }

    private async Task InstallFromNoticeAsync()
    {
        // Startup can be testing strategies while the notification is visible.
        while (!_exiting && _activity is { } current)
            await current.Completion;

        if (!_exiting)
            await RunAsync(token => UpdateAsync(token, installRequested: true));
    }

    private async Task AutomaticTickAsync()
    {
        var needsInstall = _release is null && (_prefs.AutoUpdate || _autoConnectPending);
        if (_activity is not null || _exiting || _autoPaused ||
            (!needsInstall && !_autoConnectPending && !_prefs.AutoTest) ||
            DateTimeOffset.UtcNow - _lastAttempt < RetryInterval ||
            !NetworkInterface.GetIsNetworkAvailable())
            return;

        if (_release is null && !needsInstall)
            return;
        if (_release is not null && RankingIsFresh() && !_autoConnectPending)
            return;

        await RunAsync(async token =>
        {
            _lastAttempt = DateTimeOffset.UtcNow;
            if (_release is null)
                await UpdateAsync(token);
            if (_autoConnectPending && RankingIsFresh() && BestProfile() is { } cached)
                await ConnectAsync(cached, automatic: true, token);
            else if (_autoConnectPending || _prefs.AutoTest)
                await SweepAsync(_autoConnectPending, token);
        });
    }

    private Task ManualSweepAsync(bool full = false) =>
        RunAsync(token => SweepAsync(_autoConnectPending, token, full));

    private bool RankingIsFresh()
    {
        var age = DateTimeOffset.UtcNow - _ranking?.TestedAt;
        return age >= TimeSpan.Zero && age < RankingLifetime &&
            _ranking?.Results.Any(result => result is { Ports: { Length: > 0 }, Error: null }) == true &&
            _ranking.NetworkKey == Benchmarks.NetworkKey();
    }

    private async Task SweepAsync(bool connectAfter, CancellationToken token, bool full = false)
    {
        var release = _release ?? throw new InvalidOperationException("Сначала установите Flowseal.");
        _lastAttempt = DateTimeOffset.UtcNow;
        var previous = _engine.Active;
        var previousRelease = _engine.ActiveRelease;
        _windows.EnsureAvailable(_engine.Id);
        _windows.PrepareLists(release, _prefs.Filters);

        var network = Benchmarks.NetworkKey();
        var releaseKey = ReleaseKey(release);
        var filterKey = FilterKey();
        var oldRanking = _ranking;
        var oldResults = _results;
        var fullAge = DateTimeOffset.UtcNow - oldRanking?.FullTestedAt;
        Profile[] candidates = !full && network.Length > 0 && fullAge >= TimeSpan.Zero && fullAge < FullRankingLifetime &&
            oldRanking?.Matches(releaseKey, filterKey, network) == true
            ? Benchmarks.QuickCandidates(release.Profiles, oldResults)
            : [];
        var quick = candidates.Length > 0 && candidates.Length < release.Profiles.Count;

        _tested = 0;
        _sweepTotal = quick ? candidates.Length : release.Profiles.Count;
        _sweep = true;
        _sweepHadConnection = previous is not null;
        var newResults = new Dictionary<string, ProfileResult>(StringComparer.OrdinalIgnoreCase);
        _results = newResults;
        Dictionary<string, ProfileResult>? savedResults = null;
        try
        {
            await _engine.StopAsync();
            await MeasureAsync(quick ? candidates : release.Profiles);
            if (quick && !Benchmarks.MeetsPreviousScore(newResults.Values, oldResults[candidates[0].Id].Passed))
            {
                quick = false;
                _sweepTotal = release.Profiles.Count;
                await MeasureAsync(release.Profiles.Where(profile => !newResults.ContainsKey(profile.Id)));
            }

            token.ThrowIfCancellationRequested();
            if (network != Benchmarks.NetworkKey() || filterKey != FilterKey())
                throw new InvalidOperationException("Сеть или параметры фильтра изменились во время теста. Запустите его ещё раз.");

            var combined = quick ? new Dictionary<string, ProfileResult>(oldResults, StringComparer.OrdinalIgnoreCase) : newResults;
            if (quick)
                foreach (var result in newResults.Values)
                    combined[result.ProfileId] = result;

            var testedAt = DateTimeOffset.UtcNow;
            var ranking = new Ranking(releaseKey, filterKey, network, testedAt, combined.Values.ToArray())
            {
                FullTestedAt = quick ? oldRanking!.FullTestedAt : testedAt,
                RetestedProfileIds = quick ? newResults.Keys.ToArray() : null
            };
            LocalFiles.WriteJson(_windows.RankingPath, ranking);
            _ranking = ranking;
            savedResults = combined;

            var best = BestProfile();
            var target = connectAfter || (previous is not null && _followBest) ? best ?? previous : previous;
            if (target is not null)
            {
                await _engine.StartAsync(release, target, _prefs.Filters, _windows.Overlay, token);
                if (connectAfter && best is not null)
                {
                    _followBest = true;
                    _autoConnectPending = false;
                }
            }

            var measured = newResults.Values.Where(result => result.Measured).ToArray();
            if (best is not null)
            {
                var label = quick ? "Быстрый тест" : "Полный тест";
                SetStatus($"{label} · измерено {measured.Length} из {release.Profiles.Count} · лидер {best.Name}.");
            }
            else if (measured.Length > 0)
            {
                var failures = measured.SelectMany(result => result.Ports).Where(port => !port.Passed)
                    .GroupBy(port => port.Status).OrderByDescending(group => group.Count()).First().Key;
                SetStatus($"Ни один адрес не ответил успешно. Чаще всего: {failures}. Подробности в столбце «Порты».");
            }
            else
                SetStatus($"Winws не запустился ни в одной стратегии. {newResults.Values.FirstOrDefault()?.Error}".Trim());
        }
        finally
        {
            _sweep = false;
            _sweepHadConnection = false;
            _results = savedResults ?? oldResults;
            if (!_engine.IsRunning && previous is not null)
                await RestoreConnectionAsync(previousRelease, previous);
        }

        async Task MeasureAsync(IEnumerable<Profile> profiles)
        {
            foreach (var profile in profiles)
            {
                token.ThrowIfCancellationRequested();
                SetStatus($"Проверка {_tested + 1} из {_sweepTotal} · {profile.Name}");
                try
                {
                    await _engine.StartAsync(release, profile, _prefs.Filters, _windows.Overlay, token);
                    var result = await Benchmarks.RunAsync(profile, token);
                    if (!_engine.IsRunning)
                        throw new InvalidOperationException(_engine.ExitError() ?? "Winws завершился во время проверки.");
                    newResults[profile.Id] = result;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
                {
                    _windows.Log($"{profile.Id}: {ex}");
                    newResults[profile.Id] = new(profile.Id, [], UserError(ex));
                }
                finally
                {
                    await _engine.StopAsync();
                }
                _tested++;
            }
        }
    }

    private async Task RestoreConnectionAsync(Release? release, Profile? profile)
    {
        if (_stopRequested || _exiting)
            return;

        try
        {
            await _engine.StopAsync();
            if (release is not null && profile is not null)
                await StartAsync(release, profile, _prefs.Filters, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _windows.Log($"Восстановление подключения: {ex}");
        }
    }

    private async Task TogglePowerAsync()
    {
        if (_exiting)
            return;
        if (_engine.Active is not null || _sweepHadConnection)
        {
            _autoPaused = true;
            _autoConnectPending = false;
            _stopRequested = true;
            var current = _activity;
            if (current is not null)
            {
                current.Cancellation.Cancel();
                await current.Completion;
            }
            await RunAsync(async _ =>
            {
                await _engine.StopAsync();
                SetStatus("Подключение выключено.");
            });
            return;
        }
        if (_activity is not null || _release is null)
            return;

        var selection = BestProfile() ?? _release.Profiles[0];
        await RunAsync(token => ConnectAsync(selection, automatic: true, token));
    }

    private async Task ConnectAsync(Profile profile, bool automatic, CancellationToken token)
    {
        var release = _release ?? throw new InvalidOperationException("Сначала установите Flowseal.");
        _windows.EnsureAvailable(_engine.Id);
        var previous = _engine.Active;
        var previousRelease = _engine.ActiveRelease;
        try
        {
            SetStatus($"Запуск {profile.Name}…");
            await _engine.StopAsync();
            await StartAsync(release, profile, _prefs.Filters, token);
            _followBest = automatic;
            _autoPaused = false;
            _autoConnectPending = false;
            SetStatus($"Подключено · {profile.Name}.");
        }
        catch
        {
            await RestoreConnectionAsync(previousRelease, previous);
            throw;
        }
    }

    private async Task StartAsync(Release release, Profile profile, Filters filters, CancellationToken token)
    {
        _windows.PrepareLists(release, filters);
        await _engine.StartAsync(release, profile, filters, _windows.Overlay, token);
    }

    private void RestoreRanking()
    {
        if (_release is null)
            return;
        var snapshot = LocalFiles.ReadJson<Ranking>(_windows.RankingPath);
        if (snapshot is null || !snapshot.Matches(ReleaseKey(_release), FilterKey(), Benchmarks.NetworkKey()) ||
            snapshot.Results is null)
            return;

        _ranking = snapshot;
        _results = snapshot.Results.Where(result => result is not null && result.ProfileId is not null && result.Ports is not null)
            .DistinctBy(result => result.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(result => result.ProfileId, StringComparer.OrdinalIgnoreCase);
    }

    private void ResetRanking()
    {
        _ranking = null;
        _results = new Dictionary<string, ProfileResult>(StringComparer.OrdinalIgnoreCase);
        _lastAttempt = DateTimeOffset.MinValue;
        try { File.Delete(_windows.RankingPath); }
        catch (IOException ex) { _windows.Log(ex.Message); }
        catch (UnauthorizedAccessException ex) { _windows.Log(ex.Message); }
    }

    private string FilterKey() => LocalFiles.Fingerprint(_prefs.Filters, _windows.IpSetKey);
    private static string ReleaseKey(Release release) => Path.GetFileName(release.Root);
    private Profile? BestProfile() => _release is null ? null :
        Benchmarks.Sort(_release.Profiles, _results)
            .FirstOrDefault(profile => FromLatestTest(profile) && _results.GetValueOrDefault(profile.Id)?.Passed > 0);

    private bool FromLatestTest(Profile profile) => _sweep || _ranking?.WasRetested(profile.Id) != false;

    private void SetStatus(string status)
    {
        _status = status;
        Refresh();
    }

    private void Refresh()
    {
        if (!_painted || _disposed)
            return;

        var best = BestProfile();
        var busy = _activity is not null;
        var active = _sweep ? null : _engine.Active;
        var canStop = (!_sweep && active is not null) || _sweepHadConnection;
        var ready = _release is not null;
        SidebarVersion.Text = _release?.Tag ?? "—";
        CheckTime.Text = _ranking switch
        {
            { RetestedProfileIds: { } ids } ranking => $"Быстрый тест · {ids.Length} из {_release?.Profiles.Count} · {ranking.TestedAt.LocalDateTime:HH:mm}",
            { } ranking => $"Полный тест · {ranking.TestedAt.LocalDateTime:dd.MM, HH:mm}",
            _ => "Результаты проверки портов"
        };
        StatusText.Text = _status;
        ConnectionLabel.Text = _sweep ? "Проверка стратегий" : active is not null ? "Подключено" : "Отключено";
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            _sweep ? "AccentBrush" : active is not null ? "GoodBrush" : "QuietBrush");

        var presented = active ?? best ?? _release?.Profiles.FirstOrDefault();
        var reading = presented is null ? null : _results.GetValueOrDefault(presented.Id);
        HeroTitle.Text = presented?.Name ?? "Установка Flowseal";
        HeroDetail.Text = reading is { Passed: > 0, Median: double median }
            ? $"{reading.Passed} из {Benchmarks.PortCount} портов · {median:0} мс"
            : ready ? (_sweep ? "Проверяем доступность Discord и YouTube." : "Проверьте стратегии, чтобы выбрать лучшую.")
            : "Стратегии появятся после загрузки.";
        PowerButton.Content = canStop ? "Выключить" : "Включить";
        PowerButton.IsEnabled = canStop || (ready && !busy);
        TestButton.IsEnabled = ready && !busy;
        FullTestButton.IsEnabled = TestButton.IsEnabled;
        CancelButton.Visibility = _sweep ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _activity?.Cancellation.IsCancellationRequested == false;
        _trayPower.Text = (string)PowerButton.Content;
        _trayPower.Enabled = PowerButton.IsEnabled;
        _trayTest.Enabled = TestButton.IsEnabled;
        _tray.Text = active is not null ? $"{WindowsTools.ProductName} · {active.Name[..Math.Min(60, active.Name.Length)]}" : WindowsTools.ProductName;
        OperationalSettings.IsEnabled = !busy;
        FlowsealUpdateText.Visibility = _availableFlowseal is null || _release is null ? Visibility.Collapsed : Visibility.Visible;
        FlowsealUpdateText.Text = _availableFlowseal is { } tag ? $"Доступен Flowseal {tag}." : "";
        EmptyProfiles.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        SweepProgress.Visibility = _sweep ? Visibility.Visible : Visibility.Collapsed;
        SweepProgress.Maximum = Math.Max(1, _sweepTotal);
        SweepProgress.Value = _tested;
        ProfileRows.ItemsSource = ready
            ? Benchmarks.Sort(_release!.Profiles, _results).OrderByDescending(FromLatestTest).Select((profile, index) =>
                new Row(profile, index + 1, _results.GetValueOrDefault(profile.Id), profile.Id == best?.Id,
                    profile.Id == active?.Id, !busy && profile.Id != active?.Id, !FromLatestTest(profile))).ToArray()
            : null;
    }

    private sealed record Row(Profile Profile, int Position, ProfileResult? Result,
        bool IsBest, bool IsActive, bool CanConnect, bool FromPreviousTest)
    {
        public string Rank => !FromPreviousTest && Result is { Measured: true } ? Position.ToString() : "—";
        public string Name => Profile.Name;
        public string Label => IsActive && IsBest ? "Подключено · Лучшая" : IsActive ? "Подключено" :
            IsBest ? "Лучшая" : FromPreviousTest && Result is not null ? "Прошлый тест" : "";
        public string Ports => Result?.Error is not null ? "Ошибка" :
            Result is { Measured: true } ? $"{Result.Passed}/{Result.Ports.Length}" : "—";
        public string Latency => Result?.Median is { } ms ? $"{ms:0} мс" : "—";
        public string Detail => Result?.Error ?? (Result is null ? "Ожидает проверки." :
            string.Join("\n", Result.Ports.Select(port => port.Milliseconds is { } ms
                ? $"{port.Endpoint}  ·  {ms:0} мс" : $"{port.Endpoint}  ·  {port.Status}")));
        public string Action => $"Подключить {Name}";
    }

    private sealed class Activity
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    private void Save(Preferences prefs)
    {
        LocalFiles.WriteJson(_windows.SettingsPath, prefs);
        _prefs = prefs;
    }

    private void SyncStartupFields()
    {
        AutoUpdateChoice.IsChecked = _prefs.AutoUpdate;
        AutoTestChoice.IsChecked = _prefs.AutoTest;
        TrayChoice.IsChecked = _prefs.StartInTray;
        ConnectChoice.IsChecked = _prefs.AutoConnect;
    }

    private async void StartupChanged(object sender, RoutedEventArgs e)
    {
        var startInTray = TrayChoice.IsChecked == true;
        var autoConnect = ConnectChoice.IsChecked == true;
        if (ReferenceEquals(sender, ConnectChoice) && autoConnect)
            startInTray = true;
        if (!startInTray)
            autoConnect = false;

        await RunAsync(async token =>
        {
            var old = _prefs;
            var next = old with { StartInTray = startInTray, AutoConnect = autoConnect,
                AutoUpdate = AutoUpdateChoice.IsChecked == true, AutoTest = AutoTestChoice.IsChecked == true };
            if (next.StartInTray != old.StartInTray)
                await _windows.ConfigureTaskAsync(next.StartInTray, token);
            try
            {
                Save(next with { Light = _prefs.Light, Accent = _prefs.Accent });
            }
            catch
            {
                if (next.StartInTray != old.StartInTray)
                    await _windows.ConfigureTaskAsync(old.StartInTray, CancellationToken.None);
                throw;
            }
            if (!_prefs.AutoConnect)
                _autoConnectPending = false;
            if (_prefs.AutoTest && !old.AutoTest)
                _autoPaused = false;
            SetStatus("Параметры запуска сохранены.");
        });
        SyncStartupFields();
    }

    private void SyncFilterFields()
    {
        GameChoice.SelectedIndex = (int)_prefs.Filters.Game;
        IpSetChoice.SelectedIndex = (int)_prefs.Filters.IpSet;
        TcpField.Text = _prefs.Filters.TcpPorts;
        UdpField.Text = _prefs.Filters.UdpPorts;
        ExtraDomainsField.Text = _prefs.Filters.ExtraDomains;
        ExcludedDomainsField.Text = _prefs.Filters.ExcludedDomains;
        ExcludedNetworksField.Text = _prefs.Filters.ExcludedNetworks;
        SyncFakeFields();
    }

    private sealed record FakeChoice(string Name, string Label);

    private void SyncFakeFields()
    {
        var options = new List<FakeChoice> { new("", "Из релиза Flowseal") };
        if (_release is not null)
            options.AddRange(_release.Fakes.Select(file => new FakeChoice(file, Path.GetFileNameWithoutExtension(file))));

        DiscordFakeChoice.ItemsSource = options;
        GameFakeChoice.ItemsSource = options;
        DiscordFakeChoice.SelectedItem = Choose(_prefs.Filters.DiscordFake);
        GameFakeChoice.SelectedItem = Choose(_prefs.Filters.GameFake);

        FakeChoice Choose(string name) => options.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? options[0];
    }

    private async void ApplyFiltersClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var options = new Filters
        {
            Game = (GameMode)GameChoice.SelectedIndex,
            IpSet = (IpSetMode)IpSetChoice.SelectedIndex,
            TcpPorts = Filters.NormalizePorts(TcpField.Text) ?? throw new InvalidDataException("Проверьте диапазоны TCP. Пример: 1024-1934,1936-65535."),
            UdpPorts = Filters.NormalizePorts(UdpField.Text) ?? throw new InvalidDataException("Проверьте диапазоны UDP. Допустимые порты от 1 до 65535."),
            DiscordFake = (DiscordFakeChoice.SelectedItem as FakeChoice)?.Name ?? "",
            GameFake = (GameFakeChoice.SelectedItem as FakeChoice)?.Name ?? "",
            ExtraDomains = ExtraDomainsField.Text,
            ExcludedDomains = ExcludedDomainsField.Text,
            ExcludedNetworks = ExcludedNetworksField.Text
        };
        await ChangeFiltersAsync(options, null, token);
        SyncFilterFields();
        SetStatus("Фильтры применены.");
    });

    private async Task ChangeFiltersAsync(Filters next, string? newIpSet, CancellationToken token)
    {
        Filters.ParseList(next.ExtraDomains, networks: false);
        Filters.ParseList(next.ExcludedDomains, networks: false);
        Filters.ParseList(next.ExcludedNetworks, networks: true);
        if (next == _prefs.Filters && newIpSet is null)
            return;

        var release = _engine.ActiveRelease ?? _release;
        var profile = _engine.Active;
        byte[]? previousList = newIpSet is not null && File.Exists(_windows.DownloadedIpSet)
            ? File.ReadAllBytes(_windows.DownloadedIpSet) : null;
        try
        {
            await _engine.StopAsync();
            token.ThrowIfCancellationRequested();
            if (newIpSet is not null)
                LocalFiles.WriteText(_windows.DownloadedIpSet, newIpSet);

            if (release is not null)
            {
                _windows.PrepareLists(release, next);
                if (profile is not null)
                    await _engine.StartAsync(release, profile, next, _windows.Overlay, token);
            }
            Save(_prefs with { Filters = next });
        }
        catch
        {
            if (newIpSet is not null)
            {
                if (previousList is null)
                    File.Delete(_windows.DownloadedIpSet);
                else
                    LocalFiles.Write(_windows.DownloadedIpSet, previousList);
            }
            await RestoreConnectionAsync(release, profile);
            throw;
        }
        ResetRanking();
    }

    private async void IpSetClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        SetStatus("Скачивание списка IPSet…");
        var list = Filters.ParseList(await _source.IpSetAsync(token), networks: true);
        if (list.Length == 0)
            throw new InvalidDataException("Flowseal вернул пустой список IPSet.");
        await ChangeFiltersAsync(_prefs.Filters with { IpSet = IpSetMode.Loaded }, list, token);
        IpSetChoice.SelectedIndex = (int)_prefs.Filters.IpSet;
        SetStatus("Список IPSet обновлён и включён.");
    });

    private async void HostsClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        SetStatus("Скачивание списка hosts…");
        var result = await _windows.EditHostsAsync(await _source.HostsAsync(token), token);
        ResetRanking();
        SetStatus($"Hosts обновлён. Записей Flowseal: {result.Added}. Совпадений с вашими записями: {result.Skipped}.");
    });

    private async void RestoreHostsClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await _windows.EditHostsAsync(null, token);
        ResetRanking();
        SetStatus("Блок Zapret_app_Pff удалён из hosts.");
    });

    private async void DiagnoseClick(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        SetStatus("Проверка системы…");
        var lastError = _results.Values.FirstOrDefault(result => result.Error is not null)?.Error;
        DiagnosticText.Text = await Task.Run(() => _windows.Diagnose(_release, _engine.Id));
        if (lastError is not null)
            DiagnosticText.Text += $"{Environment.NewLine}Последняя ошибка запуска: {lastError}";
        RemoveLegacyButton.Visibility = _windows.IsOwnLegacyFlowseal ? Visibility.Visible : Visibility.Collapsed;
        SetStatus("Диагностика завершена.");
    });

    private async void RemoveLegacyClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Остановить и удалить прежнюю службу zapret, установленную Flowseal?",
                WindowsTools.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunAsync(async token =>
        {
            await _windows.RemoveLegacyAsync(token);
            RemoveLegacyButton.Visibility = Visibility.Collapsed;
            DiagnosticText.Text = await Task.Run(() => _windows.Diagnose(_release, _engine.Id), token);
            SetStatus("Прежняя служба Flowseal удалена.");
        });
    }

    private async void TimestampsClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await _windows.EnableTimestampsAsync(token);
        SetStatus("TCP timestamps включены в Windows.");
    });

    private void ThemeChanged(object sender, RoutedEventArgs e)
    {
        _accentSaveTimer.Stop();
        var accent = _pendingAccent ?? _prefs.Accent;
        _pendingAccent = null;
        SaveAppearance(_prefs with { Light = LightThemeToggle.IsChecked == true, Accent = accent });
    }

    private void AccentBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_painted)
            AccentPicker.Brightness = e.NewValue;
    }

    private void AccentChanged(object? sender, EventArgs e)
    {
        var color = AccentPicker.SelectedColor;
        var accent = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (string.Equals(accent, _pendingAccent ?? _prefs.Accent, StringComparison.OrdinalIgnoreCase))
            return;

        Theme.ApplyAccent(_prefs with { Accent = accent });
        _pendingAccent = accent;
        _accentSaveTimer.Stop();
        _accentSaveTimer.Start();
    }

    private void CommitAccent()
    {
        _accentSaveTimer.Stop();
        if (_pendingAccent is not { } accent)
            return;

        _pendingAccent = null;
        if (!string.Equals(accent, _prefs.Accent, StringComparison.OrdinalIgnoreCase))
            SaveAppearance(_prefs with { Accent = accent });
    }

    private void SaveAppearance(Preferences prefs)
    {
        var themeChanged = _prefs.Light != prefs.Light;
        try
        {
            Save(prefs);
        }
        catch (Exception ex)
        {
            Theme.Apply(_prefs);
            LightThemeToggle.IsChecked = _prefs.Light;
            LoadAccentPicker();
            _windows.Log(ex.ToString());
            SetStatus(UserError(ex));
            return;
        }

        if (themeChanged)
            Theme.Apply(_prefs);
    }

    private void LoadAccentPicker()
    {
        AccentPicker.SetColor((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_prefs.Accent));
        AccentBrightness.Value = AccentPicker.Brightness;
    }

    private void NavigateClick(object sender, RoutedEventArgs e)
    {
        if (!_painted)
            return;
        var settings = SettingsNav.IsChecked == true;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        DashboardPage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ConnectRowClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is Profile profile)
            await RunAsync(token => ConnectAsync(profile, automatic: false, token));
    }

    private async void PowerClick(object sender, RoutedEventArgs e) => await TogglePowerAsync();
    private async void TestClick(object sender, RoutedEventArgs e) => await ManualSweepAsync();
    private async void FullTestClick(object sender, RoutedEventArgs e) => await ManualSweepAsync(full: true);
    private async void UpdateClick(object sender, RoutedEventArgs e) =>
        await RunAsync(token => UpdateAsync(token, installRequested: true));
    private void CancelClick(object sender, RoutedEventArgs e) => _activity?.Cancellation.Cancel();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Hide();

    public void ShowFromTray()
    {
        if (_exiting)
            return;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    internal async Task ExitAsync()
    {
        if (_exiting)
            return;
        CommitAccent();
        _exiting = true;
        _stopRequested = true;
        var running = _activity;
        if (running is not null)
        {
            running.Cancellation.Cancel();
            await running.Completion;
        }
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _exiting = true;
        _timer.Stop();
        _accentSaveTimer.Stop();
        _updateNotice?.Close();
        _activity?.Cancellation.Cancel();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _trayIcon.Dispose();
        _engine.Dispose();
        _source.Dispose();
    }
}
