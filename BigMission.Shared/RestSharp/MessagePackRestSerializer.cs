using MessagePack;
using MessagePack.Resolvers;
using RestSharp;
using RestSharp.Serializers;

namespace BigMission.Shared.RestSharp;

/// <summary>
/// Provides MessagePack-based serialization and deserialization for REST requests and responses using the binary
/// MessagePack format.
/// </summary>
/// <remarks>This class implements both serialization and deserialization interfaces for RESTful communication,
/// enabling efficient binary encoding of payloads. It supports the "application/x-msgpack" content type and is suitable
/// for scenarios where compact, high-performance data transfer is required. The serializer encodes objects as
/// base64-encoded MessagePack binary data, and deserialization expects MessagePack-encoded byte arrays. Thread safety
/// is not guaranteed; use separate instances in multi-threaded environments.</remarks>
public class MessagePackRestSerializer : IRestSerializer, ISerializer, IDeserializer
{
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard
        .WithResolver(StandardResolver.Instance);

    /// <summary>
    /// Serializes a REST parameter value to a base64-encoded MessagePack string.
    /// </summary>
    /// <param name="bodyParameter">The parameter containing the value to serialize.</param>
    /// <returns>A base64-encoded string representation of the serialized MessagePack binary data.</returns>
    public string? Serialize(Parameter bodyParameter) => Serialize(bodyParameter.Value);

    /// <summary>
    /// Gets or sets the content type for the serialized data.
    /// </summary>
    /// <value>The content type, defaulting to <see cref="ContentType.Binary"/>.</value>
    public ContentType ContentType { get; set; } = ContentType.Binary;

    /// <summary>
    /// Gets the serializer instance for encoding objects.
    /// </summary>
    /// <value>This instance, which implements <see cref="ISerializer"/>.</value>
    public ISerializer Serializer => this;
    
    /// <summary>
    /// Gets the deserializer instance for decoding objects.
    /// </summary>
    /// <value>This instance, which implements <see cref="IDeserializer"/>.</value>
    public IDeserializer Deserializer => this;
    
    /// <summary>
    /// Gets the data format used by this serializer.
    /// </summary>
    /// <value>Always returns <see cref="DataFormat.Binary"/>.</value>
    public DataFormat DataFormat => DataFormat.Binary;
    
    /// <summary>
    /// Gets the array of content types accepted by this serializer.
    /// </summary>
    /// <value>An array containing "application/x-msgpack".</value>
    public string[] AcceptedContentTypes => ["application/x-msgpack"];
    
    /// <summary>
    /// Gets a function that determines whether a given content type is supported by this serializer.
    /// </summary>
    /// <value>A function that returns <c>true</c> if the content type ends with "msgpack" (case-insensitive); otherwise, <c>false</c>.</value>
    public SupportsContentType SupportsContentType
        => contentType => contentType.Value.EndsWith("msgpack", StringComparison.InvariantCultureIgnoreCase);

    /// <summary>
    /// Serializes an object to a base64-encoded MessagePack string.
    /// </summary>
    /// <param name="obj">The object to serialize. Can be <c>null</c>.</param>
    /// <returns>A base64-encoded string representation of the serialized MessagePack binary data.</returns>
    public string Serialize(object? obj)
    {
        var bytes = MessagePackSerializer.Serialize(obj, Options);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Deserializes a REST response containing MessagePack binary data into an object of type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize to.</typeparam>
    /// <param name="response">The REST response containing the MessagePack binary data in its property.</param>
    /// <returns>The deserialized object of type <typeparamref name="T"/>, or <c>default(T)</c> if the response contains no data.</returns>
    public T? Deserialize<T>(RestResponse response)
    {
        if (response.RawBytes == null || response.RawBytes.Length == 0)
            return default;
        return MessagePackSerializer.Deserialize<T>(response.RawBytes, Options);
    }
}
