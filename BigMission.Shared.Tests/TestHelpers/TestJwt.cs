using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace BigMission.Shared.Tests.TestHelpers;

/// <summary>
/// Builds unsigned JWTs for the expiry checks in <c>KeycloakServiceToken</c>.
/// </summary>
/// <remarks>
/// The production code reads tokens with <see cref="JwtSecurityTokenHandler.ReadJwtToken"/>, which
/// parses without validating, so an unsigned token is indistinguishable from a Keycloak-issued one for
/// these purposes and keeps the tests free of key material.
/// </remarks>
internal static class TestJwt
{
    /// <summary>Builds a token whose <c>exp</c> claim is <paramref name="expires"/>.</summary>
    public static string WithExpiry(DateTime expires)
        => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(expires: expires));

    /// <summary>Builds a token that expires <paramref name="fromNow"/> from now.</summary>
    public static string ExpiringIn(TimeSpan fromNow) => WithExpiry(DateTime.UtcNow.Add(fromNow));

    /// <summary>Builds a token carrying claims but no <c>exp</c>.</summary>
    public static string WithoutExpiry()
        => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: [new Claim("sub", "service-client")]));
}
