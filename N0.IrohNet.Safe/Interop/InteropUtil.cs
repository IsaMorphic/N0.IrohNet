using System.Text;

namespace N0.IrohNet;

/// <summary>String, buffer, and result-code marshalling helpers for the safe layer's FFI calls.</summary>
internal static unsafe class InteropUtil
{
    /// <summary>Encodes a string as UTF-8 with a trailing NUL, as required by native string-input parameters.</summary>
    public static byte[] GetNulTerminatedUtf8(string value) => Encoding.UTF8.GetBytes(value + '\0');

    /// <summary>Reads a NUL-terminated UTF-8 string returned by native code and frees the native buffer with <c>rust_free_string</c>.</summary>
    public static string ReadStringAndFree(byte* nativeString)
    {
        if (nativeString is null)
        {
            return string.Empty;
        }

        int length = 0;
        while (nativeString[length] != 0)
        {
            length++;
        }

        string result = Encoding.UTF8.GetString(nativeString, length);
        iroh.rust_free_string(nativeString);
        return result;
    }

    /// <summary>Copies a filled native <c>Vec_uint8</c> into a managed array and frees the native buffer with <c>rust_buffer_free</c>.</summary>
    public static byte[] ToArrayAndFree(ref Vec_uint8 buffer)
    {
        byte[] result = new ReadOnlySpan<byte>(buffer.ptr, checked((int)buffer.len)).ToArray();
        iroh.rust_buffer_free(buffer);
        buffer = default;
        return result;
    }

    /// <summary>Validates a node address string (ticket) by parsing a native copy, then freeing it.</summary>
    public static bool TryValidateTicket(string ticket)
    {
        byte[] input = GetNulTerminatedUtf8(ticket);
        EndpointAddr addr = iroh.endpoint_addr_default();
        fixed (byte* inputPtr = input)
        {
            if (iroh.endpoint_addr_from_string(inputPtr, &addr) != AddrResult.ADDR_RESULT_OK)
            {
                return false;
            }
        }

        // This instance is never handed to endpoint_connect, so it must be freed here.
        iroh.endpoint_addr_free(addr);
        return true;
    }

    /// <summary>Converts a native endpoint result into an <see cref="IrohException"/> carrying the mapped error code.</summary>
    public static IrohException ToException(EndpointResult result, string message) =>
        new(ToErrorCode(result), $"{message} (native result: {ToErrorCode(result)}).");

    /// <summary>Maps a native endpoint result code to its managed counterpart.</summary>
    public static IrohErrorCode ToErrorCode(EndpointResult result) => result switch
    {
        EndpointResult.ENDPOINT_RESULT_OK => IrohErrorCode.Ok,
        EndpointResult.ENDPOINT_RESULT_BIND_ERROR => IrohErrorCode.BindError,
        EndpointResult.ENDPOINT_RESULT_ACCEPT_FAILED => IrohErrorCode.AcceptFailed,
        EndpointResult.ENDPOINT_RESULT_ACCEPT_UNI_FAILED => IrohErrorCode.AcceptUniFailed,
        EndpointResult.ENDPOINT_RESULT_ACCEPT_BI_FAILED => IrohErrorCode.AcceptBiFailed,
        EndpointResult.ENDPOINT_RESULT_CONNECT_UNI_ERROR => IrohErrorCode.ConnectUniError,
        EndpointResult.ENDPOINT_RESULT_CONNECT_BI_ERROR => IrohErrorCode.ConnectBiError,
        EndpointResult.ENDPOINT_RESULT_CONNECT_ERROR => IrohErrorCode.ConnectError,
        EndpointResult.ENDPOINT_RESULT_ADDR_ERROR => IrohErrorCode.AddrError,
        EndpointResult.ENDPOINT_RESULT_SEND_ERROR => IrohErrorCode.SendError,
        EndpointResult.ENDPOINT_RESULT_READ_ERROR => IrohErrorCode.ReadError,
        EndpointResult.ENDPOINT_RESULT_TIMEOUT => IrohErrorCode.Timeout,
        EndpointResult.ENDPOINT_RESULT_CLOSE_ERROR => IrohErrorCode.CloseError,
        EndpointResult.ENDPOINT_RESULT_INCOMING_ERROR => IrohErrorCode.IncomingError,
        _ => IrohErrorCode.ConnectionTypeError, // unreachable for well-formed native results
    };
}
