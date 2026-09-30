using System.Buffers;
using System.Threading.Channels;

namespace N0.IrohNet;

/// <summary>An established connection to a remote endpoint, supporting bidirectional streams and unreliable datagrams.</summary>
public sealed class IrohConnection : IAsyncDisposable, IDisposable
{
    private const int DatagramPollMilliseconds = 1000;
    private const int DatagramQueueCapacity = 4096;
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(3);

    private readonly SafeConnectionHandle _handle;
    private readonly IrohEndpoint _owner;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Channel<byte[]> _incomingDatagrams;
    private readonly Thread _datagramPump;
    private volatile bool _closed;
    private int _disposed;

    internal IrohConnection(SafeConnectionHandle handle, byte[]? negotiatedAlpn, IrohEndpoint owner)
    {
        _handle = handle;
        _owner = owner;
        NegotiatedAlpn = negotiatedAlpn;
        _incomingDatagrams = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(DatagramQueueCapacity)
        {
            SingleWriter = true,

            // When the queue fills up (4096 undelivered datagrams) the oldest queued datagram is dropped,
            // favoring recent game-state-like data over unbounded memory growth.
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _datagramPump = new Thread(PumpDatagrams)
        {
            IsBackground = true,
            Name = "N0.IrohNet.DatagramPump",
        };
        _datagramPump.Start();
    }

    /// <summary>Gets the ALPN negotiated for this connection; from the accepted protocol when accepted, or the requested one when connecting.</summary>
    public byte[]? NegotiatedAlpn { get; }

    /// <summary>Gets the maximum datagram size in bytes, or 0 when datagrams are not supported.</summary>
    /// <remarks>The limit reflects the connection's current path and can change over its lifetime (e.g. path MTU discovery raising it).</remarks>
    public unsafe nuint MaxDatagramSize
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                using NativeGuard guard = new(_handle);
                ObjectDisposedException.ThrowIf(!guard.IsValid, this);
                Connection* connection = (Connection*)guard.Pointer;
                return iroh.connection_max_datagram_size(&connection);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Gets the estimated round-trip time of the connection's selected path, or zero when no path is selected.</summary>
    /// <remarks>Performs a lightweight native query on the calling thread.</remarks>
    public unsafe TimeSpan Rtt
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                using NativeGuard guard = new(_handle);
                ObjectDisposedException.ThrowIf(!guard.IsValid, this);
                Connection* connection = (Connection*)guard.Pointer;
                return TimeSpan.FromMilliseconds(iroh.connection_rtt(&connection));
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Gets the ratio of lost packets to sent packets on the selected path, or zero when nothing was sent yet.</summary>
    /// <remarks>Performs a lightweight native query on the calling thread.</remarks>
    public unsafe double PacketLoss
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                using NativeGuard guard = new(_handle);
                ObjectDisposedException.ThrowIf(!guard.IsValid, this);
                Connection* connection = (Connection*)guard.Pointer;
                return iroh.connection_packet_loss(&connection);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Opens a bidirectional stream; the peer must call <see cref="AcceptStreamAsync"/> to receive it.</summary>
    public Task<Stream> OpenStreamAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => OpenStreamCore(), cancellationToken);
    }

    /// <summary>Accepts a bidirectional stream opened by the remote peer.</summary>
    public Task<IrohStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => AcceptStreamCore(), cancellationToken);
    }

    /// <summary>Sends an unreliable datagram, throwing <see cref="IrohException"/> when the payload exceeds the current maximum datagram size.</summary>
    /// <remarks>The size check uses the limit observed at call time; since the transport's limit can change (path MTU discovery), it is an advisory guard and the native send remains the authority.</remarks>
    public Task SendDatagramAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nuint maxDatagramSize = MaxDatagramSize;
        if (maxDatagramSize > 0 && payload.Length > (long)maxDatagramSize)
        {
            throw new IrohException(IrohErrorCode.SendError, $"The datagram payload is {payload.Length} bytes, exceeding the connection's maximum datagram size of {maxDatagramSize} bytes.");
        }

        return Task.Run(() => SendDatagramCore(payload), cancellationToken);
    }

    /// <summary>Receives the next datagram sent by the remote peer.</summary>
    /// <remarks>Datagrams are pumped into a bounded queue (4096 entries); when full, the oldest queued datagram is dropped.</remarks>
    public async Task<byte[]> ReceiveDatagramAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _incomingDatagrams.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw _closed
                ? new ObjectDisposedException(nameof(IrohConnection))
                : new IrohException(IrohErrorCode.CloseError, "The connection was closed before a datagram could be received.", exception);
        }
    }

    /// <summary>Closes the connection gracefully and stops the datagram pump.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the connection gracefully and stops the datagram pump.</summary>
    /// <remarks>
    /// Native accept-family calls hold the connection for their entire — uncancellable — duration,
    /// so an in-flight <see cref="AcceptStreamAsync"/> can hold the lock this method needs. The wait
    /// is bounded: freeing the container while a native call still borrows it would be use-after-free,
    /// so on timeout the container is deliberately leaked instead. Streams should be drained before
    /// closing: as in any QUIC implementation, an immediate connection close discards stream data
    /// that has not been delivered to the peer yet.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closed = true;

        if (!_lock.TryEnterWriteLock(DisposeWaitTimeout))
        {
            // A native call (e.g. a pending accept) still borrows the container and cannot be
            // interrupted; freeing it now would be use-after-free, so leak it as a safe degradation.
            _handle.SetHandleAsInvalid();
        }
        else
        {
            try
            {
                using NativeGuard guard = new(_handle);
                if (guard.IsValid)
                {
                    unsafe
                    {
                        Connection* connection = (Connection*)guard.Pointer;
                        iroh.connection_close(connection); // consumes the container
                    }
                }

                _handle.SetHandleAsInvalid();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        _incomingDatagrams.Writer.TryComplete();
        if (Thread.CurrentThread != _datagramPump)
        {
            _datagramPump.Join(TimeSpan.FromSeconds(3)); // the pump notices the close within one poll interval
        }

        _owner.Untrack(this);
    }

    private unsafe void PumpDatagrams()
    {
        Vec_uint8 buffer = default;
        try
        {
            while (true)
            {
                bool stopping = true;
                EndpointResult result = EndpointResult.ENDPOINT_RESULT_TIMEOUT;
                _lock.EnterReadLock();
                try
                {
                    if (!_closed)
                    {
                        using NativeGuard guard = new(_handle);
                        if (guard.IsValid)
                        {
                            stopping = false;
                            Connection* connection = (Connection*)guard.Pointer;
                            result = iroh.connection_read_datagram_timeout(&connection, &buffer, DatagramPollMilliseconds);
                        }
                    }
                }
                finally
                {
                    _lock.ExitReadLock();
                }

                if (stopping)
                {
                    break;
                }

                if (result == EndpointResult.ENDPOINT_RESULT_OK)
                {
                    _incomingDatagrams.Writer.TryWrite(InteropUtil.ToArrayAndFree(ref buffer));
                }
                else if (result != EndpointResult.ENDPOINT_RESULT_TIMEOUT)
                {
                    break; // the connection is gone (read error); complete the queue
                }
            }
        }
        catch (Exception exception)
        {
            // An unhandled exception on a raw thread terminates the process; a pump failure must
            // degrade into a completed queue (readers see it as the channel's error) instead.
            _incomingDatagrams.Writer.TryComplete(exception);
            return;
        }

        _incomingDatagrams.Writer.TryComplete();
    }

    private Stream OpenStreamCore()
    {
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_handle);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);

            SafeSendStreamHandle send = SafeSendStreamHandle.Allocate();
            SafeRecvStreamHandle recv = SafeRecvStreamHandle.Allocate();
            bool owned = false;
            try
            {
                unsafe
                {
                    using NativeGuard sendGuard = new(send);
                    using NativeGuard recvGuard = new(recv);
                    Connection* connection = (Connection*)guard.Pointer;
                    SendStream* sendStream = (SendStream*)sendGuard.Pointer;
                    RecvStream* recvStream = (RecvStream*)recvGuard.Pointer;
                    EndpointResult result = iroh.connection_open_bi(&connection, &sendStream, &recvStream);
                    if (result != EndpointResult.ENDPOINT_RESULT_OK)
                    {
                        throw InteropUtil.ToException(result, "Failed to open a bidirectional stream.");
                    }
                }

                owned = true;
                return new IrohStream(send, recv);
            }
            finally
            {
                if (!owned)
                {
                    send.Dispose();
                    recv.Dispose();
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private IrohStream AcceptStreamCore()
    {
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_handle);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);

            SafeSendStreamHandle send = SafeSendStreamHandle.Allocate();
            SafeRecvStreamHandle recv = SafeRecvStreamHandle.Allocate();
            bool owned = false;
            try
            {
                unsafe
                {
                    using NativeGuard sendGuard = new(send);
                    using NativeGuard recvGuard = new(recv);
                    Connection* connection = (Connection*)guard.Pointer;
                    SendStream* sendStream = (SendStream*)sendGuard.Pointer;
                    RecvStream* recvStream = (RecvStream*)recvGuard.Pointer;
                    EndpointResult result = iroh.connection_accept_bi(&connection, &sendStream, &recvStream);
                    if (result != EndpointResult.ENDPOINT_RESULT_OK)
                    {
                        throw InteropUtil.ToException(result, "Failed to accept a bidirectional stream.");
                    }
                }

                owned = true;
                return new IrohStream(send, recv);
            }
            finally
            {
                if (!owned)
                {
                    send.Dispose();
                    recv.Dispose();
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private unsafe void SendDatagramCore(ReadOnlyMemory<byte> payload)
    {
        using MemoryHandle pinned = payload.Pin();
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_handle);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);
            Connection* connection = (Connection*)guard.Pointer;
            EndpointResult result = iroh.connection_write_datagram(&connection, new slice_ref_uint8 { ptr = (byte*)pinned.Pointer, len = (nuint)payload.Length });
            if (result != EndpointResult.ENDPOINT_RESULT_OK)
            {
                throw InteropUtil.ToException(result, "Failed to send the datagram.");
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
