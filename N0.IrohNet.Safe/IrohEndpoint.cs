namespace N0.IrohNet;

/// <summary>An iroh endpoint that can connect to and accept connections from remote nodes.</summary>
public sealed class IrohEndpoint : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(3);

    private readonly SafeEndpointHandle _handle;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly object _connectionsGate = new();
    private readonly List<IrohConnection> _connections = new();
    private volatile bool _closed;
    private int _disposed;

    private IrohEndpoint(SafeEndpointHandle handle) => _handle = handle;

    /// <summary>Binds a new endpoint with the given options; blocks on a worker thread until the bind completes.</summary>
    public static Task<IrohEndpoint> BindAsync(IrohEndpointOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Alpns.Count == 0)
        {
            throw new ArgumentException("At least one ALPN protocol must be specified.", nameof(options));
        }

        for (int i = 0; i < options.Alpns.Count; i++)
        {
            if (options.Alpns[i] is not { Length: > 0 })
            {
                throw new ArgumentException("ALPN protocol entries must be non-empty byte arrays.", nameof(options));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => BindCore(options), cancellationToken);
    }

    private static IrohEndpoint BindCore(IrohEndpointOptions options)
    {
        EndpointConfig config = iroh.endpoint_config_default();
        bool configFreed = false;
        try
        {
            config.relay_mode = options.RelayMode == IrohRelayMode.Disabled
                ? RelayMode.RELAY_MODE_DISABLED
                : RelayMode.RELAY_MODE_DEFAULT;
            config.discovery_cfg = options.Discovery switch
            {
                IrohDiscoveryConfig.Dns => DiscoveryConfig.DISCOVERY_CONFIG_D_N_S,
                IrohDiscoveryConfig.Mdns => DiscoveryConfig.DISCOVERY_CONFIG_MDNS,
                IrohDiscoveryConfig.All => DiscoveryConfig.DISCOVERY_CONFIG_ALL,
                _ => DiscoveryConfig.DISCOVERY_CONFIG_NONE,
            };

            for (int i = 0; i < options.Alpns.Count; i++)
            {
                byte[] alpn = options.Alpns[i];
                unsafe
                {
                    fixed (byte* alpnPtr = alpn)
                    {
                        iroh.endpoint_config_add_alpn(&config, new slice_ref_uint8 { ptr = alpnPtr, len = (nuint)alpn.Length });
                    }
                }
            }

            if (options.SecretKey is IrohSecretKey secretKey)
            {
                // Bind a fresh native copy so the managed key remains reusable; the copy's ownership moves into the config.
                using SafeSecretKeyHandle nativeKey = SafeSecretKeyHandle.CloneFrom(secretKey);
                unsafe
                {
                    using NativeGuard keyGuard = new(nativeKey);
                    iroh.endpoint_config_add_secret_key(&config, (SecretKey*)keyGuard.Pointer);
                }

                nativeKey.SetHandleAsInvalid();
            }

            SafeEndpointHandle handle = new();
            unsafe
            {
                using NativeGuard endpointGuard = new(handle);
                Endpoint* endpoint = (Endpoint*)endpointGuard.Pointer;
                EndpointResult result = iroh.endpoint_bind(&config, null, null, &endpoint);
                iroh.endpoint_config_free(config); // endpoint_bind only borrows the config; freeing it here releases the ALPN vectors add_alpn allocated
                configFreed = true;
                if (result != EndpointResult.ENDPOINT_RESULT_OK)
                {
                    throw InteropUtil.ToException(result, "Failed to bind the iroh endpoint.");
                }
            }

            return new IrohEndpoint(handle);
        }
        finally
        {
            if (!configFreed)
            {
                unsafe
                {
                    iroh.endpoint_config_free(config);
                }
            }
        }
    }

    /// <summary>Returns this endpoint's own address (ticket) for sharing with remote peers.</summary>
    public unsafe IrohNodeAddr LocalAddr()
    {
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_handle);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);
            Endpoint* endpoint = (Endpoint*)guard.Pointer;
            EndpointAddr addr = iroh.endpoint_addr_default();
            EndpointResult result = iroh.endpoint_addr(&endpoint, &addr);
            if (result != EndpointResult.ENDPOINT_RESULT_OK)
            {
                throw InteropUtil.ToException(result, "Failed to retrieve the local node address.");
            }

            try
            {
                return IrohNodeAddr.FromNativeTicket(InteropUtil.ReadStringAndFree(iroh.endpoint_addr_as_str(&addr)));
            }
            finally
            {
                iroh.endpoint_addr_free(addr); // this instance is owned here; it was not passed to a connect call
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Waits until the endpoint is online (home relay and at least one direct address); false when it times out.</summary>
    public Task<bool> WaitForOnlineAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be non-negative.");
        }

        ulong timeoutMilliseconds = (ulong)Math.Min(timeout.TotalMilliseconds, long.MaxValue);
        return Task.Run(() =>
        {
            _lock.EnterReadLock();
            try
            {
                using NativeGuard guard = new(_handle);
                ObjectDisposedException.ThrowIf(!guard.IsValid, this);
                unsafe
                {
                    Endpoint* endpoint = (Endpoint*)guard.Pointer;
                    return iroh.endpoint_online(&endpoint, timeoutMilliseconds) switch
                    {
                        EndpointResult.ENDPOINT_RESULT_OK => true,
                        EndpointResult.ENDPOINT_RESULT_TIMEOUT => false,
                        var result => throw InteropUtil.ToException(result, "Failed to wait for the endpoint to come online."),
                    };
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
        });
    }

    /// <summary>Connects to a remote node using the given ALPN; the returned connection reports the negotiated ALPN.</summary>
    /// <remarks>Cancellation is observed only before the native connect starts; an in-flight connect cannot be interrupted.</remarks>
    public Task<IrohConnection> ConnectAsync(IrohNodeAddr remote, byte[] alpn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(alpn);
        if (alpn.Length == 0)
        {
            throw new ArgumentException("The ALPN protocol must be non-empty.", nameof(alpn));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => ConnectCore(remote, alpn), cancellationToken);
    }

    /// <summary>Waits for an incoming connection of any ALPN; the negotiated ALPN is exposed on the returned connection.</summary>
    /// <remarks>Cancellation is observed only before the native accept starts; an in-flight accept cannot be interrupted.</remarks>
    public Task<IrohConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => AcceptCore(), cancellationToken);
    }

    /// <summary>Notifies the native endpoint that network conditions changed (primarily relevant on Android).</summary>
    public unsafe void NetworkChanged()
    {
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_handle);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);
            Endpoint* endpoint = (Endpoint*)guard.Pointer;
            iroh.endpoint_network_change(&endpoint);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Closes the endpoint and all of its connections gracefully; waits at most a few seconds for in-flight operations.</summary>
    /// <remarks>Native connect/accept calls cannot be interrupted; if one is still in flight after the bounded wait, the native container is deliberately leaked rather than freed under a live borrow.</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the endpoint and all of its connections gracefully; waits at most a few seconds for in-flight operations.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // First pass outside the endpoint lock so a slow (or indefinitely blocked) connection teardown does not block new work.
        foreach (IrohConnection connection in DrainConnections())
        {
            TryDispose(connection);
        }

        _closed = true;

        if (!_lock.TryEnterWriteLock(DisposeWaitTimeout))
        {
            // A native call (e.g. a pending accept or connect, which cannot be interrupted) still
            // borrows the container; freeing it now would be use-after-free, so leak it instead.
            _handle.SetHandleAsInvalid();
            return;
        }

        try
        {
            // Connections tracked by an operation that raced the first pass are closed here, before the endpoint itself.
            foreach (IrohConnection connection in DrainConnections())
            {
                TryDispose(connection);
            }

            using NativeGuard guard = new(_handle);
            if (guard.IsValid)
            {
                unsafe
                {
                    Endpoint* endpoint = (Endpoint*)guard.Pointer;
                    iroh.endpoint_close(endpoint); // consumes the container
                }
            }

            _handle.SetHandleAsInvalid();
        }
        finally
        {
            _lock.ExitWriteLock();
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
            // Keep closing the remaining connections even if one fails.
        }
    }

    private IrohConnection[] DrainConnections()
    {
        lock (_connectionsGate)
        {
            if (_connections.Count == 0)
            {
                return Array.Empty<IrohConnection>();
            }

            IrohConnection[] snapshot = _connections.ToArray();
            _connections.Clear();
            return snapshot;
        }
    }

    private void Track(IrohConnection connection)
    {
        lock (_connectionsGate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _connections.Add(connection);
        }
    }

    internal void Untrack(IrohConnection connection)
    {
        lock (_connectionsGate)
        {
            _connections.Remove(connection);
        }
    }

    private IrohConnection ConnectCore(IrohNodeAddr remote, byte[] alpn)
    {
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_handle);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);

            byte[] ticket = InteropUtil.GetNulTerminatedUtf8(remote.Ticket);
            SafeConnectionHandle connectionHandle = new();
            unsafe
            {
                // A fresh EndpointAddr is parsed per connect: it is passed by value and consumed by the native side, so it must never be freed here or reused.
                EndpointAddr addr = iroh.endpoint_addr_default();
                fixed (byte* ticketPtr = ticket)
                {
                    if (iroh.endpoint_addr_from_string(ticketPtr, &addr) != AddrResult.ADDR_RESULT_OK)
                    {
                        iroh.endpoint_addr_free(addr); // the parse failed, so native did not take ownership of the container
                        throw new IrohException(IrohErrorCode.InvalidEndpointAddr, $"The node address '{remote.Ticket}' could not be parsed.");
                    }
                }

                using NativeGuard connectionGuard = new(connectionHandle);
                Endpoint* endpoint = (Endpoint*)guard.Pointer;
                Connection* connection = (Connection*)connectionGuard.Pointer;
                EndpointResult result;
                fixed (byte* alpnPtr = alpn)
                {
                    result = iroh.endpoint_connect(&endpoint, new slice_ref_uint8 { ptr = alpnPtr, len = (nuint)alpn.Length }, addr, &connection);
                }

                if (result != EndpointResult.ENDPOINT_RESULT_OK)
                {
                    throw InteropUtil.ToException(result, $"Failed to connect to '{remote.Ticket}'.");
                }
            }

            IrohConnection established = new(connectionHandle, (byte[])alpn.Clone(), this);
            try
            {
                Track(established);
            }
            catch
            {
                // The endpoint closed while the connect completed; dispose deterministically so the
                // connection's pump thread does not linger until finalization.
                established.Dispose();
                throw;
            }

            return established;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private IrohConnection AcceptCore()
    {
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_handle);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);

            SafeConnectionHandle connectionHandle = new();
            Vec_uint8 alpnBuffer = default; // zero-initialized out-container is safe; filled on success
            unsafe
            {
                using NativeGuard connectionGuard = new(connectionHandle);
                Endpoint* endpoint = (Endpoint*)guard.Pointer;
                Connection* connection = (Connection*)connectionGuard.Pointer;
                EndpointResult result = iroh.endpoint_accept_any(&endpoint, &alpnBuffer, &connection);
                if (result != EndpointResult.ENDPOINT_RESULT_OK)
                {
                    throw InteropUtil.ToException(result, "Failed to accept an incoming connection.");
                }
            }

            byte[] negotiatedAlpn = InteropUtil.ToArrayAndFree(ref alpnBuffer);
            IrohConnection accepted = new(connectionHandle, negotiatedAlpn, this);
            try
            {
                Track(accepted);
            }
            catch
            {
                // The endpoint closed while the accept completed; dispose deterministically so the
                // connection's pump thread does not linger until finalization.
                accepted.Dispose();
                throw;
            }

            return accepted;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
