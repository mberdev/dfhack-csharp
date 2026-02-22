using Grpc.Core;
using Dfproto;
using System;


try
{
    var channel = new Channel("localhost", 5000, ChannelCredentials.Insecure);
    var client = new BasicApiRpcService.BasicApiRpcServiceClient(channel);

    var version = client.GetDFVersion(new EmptyMessage());
    Console.WriteLine("Succesffully called method 'GetDFVersion' of DFHack.");
    Console.WriteLine("DFHack version: " + version.Value);

    await channel.ShutdownAsync();
}
catch (Grpc.Core.RpcException ex)
{
    Console.WriteLine("Failed to call DFHack.");
    Console.WriteLine("Make sure that Dwarf Fortress is currently running and that the version of DFHack that matches this library is installed on DF and is enabled.");
    Console.WriteLine("");
    Console.WriteLine("Details:");
    Console.WriteLine(ex.Status.Detail);
}
