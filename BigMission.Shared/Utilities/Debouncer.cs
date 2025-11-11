namespace BigMission.Shared.Utilities;

/// <summary>
/// Provides a mechanism to delay and suppress repeated execution of asynchronous actions within a specified time
/// interval.
/// </summary>
/// <remarks>Use this class to prevent multiple rapid invocations of an action, such as in response to frequent
/// user input or events. If <see cref="IsDisabled"/> is set to <see langword="true"/>, all actions are executed
/// immediately without debouncing.</remarks>
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
    /// Executes the specified asynchronous action after a delay, unless execution is currently disabled or another
    /// action is already pending.
    /// </summary>
    /// <remarks>If execution is disabled or an action is already pending, the method returns immediately
    /// without executing the provided action. The delay duration is determined by the current value of the delay field.
    /// This method is thread-safe.</remarks>
    /// <param name="action">The asynchronous operation to execute after the delay. Cannot be null.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the delay or the execution of the action. The default value is
    /// <see cref="CancellationToken.None"/>.</param>
    /// <returns>A task that represents the asynchronous execution of the specified action.</returns>
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