namespace N0.IrohNet;

/// <summary>A bidirectional stream; supports one concurrent reader and one concurrent writer, like any <see cref="Stream"/>.</summary>
/// <remarks>
/// Open streams stay valid independently of the endpoint's and connection's lifetime; disposing those
/// while streams are still open drains gracefully rather than invalidating the streams (the native
/// stream types are reference-counted).
/// </remarks>
public sealed class IrohStream : Stream
{
    private const int AsyncSliceMilliseconds = 250;
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(3);

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly SafeSendStreamHandle _send;
    private readonly SafeRecvStreamHandle _recv;
    private TimeSpan _readTimeout = Timeout.InfiniteTimeSpan;
    private TimeSpan _writeTimeout = Timeout.InfiniteTimeSpan;
    private int _disposed;

    internal IrohStream(SafeSendStreamHandle send, SafeRecvStreamHandle recv)
    {
        _send = send;
        _recv = recv;
    }

    /// <inheritdoc />
    public override bool CanRead => _disposed == 0;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _disposed == 0;

    /// <inheritdoc />
    public override bool CanTimeout => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("IrohStream does not support seeking or length queries.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("IrohStream does not support seeking.");
        set => throw new NotSupportedException("IrohStream does not support seeking.");
    }

    /// <inheritdoc />
    public override int ReadTimeout
    {
        get => _readTimeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)_readTimeout.TotalMilliseconds;
        set => _readTimeout = ValidateTimeout(value);
    }

    /// <inheritdoc />
    public override int WriteTimeout
    {
        get => _writeTimeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)_writeTimeout.TotalMilliseconds;
        set => _writeTimeout = ValidateTimeout(value);
    }

    /// <summary>Reads from the stream, blocking until data is available (or the stream is finished).</summary>
    public override unsafe int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>Reads from the stream, blocking until data is available (or the stream is finished).</summary>
    public override unsafe int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // The read lock serializes against Dispose: the native read borrows the recv container
        // (&mut box), so freeing it concurrently would be use-after-free.
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_recv);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);
            RecvStream* stream = (RecvStream*)guard.Pointer;
            long read;
            if (_readTimeout == Timeout.InfiniteTimeSpan)
            {
                fixed (byte* bufferPtr = buffer)
                {
                    read = iroh.recv_stream_read(&stream, new slice_mut_uint8 { ptr = bufferPtr, len = (nuint)buffer.Length });
                }
            }
            else
            {
                fixed (byte* bufferPtr = buffer)
                {
                    read = iroh.recv_stream_read_timeout(&stream, new slice_mut_uint8 { ptr = bufferPtr, len = (nuint)buffer.Length }, (ulong)_readTimeout.TotalMilliseconds);
                }

                if (read == -2)
                {
                    throw new TimeoutException($"The stream read did not complete within {_readTimeout}.");
                }
            }

            if (read < 0)
            {
                throw new IrohException(IrohErrorCode.ReadError, "Failed to read from the stream.");
            }

            return (int)read;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Reads from the stream, honoring the cancellation token between bounded native read slices.</summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (buffer.IsEmpty)
        {
            return ValueTask.FromResult(0);
        }

        // The native read blocks its thread (in bounded slices), so the loop runs on the thread pool.
        return new ValueTask<int>(Task.Run(() => ReadUntilAvailable(buffer, cancellationToken), cancellationToken));
    }

    private int ReadUntilAvailable(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        long deadline = _readTimeout == Timeout.InfiniteTimeSpan
            ? -1
            : Environment.TickCount64 + (long)_readTimeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sliceMilliseconds = AsyncSliceMilliseconds;
            if (deadline >= 0)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    throw new TimeoutException($"The stream read did not complete within {_readTimeout}.");
                }

                sliceMilliseconds = (int)Math.Min(sliceMilliseconds, remaining);
            }

            // A negative return marks an idle slice; no bytes are consumed, so retrying is safe.
            int bytesRead = ReadSlice(buffer.Span, sliceMilliseconds);
            if (bytesRead >= 0)
            {
                return bytesRead;
            }
        }
    }

    /// <summary>Writes the buffer to the stream in its entirety.</summary>
    public override unsafe void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <summary>Writes the buffer to the stream in its entirety.</summary>
    /// <remarks>A timed-out write leaves the receiver's stream position indeterminate; the stream should be discarded.</remarks>
    public override unsafe void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (buffer.IsEmpty)
        {
            return;
        }

        // The read lock serializes against Dispose: the native write borrows the send container
        // (&mut box) and can block on flow control, so finishing it concurrently would be use-after-free.
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_send);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);
            SendStream* stream = (SendStream*)guard.Pointer;
            EndpointResult result;
            fixed (byte* bufferPtr = buffer)
            {
                result = _writeTimeout == Timeout.InfiniteTimeSpan
                    ? iroh.send_stream_write(&stream, new slice_ref_uint8 { ptr = bufferPtr, len = (nuint)buffer.Length })
                    : iroh.send_stream_write_timeout(&stream, new slice_ref_uint8 { ptr = bufferPtr, len = (nuint)buffer.Length }, (ulong)_writeTimeout.TotalMilliseconds);
            }

            if (result == EndpointResult.ENDPOINT_RESULT_TIMEOUT)
            {
                // The native write is not cancel-safe (an unknown prefix may already have been written), so it must not be retried.
                throw new TimeoutException($"The stream write did not complete within {_writeTimeout}.");
            }

            if (result != EndpointResult.ENDPOINT_RESULT_OK)
            {
                throw new IrohException(IrohErrorCode.SendError, "Failed to write to the stream.");
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Writes the buffer to the stream in its entirety.</summary>
    /// <remarks>Cancellation is observed before the write starts; an in-flight native write cannot be interrupted.</remarks>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (buffer.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Task.Run(() =>
        {
            try
            {
                Write(buffer.Span);
            }
            catch (TimeoutException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }, cancellationToken));
    }

    /// <summary>A no-op; flushing is handled by the native transport.</summary>
    public override void Flush()
    {
    }

    /// <summary>A no-op; flushing is handled by the native transport.</summary>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("IrohStream does not support seeking.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("IrohStream does not support setting the length.");

    /// <summary>Finishes the send side (graceful FIN) and frees the receive side.</summary>
    /// <remarks>
    /// Native read/write calls borrow the stream containers for their entire — uncancellable —
    /// duration. If one is still in flight after a bounded wait, both containers are deliberately
    /// leaked instead of freed under the live borrow; the pending call then observes the closed
    /// handle and fails with <see cref="ObjectDisposedException"/>.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
        {
            if (!_lock.TryEnterWriteLock(DisposeWaitTimeout))
            {
                // A native read/write still borrows a container and cannot be interrupted; freeing it
                // now would be use-after-free, so leak both as a safe degradation.
                _send.SetHandleAsInvalid();
                _recv.SetHandleAsInvalid();
                base.Dispose(disposing);
                return;
            }

            try
            {
                using (NativeGuard sendGuard = new(_send))
                {
                    if (sendGuard.IsValid)
                    {
                        unsafe
                        {
                            iroh.send_stream_finish((SendStream*)sendGuard.Pointer); // consumes the send container
                        }
                    }
                }

                _send.SetHandleAsInvalid();

                using (NativeGuard recvGuard = new(_recv))
                {
                    if (recvGuard.IsValid)
                    {
                        unsafe
                        {
                            iroh.recv_stream_free((RecvStream*)recvGuard.Pointer);
                        }
                    }
                }

                _recv.SetHandleAsInvalid();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        base.Dispose(disposing);
    }

    private static TimeSpan ValidateTimeout(int milliseconds) => milliseconds switch
    {
        Timeout.Infinite => Timeout.InfiniteTimeSpan,
        > 0 => TimeSpan.FromMilliseconds(milliseconds),
        _ => throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "The timeout must be positive or Timeout.Infinite."),
    };

    private unsafe int ReadSlice(Span<byte> buffer, int timeoutMilliseconds)
    {
        _lock.EnterReadLock();
        try
        {
            using NativeGuard guard = new(_recv);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);
            RecvStream* stream = (RecvStream*)guard.Pointer;
            long read;
            fixed (byte* bufferPtr = buffer)
            {
                read = iroh.recv_stream_read_timeout(&stream, new slice_mut_uint8 { ptr = bufferPtr, len = (nuint)buffer.Length }, (ulong)timeoutMilliseconds);
            }

            if (read == -2)
            {
                return -1; // idle slice; no data was consumed
            }

            if (read < 0)
            {
                throw new IrohException(IrohErrorCode.ReadError, "Failed to read from the stream.");
            }

            return (int)read;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
