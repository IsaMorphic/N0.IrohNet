using System.Text;
using Xunit;

namespace N0.IrohNet.Tests;

/// <summary>
/// Pins the raw FFI contract the safe layer is built on: pre-allocated containers via
/// <c>*_default()</c>, configs freed by the caller after a borrowed bind, a freshly parsed
/// <c>EndpointAddr</c> per connect (ownership transfers), and close-then-no-free teardown.
/// </summary>
public class RawFfiContractTests
{
    [Fact]
    public unsafe void CanonicalPattern_BindConnectEchoTeardown()
    {
        byte[] alpn = "test/1"u8.ToArray();
        byte[] message = [0x31, 0x32, 0x33, 0x34, 0x35];

        Endpoint* server = BindEndpoint(alpn);
        nint serverPointer = (nint)server;
        try
        {
            string ticket = AddrString(server);

            nint clientPointer = (nint)BindEndpoint(alpn);
            try
            {
                long serverBytesEchoed = 0;
                Thread serverEcho = new(() =>
                {
                    Endpoint* endpoint = (Endpoint*)serverPointer;
                    Connection* connection = iroh.connection_default();
                    Vec_uint8 negotiatedAlpn = default; // zero-initialized out-container, filled on success
                    if (iroh.endpoint_accept_any(&endpoint, &negotiatedAlpn, &connection) != EndpointResult.ENDPOINT_RESULT_OK)
                    {
                        return;
                    }

                    iroh.rust_buffer_free(negotiatedAlpn); // the out-buffer is owned by the caller

                    SendStream* send = iroh.send_stream_default();
                    RecvStream* recv = iroh.recv_stream_default();
                    if (iroh.connection_accept_bi(&connection, &send, &recv) != EndpointResult.ENDPOINT_RESULT_OK)
                    {
                        return;
                    }

                    byte[] buffer = new byte[message.Length];
                    long total = 0;
                    while (total < buffer.Length)
                    {
                        long read;
                        fixed (byte* bufferPtr = buffer)
                        {
                            read = iroh.recv_stream_read(&recv, new slice_mut_uint8 { ptr = bufferPtr + total, len = (nuint)(buffer.Length - total) });
                        }

                        if (read <= 0)
                        {
                            return; // stream ended prematurely
                        }

                        total += read;
                    }

                    fixed (byte* bufferPtr = buffer)
                    {
                        iroh.send_stream_write(&send, new slice_ref_uint8 { ptr = bufferPtr, len = (nuint)total });
                    }

                    iroh.send_stream_finish(send); // consumes the send container
                    iroh.recv_stream_free(recv);
                    iroh.connection_close(connection);
                    Interlocked.Exchange(ref serverBytesEchoed, total);
                });
                serverEcho.Start();

                // Client side: connect with a freshly parsed EndpointAddr (passed by value, consumed
                // by connect — it must never be freed or reused here).
                Connection* clientConnection = iroh.connection_default();
                EndpointAddr remote = ParseAddr(ticket);
                Endpoint* clientEndpoint = (Endpoint*)clientPointer;
                bool connected;
                fixed (byte* alpnPtr = alpn)
                {
                    connected = iroh.endpoint_connect(&clientEndpoint, new slice_ref_uint8 { ptr = alpnPtr, len = (nuint)alpn.Length }, remote, &clientConnection)
                        == EndpointResult.ENDPOINT_RESULT_OK;
                }

                Assert.True(connected, "endpoint_connect failed");

                SendStream* clientSend = iroh.send_stream_default();
                RecvStream* clientRecv = iroh.recv_stream_default();
                Assert.Equal(EndpointResult.ENDPOINT_RESULT_OK, iroh.connection_open_bi(&clientConnection, &clientSend, &clientRecv));

                // The first write goes out immediately after open: waiting for the peer's readiness deadlocks the FFI.
                bool wrote;
                fixed (byte* messagePtr = message)
                {
                    wrote = iroh.send_stream_write(&clientSend, new slice_ref_uint8 { ptr = messagePtr, len = (nuint)message.Length })
                        == EndpointResult.ENDPOINT_RESULT_OK;
                }

                Assert.True(wrote, "send_stream_write failed");

                byte[] echoed = new byte[message.Length];
                long received = 0;
                while (received < echoed.Length)
                {
                    long read;
                    fixed (byte* echoedPtr = echoed)
                    {
                        read = iroh.recv_stream_read_timeout(&clientRecv, new slice_mut_uint8 { ptr = echoedPtr + received, len = (nuint)(echoed.Length - received) }, 30_000);
                    }

                    Assert.True(read > 0, $"Peer stream ended (or timed out) after {received} of {echoed.Length} bytes (read: {read}).");
                    received += read;
                }

                Assert.Equal(message, echoed);
                Assert.True(serverEcho.Join(TimeSpan.FromSeconds(30)), "the server echo thread did not finish");
                Assert.Equal(message.Length, Interlocked.Read(ref serverBytesEchoed));

                iroh.send_stream_finish(clientSend); // consumes the send container
                iroh.recv_stream_free(clientRecv);
                iroh.connection_close(clientConnection); // consumes the connection container
            }
            finally
            {
                iroh.endpoint_close((Endpoint*)clientPointer); // consumes the endpoint container; endpoint_free must not follow
            }
        }
        finally
        {
            iroh.endpoint_close((Endpoint*)server);
        }
    }

