using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;

namespace Dfproto
{
    /// <summary>
    /// A <see cref="CallInvoker"/> that speaks the DFHack native binary protocol
    /// over a plain TCP socket instead of HTTP/2 gRPC.
    ///
    /// Usage:
    ///   using var invoker = DFHackCallInvoker.Connect("127.0.0.1", 5000);
    ///   var client = new HandshakeRpcService.HandshakeRpcServiceClient(invoker);
    ///   var reply = client.Handshake(new HandshakeRequest { ... });
    /// </summary>
    public sealed class DFHackCallInvoker : CallInvoker, IDisposable
    {
        // Reply IDs used by DFHack in response message headers.
        private const short RPC_REPLY_OK   = -1;
        private const short RPC_REPLY_FAIL = -2;
        private const short RPC_REPLY_TEXT = -3;

        // BindMethod is always ID 0 (fixed by the DFHack protocol).
        private const short BIND_METHOD_ID = 0;

        private readonly Socket _socket;
        private readonly Dictionary<string, int> _methodIdCache = new Dictionary<string, int>();
        private readonly object _lock = new object();

        private DFHackCallInvoker(Socket socket)
        {
            _socket = socket;
        }

        /// <summary>
        /// Connects a TCP socket to DFHack and performs the low-level protocol handshake
        /// ("DFHack?\n" magic bytes + protocol version). Returns a ready-to-use invoker.
        /// </summary>
        public static DFHackCallInvoker Connect(string host, int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(host, port);

            // Outgoing handshake: "DFHack?\n" (8 bytes) + little-endian int32(1) (4 bytes)
            var outBuf = new byte[12];
            Encoding.ASCII.GetBytes("DFHack?\n").CopyTo(outBuf, 0);
            BitConverter.GetBytes((int)1).CopyTo(outBuf, 8);
            socket.Send(outBuf);

            // Incoming handshake: "DFHack!\n" (8 bytes) + little-endian int32(1) (4 bytes)
            var inBuf = new byte[12];
            ReceiveAll(socket, inBuf);

            string magic = Encoding.ASCII.GetString(inBuf, 0, 8);
            if (magic != "DFHack!\n")
                throw new IOException(
                    $"DFHack handshake failed: unexpected magic '{magic.Replace("\n", "\\n")}'");

            int version = BitConverter.ToInt32(inBuf, 8);
            if (version != 1)
                throw new IOException(
                    $"DFHack handshake failed: unexpected protocol version {version}");

            return new DFHackCallInvoker(socket);
        }

