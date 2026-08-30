using System.Net;
using System.Text.Json;
using BigMission.Shared.Auth;
using BigMission.Shared.Tests.TestHelpers;
using Microsoft.IdentityModel.Tokens;

namespace BigMission.Shared.Tests.Auth;

/// <summary>
/// <see cref="KeycloakServiceToken"/> is the only thing standing between a service and a 401: it swaps
/// a client secret for a bearer token, and decides how loudly to complain when that fails. These tests
/// pin the request it forms on the wire, the rules for deciding a response did or did not carry a
/// token, and what ends up in the exception - which matters because at least one consumer puts the
/// message in front of an end user while the secret must never leave the process.
/// </summary>
[TestClass]
public class KeycloakServiceTokenTests
{
    private const string Realm = "redmist";
    private const string ClientName = "service-client";
    private const string ClientSecret = "s3cr3t";

    private static Task<string> RequestAsync(StubHttpServer server, string clientName = ClientName, string clientSecret = ClientSecret)
        => KeycloakServiceToken.RequestClientToken(server.BaseUrl, Realm, clientName, clientSecret);

    #region The request that goes out

    [TestMethod]
    public async Task RequestClientToken_PostsClientCredentialsGrantToTheRealmTokenEndpoint()
    {
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":"abc"}""");

        await RequestAsync(server);

