using System.Runtime.InteropServices;

namespace N0.IrohNet;

/// <summary>One <see cref="SafeHandle"/> subclass per owned native object; <see cref="SafeHandle.ReleaseHandle"/> calls the matching free function.</summary>
internal abstract class SafeIrohHandle : SafeHandle
{
    protected SafeIrohHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;
}

/// <summary>Owns an uninitialized <c>Endpoint</c> container allocated by <c>endpoint_default</c>; freed with <c>endpoint_free</c> unless consumed by <c>endpoint_close</c>.</summary>
internal sealed class SafeEndpointHandle : SafeIrohHandle
{
    public SafeEndpointHandle()
    {
        unsafe
        {
            SetHandle((nint)iroh.endpoint_default());
        }
    }

    protected override unsafe bool ReleaseHandle()
    {
        iroh.endpoint_free((Endpoint*)handle);
        return true;
    }
}

/// <summary>Owns an uninitialized <c>Connection</c> container allocated by <c>connection_default</c>; freed with <c>connection_free</c> unless consumed by <c>connection_close</c>.</summary>
internal sealed class SafeConnectionHandle : SafeIrohHandle
{
    public SafeConnectionHandle()
    {
        unsafe
        {
            SetHandle((nint)iroh.connection_default());
        }
    }

    protected override unsafe bool ReleaseHandle()
    {
        iroh.connection_free((Connection*)handle);
        return true;
    }
}

/// <summary>Owns an uninitialized <c>SendStream</c> container allocated by <c>send_stream_default</c>; freed with <c>send_stream_free</c> unless consumed by <c>send_stream_finish</c>.</summary>
internal sealed class SafeSendStreamHandle : SafeIrohHandle
{
    public SafeSendStreamHandle()
    {
        unsafe
        {
            SetHandle((nint)iroh.send_stream_default());
        }
    }

    public static SafeSendStreamHandle Allocate() => new();

    protected override unsafe bool ReleaseHandle()
    {
        iroh.send_stream_free((SendStream*)handle);
        return true;
    }
}

/// <summary>Owns an uninitialized <c>RecvStream</c> container allocated by <c>recv_stream_default</c>; freed with <c>recv_stream_free</c>.</summary>
internal sealed class SafeRecvStreamHandle : SafeIrohHandle
{
    public SafeRecvStreamHandle()
    {
        unsafe
        {
            SetHandle((nint)iroh.recv_stream_default());
        }
    }

    public static SafeRecvStreamHandle Allocate() => new();

    protected override unsafe bool ReleaseHandle()
    {
        iroh.recv_stream_free((RecvStream*)handle);
        return true;
    }
}

/// <summary>Owns a native <c>SecretKey</c> box; freed with <c>secret_key_free</c>.</summary>
internal sealed class SafeSecretKeyHandle : SafeIrohHandle
{
    private SafeSecretKeyHandle()
    {
        unsafe
        {
            SetHandle((nint)iroh.secret_key_default());
        }
    }

    /// <summary>Generates a fresh native secret key.</summary>
    public static SafeSecretKeyHandle Generate() => new();

    /// <summary>Parses a key string into a freshly allocated native secret key box.</summary>
    public static unsafe SafeSecretKeyHandle FromString(string value)
    {
        SafeSecretKeyHandle handle = new();
        byte[] input = InteropUtil.GetNulTerminatedUtf8(value);
        using NativeGuard guard = new(handle);
        fixed (byte* inputPtr = input)
        {
            SecretKey* key = (SecretKey*)guard.Pointer;
            if (iroh.secret_key_from_base32(inputPtr, &key) != KeyResult.KEY_RESULT_OK)
            {
                throw new IrohException(IrohErrorCode.InvalidSecretKey, "The provided secret key string is not valid.");
            }
        }

        return handle;
    }

    /// <summary>Creates a fresh native copy of a managed key so the original stays reusable after it is consumed by a bind.</summary>
    public static SafeSecretKeyHandle CloneFrom(IrohSecretKey key) => FromString(key.AsBase32());

    protected override unsafe bool ReleaseHandle()
    {
        iroh.secret_key_free((SecretKey*)handle);
        return true;
    }
}
