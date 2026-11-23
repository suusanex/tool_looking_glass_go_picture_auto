using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LookGo.Core.Models;

namespace LookGo.Core.Backend;

public sealed class PythonBridgeClient : IBackendClient
{
    private static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly string _workingDirectory;
    private readonly string _executable;
    private readonly IReadOnlyList<string> _baseArguments;
    private readonly IReadOnlyDictionary<string, string>? _extraEnvironment;
    private readonly TimeSpan _readyTimeout;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    private Process? _process;
    private Task? _readerTask;
    private Task? _stderrTask;
    private readonly StringBuilder _stderrTail = new();
    private bool _disposed;

    public PythonBridgeClient(
        string workingDirectory,
        string executable = "uv",
        IReadOnlyList<string>? baseArguments = null,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        TimeSpan? readyTimeout = null)
    {
        _workingDirectory = workingDirectory;
        _executable = executable;
        _baseArguments = baseArguments ?? ["run", "lookgo.py", "_bridge"];
        _extraEnvironment = extraEnvironment;
        _readyTimeout = readyTimeout ?? DefaultReadyTimeout;
    }

    public Task<BackendResult> GetStatusAsync(CancellationToken ct = default)
        => SendAsync(new { command = "status" }, ct);

    public Task<BackendResult> ShowAsync(
        string listPath,
        double intervalSeconds,
        string operationId,
        string? expectedSha256 = null,
        CancellationToken ct = default)
    {
        object payload = expectedSha256 is null
            ? new
            {
                command = "show",
                list_path = listPath,
                interval = intervalSeconds,
                operation_id = operationId,
            }
            : new
            {
                command = "show",
                list_path = listPath,
                interval = intervalSeconds,
                operation_id = operationId,
                expected_sha256 = expectedSha256,
            };
        return SendAsync(payload, ct);
    }

    public Task<BackendResult> CancelShowAsync(string operationId, CancellationToken ct = default)
        => SendAsync(new { command = "cancel", operation_id = operationId }, ct);

    public Task<BackendResult> NextAsync(CancellationToken ct = default)
        => SendAsync(new { command = "next" }, ct);

    public Task<BackendResult> PrevAsync(CancellationToken ct = default)
        => SendAsync(new { command = "prev" }, ct);

    public Task<BackendResult> PauseAsync(CancellationToken ct = default)
        => SendAsync(new { command = "pause" }, ct);

    public Task<BackendResult> ResumeAsync(CancellationToken ct = default)
        => SendAsync(new { command = "resume" }, ct);

    public Task<BackendResult> JumpAsync(int index, CancellationToken ct = default)
        => SendAsync(new { command = "jump", index }, ct);

    public Task<BackendResult> StopAsync(CancellationToken ct = default)
        => SendAsync(new { command = "stop" }, ct);

    private async Task<BackendResult> SendAsync(object payload, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var process = await EnsureStartedAsync(ct).ConfigureAwait(false);
            var jsonLine = SerializeWithId(id, payload);

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_process != process || process.HasExited)
                    throw new IOException("Python bridge process is not running.");
                await process.StandardInput.WriteLineAsync(jsonLine.AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            var response = await tcs.Task.ConfigureAwait(false);
            var ok = response.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            var error = response.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String
                ? errProp.GetString()
                : null;
            PlaybackState? state = null;
            if (response.TryGetProperty("state", out var stateProp) && stateProp.ValueKind == JsonValueKind.Object)
                state = PlaybackState.FromJson(stateProp);

            return new BackendResult(ok, state, ok ? null : (error ?? "Unknown backend error"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BackendResult(false, null, ex.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static string SerializeWithId(string id, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload)!.AsObject();
        node["id"] = id;
        return node.ToJsonString();
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken ct)
    {
        var existing = _process;
        if (existing is not null && !existing.HasExited)
            return existing;

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is not null && !_process.HasExited)
                return _process;

            CleanupProcessHandle();

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var psi = new ProcessStartInfo
            {
                FileName = _executable,
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = utf8,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8,
            };
            foreach (var arg in _baseArguments)
                psi.ArgumentList.Add(arg);
            psi.Environment["PYTHONUTF8"] = "1";
            if (_extraEnvironment is not null)
            {
                foreach (var (k, v) in _extraEnvironment)
                    psi.Environment[k] = v;
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Failed to start Python bridge process.");
            }
            catch
            {
                process.Dispose();
                throw;
            }

            try
            {
                _stderrTail.Clear();
                _stderrTask = Task.Run(() => DrainStderrAsync(process.StandardError));

                using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readyCts.CancelAfter(_readyTimeout);

                string? readyLine;
                try
                {
                    readyLine = await process.StandardOutput.ReadLineAsync(readyCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    var detail = GetStderrSummary();
                    throw new TimeoutException($"Timed out waiting for Python bridge ready event. {detail}".Trim(), ex);
                }

                if (readyLine is null)
                {
                    var detail = GetStderrSummary();
                    throw new IOException($"Python bridge exited before ready event. {detail}".Trim());
                }

                using (var doc = JsonDocument.Parse(readyLine))
                {
                    if (!doc.RootElement.TryGetProperty("event", out var ev) || ev.GetString() != "ready")
                        throw new InvalidDataException($"Unexpected bridge handshake: {readyLine}");
                }

                _process = process;
                _readerTask = Task.Run(() => ReadLoopAsync(process));
                return process;
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                if (_stderrTask is { } stderrTask)
                {
                    try { await stderrTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
                    _stderrTask = null;
                }
                process.Dispose();
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task DrainStderrAsync(StreamReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrTail)
                {
                    if (_stderrTail.Length > 4000)
                        _stderrTail.Remove(0, 2000);
                    _stderrTail.AppendLine(line);
                }
            }
        }
        catch
        {
        }
    }

    private string GetStderrSummary()
    {
        lock (_stderrTail)
            return _stderrTail.ToString().Trim();
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement.Clone();
                    if (root.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.String &&
                        idEl.GetString() is { } id &&
                        _pending.TryRemove(id, out var tcs))
                    {
                        tcs.TrySetResult(root);
                    }
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            FailAllPending(new IOException($"Python bridge stream failed: {ex.Message}", ex));
            return;
        }

        var detail = GetStderrSummary();
        FailAllPending(new IOException($"Python bridge closed unexpectedly. {detail}".Trim()));
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(ex);
        }
    }

    private void CleanupProcessHandle()
    {
        if (_process is { } old)
        {
            _process = null;
            try { old.Dispose(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    var id = Guid.NewGuid().ToString("N");
                    var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending[id] = tcs;

                    await _writeLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var shutdownLine = SerializeWithId(id, new { command = "shutdown" });
                        await process.StandardInput.WriteLineAsync(shutdownLine).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync().ConfigureAwait(false);
                        process.StandardInput.Close();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        _writeLock.Release();
                    }

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch
            {
            }
        }

        FailAllPending(new ObjectDisposedException(nameof(PythonBridgeClient)));
        CleanupProcessHandle();

        if (_readerTask is not null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        }
        if (_stderrTask is not null)
        {
            try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        }

        _lifecycleLock.Dispose();
        _writeLock.Dispose();
    }
}
