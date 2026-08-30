using BigMission.Shared.SignalR;

// MSTest brings its own RetryContext into scope through the global using.
using RetryContext = Microsoft.AspNetCore.SignalR.Client.RetryContext;

namespace BigMission.Shared.Tests.SignalR;

/// <summary>
/// <see cref="InfiniteRetryPolicy"/> is what stops SignalR giving up on a hub. These tests pin the
/// short-then-long schedule and, above all, that the policy never returns null - the value that ends
/// automatic reconnection for good and leaves a client quietly disconnected.
/// </summary>
[TestClass]
public class InfiniteRetryPolicyTests
{
    private static TimeSpan? DelayAfter(InfiniteRetryPolicy policy, long previousRetryCount)
        => policy.NextRetryDelay(new RetryContext { PreviousRetryCount = previousRetryCount, ElapsedTime = TimeSpan.Zero });

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    [DataRow(2L)]
    public void NextRetryDelay_FirstThreeAttempts_WaitsOneSecond(long previousRetryCount)
    {
        Assert.AreEqual(TimeSpan.FromSeconds(1), DelayAfter(new InfiniteRetryPolicy(), previousRetryCount));
    }

    [TestMethod]
    [DataRow(3L)]
    [DataRow(4L)]
    [DataRow(100L)]
    public void NextRetryDelay_AfterThreeAttempts_SwitchesToTheLongInterval(long previousRetryCount)
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), DelayAfter(new InfiniteRetryPolicy(), previousRetryCount));
    }

    [TestMethod]
    public void NextRetryDelay_CustomLongRetryInterval_IsUsedForTheLaterAttempts()
    {
        var policy = new InfiniteRetryPolicy { LongRetryInterval = TimeSpan.FromMinutes(2) };

        Assert.AreEqual(TimeSpan.FromSeconds(1), DelayAfter(policy, 2), "The short attempts are not configurable.");
        Assert.AreEqual(TimeSpan.FromMinutes(2), DelayAfter(policy, 3));
    }

    [TestMethod]
    public void NextRetryDelay_AfterAVeryLongOutage_StillReturnsADelay()
    {
        // Returning null here is how SignalR is told to stop retrying. This policy exists precisely so
        // that never happens, however long the hub has been unreachable.
        var policy = new InfiniteRetryPolicy();

        Assert.IsNotNull(policy.NextRetryDelay(new RetryContext
        {
            PreviousRetryCount = long.MaxValue,
            ElapsedTime = TimeSpan.FromDays(30),
            RetryReason = new HttpRequestException("hub unreachable"),
        }));
    }
}
