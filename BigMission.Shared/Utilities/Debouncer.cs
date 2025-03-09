namespace BigMission.Shared.Utilities;

public class Debouncer(TimeSpan delay)
{
    private bool _isWaiting;
    private readonly Lock _lock = new();
    public bool IsDisabled { get; set; }

    public async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        if (IsDisabled) return;
        lock (_lock)
        {
            if (_isWaiting) return;
            _isWaiting = true;
        }

        await Task.Delay(delay, cancellationToken);

        lock (_lock)
        {
            _isWaiting = false;
        }

        await action();
    }
}