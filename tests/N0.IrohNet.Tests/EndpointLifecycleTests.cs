using Xunit;

namespace N0.IrohNet.Tests;

public class EndpointLifecycleTests
{
    [Fact]
    public async Task BindAsync_WithHermeticOptions_Succeeds()
    {
        await using IrohEndpoint endpoint = await IrohEndpoint.BindAsync(IrohTestPair.Options()).WaitAsync(TestTimeouts.Bind);
        Assert.False(string.IsNullOrWhiteSpace(endpoint.LocalAddr().Ticket));
    }

    [Fact]
    public async Task LocalAddr_RoundTripsThroughParse()
    {
        await using IrohEndpoint endpoint = await IrohEndpoint.BindAsync(IrohTestPair.Options()).WaitAsync(TestTimeouts.Bind);
        IrohNodeAddr addr = endpoint.LocalAddr();
        IrohNodeAddr parsed = IrohNodeAddr.Parse(addr.ToString());

        Assert.Equal(addr, parsed);
        Assert.Equal(addr.Ticket, parsed.Ticket);
        Assert.True(IrohNodeAddr.TryParse(addr.ToString(), out IrohNodeAddr? reparsed));
        Assert.Equal(addr, reparsed);
    }

    [Fact]
    public async Task BindAsync_RequiresAtLeastOneNonEmptyAlpn()
    {
        // Validation is synchronous: the exception is thrown before any task is created.
        IrohEndpointOptions noAlpns = new()
        {
            RelayMode = IrohRelayMode.Disabled,
            Discovery = IrohDiscoveryConfig.None,
            Alpns = Array.Empty<byte[]>(),
        };
        await Assert.ThrowsAsync<ArgumentException>(() => IrohEndpoint.BindAsync(noAlpns));

        IrohEndpointOptions emptyAlpn = new()
        {
            RelayMode = IrohRelayMode.Disabled,
            Discovery = IrohDiscoveryConfig.None,
            Alpns = new[] { Array.Empty<byte>() },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => IrohEndpoint.BindAsync(emptyAlpn));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndDoesNotThrow()
    {
        IrohEndpoint endpoint = await IrohEndpoint.BindAsync(IrohTestPair.Options()).WaitAsync(TestTimeouts.Bind);
        await endpoint.DisposeAsync();
        await endpoint.DisposeAsync(); // second async dispose must be a no-op
        endpoint.Dispose(); // sync path must be a no-op too

        // The endpoint is closed; further use reports ObjectDisposedException instead of crashing.
        Assert.Throws<ObjectDisposedException>(() => endpoint.LocalAddr());
    }
}
