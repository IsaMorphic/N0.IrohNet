namespace N0.IrohNet;

/// <summary>Selects how the endpoint uses the n0 relay infrastructure.</summary>
public enum IrohRelayMode
{
    /// <summary>Relay usage is entirely disabled; connections are direct only.</summary>
    Disabled,
    /// <summary>The default relay map is used.</summary>
    Default,
}

/// <summary>Selects which discovery mechanisms the endpoint uses to find remote nodes.</summary>
public enum IrohDiscoveryConfig
{
    /// <summary>No discovery mechanism is used.</summary>
    None,
    /// <summary>Use the n0 DNS discovery service; requires internet access.</summary>
    Dns,
    /// <summary>Discover other endpoints on the local network via mDNS.</summary>
    Mdns,
    /// <summary>Use both DNS and mDNS discovery.</summary>
    All,
}

/// <summary>Options used to bind an <see cref="IrohEndpoint"/>.</summary>
public sealed class IrohEndpointOptions
{
    /// <summary>Gets or sets the relay usage mode; defaults to <see cref="IrohRelayMode.Default"/>.</summary>
    public IrohRelayMode RelayMode { get; init; } = IrohRelayMode.Default;

    /// <summary>Gets or sets the discovery configuration; defaults to <see cref="IrohDiscoveryConfig.None"/>.</summary>
    public IrohDiscoveryConfig Discovery { get; init; } = IrohDiscoveryConfig.None;

    /// <summary>Gets or sets the ALPN protocols the endpoint accepts and offers; at least one non-empty entry is required by <see cref="IrohEndpoint.BindAsync"/>.</summary>
    public IReadOnlyList<byte[]> Alpns { get; init; } = Array.Empty<byte[]>();

    /// <summary>Gets or sets the node's secret key; a fresh random key is generated when null.</summary>
    public IrohSecretKey? SecretKey { get; init; }
}
