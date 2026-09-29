using Xunit;

namespace N0.IrohNet.Tests;

public class ConnectAcceptTests
{
    [Fact]
    public async Task ConnectAndAccept_EstablishWithNegotiatedAlpn()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        byte[] alpn = IrohTestPair.AlpnBytes();
        Assert.Equal(alpn, pair.ClientConnection.NegotiatedAlpn); // client reports the requested protocol
        Assert.Equal(alpn, pair.ServerConnection.NegotiatedAlpn); // server reports the negotiated protocol
    }

    [Fact]
    public async Task ConnectAndAccept_CustomAlpn_IsNegotiated()
    {
        const string alpn = "test/2";
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync(alpn);

        byte[] alpnBytes = IrohTestPair.AlpnBytes(alpn);
        Assert.Equal(alpnBytes, pair.ClientConnection.NegotiatedAlpn);
        Assert.Equal(alpnBytes, pair.ServerConnection.NegotiatedAlpn);
    }

    [Fact]
    public async Task FourConnections_AreAcceptedConcurrently()
    {
        IrohEndpoint? server = null;
        IrohEndpoint? client = null;
        List<IrohConnection> connections = new();
        try
        {
            server = await IrohEndpoint.BindAsync(IrohTestPair.Options()).WaitAsync(TestTimeouts.Bind);
            IrohNodeAddr address = server.LocalAddr();
            client = await IrohEndpoint.BindAsync(IrohTestPair.Options()).WaitAsync(TestTimeouts.Bind);

            byte[] alpn = IrohTestPair.AlpnBytes();
            // Accept loop first, then a WhenAll of connects: both sides of each handshake must run concurrently.
            Task<IrohConnection>[] accepts = [server.AcceptAsync(), server.AcceptAsync(), server.AcceptAsync(), server.AcceptAsync()];
            Task<IrohConnection>[] connects =
            [
                client.ConnectAsync(address, alpn),
                client.ConnectAsync(address, alpn),
                client.ConnectAsync(address, alpn),
                client.ConnectAsync(address, alpn),
            ];

            IrohConnection[] accepted = await Task.WhenAll(accepts).WaitAsync(TestTimeouts.Handshake);
            IrohConnection[] connected = await Task.WhenAll(connects).WaitAsync(TestTimeouts.Handshake);
            connections.AddRange(accepted);
            connections.AddRange(connected);

            Assert.Equal(4, accepted.Length);
            Assert.Equal(4, connected.Length);
            Assert.All(accepted, connection => Assert.Equal(alpn, connection.NegotiatedAlpn));
            Assert.All(connected, connection => Assert.Equal(alpn, connection.NegotiatedAlpn));
        }
        finally
        {
            foreach (IrohConnection connection in connections)
            {
                TryDispose(connection);
            }

            if (client is not null)
            {
                await client.DisposeAsync();
            }

            if (server is not null)
            {
                await server.DisposeAsync();
            }
        }
    }

    private static void TryDispose(IrohConnection connection)
    {
        try
        {
            connection.Dispose();
        }
        catch
        {
            // Cleanup must never mask the test result.
        }
    }
}
