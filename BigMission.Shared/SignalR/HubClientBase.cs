using BigMission.Shared.Auth;
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
/// Derived classes should override <see cref="ExecuteAsync"/> to implement hub-specific functionality.
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

    /// <summary>
    /// Gets the delay between reconnection attempts when the initial connection fails.
    /// </summary>
    /// <value>The default delay is 5 seconds. Override this property to customize the reconnection delay.</value>
    protected virtual TimeSpan ReconnectDelay => TimeSpan.FromSeconds(5);

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
            .WithUrl(url, options => options.AccessTokenProvider = async () =>
            {
                try
                {
                    return await KeycloakServiceToken.RequestClientToken(authUrl, realm, clientId, clientSecret);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to get server hub access token");
                    return null;
                }
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
        var connection = GetConnection();
        _ = Task.Run(async () =>
        {
            bool firstTime = false;
            while (!stoppingToken.IsCancellationRequested && !firstTime)
            {
                try
                {
                    ConnectionStatusChanged?.Invoke(connection.State);

                    // Retry starting the initial connection to the hub
                    if (connection.State == HubConnectionState.Disconnected)
                    {
                        Logger.LogInformation("Connecting to hub...");
                        await connection.StartAsync(stoppingToken);
                        firstTime = true;
                        ConnectionStatusChanged?.Invoke(connection.State);
                        Logger.LogInformation($"Connected to hub: {connection.ConnectionId}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Error connecting to hub: {ex.Message}");
                }

                ConnectionStatusChanged?.Invoke(connection.State);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }, stoppingToken);

        return connection;
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
}
