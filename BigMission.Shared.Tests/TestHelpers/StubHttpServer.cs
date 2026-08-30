using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BigMission.Shared.Tests.TestHelpers;

/// <summary>
/// A request the stub server received, captured so a test can assert on what was actually sent.
/// </summary>
internal sealed record StubRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// The reply a test wants the stub server to send back.
/// </summary>
internal sealed record StubResponse(HttpStatusCode StatusCode, string Body, string ContentType = "application/json");

/// <summary>
/// A one-connection-at-a-time HTTP/1.1 server on a loopback port, spoken over a raw
/// <see cref="TcpListener"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>KeycloakServiceToken.RequestClientToken</c> news up its own <see cref="HttpClient"/>, so there is
/// no handler to substitute; the only seam left is the URL. This stands a real server on the other end
/// of it, which is enough to drive every branch of the response handling - including the ones that only
/// happen when something other than Keycloak answers.
/// </para>
/// <para>
/// <see cref="HttpListener"/> would be the obvious choice and is deliberately not used: on Windows it
/// needs a URL ACL reservation or an elevated process for anything but a pre-registered prefix, which
/// would make these tests pass or fail depending on how the build agent was launched.
/// </para>
/// </remarks>
internal sealed class StubHttpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task acceptLoop;
    private readonly ConcurrentQueue<StubRequest> requests = new();

    /// <summary>
    /// Starts a server that answers every request with <paramref name="respond"/>.
    /// </summary>
    public StubHttpServer(Func<StubRequest, StubResponse> respond)
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(() => AcceptLoopAsync(respond, cancellation.Token));
    }

    /// <summary>Convenience for the common case of a fixed reply.</summary>
    public StubHttpServer(HttpStatusCode statusCode, string body, string contentType = "application/json")
        : this(_ => new StubResponse(statusCode, body, contentType)) { }

    /// <summary>The loopback port the server bound to.</summary>
    public int Port { get; }

    /// <summary>The value to pass as the Keycloak authUrl.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Everything the server has been asked for so far, oldest first.</summary>
    public IReadOnlyList<StubRequest> Requests => [.. requests];

    private async Task AcceptLoopAsync(Func<StubRequest, StubResponse> respond, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception)
            {
                // Shutting down, or the listener was torn out from under the accept.
                return;
            }

            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, cancellationToken);
                    if (request is null)
                    {
                        continue;
                    }

                    requests.Enqueue(request);
                    await WriteResponseAsync(stream, respond(request), cancellationToken);
                }
                catch (Exception)
                {
                    // A client that hung up mid-request is the client's problem, not the test's.
                }
            }
        }
    }

    /// <summary>
    /// Reads one request: the head up to the blank line, then exactly Content-Length bytes of body.
    /// </summary>
    /// <remarks>
    /// The body has to be drained before replying. Answering early and closing leaves unread bytes in
    /// the receive buffer, and Windows turns that into an RST that surfaces on the client as a
    /// connection-reset <see cref="HttpRequestException"/> instead of the response under test.
    /// </remarks>
    private static async Task<StubRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var received = new MemoryStream();
        int headEnd = -1;

        while (headEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            received.Write(buffer, 0, read);
            headEnd = IndexOfHeadEnd(received.GetBuffer(), (int)received.Length);
        }

        var raw = received.GetBuffer();
        var head = Encoding.ASCII.GetString(raw, 0, headEnd);
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        var bodyStart = headEnd + 4;
        var bodyLength = headers.TryGetValue("Content-Length", out var lengthHeader) && int.TryParse(lengthHeader, out var length) ? length : 0;
        var body = new MemoryStream();
        body.Write(raw, bodyStart, (int)received.Length - bodyStart);

        while (body.Length < bodyLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            body.Write(buffer, 0, read);
        }

        return new StubRequest(
            requestLine[0],
            requestLine.Length > 1 ? requestLine[1] : string.Empty,
            headers,
            Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
    }

    private static int IndexOfHeadEnd(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, StubResponse response, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {(int)response.StatusCode} {response.StatusCode}\r\n")
            .Append($"Content-Type: {response.ContentType}\r\n")
            .Append($"Content-Length: {body.Length}\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        listener.Stop();
        try
        {
            await acceptLoop;
        }
        catch (Exception)
        {
            // The accept loop is expected to end on cancellation.
        }

        cancellation.Dispose();
    }
}
