using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AnzExplorer.Contracts;

namespace AnzExplorer.Core;

public sealed class IndexerClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private Process? _worker;

    public void EnsureWorkerStarted(string? executablePath = null)
    {
        if (_worker is { HasExited: false })
        {
            return;
        }

        var path = executablePath ?? Path.Combine(AppContext.BaseDirectory, "AnzExplorer.Indexer.exe");
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
        var request = new GetFolderSizeRequest(Guid.NewGuid(), PathCanonicalizer.Canonicalize(path), highPriority);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
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
            IndexProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2_000, cancellationToken).ConfigureAwait(false);
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
