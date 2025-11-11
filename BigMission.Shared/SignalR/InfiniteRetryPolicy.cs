using Microsoft.AspNetCore.SignalR.Client;

namespace BigMission.Shared.SignalR;

/// <summary>
/// Defines a retry policy that allows infinite retries with configurable intervals between attempts.
/// </summary>
/// <remarks>This policy initially uses a short delay for the first few retries, then switches to a longer
/// interval for subsequent attempts. It is suitable for scenarios where operations should be retried indefinitely until
/// successful, such as background processing or resilient network calls. Use caution when applying this policy, as it
/// may result in unbounded retry loops if the underlying operation cannot succeed.</remarks>
public class InfiniteRetryPolicy : IRetryPolicy
{
    /// <summary>
    /// Gets or sets the interval to wait before performing a long retry operation.
    /// </summary>
    public TimeSpan LongRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Calculates the delay interval before the next retry attempt based on the provided retry context.
    /// </summary>
    /// <remarks>If the number of previous retry attempts is less than three, a short delay is returned;
    /// otherwise, a longer retry interval is used. This method can be used to implement adaptive retry
    /// strategies.</remarks>
    /// <param name="retryContext">An object containing information about the current retry attempt, including the number of previous retries.
    /// Cannot be null.</param>
    /// <returns>A <see cref="TimeSpan"/> representing the delay before the next retry attempt, or <see langword="null"/> if no
    /// further retries should be performed.</returns>
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.PreviousRetryCount < 3)
        {
            return TimeSpan.FromSeconds(1);
        }

        return LongRetryInterval;
    }
}
