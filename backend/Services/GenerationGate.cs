namespace Harness.Services;

// One model request at a time, and people go first. Scheduled work waits until nobody has used the model
// for a quiet spell, and a person who starts chatting interrupts a running background request; the
// background run then retries that step when the model is free again.
public sealed class GenerationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Lock _lock = new();
    private int _foregroundWaiting;
    private DateTime _lastForeground = DateTime.MinValue;
    private CancellationTokenSource? _background;

    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromSeconds(60);

    public Task<bool> TryEnterAsync(CancellationToken ct) => _semaphore.WaitAsync(0, ct);

    // A person's request.
    public async Task EnterAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _foregroundWaiting++;
            _background?.Cancel();
        }

        try
        {
            await _semaphore.WaitAsync(ct);
        }
        finally
        {
            lock (_lock)
            {
                _foregroundWaiting--;
                _lastForeground = DateTime.UtcNow;
            }
        }
    }

    // Scheduled work. The returned token is cancelled when a person needs the model; the caller must then
    // release the gate and try again.
    public async Task<CancellationToken> EnterBackgroundAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Quiet() && await _semaphore.WaitAsync(0, ct))
            {
                lock (_lock)
                {
                    if (_foregroundWaiting == 0)
                    {
                        _background = new CancellationTokenSource();
                        return _background.Token;
                    }
                }

                _semaphore.Release();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private bool Quiet()
    {
        lock (_lock)
        {
            return _foregroundWaiting == 0 && DateTime.UtcNow - _lastForeground >= QuietPeriod;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_background is null)
                _lastForeground = DateTime.UtcNow;
            _background?.Dispose();
            _background = null;
        }

        _semaphore.Release();
    }

    public void Dispose()
    {
        _background?.Dispose();
        _semaphore.Dispose();
    }
}
