using System.Net;
using BigMission.Shared.SignalR;
using BigMission.Shared.Tests.TestHelpers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BigMission.Shared.Tests.SignalR;

/// <summary>
/// <see cref="HubClientBase"/> reads its settings, builds an authenticated hub connection, and retries
/// forever until it gets one. These tests cover the configuration guard rails and the connect-failure
/// reporting, which exists so that an outage produces one error rather than one every five seconds -
/// behavior that is invisible from the outside and easy to undo by accident.
/// </summary>
/// <remarks>
/// The failure tests drive the real retry loop, so how quickly an attempt fails decides how long they
/// take. They point the client at loopback stubs that answer and refuse rather than at a dead port: a
/// refused connection costs seconds on some machines, which turned a test that needs five attempts into
/// a test that needs half a minute.
/// </remarks>
[TestClass]
public class HubClientBaseTests
{
    private const string ConnectFailureMessage = "Error connecting to hub";
    private const string SubscriberFault = "subscriber is broken";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Exposes the protected surface of <see cref="HubClientBase"/> and shortens the retry delay.</summary>
    private sealed class TestHubClient(ILoggerFactory loggerFactory, IConfiguration configuration)
        : HubClientBase(loggerFactory, configuration)
    {
        public TimeSpan Reconnect { get; set; } = TimeSpan.FromMilliseconds(20);
        public int Threshold { get; set; } = 3;

        protected override TimeSpan ReconnectDelay => Reconnect;
        protected override int ConnectErrorThreshold => Threshold;

        public HubConnection Build() => GetConnection();
        public HubConnection Connect(CancellationToken stoppingToken = default) => StartConnection(stoppingToken);

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    /// <summary>A Keycloak that hands out a usable token straight away.</summary>
    private static StubHttpServer KeycloakStub()
        => new(HttpStatusCode.OK, $$"""{"access_token":"{{TestJwt.ExpiringIn(TimeSpan.FromHours(1))}}"}""");

    /// <summary>
    /// A hub that answers the WebSocket upgrade with a plain response, so the handshake fails at once.
    /// </summary>
    private static StubHttpServer HubStub() => new(HttpStatusCode.NotFound, "no hub here", "text/plain");

    /// <summary>
    /// An address nothing can be listening on. Port 1 is reserved and privileged, so unlike a port
    /// briefly opened and closed to find a free number, it cannot be claimed by a stub server starting
    /// up in a test running alongside this one.
    /// </summary>
    private const string Unreachable = "http://127.0.0.1:1";

    /// <summary>Settings that are complete but point nowhere; for the tests that never connect.</summary>
    private static IConfiguration Config(params (string Key, string? Value)[] overrides)
        => Config($"{Unreachable}/hub", Unreachable, overrides);

    private static IConfiguration Config(string hubUrl, string authUrl, params (string Key, string? Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Hub:Url"] = hubUrl,
            ["Keycloak:AuthServerUrl"] = authUrl,
            ["Keycloak:Realm"] = "redmist",
            ["Keycloak:ClientId"] = "service-client",
            ["Keycloak:ClientSecret"] = "s3cr3t",
        };

        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>Settings pointing at a Keycloak that works and a hub that refuses.</summary>
    private static IConfiguration FailingHubConfig(StubHttpServer keycloak, StubHttpServer hub)
        => Config($"{hub.BaseUrl}/hub", keycloak.BaseUrl);

    #region Configuration guard rails

    [TestMethod]
    [DataRow("Hub:Url", "Hub URL is not configured.")]
    [DataRow("Keycloak:AuthServerUrl", "Keycloak URL is not configured.")]
    [DataRow("Keycloak:Realm", "Keycloak realm is not configured.")]
    [DataRow("Keycloak:ClientId", "Keycloak client ID is not configured.")]
    [DataRow("Keycloak:ClientSecret", "Keycloak client secret is not configured.")]
    public void GetConnection_RequiredSettingMissing_SaysWhichOne(string missingKey, string expectedMessage)
    {
        using var client = new TestHubClient(new RecordingLoggerFactory(), Config((missingKey, null)));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => client.Build());

        Assert.AreEqual(expectedMessage, ex.Message);
    }

    [TestMethod]
    public async Task GetConnection_FullyConfigured_ReturnsAConnectionThatHasNotStarted()
    {
        using var client = new TestHubClient(new RecordingLoggerFactory(), Config());

        await using var connection = client.Build();

        Assert.AreEqual(HubConnectionState.Disconnected, connection.State);
    }

    [TestMethod]
    public async Task GetConnection_DoesNotLogTheClientSecret()
    {
        // The debug lines print every connection parameter, and the secret is masked among them.
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, Config(("Keycloak:ClientSecret", "correct-horse-battery-staple")));

        await using var connection = client.Build();

        Assert.IsFalse(loggerFactory.Entries.Any(e => e.Message.Contains("correct-horse-battery-staple")));
        Assert.IsTrue(loggerFactory.Entries.Any(e => e.Message.Contains(new string('*', "correct-horse-battery-staple".Length))));
    }