        // ---- CallInvoker overrides ----

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string host, CallOptions options, TRequest request)
        {
            lock (_lock)
            {
                int id = GetOrBindMethod(method);
                SendMessage(id, ((IMessage)request).ToByteArray());
                return ReceiveReply(method.ResponseMarshaller);
            }
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string host, CallOptions options, TRequest request)
        {
            var task = Task.Run(() => BlockingUnaryCall(method, host, options, request));
            return new AsyncUnaryCall<TResponse>(
                task,
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string host, CallOptions options, TRequest request)
            => throw new NotSupportedException("DFHack does not use server-streaming RPCs.");

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string host, CallOptions options)
            => throw new NotSupportedException("DFHack does not use client-streaming RPCs.");

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string host, CallOptions options)
            => throw new NotSupportedException("DFHack does not use duplex-streaming RPCs.");

        public void Dispose() => _socket.Dispose();

        // ---- private helpers ----

        /// <summary>
        /// Returns the DFHack-assigned RPC method ID for the given gRPC method,
        /// calling BindMethod on the first use and caching the result.
        /// </summary>
        // Returns the proto fully-qualified message name (e.g. "dfproto.EmptyMessage")
        // by reading the static Descriptor property that Google.Protobuf generates on every message type.
        private static string GetProtoFullName<T>() where T : class
        {
            var prop = typeof(T).GetProperty("Descriptor",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prop?.GetValue(null) is Google.Protobuf.Reflection.MessageDescriptor desc)
                return desc.FullName;
            return typeof(T).Name; // fallback
        }

        private int GetOrBindMethod<TRequest, TResponse>(Method<TRequest, TResponse> method)
            where TRequest : class
            where TResponse : class
        {
            if (_methodIdCache.TryGetValue(method.FullName, out int cached))
                return cached;

            var bindRequest = new CoreBindRequest
            {
                Method    = method.Name,
                InputMsg  = GetProtoFullName<TRequest>(),
                OutputMsg = GetProtoFullName<TResponse>(),
            };

            SendMessage(BIND_METHOD_ID, bindRequest.ToByteArray());

            byte[] replyPayload = ReceiveRawReply();
            int id = CoreBindReply.Parser.ParseFrom(replyPayload).AssignedId;
            _methodIdCache[method.FullName] = id;
            return id;
        }

        /// <summary>
        /// Sends an 8-byte DFHack message header followed by the protobuf payload.
        /// Header layout: int16 id | int16 pad(0) | int32 size
        /// </summary>
        private void SendMessage(int id, byte[] payload)
        {
            var header = new byte[8];
            BitConverter.GetBytes((short)id).CopyTo(header, 0);
            // bytes 2-3: pad — left as zero
            BitConverter.GetBytes(payload.Length).CopyTo(header, 4);
            _socket.Send(header);
            if (payload.Length > 0)
                _socket.Send(payload);
        }

        /// <summary>
        /// Deserializes the next OK reply from the socket using the given marshaller.
        /// </summary>
        private TResponse ReceiveReply<TResponse>(Marshaller<TResponse> marshaller)
            where TResponse : class
        {
            byte[] payload = ReceiveRawReply();
            return marshaller.ContextualDeserializer(new ByteDeserializationContext(payload));
        }

        /// <summary>
        /// Reads messages from the socket, skipping any RPC_REPLY_TEXT notifications,
        /// until RPC_REPLY_OK (returns payload) or RPC_REPLY_FAIL (throws).
        /// </summary>
        private byte[] ReceiveRawReply()
        {
            while (true)
            {
                var header = new byte[8];
                ReceiveAll(_socket, header);
                short replyId = BitConverter.ToInt16(header, 0);
                int   size    = BitConverter.ToInt32(header, 4);

                // RPC_REPLY_FAIL is special: size is NOT a payload length, it IS the error code.
                // No payload bytes follow — reading any would block forever.
                if (replyId == RPC_REPLY_FAIL)
                    throw new RpcException(new Status(StatusCode.Internal,
                        $"DFHack returned RPC_REPLY_FAIL (error code: {size})."));

                byte[] payload = new byte[size];
                if (size > 0)
                    ReceiveAll(_socket, payload);

                if (replyId == RPC_REPLY_TEXT)
                {
                    Console.WriteLine("DFHack text notification: " + Encoding.UTF8.GetString(payload));
                    continue;
                }

                if (replyId == RPC_REPLY_OK)
                    return payload;

                throw new RpcException(new Status(StatusCode.Internal,
                    $"Unexpected DFHack reply id: {replyId}"));
            }
        }

        private static void ReceiveAll(Socket socket, byte[] buffer)
        {
            int received = 0;
            while (received < buffer.Length)
            {
                int n = socket.Receive(buffer, received, buffer.Length - received, SocketFlags.None);
                if (n <= 0)
                    throw new IOException("Connection closed by DFHack.");
                received += n;
            }
        }

        /// <summary>
        /// Minimal <see cref="DeserializationContext"/> backed by a byte array,
        /// used to feed received socket bytes into the generated protobuf marshallers.
        /// </summary>
        private sealed class ByteDeserializationContext : DeserializationContext
        {
            private readonly byte[] _data;
            public ByteDeserializationContext(byte[] data) { _data = data; }

            public override int PayloadLength => _data.Length;

            public override byte[] PayloadAsNewBuffer() => (byte[])_data.Clone();

            public override System.Buffers.ReadOnlySequence<byte> PayloadAsReadOnlySequence()
                => new System.Buffers.ReadOnlySequence<byte>(_data);
        }
    }
}
