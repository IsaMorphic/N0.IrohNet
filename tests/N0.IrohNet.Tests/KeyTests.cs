using Xunit;

namespace N0.IrohNet.Tests;

public class KeyTests
{
    [Fact]
    public void SecretKey_Base32RoundTrip_PublicKeyRoundTrip()
    {
        using IrohSecretKey key = IrohSecretKey.Generate();
        string base32 = key.AsBase32();
        Assert.False(string.IsNullOrWhiteSpace(base32));

        using IrohSecretKey parsed = IrohSecretKey.FromBase32(base32);
        Assert.Equal(base32, parsed.AsBase32());

        // The parsed key must carry the same identity as the original.
        Assert.Equal(key.PublicKey, parsed.PublicKey);

        IrohPublicKey publicKey = key.PublicKey;
        Assert.Equal(IrohPublicKey.SizeInBytes, publicKey.ToBytes().Length);

        string publicBase32 = publicKey.AsBase32();
        IrohPublicKey reparsed = IrohPublicKey.FromBase32(publicBase32);
        Assert.Equal(publicKey, reparsed);
        Assert.Equal(publicBase32, reparsed.AsBase32());
    }

    [Fact]
    public void TwoGeneratedKeys_Differ()
    {
        using IrohSecretKey first = IrohSecretKey.Generate();
        using IrohSecretKey second = IrohSecretKey.Generate();

        Assert.NotEqual(first.AsBase32(), second.AsBase32());
        Assert.NotEqual(first.PublicKey, second.PublicKey);
        Assert.False(first.PublicKey.ToBytes().AsSpan().SequenceEqual(second.PublicKey.ToBytes()));
    }

    [Fact]
    public void PublicKey_InvalidBase32_IsRejected()
    {
        Assert.Throws<IrohException>(() => IrohPublicKey.FromBase32("this is not a base32 key!!"));
        Assert.False(IrohPublicKey.TryParse("", out _));
    }

    [Fact]
    public void SecretKey_ConstructorGeneratesDistinctKeys()
    {
        // The instance constructor path must generate fresh material, same as the static factory.
        using IrohSecretKey first = new();
        using IrohSecretKey second = new();
        Assert.NotEqual(first.AsBase32(), second.AsBase32());
        Assert.NotEqual(first.PublicKey, second.PublicKey);
    }
}
