namespace BigMission.Shared.Utilities;

/// <summary>
/// Provides a mechanism to delay and suppress repeated execution of asynchronous actions within a specified time
/// interval.
/// </summary>
/// <remarks>Use this class to prevent multiple rapid invocations of an action, such as in response to frequent
/// user input or events. The first call in a burst is the one that runs; the rest are dropped. If
/// <see cref="IsDisabled"/> is set to <see langword="true"/>, all actions are executed immediately without
/// debouncing.</remarks>
/// <param name="delay">The minimum time interval to wait before allowing the next action to execute. Actions triggered within this interval
/// are ignored.</param>
public class Debouncer(TimeSpan delay)
{
    private bool _isWaiting;
    private readonly Lock _lock = new();
    /// <summary>
    /// Gets or sets a value indicating whether the item is disabled.
    /// </summary>
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Executes the specified asynchronous action after a delay, unless another action is already pending.
    /// </summary>
    /// <remarks>If an action is already pending, the method returns immediately without executing the provided
    /// action. If <see cref="IsDisabled"/> is set, the action runs straight away with no delay and without
    /// suppressing anything else. The delay duration is determined by the current value of the delay field.
    /// This method is thread-safe.</remarks>
    /// <param name="action">The asynchronous operation to execute after the delay. Cannot be null.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the delay. The default value is
    /// <see cref="CancellationToken.None"/>.</param>
    /// <returns>A task that represents the asynchronous execution of the specified action.</returns>
    /// <exception cref="OperationCanceledException">The delay was canceled. The action does not run, and the
    /// debouncer is left ready to accept the next call.</exception>
    public async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        // Disabled means no debouncing rather than no execution. Dropping the call instead would make
        // the flag a way to silently lose work, which is the opposite of what a caller turning
        // debouncing off is asking for.
        if (IsDisabled)
        {
            await action();
            return;
        }

        lock (_lock)
        {
            if (_isWaiting) return;
            _isWaiting = true;
        }

        // The flag has to be cleared even when the delay does not finish. Cancellation is the ordinary
        // way out for a caller that passes a stopping token, and leaving the flag set there suppressed
        // every later call for the life of the instance. It is cleared before the action runs, so the
        // window a call is suppressed in stays the delay itself.
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        finally
        {
            lock (_lock)
            {
                _isWaiting = false;
            }
        }

        await action();
    }
}