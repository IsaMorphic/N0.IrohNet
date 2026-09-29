using System.Text;

namespace N0.IrohNet.Tests;

/// <summary>Shared bounds so a contract violation fails a test with a clear timeout instead of hanging the run.</summary>
internal static class TestTimeouts
{
    public static readonly TimeSpan Bind = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Handshake = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Io = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Teardown = TimeSpan.FromSeconds(25);
}

/// <summary>
/// Connected-pair fixture: two hermetic endpoints (relay disabled, no discovery) on the same machine,
/// with one fully established connection between them. Disposal is ordered and bounded so a failing
/// test cannot poison the next one.
/// </summary>
public sealed class ConnectedPair : IAsyncDisposable
{
    public ConnectedPair((IrohEndpoint server, IrohEndpoint client, IrohConnection serverConn, IrohConnection clientConn) pair)
    {
        Server = pair.server;
        Client = pair.client;
        ServerConnection = pair.serverConn;
        ClientConnection = pair.clientConn;
    }

    public IrohEndpoint Server { get; }

    public IrohEndpoint Client { get; }

    public IrohConnection ServerConnection { get; }

    public IrohConnection ClientConnection { get; }

    public async ValueTask DisposeAsync()
    {
        // Bounded so a stuck teardown surfaces as a test failure rather than an infinite run.
        await Task.Run(DisposeCore).WaitAsync(TestTimeouts.Teardown).ConfigureAwait(false);
    }

    private void DisposeCore()
    {
        Quiet(() => ClientConnection.Dispose());
        Quiet(() => ServerConnection.Dispose());
        Quiet(() => Client.Dispose());
        Quiet(() => Server.Dispose());
    }

    private static void Quiet(Action dispose)
    {
        try
        {
            dispose();
        }
        catch
        {
            // Cleanup must never mask the actual test result; the operations above are idempotent.
        }
    }
}

/// <summary>Factory for hermetic endpoints and connected pairs, reused across the suite.</summary>
public static class IrohTestPair
{
    public const string DefaultAlpn = "test/1";

    public static byte[] AlpnBytes(string alpn = DefaultAlpn) => Encoding.UTF8.GetBytes(alpn);

    public static IrohEndpointOptions Options(string alpn = DefaultAlpn) => new()
    {
        RelayMode = IrohRelayMode.Disabled,
        Discovery = IrohDiscoveryConfig.None,
        Alpns = new[] { AlpnBytes(alpn) },
    };

    /// <summary>Creates a connected pair: the server accepts while the client connects over loopback.</summary>
    public static async Task<(IrohEndpoint server, IrohEndpoint client, IrohConnection serverConn, IrohConnection clientConn)> CreateAsync(string alpn = DefaultAlpn)
    {
        byte[] alpnBytes = AlpnBytes(alpn);
        IrohEndpoint? server = null;
        IrohEndpoint? client = null;
        try
        {
            server = await IrohEndpoint.BindAsync(Options(alpn)).WaitAsync(TestTimeouts.Bind).ConfigureAwait(false);
            IrohNodeAddr address = server.LocalAddr();

            // The accept must be started before (or concurrently with) the connect; both sides of the
            // handshake block until they meet.
            Task<IrohConnection> acceptTask = server.AcceptAsync().WaitAsync(TestTimeouts.Handshake);
            client = await IrohEndpoint.BindAsync(Options(alpn)).WaitAsync(TestTimeouts.Bind).ConfigureAwait(false);
            Task<IrohConnection> connectTask = client.ConnectAsync(address, alpnBytes).WaitAsync(TestTimeouts.Handshake);
            await Task.WhenAll(acceptTask, connectTask).WaitAsync(TestTimeouts.Handshake).ConfigureAwait(false);

            return (server, client, acceptTask.Result, connectTask.Result);
        }
        catch
        {
            if (client is not null)
            {
                await TryDisposeAsync(client).ConfigureAwait(false);
            }

            if (server is not null)
            {
                await TryDisposeAsync(server).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Same as <see cref="CreateAsync"/> but wrapped in a disposable fixture for <c>await using</c>.</summary>
    public static async Task<ConnectedPair> CreateConnectedAsync(string alpn = DefaultAlpn) =>
        new(await CreateAsync(alpn).ConfigureAwait(false));

    private static async Task TryDisposeAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Setup failed; best-effort cleanup so the next test starts from a clean slate.
        }
    }
}

/// <summary>Stream and payload helpers shared by the connection tests.</summary>
public static class TestPayload
{
    /// <summary>Deterministic non-uniform filler; different seeds produce different content.</summary>
    public static byte[] Pattern(int length, byte seed)
    {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = unchecked((byte)(i * 31 + seed));
        }

        return result;
    }

    /// <summary>Reads exactly <paramref name="buffer.Length"/> bytes, failing instead of hanging if the peer stalls.</summary>
    public static async Task ReadExactlyAsync(this Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(offset)).AsTask().WaitAsync(TestTimeouts.Io).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new InvalidOperationException($"The stream ended after {offset} of {buffer.Length} expected bytes.");
            }

            offset += bytesRead;
        }
    }

    /// <summary>One stream round trip over a connected pair; writes the first message immediately after open (the FFI deadlocks if both sides wait).</summary>
    public static async Task<byte[]> RoundTripAsync(ConnectedPair pair, byte[] payload)
    {
        Task<IrohStream> acceptTask = pair.ServerConnection.AcceptStreamAsync().WaitAsync(TestTimeouts.Handshake);
        await using Stream clientStream = await pair.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake).ConfigureAwait(false);
        await clientStream.WriteAsync(payload).AsTask().WaitAsync(TestTimeouts.Io).ConfigureAwait(false);

        await using IrohStream serverStream = await acceptTask.ConfigureAwait(false);
        byte[] received = new byte[payload.Length];
        await serverStream.ReadExactlyAsync(received).ConfigureAwait(false);
        return received;
    }
}
