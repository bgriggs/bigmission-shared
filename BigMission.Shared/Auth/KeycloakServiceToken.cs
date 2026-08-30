using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BigMission.Shared.Auth;

/// <summary>
/// Utilities for Keycloak based authentication for services.
/// </summary>
public class KeycloakServiceToken
{
    /// <summary>
    /// The URL template for the Keycloak token endpoint.
    /// </summary>
    const string URL = "{0}/realms/{1}/protocol/openid-connect/token";

    /// <summary>
    /// The grant request body template for client credentials flow.
    /// </summary>
    const string GRANTREQUEST = "grant_type=client_credentials&client_id={0}&client_secret={1}";

    /// <summary>
    /// The content type for form-urlencoded requests.
    /// </summary>
    const string CONTENTTYPE = "application/x-www-form-urlencoded";

    /// <summary>
    /// How much of a failure response body to keep. The body comes from whoever actually answered,
    /// which is not always Keycloak, so this bounds an error page - and anything a proxy chose to
    /// put in one - rather than letting it all through to a log.
    /// </summary>
    const int MAXREPORTEDBODY = 200;

    /// <summary>
    /// Gets a token for a service given its name and secret. The client has to be previously configured in Keycloak.
    /// </summary>
    /// <param name="authUrl">The server path</param>
    /// <param name="realm">The Keycloak realm name, e.g. redmist</param>
    /// <param name="clientName">The client name, e.g. service-client</param>
    /// <param name="clientSecret">The client secret from Keycloak</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the access token string.</returns>
    /// <exception cref="KeycloakTokenException">
    /// Keycloak replied without an access token. Carries the status code and, where Keycloak sent
    /// them, its own error and error_description.
    /// </exception>
    /// <exception cref="HttpRequestException">Keycloak could not be reached.</exception>
    /// <exception cref="TaskCanceledException">The request exceeded the default HttpClient timeout.</exception>
    /// <remarks>
    /// This method uses the OAuth 2.0 client credentials grant type to authenticate with Keycloak
    /// and retrieve an access token for service-to-service communication.
    ///
    /// It throws rather than returning null on failure, because every caller is about to make a
    /// request that cannot succeed without a token. Failing quietly only moves the confusion to an
    /// unauthorized response further out, by which point the reason is gone. Callers that would
    /// rather carry on without a token catch it - the SignalR access token providers do.
    /// </remarks>
    public static async Task<string> RequestClientToken(string authUrl, string realm, string clientName, string clientSecret)
    {
        var url = string.Format(URL, authUrl, realm);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        // Escaped because this is a form-urlencoded body: a secret containing +, &, % or = would
        // otherwise be mis-decoded server side and rejected, and the failure below would report
        // invalid_client - correct in wording, and pointing at the wrong cause.
        string content = string.Format(GRANTREQUEST, Uri.EscapeDataString(clientName), Uri.EscapeDataString(clientSecret));
        request.Content = new StringContent(content, Encoding.UTF8, CONTENTTYPE);
        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (TryReadAccessToken(json, out var accessToken))
        {
            return accessToken;
        }

        throw DescribeFailure(clientName, realm, response.StatusCode, json);
    }

    /// <summary>
    /// Reads the access token out of a token endpoint response, if there is one to read.
    /// </summary>
    /// <remarks>
    /// Driven by the content rather than the status code, so a proxy that rewrites a status cannot
    /// discard a token that did arrive. An empty or non-string token counts as absent: it would
    /// otherwise be sent as a bare bearer header and fail further out.
    /// </remarks>
    private static bool TryReadAccessToken(string json, [NotNullWhen(true)] out string? accessToken)
    {
        accessToken = null;
        try
        {
            using var jsonDoc = JsonDocument.Parse(json);
            if (jsonDoc.RootElement.ValueKind == JsonValueKind.Object
                && jsonDoc.RootElement.TryGetProperty("access_token", out var token)
                && token.ValueKind == JsonValueKind.String)
            {
                accessToken = token.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON at all, which is its own answer: something other than Keycloak replied.
            // DescribeFailure quotes it, rather than this leaving as a parse error.
        }

        return !string.IsNullOrEmpty(accessToken);
    }

    /// <summary>
    /// Turns a failed token response into something worth putting in front of a person.
    /// </summary>
    private static KeycloakTokenException DescribeFailure(string clientName, string realm, HttpStatusCode statusCode, string json)
    {
        string? error = null;
        string? errorDescription = null;
        try
        {
            using var jsonDoc = JsonDocument.Parse(json);
            if (jsonDoc.RootElement.ValueKind == JsonValueKind.Object)
            {
                error = GetStringProperty(jsonDoc.RootElement, "error");
                errorDescription = GetStringProperty(jsonDoc.RootElement, "error_description");
            }
        }
        catch (JsonException) { }

        var reason = string.Join(": ", new[] { error, errorDescription }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (reason.Length == 0)
        {
            reason = string.IsNullOrWhiteSpace(json)
                ? "the response was empty"
                : "the response came from something other than Keycloak";
        }

        // Built from the response only. The request body carries the client secret and must never
        // reach an exception message, a log, or a crash report. The body itself stays off the
        // message and goes on the exception, where a consumer can choose to report it.
        return new KeycloakTokenException(
            $"Keycloak returned no access token for client '{clientName}' in realm '{realm}' ({(int)statusCode} {statusCode}): {reason}.",
            statusCode,
            error,
            errorDescription,
            Truncate(json));
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Truncate(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        return trimmed.Length > MAXREPORTEDBODY ? $"{trimmed[..MAXREPORTEDBODY]}..." : trimmed;
    }

    /// <summary>
    /// Gets the expiration time of a JWT token.
    /// </summary>
    /// <param name="token">The JWT token string to parse.</param>
    /// <returns>The token expiration time as Unix time in seconds (ticks).</returns>
    /// <remarks>
    /// This method reads the "exp" claim from the JWT token and returns its value as a Unix timestamp.
    /// </remarks>
    public static long GetTokenExpirationTime(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var jwtSecurityToken = handler.ReadJwtToken(token);
        var tokenExp = jwtSecurityToken.Claims.First(claim => claim.Type.Equals("exp")).Value;
        var ticks = long.Parse(tokenExp);
        return ticks;
    }

    /// <summary>
    /// Checks whether a JWT token is still valid based on its expiration time.
    /// </summary>
    /// <param name="token">The JWT token string to validate.</param>
    /// <returns><c>true</c> if the token has not expired; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// This method compares the token's expiration time with the current UTC time to determine validity.
    /// </remarks>
    public static bool CheckTokenIsValid(string token)
    {
        var tokenTicks = GetTokenExpirationTime(token);
        var tokenDate = DateTimeOffset.FromUnixTimeSeconds(tokenTicks).UtcDateTime;

        var now = DateTime.Now.ToUniversalTime();

        var valid = tokenDate >= now;

        return valid;
    }
}
