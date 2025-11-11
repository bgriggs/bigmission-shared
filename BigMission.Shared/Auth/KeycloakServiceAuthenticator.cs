using RestSharp;
using RestSharp.Authenticators;

namespace BigMission.Shared.Auth;

/// <summary>
/// Authenticator for Keycloak based RestSharp services that automatically manages and refreshes access tokens.
/// </summary>
/// <param name="token">The initial access token. Can be empty if not available.</param>
/// <param name="authUrl">The Keycloak server URL</param>
/// <param name="realm">The Keycloak realm name.</param>
/// <param name="clientId">The client ID configured in Keycloak.</param>
/// <param name="clientSecret">The client secret from Keycloak.</param>
/// <remarks>
/// This authenticator extends <see cref="AuthenticatorBase"/> to provide automatic token management for RestSharp HTTP clients.
/// It validates the token before each request and automatically requests a new token from Keycloak if the current token
/// is invalid or expired. The authenticator uses the OAuth 2.0 client credentials grant type for token acquisition.
/// </remarks>
public class KeycloakServiceAuthenticator(string token, string authUrl, string realm, string clientId, string clientSecret) : AuthenticatorBase(token)
{
    /// <summary>
    /// Gets the authentication parameter to be added to the HTTP request, ensuring the token is valid.
    /// </summary>
    /// <param name="accessToken">The current access token.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="HeaderParameter"/> 
    /// with the Bearer token for the Authorization header.</returns>
    /// <remarks>
    /// This method is called by the RestSharp client before each request. It checks if the token is valid and requests
    /// a new token from Keycloak if necessary. The token is then added as a Bearer token in the Authorization header.
    /// </remarks>
    protected override async ValueTask<Parameter> GetAuthenticationParameter(string accessToken)
    {
        if (string.IsNullOrEmpty(Token) || !IsTokenValid())
        {
            Token = await KeycloakServiceToken.RequestClientToken(authUrl, realm, clientId, clientSecret) ?? string.Empty;
        }
        return new HeaderParameter(KnownHeaders.Authorization, $"Bearer {Token}");
    }

    /// <summary>
    /// Checks whether the current token is valid based on its expiration time.
    /// </summary>
    /// <returns><c>true</c> if the token is valid and has not expired; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// This method wraps the token validation logic in a try-catch block to handle any exceptions that may occur
    /// during token parsing or validation. If an exception occurs, the token is considered invalid.
    /// </remarks>
    private bool IsTokenValid()
    {
        try
        {
            return KeycloakServiceToken.CheckTokenIsValid(Token);
        }
        catch { }
        return false;
    }
}