        var request = server.Requests.Single();
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual($"/realms/{Realm}/protocol/openid-connect/token", request.Path);
        StringAssert.StartsWith(request.Headers["Content-Type"], "application/x-www-form-urlencoded");
        Assert.AreEqual($"grant_type=client_credentials&client_id={ClientName}&client_secret={ClientSecret}", request.Body);
    }

    [TestMethod]
    public async Task RequestClientToken_SecretContainingFormReservedCharacters_IsEscapedInTheBody()
    {
        // A secret with +, & or = would otherwise be mis-decoded server side and come back as
        // invalid_client, sending whoever reads the log looking for a rotated secret that is fine.
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":"abc"}""");

        await RequestAsync(server, clientSecret: "a+b&c=d%e");

        Assert.AreEqual(
            $"grant_type=client_credentials&client_id={ClientName}&client_secret=a%2Bb%26c%3Dd%25e",
            server.Requests.Single().Body);
    }

    #endregion

    #region Deciding a token arrived

    [TestMethod]
    public async Task RequestClientToken_ResponseCarriesAToken_ReturnsIt()
    {
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":"header.payload.signature","expires_in":300}""");

        Assert.AreEqual("header.payload.signature", await RequestAsync(server));
    }

    [TestMethod]
    public async Task RequestClientToken_TokenArrivesUnderANonSuccessStatus_IsStillReturned()
    {
        // Reading the content rather than the status means a proxy that rewrites a status cannot throw
        // away a token that genuinely arrived.
        await using var server = new StubHttpServer(HttpStatusCode.Accepted, """{"access_token":"abc"}""");

        Assert.AreEqual("abc", await RequestAsync(server));
    }

    [TestMethod]
    public async Task RequestClientToken_EmptyAccessToken_IsTreatedAsNoToken()
    {
        // An empty token would go out as a bare "Bearer " header and fail somewhere less obvious.
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":""}""");

        await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));
    }

    [TestMethod]
    public async Task RequestClientToken_NonStringAccessToken_IsTreatedAsNoToken()
    {
        await using var server = new StubHttpServer(HttpStatusCode.OK, """{"access_token":12345}""");

        await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));
    }

    [TestMethod]
    public async Task RequestClientToken_JsonArrayResponse_IsTreatedAsNoToken()
    {
        // Valid JSON, wrong shape: parses without throwing, so the object check is what rejects it.
        await using var server = new StubHttpServer(HttpStatusCode.OK, """["access_token"]""");

        await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));
    }

    #endregion

    #region What the failure says

    [TestMethod]
    public async Task RequestClientToken_KeycloakRejectsTheClient_ReportsItsErrorAndDescription()
    {
        await using var server = new StubHttpServer(
            HttpStatusCode.Unauthorized,
            """{"error":"invalid_client","error_description":"Invalid client or Invalid client credentials"}""");

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));

        Assert.AreEqual(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.AreEqual("invalid_client", ex.Error);
        Assert.AreEqual("Invalid client or Invalid client credentials", ex.ErrorDescription);
        StringAssert.Contains(ex.Message, "invalid_client: Invalid client or Invalid client credentials");
        StringAssert.Contains(ex.Message, ClientName);
        StringAssert.Contains(ex.Message, Realm);
        StringAssert.Contains(ex.Message, "401 Unauthorized");
    }

    [TestMethod]
    public async Task RequestClientToken_KeycloakSendsOnlyAnErrorCode_ReportsThatAlone()
    {
        await using var server = new StubHttpServer(HttpStatusCode.BadRequest, """{"error":"unauthorized_client"}""");

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));

        Assert.AreEqual("unauthorized_client", ex.Error);
        Assert.IsNull(ex.ErrorDescription);
        // No dangling separator from the absent half.
        StringAssert.Contains(ex.Message, "unauthorized_client.");
    }

    [TestMethod]
    public async Task RequestClientToken_SomethingOtherThanKeycloakAnswers_SaysSoRatherThanReportingAParseError()
    {
        await using var server = new StubHttpServer(HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>", "text/html");

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));

        Assert.AreEqual(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.IsNull(ex.Error);
        StringAssert.Contains(ex.Message, "the response came from something other than Keycloak");
    }

    [TestMethod]
    public async Task RequestClientToken_EmptyResponseBody_SaysTheResponseWasEmpty()
    {
        await using var server = new StubHttpServer(HttpStatusCode.ServiceUnavailable, string.Empty, "text/plain");

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));

        StringAssert.Contains(ex.Message, "the response was empty");
        Assert.IsNull(ex.ResponseBody);
    }

    [TestMethod]
    public async Task RequestClientToken_WhitespaceOnlyResponseBody_CountsAsEmpty()
    {
        await using var server = new StubHttpServer(HttpStatusCode.ServiceUnavailable, "   \r\n  ", "text/plain");

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));

        StringAssert.Contains(ex.Message, "the response was empty");
        Assert.IsNull(ex.ResponseBody);
    }

    #endregion

    #region Keeping the secret and the error page out of the message

    [TestMethod]
    public async Task RequestClientToken_OnFailure_TheClientSecretAppearsNowhereInTheException()
    {
        const string secret = "correct-horse-battery-staple";
        await using var server = new StubHttpServer(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server, clientSecret: secret));

        Assert.IsFalse(ex.ToString().Contains(secret), "The client secret must never reach an exception message, a log, or a crash report.");
    }

    [TestMethod]
    public async Task RequestClientToken_LongErrorPage_IsCappedOnTheExceptionAndKeptOffTheMessage()
    {
        var page = new string('x', 1000);
        await using var server = new StubHttpServer(HttpStatusCode.BadGateway, page, "text/html");

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));

        Assert.AreEqual($"{new string('x', 200)}...", ex.ResponseBody);
        Assert.IsFalse(ex.Message.Contains('x'), "The body belongs on the exception, not on a message a consumer may show to a user.");
    }

    [TestMethod]
    public async Task RequestClientToken_ShortErrorPage_IsCarriedWholeAndTrimmed()
    {
        await using var server = new StubHttpServer(HttpStatusCode.BadGateway, "  upstream connect error  ", "text/plain");

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));

        Assert.AreEqual("upstream connect error", ex.ResponseBody);
    }

    [TestMethod]
    public async Task RequestClientToken_KeycloakTokenException_IsNotAnHttpRequestException()
    {
        // Consumers back off and throttle HttpRequestException as transient transport trouble. A
        // rotated secret or a disabled client will not come good on its own and has to be seen every
        // time, so the two must stay distinguishable.
        await using var server = new StubHttpServer(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");

        var ex = await Assert.ThrowsExactlyAsync<KeycloakTokenException>(() => RequestAsync(server));

        Assert.IsNotInstanceOfType<HttpRequestException>(ex);
    }

    [TestMethod]
    public async Task RequestClientToken_KeycloakUnreachable_StillSurfacesAsHttpRequestException()
    {
        // A genuine transport failure keeps its usual type, so callers that do want to back off can.
        // Port 1 is reserved and privileged, so unlike a port briefly opened and closed to find a free
        // number, no stub server in a test running alongside this one can end up answering here.
        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => KeycloakServiceToken.RequestClientToken("http://127.0.0.1:1", Realm, ClientName, ClientSecret));
    }

    #endregion

    #region Token expiry

    [TestMethod]
    public void GetTokenExpirationTime_ReturnsTheExpClaimAsUnixSeconds()
    {
        var expires = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.AreEqual(new DateTimeOffset(expires).ToUnixTimeSeconds(), KeycloakServiceToken.GetTokenExpirationTime(TestJwt.WithExpiry(expires)));
    }

    [TestMethod]
    public void GetTokenExpirationTime_TokenWithoutAnExpClaim_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => KeycloakServiceToken.GetTokenExpirationTime(TestJwt.WithoutExpiry()));
    }

    [TestMethod]
    public void GetTokenExpirationTime_NotAJwt_Throws()
    {
        // The authenticator relies on this throwing rather than returning a bogus expiry, since it
        // treats any failure to read the token as a reason to fetch a new one.
        Assert.ThrowsExactly<SecurityTokenMalformedException>(() => KeycloakServiceToken.GetTokenExpirationTime("not-a-token"));
    }

    [TestMethod]
    public void CheckTokenIsValid_TokenExpiringInTheFuture_IsValid()
    {
        Assert.IsTrue(KeycloakServiceToken.CheckTokenIsValid(TestJwt.ExpiringIn(TimeSpan.FromHours(1))));
    }

    [TestMethod]
    public void CheckTokenIsValid_ExpiredToken_IsNotValid()
    {
        Assert.IsFalse(KeycloakServiceToken.CheckTokenIsValid(TestJwt.ExpiringIn(TimeSpan.FromHours(-1))));
    }

    [TestMethod]
    public void CheckTokenIsValid_ComparesInUtcRatherThanLocalTime()
    {
        // A machine set to a non-UTC zone would otherwise judge every token off by its offset - which
        // on a UTC build agent is the one thing that stays invisible.
        var expires = DateTime.UtcNow.AddMinutes(30);
        var token = TestJwt.WithExpiry(expires);

        Assert.IsTrue(KeycloakServiceToken.CheckTokenIsValid(token));
        Assert.IsFalse(KeycloakServiceToken.CheckTokenIsValid(TestJwt.WithExpiry(DateTime.UtcNow.AddMinutes(-30))));
    }

    [TestMethod]
    public void CheckTokenIsValid_TokenIsSecondsFromExpiring_IsStillReportedValid()
    {
        // Documented gap: there is no clock skew or refresh margin, so a token this close to expiry is
        // handed out and may be rejected by the time the request lands.
        Assert.IsTrue(KeycloakServiceToken.CheckTokenIsValid(TestJwt.ExpiringIn(TimeSpan.FromSeconds(5))));
    }

    #endregion

    #region Round trip through a realistic Keycloak reply

    [TestMethod]
    public async Task RequestClientToken_TokenFromAKeycloakShapedResponse_IsUsableForTheExpiryCheck()
    {
        var accessToken = TestJwt.ExpiringIn(TimeSpan.FromMinutes(5));
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["expires_in"] = 300,
            ["token_type"] = "Bearer",
            ["scope"] = "profile email",
        });
        await using var server = new StubHttpServer(HttpStatusCode.OK, body);

        var token = await RequestAsync(server);

        Assert.AreEqual(accessToken, token);
        Assert.IsTrue(KeycloakServiceToken.CheckTokenIsValid(token));
    }

    #endregion
}
