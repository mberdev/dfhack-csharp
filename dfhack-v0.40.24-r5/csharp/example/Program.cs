using Grpc.Core;
using Dfproto;
using System;

var channel = new Channel("localhost", 5000, ChannelCredentials.Insecure);
var client = new BasicApiRpcService.BasicApiRpcServiceClient(channel);

var version = client.GetVersion(new EmptyMessage());
Console.WriteLine("DFHack version: " + version.Value);

await channel.ShutdownAsync();
