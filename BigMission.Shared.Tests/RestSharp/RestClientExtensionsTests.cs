using BigMission.Shared.RestSharp;
using RestSharp;

namespace BigMission.Shared.Tests.RestSharp;

/// <summary>
/// <see cref="RestClientExtensions.CreateWithMessagePack"/> is the one call every service uses to build
/// its REST client. These tests pin what that call actually configures, since a client that quietly
/// lost its Accept header would still work - by negotiating JSON - and the regression would only show
/// up as extra bytes on the wire.
/// </summary>
[TestClass]
public class RestClientExtensionsTests
{
    private static string? DefaultHeader(RestClient client, string name)
        => client.DefaultParameters.FirstOrDefault(p => p.Type == ParameterType.HttpHeader && p.Name == name)?.Value as string;

    [TestMethod]
    public void CreateWithMessagePack_AsksForMessagePackFirstAndJsonAsAFallback()
    {
        using var client = new RestClientOptions("http://127.0.0.1:1").CreateWithMessagePack();

        Assert.AreEqual("application/msgpack, application/json", DefaultHeader(client, "Accept"));
    }

    [TestMethod]
    public void CreateWithMessagePack_KeepsTheOptionsItWasGiven()
    {
        var options = new RestClientOptions("http://127.0.0.1:1/api") { Timeout = TimeSpan.FromSeconds(17) };

        using var client = options.CreateWithMessagePack();

        Assert.AreEqual(new Uri("http://127.0.0.1:1/api"), client.Options.BaseUrl);
        Assert.AreEqual(TimeSpan.FromSeconds(17), client.Options.Timeout);
    }

    [TestMethod]
    public void CreateWithMessagePack_KeepsTheAuthenticatorItWasGiven()
    {
        // Losing this would send every request unauthenticated, which surfaces as a 401 rather than as
        // anything pointing back at client construction.
        var authenticator = new BigMission.Shared.Auth.KeycloakServiceAuthenticator(
            string.Empty, "http://127.0.0.1:1", "redmist", "service-client", "s3cr3t");
        var options = new RestClientOptions("http://127.0.0.1:1") { Authenticator = authenticator };

        using var client = options.CreateWithMessagePack();

        Assert.AreSame(authenticator, client.Options.Authenticator);
    }

    [TestMethod]
    public void CreateWithMessagePack_RegistersTheMessagePackSerializerForBinaryPayloads()
    {
        using var client = new RestClientOptions("http://127.0.0.1:1").CreateWithMessagePack();

        Assert.IsInstanceOfType<MessagePackRestSerializer>(client.Serializers.GetSerializer(DataFormat.Binary));
    }

    [TestMethod]
    public void CreateWithMessagePack_TheClientAdvertisesTheMsgpackContentType()
    {
        using var client = new RestClientOptions("http://127.0.0.1:1").CreateWithMessagePack();

        CollectionAssert.Contains(client.AcceptedContentTypes, "application/x-msgpack");
    }
}