    [Fact]
    public unsafe void RustBuffer_AllocLenFree_RoundTrip()
    {
        const int Size = 64;
        Vec_uint8 buffer = iroh.rust_buffer_alloc(Size);
        try
        {
            Assert.Equal((nuint)Size, iroh.rust_buffer_len(&buffer));
            Assert.True(buffer.ptr != null, "rust_buffer_alloc returned a null pointer for a non-zero size");

            for (int i = 0; i < Size; i++)
            {
                buffer.ptr[i] = unchecked((byte)(i * 7 + 1));
            }

            for (int i = 0; i < Size; i++)
            {
                Assert.Equal(unchecked((byte)(i * 7 + 1)), buffer.ptr[i]);
            }
        }
        finally
        {
            iroh.rust_buffer_free(buffer); // exactly one free for the one alloc
        }

        Vec_uint8 empty = iroh.rust_buffer_alloc(0);
        try
        {
            Assert.Equal(0u, (uint)iroh.rust_buffer_len(&empty));
        }
        finally
        {
            iroh.rust_buffer_free(empty);
        }
    }

    /// <summary>Binds an endpoint with the canonical pattern; the config is borrowed by bind and freed by the caller.</summary>
    private static unsafe Endpoint* BindEndpoint(byte[] alpn)
    {
        EndpointConfig config = iroh.endpoint_config_default();
        try
        {
            config.relay_mode = RelayMode.RELAY_MODE_DISABLED;
            config.discovery_cfg = DiscoveryConfig.DISCOVERY_CONFIG_NONE;
            fixed (byte* alpnPtr = alpn)
            {
                iroh.endpoint_config_add_alpn(&config, new slice_ref_uint8 { ptr = alpnPtr, len = (nuint)alpn.Length });
            }

            Endpoint* endpoint = iroh.endpoint_default(); // pre-allocated out container
            EndpointResult result = iroh.endpoint_bind(&config, null, null, &endpoint);
            if (result != EndpointResult.ENDPOINT_RESULT_OK)
            {
                throw new InvalidOperationException($"endpoint_bind failed: {result}");
            }

            return endpoint;
        }
        finally
        {
            iroh.endpoint_config_free(config); // the caller owns the config even after a successful bind
        }
    }

    /// <summary>Reads the endpoint's ticket string; the string and the addr container are both owned here.</summary>
    private static unsafe string AddrString(Endpoint* endpoint)
    {
        EndpointAddr addr = iroh.endpoint_addr_default();
        EndpointResult result = iroh.endpoint_addr(&endpoint, &addr);
        if (result != EndpointResult.ENDPOINT_RESULT_OK)
        {
            throw new InvalidOperationException($"endpoint_addr failed: {result}");
        }

        try
        {
            byte* native = iroh.endpoint_addr_as_str(&addr);
            int length = 0;
            while (native[length] != 0)
            {
                length++;
            }

            string ticket = Encoding.UTF8.GetString(native, length);
            iroh.rust_free_string(native);
            return ticket;
        }
        finally
        {
            iroh.endpoint_addr_free(addr); // owned here: never handed to endpoint_connect
        }
    }

    /// <summary>Parses a fresh EndpointAddr; the result is consumed by endpoint_connect and must not be freed.</summary>
    private static unsafe EndpointAddr ParseAddr(string ticket)
    {
        byte[] input = Encoding.UTF8.GetBytes(ticket + '\0');
        EndpointAddr addr = iroh.endpoint_addr_default();
        fixed (byte* inputPtr = input)
        {
            if (iroh.endpoint_addr_from_string(inputPtr, &addr) != AddrResult.ADDR_RESULT_OK)
            {
                throw new InvalidOperationException($"endpoint_addr_from_string failed for '{ticket}'");
            }
        }

        return addr;
    }
}
