using RestSharp;

namespace BigMission.Shared.RestSharp;

/// <summary>
/// Provides extension methods for configuring RestClient instances with MessagePack serialization and appropriate HTTP
/// Accept headers.
/// </summary>
/// <remarks>This static class contains helper methods to simplify the setup of RestClient for scenarios where
/// MessagePack serialization is preferred. The extensions ensure that the client is properly configured to communicate
/// with APIs expecting MessagePack or JSON payloads.</remarks>
public static class RestClientExtensions
{
    /// <summary>
    /// Creates a RestClient configured with MessagePack serialization and appropriate Accept headers.
    /// </summary>
    /// <param name="options">The RestClient options including base URL and authenticator.</param>
    /// <returns>A configured RestClient instance.</returns>
    public static RestClient CreateWithMessagePack(this RestClientOptions options)
    {
        var client = new RestClient(options,
            configureSerialization: s => s.UseSerializer(() => new MessagePackRestSerializer()));

        // Add default Accept header for all requests (MessagePack preferred, JSON fallback)
        client.AddDefaultHeader("Accept", "application/msgpack, application/json");

        return client;
    }
}