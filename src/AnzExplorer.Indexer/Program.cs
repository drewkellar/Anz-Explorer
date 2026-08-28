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
                GetFolderSizeRequest sizeRequest => await GetSizeAsync(sizeRequest),
                PauseIndexingRequest pauseRequest => Pause(pauseRequest),
                ResumeIndexingRequest resumeRequest => Resume(resumeRequest),
                ShutdownRequest shutdownRequest => Shutdown(shutdownRequest),
                null => new IndexResponse(Guid.Empty, false, null, "Request was empty."),
                _ => new IndexResponse(request.RequestId, false, null, "Request type is not supported.")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = new IndexResponse(request?.RequestId ?? Guid.Empty, false, null, exception.Message);
        }

        await PipeJsonProtocol.WriteAsync(pipe, response, jsonOptions, CancellationToken.None);
    }
}

async Task<IndexResponse> GetSizeAsync(GetFolderSizeRequest request)
{
    var cached = await repository.GetAsync(request.Path, shutdown.Token);
    if (cached is { State: FolderSizeState.Complete or FolderSizeState.Partial, LastUpdated: not null }
        && cached.LastUpdated >= DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30))
    {
        return new IndexResponse(request.RequestId, true, cached, null);
    }

    if (paused && !request.HighPriority)
    {
        var queued = new FolderSizeResult(request.Path, null, FolderSizeState.Queued, null, null);
        return new IndexResponse(request.RequestId, true, queued, null);
    }

    var result = await scanner.ScanAsync(request.Path, cancellationToken: shutdown.Token);
    await repository.PutAsync(result, shutdown.Token);
    return new IndexResponse(request.RequestId, true, result, null);
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
