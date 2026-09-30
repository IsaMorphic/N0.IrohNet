using System.Diagnostics.CodeAnalysis;

namespace N0.IrohNet;

/// <summary>A node's addressing information, wrapped around the opaque iroh ticket string; obtained from <see cref="IrohEndpoint.LocalAddr"/> or parsed from a ticket.</summary>
public sealed class IrohNodeAddr : IEquatable<IrohNodeAddr>
{
    private IrohNodeAddr(string ticket) => Ticket = ticket;

    /// <summary>Gets the opaque ticket string encoding the node id and dialing information.</summary>
    public string Ticket { get; }

    /// <summary>Parses a ticket string, throwing <see cref="IrohException"/> when it is invalid.</summary>
    public static IrohNodeAddr Parse(string ticket) =>
        TryParse(ticket, out IrohNodeAddr? addr)
            ? addr
            : throw new IrohException(IrohErrorCode.InvalidEndpointAddr, $"The provided node address '{ticket}' is not valid.");

    /// <summary>Parses a ticket string, returning false when it is invalid.</summary>
    public static bool TryParse(string? ticket, [NotNullWhen(true)] out IrohNodeAddr? addr)
    {
        addr = null;
        if (string.IsNullOrEmpty(ticket) || !InteropUtil.TryValidateTicket(ticket))
        {
            return false;
        }

        addr = new IrohNodeAddr(ticket);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(IrohNodeAddr? other) =>
        other is not null && string.Equals(Ticket, other.Ticket, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IrohNodeAddr other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Ticket);

    /// <inheritdoc />
    public override string ToString() => Ticket;

    internal static IrohNodeAddr FromNativeTicket(string ticket) => new(ticket);
}
