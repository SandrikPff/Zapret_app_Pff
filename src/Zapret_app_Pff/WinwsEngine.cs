using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZapretAppPff;

public sealed class WinwsEngine : IDisposable
{
    private readonly Job _job = new();
    private Process? _process;
    private Queue<string>? _output;

    public event Action<Process>? ProcessExited;
    public Profile? Active { get; private set; }
    public Release? ActiveRelease { get; private set; }
    public int? Id => _process?.Id;
    public bool IsRunning => _process is { HasExited: false };

    public string? ReapExited()
    {
        return _process is { HasExited: true } process ? SetExited(process) : null;
    }

    public string? SetExited(Process process)
    {
        if (!ReferenceEquals(_process, process))
            return null;

        var error = ExitError() ?? "Winws завершился.";
        ClearProcess();
        process.Dispose();
        return error;
    }

    public string? ExitError()
    {
        if (_process is not { HasExited: true } process)
            return null;

        var message = $"Winws завершился с кодом {process.ExitCode}.";
        if (_output is not { } output)
            return message;

        lock (output)
        {
            var errors = output.Where(line =>
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("cannot", StringComparison.OrdinalIgnoreCase)).TakeLast(4).ToArray();
            var detail = errors.Length > 0 ? errors : output.TakeLast(3);
            return $"{message} {string.Join(" · ", detail)}".Trim();
        }
    }

    public async Task StartAsync(Release release, Profile profile, Filters filters, string overlay, CancellationToken token)
    {
        if (_process is not null)
            throw new InvalidOperationException("Сначала остановите текущую стратегию.");
        token.ThrowIfCancellationRequested();

        var info = WindowsTools.Hidden(Path.Combine(release.Bin, "winws.exe"));
        info.WorkingDirectory = release.Bin;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        foreach (var argument in ProfileCatalog.Resolve(release, profile, filters, overlay))
            info.ArgumentList.Add(argument);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var output = new Queue<string>();
        void Capture(object sender, DataReceivedEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.Data))
                return;
            lock (output)
            {
                if (output.Count == 24)
                    output.Dequeue();
                output.Enqueue(args.Data[..Math.Min(240, args.Data.Length)]);
            }
        }
        process.OutputDataReceived += Capture;
        process.ErrorDataReceived += Capture;
        process.Exited += (_, _) => ProcessExited?.Invoke(process);

        try
        {
            process.Start();
            _process = process;
            _output = output;
            try { _job.Add(process); }
            catch (Win32Exception) when (process.HasExited)
            {
                // Capture the child's startup error even if it terminated before joining the job.
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
            ready.CancelAfter(TimeSpan.FromMilliseconds(700));
            try
            {
                await process.WaitForExitAsync(ready.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // A working winws stays alive until disconnected.
            }
            token.ThrowIfCancellationRequested();
            if (ExitError() is { } error)
                throw new InvalidOperationException(error);

            Active = profile;
            ActiveRelease = release;
        }
        catch
        {
            if (ReferenceEquals(_process, process))
                await StopAsync();
            else
                process.Dispose();
            throw;
        }
    }

    public async Task StopAsync()
    {
        var process = _process;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (Win32Exception) when (process.HasExited) { }
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("Winws не остановился. Повторите отключение.", ex);
        }
        finally
        {
            if (ReferenceEquals(process, _process) && process.HasExited)
            {
                ClearProcess();
                process.Dispose();
            }
        }
    }

    private void ClearProcess()
    {
        _process = null;
        _output = null;
        Active = null;
        ActiveRelease = null;
    }

    public void Dispose()
    {
        _job.Dispose();
        _process?.Dispose();
        ClearProcess();
    }

    private sealed class Job : IDisposable
    {
        private readonly SafeFileHandle _handle = CreateJobObject(IntPtr.Zero, null);

        public Job()
        {
            if (_handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                var error = Marshal.GetLastWin32Error();
                _handle.Dispose();
                throw new Win32Exception(error);
            }
        }

        public void Add(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось привязать winws к процессу приложения.");
        }

        public void Dispose() => _handle.Dispose();

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessTime;
            public long PerJobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet;
            public UIntPtr MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
