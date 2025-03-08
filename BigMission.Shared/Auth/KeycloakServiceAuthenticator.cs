using RestSharp;
using RestSharp.Authenticators;

namespace BigMission.Shared.Auth;

/// <summary>
/// Authenticator for Keycloak based rest sharp services.
/// </summary>
/// <param name="token"></param>
/// <param name="authUrl"></param>
/// <param name="realm"></param>
/// <param name="clientId"></param>
/// <param name="clientSecret"></param>
public class KeycloakServiceAuthenticator(string token, string authUrl, string realm, string clientId, string clientSecret) : AuthenticatorBase(token)
{
    protected override async ValueTask<Parameter> GetAuthenticationParameter(string accessToken)
    {
        if (string.IsNullOrEmpty(Token) || !IsTokenValid())
        {
            Token = await KeycloakServiceToken.RequestClientToken(authUrl, realm, clientId, clientSecret) ?? string.Empty;
        }
        return new HeaderParameter(KnownHeaders.Authorization, $"Bearer {Token}");
    }

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
