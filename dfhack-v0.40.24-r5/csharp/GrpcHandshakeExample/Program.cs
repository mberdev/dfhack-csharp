using System;
using Grpc.Core;
using Dfproto;

//var host = "localhost";
var host = "127.0.0.1";
//var host = "::1";

int port = 5000;

Console.WriteLine($"Connecting to DFHack at '{host}:{port}' via gRPC...");

var channel = new Channel(host, port, ChannelCredentials.Insecure);
var client = new HandshakeRpcServiceClient(channel);

var request = new HandshakeRequest
{
    RequestMagic = "DFHack?\n",
    ProtocolVersion = 1
};

Console.WriteLine("Sending handshake request...");

HandshakeReply reply;
try
{
    reply = client.Handshake(request);
} catch (RpcException e)
{
    Console.WriteLine("gRPC call failed: " + e.Message);
    return;
}

Console.WriteLine("Received reply:");
Console.WriteLine("  ResponseMagic: " + reply.ResponseMagic.Replace("\n", "\\n"));
Console.WriteLine("  ProtocolVersion: " + reply.ProtocolVersion);

if (reply.ResponseMagic != "DFHack!\n")
{
    Console.WriteLine("Unexpected response magic: " + reply.ResponseMagic.Replace("\n", "\\n"));
    return;
}

if (reply.ProtocolVersion != 1)
{
    Console.WriteLine($"Unexpected protocol version: {reply.ProtocolVersion}");
    return;
}

Console.WriteLine("DFHack replied what we expected.");

Console.WriteLine("\nThis example program successfully connected to DFHack via gRPC and it responded!");

channel.ShutdownAsync().Wait();