    [TestMethod]
    public void ReloadClientCredentials_CredentialsNowMissing_Throws()
    {
        using var client = new TestHubClient(new RecordingLoggerFactory(), Config(("Keycloak:ClientId", null)));

        Assert.ThrowsExactly<InvalidOperationException>(client.ReloadClientCredentials);
    }

    [TestMethod]
    public void ReloadClientCredentials_CredentialsPresent_Succeeds()
    {
        using var client = new TestHubClient(new RecordingLoggerFactory(), Config());

        client.ReloadClientCredentials();
    }

    #endregion

    #region Reporting a hub outage

    [TestMethod]
    public async Task StartConnection_HubUnreachable_WarnsBelowTheThresholdThenReportsOneError()
    {
        // The point of the accounting: a five-second retry loop against a down hub used to emit an
        // identical error every five seconds for as long as the outage lasted.
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub)) { Threshold = 3 };

        await using var connection = client.Connect();
        var failures = await loggerFactory.WaitForAsync(ConnectFailureMessage, 5, WaitTimeout);

        Assert.IsGreaterThanOrEqualTo(5, failures.Count, "The retry loop should have kept trying.");
        Assert.AreEqual(LogLevel.Warning, failures[0].Level);
        Assert.AreEqual(LogLevel.Warning, failures[1].Level);
        Assert.AreEqual(LogLevel.Error, failures[2].Level);
        Assert.AreEqual(LogLevel.Information, failures[3].Level);
        Assert.AreEqual(LogLevel.Information, failures[4].Level);
    }

    [TestMethod]
    public async Task StartConnection_OutageContinues_TheErrorIsNeverRepeated()
    {
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub)) { Threshold = 2 };

        await using var connection = client.Connect();
        var failures = await loggerFactory.WaitForAsync(ConnectFailureMessage, 8, WaitTimeout);

        Assert.IsGreaterThanOrEqualTo(8, failures.Count);
        Assert.AreEqual(1, failures.Count(f => f.Level == LogLevel.Error));
    }

    [TestMethod]
    public async Task StartConnection_ThresholdBelowOne_ReportsOnTheFirstFailure()
    {
        // A threshold of zero or less cannot hold anything back: the attempt count starts at one, so
        // the "below threshold" comparison is false from the first failure and the error is reported
        // straight away - once, like any other. The clamp in LogConnectFailure makes this explicit
        // rather than changing it.
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub)) { Threshold = 0 };

        await using var connection = client.Connect();
        var failures = await loggerFactory.WaitForAsync(ConnectFailureMessage, 3, WaitTimeout);

        Assert.AreEqual(LogLevel.Error, failures[0].Level);
        Assert.AreEqual(1, failures.Count(f => f.Level == LogLevel.Error));
    }

    [TestMethod]
    public async Task StartConnection_EveryFailureCarriesTheAttemptCountAndTheCause()
    {
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub)) { Threshold = 3 };

        await using var connection = client.Connect();
        var failures = await loggerFactory.WaitForAsync(ConnectFailureMessage, 3, WaitTimeout);

        StringAssert.Contains(failures[0].Message, "attempt 1");
        StringAssert.Contains(failures[2].Message, "3 attempts");
        Assert.IsTrue(failures.All(f => f.Exception is not null), "The cause has to survive the level change.");
    }

    [TestMethod]
    public async Task StartConnection_KeycloakAlsoUnreachable_WarnsAboutTheTokenSeparately()
    {
        // The token failure is only a warning: returning null lets the connect attempt proceed, and it
        // is the connect failure that decides whether the outage has lasted long enough to report.
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, Config($"{hub.BaseUrl}/hub", Unreachable));

        await using var connection = client.Connect();
        var tokenWarnings = await loggerFactory.WaitForAsync("Failed to get server hub access token", 1, WaitTimeout);

        Assert.IsGreaterThanOrEqualTo(1, tokenWarnings.Count);
        Assert.IsTrue(tokenWarnings.All(w => w.Level == LogLevel.Warning));
    }

    [TestMethod]
    public async Task StartConnection_SendsTheConfiguredCredentialsToKeycloak()
    {
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub));

        await using var connection = client.Connect();
        await loggerFactory.WaitForAsync(ConnectFailureMessage, 1, WaitTimeout);

        var tokenRequest = keycloak.Requests.First();
        Assert.AreEqual("/realms/redmist/protocol/openid-connect/token", tokenRequest.Path);
        StringAssert.Contains(tokenRequest.Body, "client_id=service-client");
    }

    #endregion

    #region Connection status subscribers

    [TestMethod]
    public async Task StartConnection_RaisesConnectionStatusChanged()
    {
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub));
        var states = new List<HubConnectionState>();
        client.ConnectionStatusChanged += state => { lock (states) { states.Add(state); } };

        await using var connection = client.Connect();
        await loggerFactory.WaitForAsync(ConnectFailureMessage, 1, WaitTimeout);

        lock (states)
        {
            Assert.IsGreaterThanOrEqualTo(1, states.Count);
            Assert.IsTrue(states.All(s => s == HubConnectionState.Disconnected));
        }
    }

    [TestMethod]
    public async Task StartConnection_SubscriberThrows_IsLoggedAndNotCountedAsAConnectFailure()
    {
        // A faulty subscriber used to be indistinguishable from the hub being down, which both hid the
        // real cause and pushed the connect-failure count past the error threshold on its own.
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub)) { Threshold = 3 };
        client.ConnectionStatusChanged += _ => throw new InvalidOperationException(SubscriberFault);

        await using var connection = client.Connect();
        var subscriberWarnings = await loggerFactory.WaitForAsync("connection status subscriber threw", 1, WaitTimeout);
        var failures = await loggerFactory.WaitForAsync(ConnectFailureMessage, 1, WaitTimeout);

        Assert.IsGreaterThanOrEqualTo(1, subscriberWarnings.Count);
        Assert.AreEqual(LogLevel.Warning, subscriberWarnings[0].Level);

        // The subscriber's fault must not appear as a connect failure. Checking the attempt number
        // alone would not catch that: a counted subscriber fault logs "attempt 1" just the same, so
        // the exception it carries is what tells the two apart.
        Assert.IsTrue(
            failures.All(f => f.Exception is not InvalidOperationException { Message: SubscriberFault }),
            "A subscriber's exception was reported as a connect failure.");
        StringAssert.Contains(failures[0].Message, "attempt 1");
    }

    #endregion

    #region Shutdown

    [TestMethod]
    public async Task Dispose_StopsTheRetryLoop()
    {
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();

        // Not a "using" declaration, because the test disposes the client itself and HubClientBase
        // does not tolerate being disposed twice - the second Cancel lands on an already-disposed
        // token source. The try/finally is what stops an assertion failure leaking the client.
        var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub));
        var disposed = false;
        try
        {
            await using (var connection = client.Connect())
            {
                await loggerFactory.WaitForAsync(ConnectFailureMessage, 2, WaitTimeout);
                client.Dispose();
                disposed = true;
            }
        }
        finally
        {
            if (!disposed)
            {
                client.Dispose();
            }
        }

        var afterDispose = loggerFactory.Matching(ConnectFailureMessage).Count;
        await Task.Delay(500);

        // One more is allowed for an attempt that was already in flight when the token was canceled.
        Assert.IsLessThanOrEqualTo(afterDispose + 1, loggerFactory.Matching(ConnectFailureMessage).Count);
    }

    [TestMethod]
    public async Task StartConnection_StoppingTokenCanceled_StopsTheRetryLoop()
    {
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub));
        using var stopping = new CancellationTokenSource();

        await using var connection = client.Connect(stopping.Token);
        await loggerFactory.WaitForAsync(ConnectFailureMessage, 2, WaitTimeout);
        await stopping.CancelAsync();

        var afterCancel = loggerFactory.Matching(ConnectFailureMessage).Count;
        await Task.Delay(500);

        Assert.IsLessThanOrEqualTo(afterCancel + 1, loggerFactory.Matching(ConnectFailureMessage).Count);
    }

    [TestMethod]
    public async Task StartConnection_CalledAgain_AbandonsThePreviousAttempt()
    {
        // Each call cancels the last, so a caller that reconnects on a credential change does not end
        // up with two loops racing to start the same client. The abandoned loop says so on its way
        // out, which is the only outward sign the cancellation happened - comparing the two connection
        // objects proves nothing, since GetConnection builds a new one either way.
        //
        // The retry delay is long so the first loop is reliably parked in it when the second call
        // arrives. Cancelled between awaits, the loop just falls out of the while condition silently.
        await using var keycloak = KeycloakStub();
        await using var hub = HubStub();
        var loggerFactory = new RecordingLoggerFactory();
        using var client = new TestHubClient(loggerFactory, FailingHubConfig(keycloak, hub))
        {
            Reconnect = TimeSpan.FromSeconds(10),
        };

        await using var first = client.Connect();
        await loggerFactory.WaitForAsync(ConnectFailureMessage, 1, WaitTimeout);
        await Task.Delay(200);
        Assert.AreEqual(0, loggerFactory.Matching("cancelled").Count, "Nothing should have been abandoned yet.");

        await using var second = client.Connect();

        // A short timeout on purpose: the abandoned loop is parked in a delay it can be woken from
        // immediately, so waiting the full WaitTimeout here only slows down the failure case.
        var abandoned = await loggerFactory.WaitForAsync("cancelled", 1, TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, abandoned.Count, "The first retry loop should have been cancelled by the second call.");
    }

    #endregion
}
