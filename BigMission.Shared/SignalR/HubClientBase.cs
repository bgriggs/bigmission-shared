using BigMission.Shared.Auth;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BigMission.Shared.SignalR;

/// <summary>
/// Base class for SignalR hub clients. This class handles the connection, reconnection, and authentication logic.
/// </summary>
public abstract class HubClientBase : BackgroundService
{
    private readonly IConfiguration configuration;
    private ILogger Logger { get; }
    public event Action<HubConnectionState>? ConnectionStatusChanged;
    private string clientId = string.Empty;
    private string clientSecret = string.Empty;

    protected virtual TimeSpan ReconnectDelay => TimeSpan.FromSeconds(5);

    public HubClientBase(ILoggerFactory loggerFactory, IConfiguration configuration)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.configuration = configuration;
    }

    protected virtual HubConnection GetConnection()
    {
        var url = configuration["Hub:Url"] ?? throw new InvalidOperationException("Hub URL is not configured.");
        var authUrl = configuration["Keycloak:AuthServerUrl"] ?? throw new InvalidOperationException("Keycloak URL is not configured.");
        var realm = configuration["Keycloak:Realm"] ?? throw new InvalidOperationException("Keycloak realm is not configured.");
        clientId = configuration["Keycloak:ClientId"] ?? throw new InvalidOperationException("Keycloak client ID is not configured.");
        clientSecret = configuration["Keycloak:ClientSecret"] ?? throw new InvalidOperationException("Keycloak client secret is not configured.");

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
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .AddMessagePackProtocol();

        var connection = builder.Build();
        InitializeStateLogging(connection);
        return connection;
    }

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
    /// Call to connect to the server. Once connected, the hub will automatically reconnect if the connection is lost.
    /// </summary>
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

    protected void FireStatusUpdate(HubConnection hub)
    {
        ConnectionStatusChanged?.Invoke(hub.State);
    }

    public void ReloadClientCredentials()
    {
        clientId = configuration["Keycloak:ClientId"] ?? throw new InvalidOperationException("Keycloak client ID is not configured.");
        clientSecret = configuration["Keycloak:ClientSecret"] ?? throw new InvalidOperationException("Keycloak client secret is not configured.");
    }
}
