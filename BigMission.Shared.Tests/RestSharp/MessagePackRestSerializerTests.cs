using BigMission.Shared.RestSharp;
using MessagePack;
using RestSharp;
using RestSharp.Serializers;

namespace BigMission.Shared.Tests.RestSharp;

/// <summary>
/// <see cref="MessagePackRestSerializer"/> is what RestSharp consults to decide whether it can read a
/// response and how to write a body. These tests pin the content-type matching - which is what routes
/// a response to this serializer at all - and the encoding on each side, including the asymmetry
/// between them.
/// </summary>
[TestClass]
public class MessagePackRestSerializerTests
{
    [MessagePackObject]
    public class Payload
    {
        [Key(0)]
        public int Id { get; set; }

        [Key(1)]
        public string? Name { get; set; }
    }

    private static RestResponse ResponseWith(byte[]? rawBytes)
        => new(new RestRequest()) { RawBytes = rawBytes };

    #region Registration surface

    [TestMethod]
    public void DataFormat_IsBinary()
    {
        Assert.AreEqual(DataFormat.Binary, new MessagePackRestSerializer().DataFormat);
    }

    [TestMethod]
    public void AcceptedContentTypes_IsMsgpack()
    {
        CollectionAssert.AreEqual(new[] { "application/x-msgpack" }, new MessagePackRestSerializer().AcceptedContentTypes);
    }

    [TestMethod]
    public void SerializerAndDeserializer_AreTheSameInstance()
    {
        var serializer = new MessagePackRestSerializer();

        Assert.AreSame(serializer, serializer.Serializer);
        Assert.AreSame(serializer, serializer.Deserializer);
    }

    #endregion

    #region Content-type matching

    [TestMethod]
    [DataRow("application/x-msgpack")]
    [DataRow("application/msgpack")]
    [DataRow("application/X-MsgPack")]
    public void SupportsContentType_MsgpackVariants_AreAccepted(string contentType)
    {
        // Servers are inconsistent about the x- prefix and about case, and the match is a suffix
        // comparison so that both spellings route here.
        Assert.IsTrue(new MessagePackRestSerializer().SupportsContentType((ContentType)contentType));
    }

    [TestMethod]
    [DataRow("application/json")]
    [DataRow("text/plain")]
    [DataRow("application/msgpack; charset=utf-8")]
    public void SupportsContentType_AnythingNotEndingInMsgpack_IsRejected(string contentType)
    {
        // Note the last case: a charset parameter moves the suffix off the end of the value, so a
        // server that adds one falls back to whichever serializer does claim the type.
        Assert.IsFalse(new MessagePackRestSerializer().SupportsContentType((ContentType)contentType));
    }

    #endregion

    #region Encoding

    [TestMethod]
    public void Serialize_ProducesBase64OfTheMessagePackBytes()
    {
        var payload = new Payload { Id = 7, Name = "car" };

        var serialized = new MessagePackRestSerializer().Serialize(payload);

        Assert.AreEqual(Convert.ToBase64String(MessagePackSerializer.Serialize(payload)), serialized);
    }

    [TestMethod]
    public void Serialize_Null_IsEncodedAsMessagePackNil()
    {
        // Cast needed: a bare null binds to the Parameter overload, which is the more specific one.
        Assert.AreEqual(Convert.ToBase64String([0xC0]), new MessagePackRestSerializer().Serialize((object?)null));
    }

    [TestMethod]
    public void Serialize_BodyParameter_UsesTheParameterValue()
    {
        var payload = new Payload { Id = 7, Name = "car" };
        var serializer = new MessagePackRestSerializer();

        Assert.AreEqual(serializer.Serialize(payload), serializer.Serialize(new BodyParameter(payload, ContentType.Binary)));
    }

    [TestMethod]
    public void Deserialize_RawMessagePackBytes_ReturnsThePayload()
    {
        var response = ResponseWith(MessagePackSerializer.Serialize(new Payload { Id = 7, Name = "car" }));

        var payload = new MessagePackRestSerializer().Deserialize<Payload>(response);

        Assert.AreEqual(7, payload!.Id);
        Assert.AreEqual("car", payload.Name);
    }

    [TestMethod]
    public void Deserialize_ResponseWithNoBody_ReturnsDefaultRatherThanThrowing()
    {
        // A 204 arrives here with nothing to read, and callers expect null rather than a MessagePack
        // parse failure.
        Assert.IsNull(new MessagePackRestSerializer().Deserialize<Payload>(ResponseWith(null)));
    }

    [TestMethod]
    public void Deserialize_ResponseWithAnEmptyBody_ReturnsDefaultRatherThanThrowing()
    {
        Assert.IsNull(new MessagePackRestSerializer().Deserialize<Payload>(ResponseWith([])));
    }

    [TestMethod]
    public void SerializeThenDeserialize_DoesNotRoundTripDirectly()
    {
        // Documented asymmetry: Serialize base64-encodes, Deserialize reads raw bytes. It works in
        // practice because these two ends never meet - the client writes bodies and a MessagePack
        // server writes responses - but anything that fed one into the other, such as a retry that
        // replayed a serialized body, would fail here rather than round trip.
        var serializer = new MessagePackRestSerializer();
        var serialized = serializer.Serialize(new Payload { Id = 7, Name = "car" });

        Assert.ThrowsExactly<MessagePackSerializationException>(
            () => serializer.Deserialize<Payload>(ResponseWith(System.Text.Encoding.UTF8.GetBytes(serialized))));
    }

    #endregion
}
