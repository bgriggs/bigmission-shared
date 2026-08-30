using BigMission.Shared.SignalR;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace BigMission.Shared.Tests.SignalR;

/// <summary>
/// <see cref="HubConnectionBuilderExtensions.TryAddMessagePack"/> picks the hub protocol, choosing JSON
/// on iOS because MessagePack's reflection-based parameter serialization does not survive AOT. These
/// tests cover the non-iOS branch, which is what every build agent and every server-side consumer runs;
/// the iOS branch is selected by <c>RuntimeInformation.IsOSPlatform</c> and cannot be reached from here.
/// </summary>
[TestClass]
public class HubConnectionBuilderExtensionsTests
{
    private static ServiceProvider BuildProtocolServices()
    {
        var builder = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hub");
        builder.TryAddMessagePack();
        return builder.Services.BuildServiceProvider();
    }

    [TestMethod]
    public void TryAddMessagePack_ReturnsTheSameBuilderSoItCanBeChained()
    {
        var builder = new HubConnectionBuilder();

        Assert.AreSame(builder, builder.TryAddMessagePack());
    }

    [TestMethod]
    public void TryAddMessagePack_OnANonIosPlatform_RegistersTheMessagePackProtocol()
    {
        using var services = BuildProtocolServices();

        Assert.IsTrue(services.GetServices<IHubProtocol>().Any(p => p.Name == "messagepack"));
    }

    [TestMethod]
    public void TryAddMessagePack_MessagePackWinsOverTheBuildersDefaultJsonProtocol()
    {
        // HubConnectionBuilder registers JSON in its own constructor, and the connection resolves a
        // single IHubProtocol. If the registration order ever changed, the client would quietly go on
        // speaking JSON to a MessagePack hub.
        using var services = BuildProtocolServices();

        Assert.AreEqual("messagepack", services.GetRequiredService<IHubProtocol>().Name);
    }

    [TestMethod]
    public void TryAddMessagePack_TheRegisteredProtocolIsBinary()
    {
        using var services = BuildProtocolServices();

        Assert.AreEqual(TransferFormat.Binary, services.GetRequiredService<IHubProtocol>().TransferFormat);
    }
}
