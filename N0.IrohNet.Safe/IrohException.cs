namespace N0.IrohNet;

/// <summary>Fine-grained failure codes reported by <see cref="IrohException"/>; mirrors the native result enums.</summary>
public enum IrohErrorCode
{
    /// <summary>The operation succeeded (never thrown as an error).</summary>
    Ok,
    /// <summary>The endpoint failed to bind.</summary>
    BindError,
    /// <summary>Failed to accept an incoming connection.</summary>
    AcceptFailed,
    /// <summary>Failed to accept an incoming unidirectional stream.</summary>
    AcceptUniFailed,
    /// <summary>Failed to accept an incoming bidirectional stream.</summary>
    AcceptBiFailed,
    /// <summary>Failed to establish an outgoing unidirectional stream.</summary>
    ConnectUniError,
    /// <summary>Failed to establish an outgoing bidirectional stream.</summary>
    ConnectBiError,
    /// <summary>Failed to connect to the remote endpoint.</summary>
    ConnectError,
    /// <summary>Failed to retrieve the endpoint's own address.</summary>
    AddrError,
    /// <summary>Failed to send data.</summary>
    SendError,
    /// <summary>Failed to read data.</summary>
    ReadError,
    /// <summary>The operation timed out.</summary>
    Timeout,
    /// <summary>The connection or endpoint did not close cleanly.</summary>
    CloseError,
    /// <summary>An incoming connection failed before the handshake; typically not caused by the application and safe to ignore.</summary>
    IncomingError,
    /// <summary>The connection type could not be determined.</summary>
    ConnectionTypeError,
    /// <summary>The provided URL was invalid.</summary>
    InvalidUrl,
    /// <summary>The provided socket address was invalid.</summary>
    InvalidSocketAddr,
    /// <summary>The provided node address (ticket) was invalid.</summary>
    InvalidEndpointAddr,
    /// <summary>The provided public key string was invalid.</summary>
    InvalidPublicKey,
    /// <summary>The provided secret key string was invalid.</summary>
    InvalidSecretKey,
}

/// <summary>The exception thrown by the <c>N0.IrohNet.Safe</c> layer when a native iroh operation fails.</summary>
public sealed class IrohException : Exception
{
    /// <summary>Creates the exception with an error code and a generated message.</summary>
    public IrohException(IrohErrorCode errorCode)
        : this(errorCode, $"The iroh operation failed with error code {errorCode}.")
    {
    }

    /// <summary>Creates the exception with an error code and a custom message.</summary>
    public IrohException(IrohErrorCode errorCode, string? message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Creates the exception with an error code, a custom message, and an inner exception.</summary>
    public IrohException(IrohErrorCode errorCode, string? message, Exception? innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets the native error code that caused this exception.</summary>
    public IrohErrorCode ErrorCode { get; }
}
