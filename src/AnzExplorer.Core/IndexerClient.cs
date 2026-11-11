using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AnzExplorer.Contracts;

namespace AnzExplorer.Core;

public sealed class IndexerClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly int _connectTimeoutMilliseconds;
    private readonly TimeSpan _retryDelay;
    private readonly object _workerGate = new();
    private Process? _worker;
    private string? _workerPath;
    private long _unavailableUntil;

    public IndexerClient(string? pipeName = null, TimeSpan? connectTimeout = null, TimeSpan? retryDelay = null)
    {
        _pipeName = pipeName ?? IndexProtocol.PipeName;
        _connectTimeoutMilliseconds = (int)(connectTimeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// True while the worker is known to be unreachable. Callers should use an in-process
    /// scan instead of paying the connect timeout for every folder.
    /// </summary>
    public bool IsTemporarilyUnavailable =>
        Environment.TickCount64 < Interlocked.Read(ref _unavailableUntil);

    public void EnsureWorkerStarted(string? executablePath = null)
    {
        lock (_workerGate)
        {
            StartWorkerIfNeeded(executablePath);
        }
    }

    private void StartWorkerIfNeeded(string? executablePath)
    {
        if (_worker is { HasExited: false })
        {
            return;
        }

        _worker?.Dispose();
        _worker = null;
        var path = executablePath ?? _workerPath ?? Path.Combine(AppContext.BaseDirectory, "AnzExplorer.Indexer.exe");
        _workerPath = path;
        if (!File.Exists(path))
        {
            return;
        }

        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        _worker = Process.Start(startInfo);
    }

    public async Task<FolderSizeResult> GetFolderSizeAsync(
        string path,
        bool highPriority = true,
        CancellationToken cancellationToken = default)
    {
        if (IsTemporarilyUnavailable)
        {
            throw new IOException("The index worker is temporarily unavailable.");
        }

        var request = new GetFolderSizeRequest(Guid.NewGuid(), PathCanonicalizer.Canonicalize(path), highPriority);
        IndexResponse response;
        try
        {
            response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Without this, every queued folder would wait the full connect timeout before
            // falling back. Back off, and restart the worker if it has exited.
            Interlocked.Exchange(ref _unavailableUntil, Environment.TickCount64 + (long)_retryDelay.TotalMilliseconds);
            if (_workerPath is not null)
            {
                try { EnsureWorkerStarted(); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The in-process fallback keeps sizes working without the worker.
                }
            }
            throw;
        }
        if (!response.Success || response.Result is null)
        {
            throw new IOException(response.Error ?? "The index worker did not return a folder size.");
        }

        return response.Result;
    }

    public async Task<IndexResponse> SendAsync(IndexRequest request, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(_connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        await PipeJsonProtocol.WriteAsync<IndexRequest>(pipe, request, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return await PipeJsonProtocol.ReadAsync<IndexResponse>(pipe, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not { HasExited: false })
        {
            _worker?.Dispose();
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await SendAsync(new ShutdownRequest(Guid.NewGuid()), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException)
        {
            // The worker is disposable; the OS will reclaim it after the UI exits.
        }
        finally
        {
            try
            {
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _worker.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!_worker.HasExited)
                {
                    _worker.Kill(entireProcessTree: true);
                    await _worker.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            _worker.Dispose();
            _worker = null;
        }
    }
}
