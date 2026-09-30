using System.Diagnostics.CodeAnalysis;

namespace N0.IrohNet;

/// <summary>A 32-byte node public key with base32 (node id) parsing and formatting.</summary>
public readonly struct IrohPublicKey : IEquatable<IrohPublicKey>
{
    /// <summary>The size of a public key in bytes.</summary>
    public const int SizeInBytes = 32;

    private readonly byte[]? _bytes;

    private IrohPublicKey(byte[] bytes) => _bytes = bytes;

    /// <summary>Parses a base32 public key string, throwing <see cref="IrohException"/> when it is invalid.</summary>
    public static IrohPublicKey FromBase32(string value) =>
        TryParse(value, out IrohPublicKey key)
            ? key
            : throw new IrohException(IrohErrorCode.InvalidPublicKey, $"The provided public key string '{value}' is not valid.");

    /// <summary>Parses a base32 public key string, returning false when it is invalid.</summary>
    public static unsafe bool TryParse(string? value, [NotNullWhen(true)] out IrohPublicKey publicKey)
    {
        publicKey = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        byte[] input = InteropUtil.GetNulTerminatedUtf8(value);
        PublicKey native = iroh.public_key_default(); // PublicKey is a by-value inline 32-byte struct; there is nothing to free (public_key_free is a no-op)
        fixed (byte* inputPtr = input)
        {
            if (iroh.public_key_from_base32(inputPtr, &native) != KeyResult.KEY_RESULT_OK)
            {
                return false;
            }
        }

        publicKey = FromNative(native);
        return true;
    }

    /// <summary>Returns the key formatted as a base32 (node id) string.</summary>
    public unsafe string AsBase32()
    {
        byte[] bytes = _bytes ?? throw new InvalidOperationException("This key is uninitialized; use FromBase32 or TryParse.");
        PublicKey native = default;
        byte* target = &native.key.idx[0];
        fixed (byte* source = bytes)
        {
            for (int i = 0; i < SizeInBytes; i++)
            {
                target[i] = source[i];
            }
        }

        return InteropUtil.ReadStringAndFree(iroh.public_key_as_base32(&native));
    }

    /// <summary>Returns a copy of the raw 32 key bytes.</summary>
    public byte[] ToBytes() => (byte[])(_bytes ?? throw new InvalidOperationException("This key is uninitialized; use FromBase32 or TryParse.")).Clone();

    /// <inheritdoc />
    public bool Equals(IrohPublicKey other) =>
        _bytes is not null && other._bytes is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IrohPublicKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        if (_bytes is not null)
        {
            hash.AddBytes(_bytes);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public static bool operator ==(IrohPublicKey left, IrohPublicKey right) => left.Equals(right);

    /// <inheritdoc />
    public static bool operator !=(IrohPublicKey left, IrohPublicKey right) => !left.Equals(right);

    internal static unsafe IrohPublicKey FromNative(PublicKey native)
    {
        byte* source = &native.key.idx[0];
        byte[] bytes = new byte[SizeInBytes];
        fixed (byte* target = bytes)
        {
            Buffer.MemoryCopy(source, target, SizeInBytes, SizeInBytes);
        }

        return new IrohPublicKey(bytes);
    }
}
