using System.IdentityModel.Tokens.Jwt;
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
    /// Gets a token for a service given its name and secret. The client has to be previously configured in Keycloak.
    /// </summary>
    /// <param name="authUrl">The server path</param>
    /// <param name="realm">The Keycloak realm name, e.g. redmist</param>
    /// <param name="clientName">The client name, e.g. service-client</param>
    /// <param name="clientSecret">The client secret from Keycloak</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the access token string, or <c>null</c> if the request fails.</returns>
    /// <remarks>
    /// This method uses the OAuth 2.0 client credentials grant type to authenticate with Keycloak
    /// and retrieve an access token for service-to-service communication.
    /// </remarks>
    public static async Task<string?> RequestClientToken(string authUrl, string realm, string clientName, string clientSecret)
    {
        var url = string.Format(URL, authUrl, realm);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        string content = string.Format(GRANTREQUEST, clientName, clientSecret);
        request.Content = new StringContent(content, Encoding.UTF8, CONTENTTYPE);
        var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(json);
        return jsonDoc.RootElement.GetProperty("access_token").ToString();
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
