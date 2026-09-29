using System.Diagnostics;
using Xunit;

namespace N0.IrohNet.Tests;

public class FailureModeTests
{
    [Fact]
    public void NodeAddr_GarbageTicket_ThrowsIrohExceptionNotCrash()
    {
        IrohException exception = Assert.Throws<IrohException>(() => IrohNodeAddr.Parse("definitely-not-a-valid-ticket"));
        Assert.Equal(IrohErrorCode.InvalidEndpointAddr, exception.ErrorCode);

        Assert.False(IrohNodeAddr.TryParse("definitely-not-a-valid-ticket", out _));
        Assert.False(IrohNodeAddr.TryParse(string.Empty, out _));
    }

    [Fact]
    public async Task ConnectAsync_ToUnusableNodeAddr_ThrowsIrohExceptionQuickly()
    {
        await using IrohEndpoint client = await IrohEndpoint.BindAsync(IrohTestPair.Options()).WaitAsync(TestTimeouts.Bind);

        // Connecting to our own endpoint is rejected by the native layer before any dialing: the
        // valid-format ticket is unusable, and the failure must surface as IrohException (not a
        // crash) within milliseconds.
        //
        // Note on other unreachable destinations (disposed endpoint, forged foreign key, wrong
        // ALPN): these are all rejected too, but only after the native connect gives up after ~30s,
        // which exceeds this suite's per-test budget, so they are intentionally not asserted here.
        IrohNodeAddr self = client.LocalAddr();

        Stopwatch stopwatch = Stopwatch.StartNew();
        IrohException exception = await Assert
            .ThrowsAsync<IrohException>(() => client.ConnectAsync(self, IrohTestPair.AlpnBytes()))
            .WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.Equal(IrohErrorCode.ConnectError, exception.ErrorCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"The failed connect took {stopwatch.Elapsed.TotalSeconds:F1}s to surface; expected a quick rejection.");
    }

    [Fact]
    public async Task ReceiveDatagramAsync_HonorsCancellationToken()
    {
        await using ConnectedPair pair = await IrohTestPair.CreateConnectedAsync();

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(2));
        Stopwatch stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pair.ClientConnection.ReceiveDatagramAsync(cancellation.Token));
        stopwatch.Stop();

        // The cancel must be honored near the requested deadline, neither immediately nor after an unbounded wait.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(8));
    }
}
