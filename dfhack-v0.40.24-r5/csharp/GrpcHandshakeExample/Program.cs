using System;
using Grpc.Core;
using Dfproto;

//var host = "localhost";
var host = "127.0.0.1";
//var host = "::1";

int port = 5000;

Console.WriteLine($"Connecting to DFHack at '{host}:{port}'...");

DFHackCallInvoker invoker;
try
{
    invoker = DFHackCallInvoker.Connect(host, port);
}
catch (Exception ex)
{
    Console.WriteLine("Connection or handshake failed: " + ex.Message);
    return;
}

Console.WriteLine("TCP handshake successful.");

var basicApiClient = new BasicApiRpcService.BasicApiRpcServiceClient(invoker);
var coreProtocolClient = new CoreProtocolRpcService.CoreProtocolRpcServiceClient(invoker);

coreProtocolClient.CoreSuspend(new EmptyMessage(), deadline: DateTime.Now.AddSeconds(5));

StringMessage reply;
try
{
    reply = basicApiClient.GetVersion(new EmptyMessage());
}
catch (RpcException e)
{
    Console.WriteLine("RPC call failed: " + e.Message);
    invoker.Dispose();
    return;
}

Console.WriteLine("DFHack version: " + reply.Value);

invoker.Dispose();
