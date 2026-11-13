namespace GameShift.Core;

// Loading a large model can take minutes. It must not occupy the UI control
// loop's semaphore: a new game still has to be detected while load is pending.
public sealed class ModelResumeCoordinator(Func<Settings, CancellationToken, Task<LoadResult>> load) : IDisposable
{
    private Task<LoadResult>? pending;
    private CancellationTokenSource? cancellation;
    private string? signature;
    private DateTimeOffset retryAt;
    private bool disposed;

    public LoadResult Poll(Settings settings, DateTimeOffset now, CancellationToken lifetime)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var currentSignature = settings.StrataUrl + "\n" + settings.ApiKeyEnvironmentVariable;
        if (pending is not null)
        {
            if (currentSignature != signature || cancellation!.IsCancellationRequested)
            {
                cancellation!.Cancel();
                if (!pending.IsCompleted) return new(false, "Waiting for the previous model-load operation to finish cancelling. Harness remains held.");
                Clear(); // Never release a hold using an old endpoint's success.
            }
            else if (!pending.IsCompleted)
                return new(false, "Loading Strata model into RAM + VRAM. Harness requests are safely waiting.");
            else
            {
                LoadResult result;
                try { result = pending.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { result = new(false, "Model loading was cancelled. Harness remains held."); }
                catch (Exception e) when (e is HttpRequestException or IOException or ArgumentException or InvalidOperationException)
                { result = new(false, "Model loading failed (" + e.GetType().Name + "). Harness remains held; retrying."); }
                Clear();
                if (!result.Ready) retryAt = now.AddSeconds(10);
                return result;
            }
        }
        if (now < retryAt) return new(false, "Model is not ready. Retrying shortly; Harness remains held.");
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        cancellation.CancelAfter(TimeSpan.FromMinutes(3));
        signature = currentSignature;
        try { pending = load(settings, cancellation.Token); }
        catch { Clear(); throw; }
        return new(false, "Preparing Strata model. Harness stays held until readiness is confirmed.");
    }

    public void Hold()
    {
        if (disposed) return;
        retryAt = default;
        cancellation?.Cancel();
        if (pending?.IsCompleted == true) Clear();
    }

    private void Clear()
    {
        // Observe a completed discarded failure so no stale exception escapes.
        if (pending?.IsFaulted == true) _ = pending.Exception;
        pending = null;
        cancellation?.Dispose();
        cancellation = null;
        signature = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var task = pending;
        var source = cancellation;
        pending = null; cancellation = null;
        source?.Cancel();
        if (task is null || task.IsCompleted) { _ = task?.Exception; source?.Dispose(); }
        else _ = task.ContinueWith(t => { _ = t.Exception; source?.Dispose(); }, TaskScheduler.Default);
    }
}
