using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AnzExplorer.Contracts;
using AnzExplorer.Core;
using AnzExplorer.Indexer;

SQLitePCL.Batteries_V2.Init();

using var workerMutex = new Mutex(
    initiallyOwned: true,
    $"Local\\AnzExplorer.Indexer.v{IndexProtocol.Version}.{Environment.UserName}",
    out var ownsWorkerMutex);
if (!ownsWorkerMutex)
{
    return;
}

try
{
    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
}
catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
{
    // Priority reduction is an optimization, not a startup requirement.
}

var paths = new AppPaths();
paths.EnsureCreated();
var repository = new FolderSizeRepository(paths.IndexPath);
await repository.InitializeAsync(CancellationToken.None);
var scanner = new FolderSizeScanner();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var shutdown = new CancellationTokenSource();
// One cold recursive scan at a time across every client and window, to avoid seek contention.
using var scanGate = new SemaphoreSlim(1, 1);
var paused = false;

if (args is ["--scan", var diagnosticPath])
{
    var stopwatch = Stopwatch.StartNew();
    var diagnosticResult = await scanner.ScanAsync(diagnosticPath, cancellationToken: CancellationToken.None);
    stopwatch.Stop();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        diagnosticResult,
        elapsedMilliseconds = stopwatch.ElapsedMilliseconds
    }, jsonOptions));
    return;
}

// Logging starts after the one-shot --scan probe so that probe never leaves an "unclean" session.
try
{
    DiagnosticLog.Initialize(paths.LogDirectory, "indexer");
    DiagnosticLog.HookProcessExceptions();
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    // Logging is optional for the worker.
}

var parentArgumentIndex = Array.IndexOf(args, "--parent");
if (parentArgumentIndex >= 0
    && parentArgumentIndex + 1 < args.Length
    && int.TryParse(args[parentArgumentIndex + 1], out var parentProcessId))
{
    _ = MonitorParentAsync(parentProcessId);
}

while (!shutdown.IsCancellationRequested)
{
    var pipe = new NamedPipeServerStream(
        IndexProtocol.PipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    try
    {
        await pipe.WaitForConnectionAsync(shutdown.Token);
        _ = HandleAsync(pipe);
    }
    catch (OperationCanceledException)
    {
        await pipe.DisposeAsync();
        break;
    }
}

DiagnosticLog.Current?.EndSession();

async Task HandleAsync(NamedPipeServerStream pipe)
{
    await using (pipe)
    {
        IndexRequest? request = null;
        IndexResponse response;
        try
        {
            request = await PipeJsonProtocol.ReadAsync<IndexRequest>(pipe, jsonOptions, shutdown.Token);
            response = request switch
            {
                GetFolderSizeRequest sizeRequest => await GetSizeForConnectedClientAsync(pipe, sizeRequest),
                PauseIndexingRequest pauseRequest => Pause(pauseRequest),
                ResumeIndexingRequest resumeRequest => Resume(resumeRequest),
                ShutdownRequest shutdownRequest => Shutdown(shutdownRequest),
                null => new IndexResponse(Guid.Empty, false, null, "Request was empty."),
                _ => new IndexResponse(request.RequestId, false, null, "Request type is not supported.")
            };
        }
        catch (OperationCanceledException)
        {
            // Shutdown, or the client disconnected (navigated away). Nobody is waiting.
            return;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn("Indexer", $"Request {request?.GetType().Name ?? "(unread)"} failed", exception);
            response = new IndexResponse(request?.RequestId ?? Guid.Empty, false, null, exception.Message);
        }

        try
        {
            await PipeJsonProtocol.WriteAsync(pipe, response, jsonOptions, CancellationToken.None);
        }
        catch (IOException)
        {
            // The client went away between the scan finishing and the reply.
        }
    }
}

async Task<IndexResponse> GetSizeForConnectedClientAsync(NamedPipeServerStream pipe, GetFolderSizeRequest request)
{
    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
    using var stopWatching = new CancellationTokenSource();
    var watcher = PipeJsonProtocol.CancelOnDisconnectAsync(pipe, requestCancellation, stopWatching.Token);
    try
    {
        return await GetSizeAsync(request, requestCancellation.Token);
    }
    finally
    {
        stopWatching.Cancel();
        await watcher;
    }
}

async Task<IndexResponse> GetSizeAsync(GetFolderSizeRequest request, CancellationToken cancellationToken)
{
    var cached = await GetFreshCachedAsync(request.Path, cancellationToken);
    if (cached is not null) return new IndexResponse(request.RequestId, true, cached, null);

    if (paused && !request.HighPriority)
    {
        var queued = new FolderSizeResult(request.Path, null, FolderSizeState.Queued, null, null);
        return new IndexResponse(request.RequestId, true, queued, null);
    }

    await scanGate.WaitAsync(cancellationToken);
    try
    {
        // Another client may have scanned this folder while we waited.
        cached = await GetFreshCachedAsync(request.Path, cancellationToken);
        if (cached is not null) return new IndexResponse(request.RequestId, true, cached, null);

        var result = await scanner.ScanAsync(request.Path, cancellationToken: cancellationToken);
        await repository.PutAsync(result, shutdown.Token);
        return new IndexResponse(request.RequestId, true, result, null);
    }
    finally
    {
        scanGate.Release();
    }
}

async Task<FolderSizeResult?> GetFreshCachedAsync(string path, CancellationToken cancellationToken)
{
    var cached = await repository.GetAsync(path, cancellationToken);
    return cached is { State: FolderSizeState.Complete or FolderSizeState.Partial, LastUpdated: not null }
        && cached.LastUpdated >= DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)
            ? cached
            : null;
}

IndexResponse Pause(PauseIndexingRequest request)
{
    paused = true;
    return new IndexResponse(request.RequestId, true, null, null);
}

IndexResponse Resume(ResumeIndexingRequest request)
{
    paused = false;
    return new IndexResponse(request.RequestId, true, null, null);
}

IndexResponse Shutdown(ShutdownRequest request)
{
    shutdown.Cancel();
    return new IndexResponse(request.RequestId, true, null, null);
}

async Task MonitorParentAsync(int parentProcessId)
{
    try
    {
        using var parent = Process.GetProcessById(parentProcessId);
        await parent.WaitForExitAsync(shutdown.Token);
    }
    catch (ArgumentException)
    {
        // The parent exited before the monitor was established.
    }
    catch (OperationCanceledException)
    {
        return;
    }

    shutdown.Cancel();
}
