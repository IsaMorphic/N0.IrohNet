using Xunit;

namespace N0.IrohNet.Tests;

public class CleanupTests
{
    [Fact]
    public async Task MidUsageException_AwaitUsingDisposesEverything_ProcessStaysHealthy()
    {
        // The fixture and the stream are all held by `await using`; the throw below must still run
        // every disposal path without crashing or corrupting the native state.
        static async Task Explode(ConnectedPair pair)
        {
            await using ConnectedPair owned = pair;
            Stream stream = await owned.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake);
            await using Stream kept = stream;
            await kept.WriteAsync(new byte[] { 0x2B }).AsTask().WaitAsync(TestTimeouts.Io);
            throw new InvalidOperationException("boom: application failure mid-session");
        }

        ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Explode(pair));
        Assert.Equal("boom: application failure mid-session", thrown.Message);

        // The process must remain fully usable afterwards: a fresh pair completes a stream round trip.
        await using ConnectedPair fresh = await IrohTestPair.CreateConnectedAsync();
        byte[] echoed = await TestPayload.RoundTripAsync(fresh, TestPayload.Pattern(256, 0x99));
        Assert.Equal(TestPayload.Pattern(256, 0x99), echoed);
    }

    [Fact]
    public async Task EndpointDispose_WithLiveConnectionAndStream_CompletesCleanly()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        Task<IrohStream> acceptTask = pair.ServerConnection.AcceptStreamAsync().WaitAsync(TestTimeouts.Handshake);
        Stream clientStream = await pair.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake);
        await clientStream.WriteAsync(new byte[] { 0x11 }).AsTask().WaitAsync(TestTimeouts.Io); // first write immediately after open
        await using IrohStream serverStream = await acceptTask;

        // Disposing the endpoint while a connection and stream are still open must drain them gracefully.
        await pair.Server.DisposeAsync().AsTask().WaitAsync(TestTimeouts.Teardown);
        await pair.Client.DisposeAsync().AsTask().WaitAsync(TestTimeouts.Teardown);

        // Re-disposing everything through the fixture must remain a no-op.
        await pair.DisposeAsync();
    }
}
