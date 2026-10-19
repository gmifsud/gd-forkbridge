using System.Diagnostics;
using System.Text;

namespace ForkBridge.Core.Infrastructure;

/// <summary>Specification of an external process. Arguments are always passed as an array
/// (ProcessStartInfo.ArgumentList) - never concatenated into a shell string.</summary>
public sealed record ProcessSpec
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    /// <summary>Environment overrides. A null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public long MaxOutputBytes { get; init; } = 64L * 1024 * 1024;
    public byte[]? StandardInput { get; init; }
}

public sealed record ProcessResult(
    int ExitCode,
    byte[] StdOutBytes,
    string StdErr,
    bool TimedOut,
    bool Cancelled,
    bool OutputTruncated,
    TimeSpan Duration)
{
    public string StdOut => Encoding.UTF8.GetString(StdOutBytes);
    public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StandardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment)
        {
            if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        }

        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex)
        {
            return new ProcessResult(-1, [], $"Failed to start '{spec.FileName}': {ex.Message}", false, false, false, sw.Elapsed);
        }

        var stdout = new BoundedBuffer(spec.MaxOutputBytes);
        var stderr = new BoundedBuffer(Math.Min(spec.MaxOutputBytes, 4 * 1024 * 1024));
        var outTask = stdout.CopyFromAsync(proc.StandardOutput.BaseStream);
        var errTask = stderr.CopyFromAsync(proc.StandardError.BaseStream);
        if (spec.StandardInput is not null)
        {
            try
            {
                await proc.StandardInput.BaseStream.WriteAsync(spec.StandardInput, ct);
                proc.StandardInput.Close();
            }
            catch (IOException) { /* process exited early */ }
        }

        using var timeoutCts = new CancellationTokenSource(spec.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        bool timedOut = false, cancelled = false;
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
        try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }

        int code = proc.HasExited ? proc.ExitCode : -1;
        return new ProcessResult(code, stdout.ToArray(), Encoding.UTF8.GetString(stderr.ToArray()),
            timedOut, cancelled, stdout.Truncated || stderr.Truncated, sw.Elapsed);
    }

    private sealed class BoundedBuffer(long limit)
    {
        private readonly MemoryStream _ms = new();
        public bool Truncated { get; private set; }
        public async Task CopyFromAsync(Stream s)
        {
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf)) > 0)
            {
                long room = limit - _ms.Length;
                if (room > 0) _ms.Write(buf, 0, (int)Math.Min(room, n));
                if (n > room) Truncated = true; // keep draining so the child never blocks
            }
        }
        public byte[] ToArray() => _ms.ToArray();
    }
}
