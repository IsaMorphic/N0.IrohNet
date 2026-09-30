using Xunit;

namespace N0.IrohNet.Tests;

public class StreamTests
{
    // Contract rule: the client must WRITE the first message immediately after opening the stream.
    // Waiting for the peer's accept before writing deadlocks the FFI (both sides would wait forever).

    [Fact]
    public async Task Stream_ThreeMessages_ArriveInOrder()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        Task<IrohStream> acceptTask = pair.ServerConnection.AcceptStreamAsync().WaitAsync(TestTimeouts.Handshake);
        await using Stream clientStream = await pair.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake);

        byte[] oneByte = TestPayload.Pattern(1, 0x01);
        byte[] oneKib = TestPayload.Pattern(1024, 0x22);
        byte[] kib128 = TestPayload.Pattern(128 * 1024, 0x53);

        // First write goes out immediately after open, before the accept is awaited.
        await clientStream.WriteAsync(oneByte).AsTask().WaitAsync(TestTimeouts.Io);
        await using IrohStream serverStream = await acceptTask;
        await clientStream.WriteAsync(oneKib).AsTask().WaitAsync(TestTimeouts.Io);
        await clientStream.WriteAsync(kib128).AsTask().WaitAsync(TestTimeouts.Io);

        byte[] received = new byte[oneByte.Length + oneKib.Length + kib128.Length];
        await serverStream.ReadExactlyAsync(received);

        Assert.Equal(oneByte, received.AsSpan(0, oneByte.Length).ToArray());
        Assert.Equal(oneKib, received.AsSpan(oneByte.Length, oneKib.Length).ToArray());
        Assert.Equal(kib128, received.AsSpan(oneByte.Length + oneKib.Length).ToArray());
    }

    [Fact]
    public async Task Stream_ZeroLengthWrite_IsNoOp()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        Task<IrohStream> acceptTask = pair.ServerConnection.AcceptStreamAsync().WaitAsync(TestTimeouts.Handshake);
        await using Stream clientStream = await pair.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake);

        byte[] first = [0x5A];
        await clientStream.WriteAsync(first).AsTask().WaitAsync(TestTimeouts.Io); // first write immediately after open
        await using IrohStream serverStream = await acceptTask;

        for (int i = 0; i < 5; i++)
        {
            await clientStream.WriteAsync(ReadOnlyMemory<byte>.Empty).AsTask().WaitAsync(TestTimeouts.Io);
        }

        byte[] second = [0xA1];
        await clientStream.WriteAsync(second).AsTask().WaitAsync(TestTimeouts.Io);

        // Only the two real bytes may arrive: the zero-length writes consumed nothing and corrupted nothing.
        byte[] received = new byte[first.Length + second.Length];
        await serverStream.ReadExactlyAsync(received);
        Assert.Equal(new byte[] { 0x5A, 0xA1 }, received);
    }

    [Fact]
    public async Task Stream_Dispose_EndsRemoteReadWithZero()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        Task<IrohStream> acceptTask = pair.ServerConnection.AcceptStreamAsync().WaitAsync(TestTimeouts.Handshake);
        Stream clientStream = await pair.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake);

        byte[] payload = TestPayload.Pattern(64, 0x0F);
        await clientStream.WriteAsync(payload).AsTask().WaitAsync(TestTimeouts.Io); // first write immediately after open
        await using IrohStream serverStream = await acceptTask;

        byte[] received = new byte[payload.Length];
        await serverStream.ReadExactlyAsync(received);
        Assert.Equal(payload, received);

        // Disposal finishes the send side (graceful FIN); the peer's read must then return 0, not hang.
        await clientStream.DisposeAsync();

        int endOfStream = await serverStream.ReadAsync(new byte[32]).AsTask().WaitAsync(TestTimeouts.Io);
        Assert.Equal(0, endOfStream);
    }

    [Fact]
    public async Task Stream_EchoesBackOverSameStream()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        Task<IrohStream> acceptTask = pair.ServerConnection.AcceptStreamAsync().WaitAsync(TestTimeouts.Handshake);
        await using Stream clientStream = await pair.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake);

        byte[] payload = TestPayload.Pattern(32 * 1024, 0x77);
        await clientStream.WriteAsync(payload).AsTask().WaitAsync(TestTimeouts.Io); // first write immediately after open
        await using IrohStream serverStream = await acceptTask;

        byte[] inbound = new byte[payload.Length];
        await serverStream.ReadExactlyAsync(inbound);
        Assert.Equal(payload, inbound);
        await serverStream.WriteAsync(inbound).AsTask().WaitAsync(TestTimeouts.Io);

        byte[] echoed = new byte[payload.Length];
        await clientStream.ReadExactlyAsync(echoed);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Stream_DisposeDuringPendingRead_IsSafeAndBounded()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        // The peer never writes, so this read stays pending inside the native call
        // (idle reads sit in recv_stream_read_timeout for the full 250 ms slice).
        Stream clientStream = await pair.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake);
        Task<int> pendingRead = clientStream.ReadAsync(new byte[16]).AsTask();

        // Let the read reach and block inside the native slice.
        await Task.Delay(100);

        // Disposing while the native read borrows the recv container must neither crash
        // (use-after-free) nor hang: the dispose completes within the bounded wait and the
        // pending read surfaces a managed ObjectDisposedException on its next slice.
        Task disposeTask = Task.Run(clientStream.Dispose);
        await disposeTask.WaitAsync(TestTimeouts.Teardown);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pendingRead).WaitAsync(TestTimeouts.Io);
    }

    [Fact]
    public async Task Stream_DisposeDuringBlockedWrite_IsSafeAndBounded()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        // The peer never reads, so a large write eventually blocks in the native call on
        // QUIC flow control; the write timeout keeps the block bounded.
        Stream clientStream = await pair.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake);
        clientStream.WriteTimeout = 2000;
        Task writeTask = clientStream.WriteAsync(TestPayload.Pattern(16 * 1024 * 1024, 0x99)).AsTask();

        await Task.Delay(100);

        Task disposeTask = Task.Run(clientStream.Dispose);
        await disposeTask.WaitAsync(TestTimeouts.Teardown);

        // The blocked write ends in the managed TimeoutException (or ObjectDisposedException when
        // the dispose won the race); a full completion is equally acceptable. All that must hold:
        // no crash, and neither operation outlives the bounded waits.
        try
        {
            await writeTask.WaitAsync(TestTimeouts.Io);
        }
        catch (Exception exception) when (exception is TimeoutException or ObjectDisposedException)
        {
        }
    }
}
