using System.Net;
using BigMission.Shared.Auth;
using BigMission.Shared.Tests.TestHelpers;
using RestSharp;
using RestSharp.Authenticators;

namespace BigMission.Shared.Tests.Auth;

/// <summary>
/// <see cref="KeycloakServiceAuthenticator"/> decides, before every RestSharp request, whether the
/// token it is holding will do. These tests pin when it goes back to Keycloak and when it does not,
/// since fetching too eagerly puts a round trip on every call and fetching too rarely produces a 401
/// the caller has no way to explain.
/// </summary>
[TestClass]
public class KeycloakServiceAuthenticatorTests
{
    private const string Realm = "redmist";
    private const string ClientId = "service-client";
    private const string ClientSecret = "s3cr3t";

    private static async Task<RestRequest> AuthenticateAsync(KeycloakServiceAuthenticator authenticator)
    {
        var request = new RestRequest("resource");
        using var client = new RestClient("http://127.0.0.1:1");
        await ((IAuthenticator)authenticator).Authenticate(client, request);
        return request;
    }

    private static string? AuthorizationHeader(RestRequest request)
        => request.Parameters.SingleOrDefault(p => p.Name == KnownHeaders.Authorization)?.Value as string;

    [TestMethod]
    public async Task Authenticate_HoldingAValidToken_ReusesItWithoutCallingKeycloak()
    {
        var token = TestJwt.ExpiringIn(TimeSpan.FromHours(1));
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":"fetched"}""");
        var authenticator = new KeycloakServiceAuthenticator(token, server.BaseUrl, Realm, ClientId, ClientSecret);

        var request = await AuthenticateAsync(authenticator);

        Assert.AreEqual($"Bearer {token}", AuthorizationHeader(request));
        Assert.AreEqual(0, server.Requests.Count, "A token that is still good should not cost a round trip to Keycloak.");
    }

    [TestMethod]
    public async Task Authenticate_StartedWithNoToken_FetchesOne()
    {
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":"fetched"}""");
        var authenticator = new KeycloakServiceAuthenticator(string.Empty, server.BaseUrl, Realm, ClientId, ClientSecret);

        var request = await AuthenticateAsync(authenticator);

        Assert.AreEqual("Bearer fetched", AuthorizationHeader(request));
        Assert.AreEqual(1, server.Requests.Count);
    }

    [TestMethod]
    public async Task Authenticate_HoldingAnExpiredToken_FetchesAReplacement()
    {
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":"fetched"}""");
        var authenticator = new KeycloakServiceAuthenticator(
            TestJwt.ExpiringIn(TimeSpan.FromHours(-1)), server.BaseUrl, Realm, ClientId, ClientSecret);

        var request = await AuthenticateAsync(authenticator);

        Assert.AreEqual("Bearer fetched", AuthorizationHeader(request));
        Assert.AreEqual(1, server.Requests.Count);
    }

    [TestMethod]
    public async Task Authenticate_HoldingSomethingThatIsNotAJwt_FetchesAReplacementRatherThanThrowing()
    {
        // The validity check swallows parse failures on purpose: an unreadable token is no more use
        // than an expired one, and the fetch that follows is the same either way.
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":"fetched"}""");
        var authenticator = new KeycloakServiceAuthenticator("garbage", server.BaseUrl, Realm, ClientId, ClientSecret);

        var request = await AuthenticateAsync(authenticator);

        Assert.AreEqual("Bearer fetched", AuthorizationHeader(request));
    }

    [TestMethod]
    public async Task Authenticate_SecondRequestAfterAFetch_ReusesTheFetchedToken()
    {
        var issued = TestJwt.ExpiringIn(TimeSpan.FromHours(1));
        await using var server = new StubHttpServer(HttpStatusCode.OK, $$"""{"access_token":"{{issued}}"}""");
        var authenticator = new KeycloakServiceAuthenticator(string.Empty, server.BaseUrl, Realm, ClientId, ClientSecret);

        await AuthenticateAsync(authenticator);
        var second = await AuthenticateAsync(authenticator);

        Assert.AreEqual($"Bearer {issued}", AuthorizationHeader(second));
        Assert.AreEqual(1, server.Requests.Count, "The token fetched on the first request should carry the second.");
    }

    [TestMethod]
    public async Task Authenticate_KeycloakRefusesTheClient_LetsTheFailureOutRatherThanSendingABareBearer()
    {
        // Sending the request anyway would turn a rotated secret into an unexplained 401 from whatever
        // service was being called.
        await using var server = new StubHttpServer(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        var authenticator = new KeycloakServiceAuthenticator(string.Empty, server.BaseUrl, Realm, ClientId, ClientSecret);

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => AuthenticateAsync(authenticator));

        Assert.AreEqual("invalid_client", ex.Error);
    }

    [TestMethod]
    public async Task Authenticate_SendsTheConfiguredClientIdAndSecret()
    {
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":"fetched"}""");
        var authenticator = new KeycloakServiceAuthenticator(string.Empty, server.BaseUrl, Realm, ClientId, ClientSecret);

        await AuthenticateAsync(authenticator);

        var body = server.Requests.Single().Body;
        StringAssert.Contains(body, $"client_id={ClientId}");
        StringAssert.Contains(body, $"client_secret={ClientSecret}");
    }
}
