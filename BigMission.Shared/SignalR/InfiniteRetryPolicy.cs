using Microsoft.AspNetCore.SignalR.Client;

namespace BigMission.Shared.SignalR;

public class InfiniteRetryPolicy : IRetryPolicy
{
    public TimeSpan LongRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.PreviousRetryCount < 3)
        {
            return TimeSpan.FromSeconds(1);
        }

        return LongRetryInterval;
    }
}
