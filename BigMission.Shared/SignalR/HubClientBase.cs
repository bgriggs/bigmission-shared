using BigMission.Shared.Auth;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BigMission.Shared.SignalR;

/// <summary>
/// Base class for SignalR hub clients. This class handles the connection, reconnection, and authentication logic.
/// </summary>
/// <remarks>
/// This abstract class extends <see cref="BackgroundService"/> to provide a foundation for building SignalR hub clients
/// with automatic connection management, Keycloak-based authentication, and configurable reconnection logic.
/// </remarks>
public abstract class HubClientBase : BackgroundService
{
    private readonly IConfiguration configuration;

    /// <summary>
    /// Gets the logger instance for this hub client.
    /// </summary>
    private ILogger Logger { get; }

    /// <summary>
    /// Occurs when the hub connection state changes (e.g., connected, disconnected, reconnecting).
    /// </summary>
    public event Action<HubConnectionState>? ConnectionStatusChanged;

    private string clientId = string.Empty;
    private string clientSecret = string.Empty;
    private CancellationTokenSource? connectionTaskCancellation;

    /// <summary>
    /// Gets the delay between reconnection attempts when the initial connection fails.
    /// </summary>
    /// <value>The default delay is 5 seconds. Override this property to customize the reconnection delay.</value>
    protected virtual TimeSpan ReconnectDelay => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets how many consecutive failed connect attempts are tolerated before the failure is
    /// reported as an error rather than a warning.
    /// </summary>
    /// <value>
    /// The default is 3. Attempts are separated by <see cref="ReconnectDelay"/>, so the error
    /// follows roughly two delays of continuous failure. Values below one are treated as one. The
    /// count belongs to the client rather than to one connection attempt and is cleared by a
    /// successful connect, so a caller that restarts the connection faster than the threshold
    /// cannot hold the failure permanently below it.
    /// </value>
    protected virtual int ConnectErrorThreshold => 3;

    private int consecutiveConnectFailures;
    private bool connectErrorReported;

