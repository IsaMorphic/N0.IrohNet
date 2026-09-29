namespace N0.IrohNet;

/// <summary>A node secret key; disposing releases the native key material.</summary>
public sealed class IrohSecretKey : IDisposable
{
    private readonly SafeSecretKeyHandle _handle;

    private IrohSecretKey(SafeSecretKeyHandle handle) => _handle = handle;

    /// <summary>Generates a new secret key with OS randomness.</summary>
    public IrohSecretKey()
        : this(SafeSecretKeyHandle.Generate())
    {
    }

    /// <summary>Generates a new secret key with OS randomness.</summary>
    public static IrohSecretKey Generate() => new();

    /// <summary>Parses a key string (the format produced by <see cref="AsBase32"/>), throwing <see cref="IrohException"/> when it is invalid.</summary>
    public static IrohSecretKey FromBase32(string value) => new(SafeSecretKeyHandle.FromString(value));

    /// <summary>Returns the key as a string accepted by <see cref="FromBase32"/> (keep this material secret).</summary>
    public unsafe string AsBase32()
    {
        using NativeGuard guard = new(_handle);
        ObjectDisposedException.ThrowIf(!guard.IsValid, this);
        return InteropUtil.ReadStringAndFree(iroh.secret_key_as_base32((SecretKey*)guard.Pointer));
    }

    /// <summary>Gets the public key corresponding to this secret key.</summary>
    public IrohPublicKey PublicKey
    {
        get
        {
            using NativeGuard guard = new(_handle);
            ObjectDisposedException.ThrowIf(!guard.IsValid, this);
            unsafe
            {
                return IrohPublicKey.FromNative(iroh.secret_key_public((SecretKey*)guard.Pointer));
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}
