using Xunit;

namespace N0.IrohNet.Tests;

public class DatagramTests
{
    [Fact]
    public async Task Datagram_ThreeSizes_ArriveWithPayloadEquality()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        nuint maxDatagramSize = pair.ClientConnection.MaxDatagramSize;
        Assert.True(maxDatagramSize > 64, $"Expected a usable datagram MTU on loopback, got {maxDatagramSize}.");

        byte[] oneByte = TestPayload.Pattern(1, 0x11);
        byte[] sixtyFour = TestPayload.Pattern(64, 0x22);
        byte[] maxSized = TestPayload.Pattern(checked((int)maxDatagramSize), 0x33);

        await pair.ClientConnection.SendDatagramAsync(oneByte).WaitAsync(TestTimeouts.Io);
        await pair.ClientConnection.SendDatagramAsync(sixtyFour).WaitAsync(TestTimeouts.Io);
        await pair.ClientConnection.SendDatagramAsync(maxSized).WaitAsync(TestTimeouts.Io);

        // Sizes are distinct, so compare order-independently by length and content.
        byte[][] received =
        [
            await pair.ServerConnection.ReceiveDatagramAsync().WaitAsync(TestTimeouts.Io),
            await pair.ServerConnection.ReceiveDatagramAsync().WaitAsync(TestTimeouts.Io),
            await pair.ServerConnection.ReceiveDatagramAsync().WaitAsync(TestTimeouts.Io),
        ];
        byte[][] expected = [oneByte, sixtyFour, maxSized];

        Assert.Equal(
            expected.OrderBy(payload => payload.Length).Select(payload => Convert.ToHexString(payload)),
            received.OrderBy(payload => payload.Length).Select(payload => Convert.ToHexString(payload)));
    }

    [Fact]
    public async Task Datagram_OversizePayload_ThrowsIrohException()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        nuint maxDatagramSize = pair.ClientConnection.MaxDatagramSize;
        Assert.True(maxDatagramSize > 0, $"Expected datagram support on loopback, got max size {maxDatagramSize}.");

        byte[] tooBig = new byte[checked((int)maxDatagramSize) + 1];
        IrohException exception = await Assert.ThrowsAsync<IrohException>(
            () => pair.ClientConnection.SendDatagramAsync(tooBig)).WaitAsync(TestTimeouts.Io);
        Assert.Equal(IrohErrorCode.SendError, exception.ErrorCode);
    }

    [Fact]
    public async Task Datagram_InterleavedWithStreamTraffic_BothArriveIntact()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        // Stream setup with the write-first rule, then interleave datagrams and stream writes.
        Task<IrohStream> acceptTask = pair.ServerConnection.AcceptStreamAsync().WaitAsync(TestTimeouts.Handshake);
        await using Stream clientStream = await pair.ClientConnection.OpenStreamAsync().WaitAsync(TestTimeouts.Handshake);

        byte[] streamFirst = TestPayload.Pattern(1024, 0x44);
        await clientStream.WriteAsync(streamFirst).AsTask().WaitAsync(TestTimeouts.Io);
        await using IrohStream serverStream = await acceptTask;

        byte[] datagram1 = TestPayload.Pattern(1, 0x55);
        byte[] datagram2 = TestPayload.Pattern(512, 0x66);
        byte[] streamSecond = TestPayload.Pattern(8 * 1024, 0x77);

        await pair.ClientConnection.SendDatagramAsync(datagram1).WaitAsync(TestTimeouts.Io);
        await clientStream.WriteAsync(streamSecond).AsTask().WaitAsync(TestTimeouts.Io);
        await pair.ClientConnection.SendDatagramAsync(datagram2).WaitAsync(TestTimeouts.Io);

        // The datagram lane delivers both payloads intact...
        byte[][] received =
        [
            await pair.ServerConnection.ReceiveDatagramAsync().WaitAsync(TestTimeouts.Io),
            await pair.ServerConnection.ReceiveDatagramAsync().WaitAsync(TestTimeouts.Io),
        ];
        byte[][] expected = [datagram1, datagram2];
        Assert.Equal(
            expected.OrderBy(payload => payload.Length).Select(payload => Convert.ToHexString(payload)),
            received.OrderBy(payload => payload.Length).Select(payload => Convert.ToHexString(payload)));

        // ...while the stream lane preserves its own in-order payload stream.
        byte[] receivedFirst = new byte[streamFirst.Length];
        byte[] receivedSecond = new byte[streamSecond.Length];
        await serverStream.ReadExactlyAsync(receivedFirst);
        await serverStream.ReadExactlyAsync(receivedSecond);
        Assert.Equal(streamFirst, receivedFirst);
        Assert.Equal(streamSecond, receivedSecond);
    }
}