    /// <summary>
    /// Initializes a new instance of the <see cref="HubClientBase"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory used to create the logger for this hub client.</param>
    /// <param name="configuration">The configuration instance containing hub and Keycloak settings.</param>
    public HubClientBase(ILoggerFactory loggerFactory, IConfiguration configuration)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.configuration = configuration;
    }

    /// <summary>
    /// Creates and configures a new <see cref="HubConnection"/> with authentication and automatic reconnection.
    /// </summary>
    /// <returns>A configured <see cref="HubConnection"/> instance ready to be started.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when required configuration values are missing (Hub:Url, Keycloak:AuthServerUrl, Keycloak:Realm).
    /// </exception>
    /// <remarks>
    /// This method reads configuration values for the hub URL and Keycloak authentication settings,
    /// configures the hub connection with an access token provider using Keycloak service tokens,
    /// and sets up automatic reconnection with infinite retry policy.
    /// </remarks>
    protected virtual HubConnection GetConnection()
    {
        var url = configuration["Hub:Url"] ?? throw new InvalidOperationException("Hub URL is not configured.");
        var authUrl = configuration["Keycloak:AuthServerUrl"] ?? throw new InvalidOperationException("Keycloak URL is not configured.");
        var realm = configuration["Keycloak:Realm"] ?? throw new InvalidOperationException("Keycloak realm is not configured.");
        clientId = GetClientId();
        clientSecret = GetClientSecret();

        // Log all parameters
        Logger.LogDebug($"Hub URL: {url}");
        Logger.LogDebug($"Keycloak Auth URL: {authUrl}");
        Logger.LogDebug($"Keycloak Realm: {realm}");
        Logger.LogDebug($"Keycloak Client ID: {clientId}");
        Logger.LogDebug($"Keycloak Client Secret: {new string('*', clientSecret.Length)}");

        var builder = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = async () =>
                {
                    try
                    {
                        return await KeycloakServiceToken.RequestClientToken(authUrl, realm, clientId, clientSecret);
                    }
                    catch (Exception ex)
                    {
                        // Warning rather than error: returning null lets the attempt proceed
                        // and fail, and it is the connect failure that decides whether the
                        // condition has lasted long enough to be worth reporting.
                        Logger.LogWarning(ex, "Failed to get server hub access token");
                        return null;
                    }
                };
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy());

        var connection = builder.Build();
        InitializeStateLogging(connection);
        return connection;
    }

    /// <summary>
    /// Gets the Keycloak client ID from configuration.
    /// </summary>
    /// <returns>The client ID string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the Keycloak:ClientId configuration value is not set.</exception>
    /// <remarks>Override this method to provide the client ID from a different source.</remarks>
    protected virtual string GetClientId()
    {
        return configuration["Keycloak:ClientId"] ?? throw new InvalidOperationException("Keycloak client ID is not configured.");
    }

    /// <summary>
    /// Gets the Keycloak client secret from configuration.
    /// </summary>
    /// <returns>The client secret string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the Keycloak:ClientSecret configuration value is not set.</exception>
    /// <remarks>Override this method to provide the client secret from a different source.</remarks>
    protected virtual string GetClientSecret()
    {
        return configuration["Keycloak:ClientSecret"] ?? throw new InvalidOperationException("Keycloak client secret is not configured.");
    }

    /// <summary>
    /// Initializes event handlers for logging hub connection state changes.
    /// </summary>
    /// <param name="connection">The hub connection to attach event handlers to.</param>
    /// <remarks>
    /// This method sets up handlers for the Reconnected, Closed, and Reconnecting events,
    /// logging appropriate messages and invoking the <see cref="ConnectionStatusChanged"/> event
    /// to notify subscribers of state changes.
    /// </remarks>
    protected virtual void InitializeStateLogging(HubConnection connection)
    {
        connection.Reconnected += msg =>
        {
            Logger.LogInformation($"Hub connected: {msg}");
            ConnectionStatusChanged?.Invoke(connection.State);
            return Task.CompletedTask;
        };
        connection.Closed += ex =>
        {
            Logger.LogWarning($"Connection closed: {ex?.Message}");
            ConnectionStatusChanged?.Invoke(connection.State);
            return Task.CompletedTask;
        };
        connection.Reconnecting += ex =>
        {
            Logger.LogWarning($"Connection reconnecting: {ex?.Message}");
            ConnectionStatusChanged?.Invoke(connection.State);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Initiates the hub connection and implements retry logic for initial connection failures.
    /// </summary>
    /// <param name="stoppingToken">A cancellation token that can be used to stop the connection attempt.</param>
    /// <returns>The <see cref="HubConnection"/> instance that was started.</returns>
    /// <remarks>
    /// This method creates a new hub connection and attempts to start it. If the initial connection fails,
    /// it will retry after the <see cref="ReconnectDelay"/> period until successful or until cancellation is requested.
    /// Once connected, the hub will automatically reconnect if the connection is lost due to the configured
    /// automatic reconnection policy. The <see cref="ConnectionStatusChanged"/> event is raised when the connection state changes.
    /// </remarks>
    protected virtual HubConnection StartConnection(CancellationToken stoppingToken = default)
    {
        // Cancel any previous connection attempt
        connectionTaskCancellation?.Cancel();
        connectionTaskCancellation?.Dispose();
        connectionTaskCancellation = new CancellationTokenSource();

        var connection = GetConnection();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, connectionTaskCancellation.Token);

        _ = Task.Run(async () =>
        {
            bool firstTime = false;
            while (!linkedCts.Token.IsCancellationRequested && !firstTime)
            {
                try
                {
                    RaiseConnectionStatusChanged(connection);

                    // Retry starting the initial connection to the hub
                    if (connection.State == HubConnectionState.Disconnected)
                    {
                        Logger.LogInformation("Connecting to hub...");
                        await connection.StartAsync(linkedCts.Token);
                        firstTime = true;
                        Interlocked.Exchange(ref consecutiveConnectFailures, 0);
                        connectErrorReported = false;
                        RaiseConnectionStatusChanged(connection);
                        Logger.LogInformation($"Connected to hub: {connection.ConnectionId}");
                    }
                }
                catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
                {
                    Logger.LogDebug("Connection attempt cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    // A cancellation that did not come from the token is a failure like any other -
                    // an HTTP timeout during negotiate arrives as TaskCanceledException, and
                    // treating that as a cancellation used to break the loop for good, leaving the
                    // client silently never reconnecting.
                    LogConnectFailure(ex);
                }

                if (!linkedCts.Token.IsCancellationRequested)
                {
                    RaiseConnectionStatusChanged(connection);
                    try
                    {
                        await Task.Delay(ReconnectDelay, linkedCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.LogDebug("Connection retry delay cancelled");
                        break;
                    }
                }
            }

            linkedCts.Dispose();
        }, linkedCts.Token);

        return connection;
    }

    /// <summary>
    /// Raises <see cref="ConnectionStatusChanged"/>, keeping a subscriber's fault out of the
    /// connect-failure accounting it would otherwise be counted as.
    /// </summary>
    private void RaiseConnectionStatusChanged(HubConnection connection)
    {
        try
        {
            ConnectionStatusChanged?.Invoke(connection.State);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "A connection status subscriber threw");
        }
    }

    /// <summary>
    /// Logs a failed connect attempt, raising it to an error once the failure has persisted.
    /// </summary>
    /// <remarks>
    /// The retry loop runs until it connects, so the early attempts are ordinary: a client can
    /// change network, wake from sleep, or outlast a server restart. Logging every attempt at error
    /// level turned a brief outage into a stream of identical reports, one per retry. The error is
    /// raised once, when the failure has lasted long enough to be worth acting on, and the attempts
    /// on either side of it are quieter so a long outage cannot crowd out unrelated diagnostics.
    /// </remarks>
    private void LogConnectFailure(Exception ex)
    {
        var attempt = Interlocked.Increment(ref consecutiveConnectFailures);
        var threshold = Math.Max(1, ConnectErrorThreshold);

        if (attempt < threshold)
        {
            Logger.LogWarning(ex, "Error connecting to hub on attempt {Attempt}", attempt);
        }
        else if (!connectErrorReported)
        {
            connectErrorReported = true;
            Logger.LogError(ex, "Error connecting to hub, still failing after {Attempt} attempts", attempt);
        }
        else
        {
            Logger.LogInformation(ex, "Error connecting to hub on attempt {Attempt}", attempt);
        }
    }

    /// <summary>
    /// Manually triggers the <see cref="ConnectionStatusChanged"/> event with the current hub connection state.
    /// </summary>
    /// <param name="hub">The hub connection whose state should be broadcast.</param>
    /// <remarks>
    /// This method can be called by derived classes to notify subscribers of the connection status
    /// at any point in time, outside of the automatic state change notifications.
    /// </remarks>
    protected void FireStatusUpdate(HubConnection hub)
    {
        ConnectionStatusChanged?.Invoke(hub.State);
    }

    /// <summary>
    /// Reloads the Keycloak client credentials from configuration.
    /// </summary>
    /// <remarks>
    /// This method can be called to refresh the client ID and client secret from configuration,
    /// which is useful if the configuration has been updated at runtime. Note that this does not
    /// affect existing connections; it will only apply to new connections created after this call.
    /// </remarks>
    public virtual void ReloadClientCredentials()
    {
        clientId = GetClientId();
        clientSecret = GetClientSecret();
    }

    /// <summary>
    /// Disposes resources used by this hub client, including cancelling any pending connection attempts.
    /// </summary>
    public override void Dispose()
    {
        connectionTaskCancellation?.Cancel();
        connectionTaskCancellation?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
