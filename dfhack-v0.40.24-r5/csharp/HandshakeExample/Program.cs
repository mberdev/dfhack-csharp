using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

string DFHackHandshakeRequest = "DFHack?\n";
string DFHackHandshakeReply = "DFHack!\n";


//var host = "localhost";
var host = "127.0.0.1";
//var host = "::1";

int port = 5000;

Console.WriteLine($"Connecting socket to host '{host}' on port {port}...");

var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
try
{
    socket.Connect(host, 5000);
} catch (SocketException){
    Console.WriteLine("Could not connect socket)");
    return;
}

Console.WriteLine("Connection successful.");

var headerList = new List<byte>();
headerList.AddRange(Encoding.ASCII.GetBytes(DFHackHandshakeRequest)); // See DFHack's handshake documentation
headerList.AddRange(BitConverter.GetBytes(1)); // 1 -> [1, 0, 0, 0]. Supposed to be DFHack's protocol version, on 4 bytes. See DFHack's handshake documentation
byte[] header = headerList.ToArray();

Console.WriteLine("Sending DFHack header and version...");

if (socket.Send(header) != header.Length)
{
    Console.WriteLine("Could not send handshake header.\n");
    return;
}

Console.WriteLine("Sent successfully.");
Console.WriteLine("Receiving DFHack's response and protocol version...");

// Read the hanshake reply magic word
byte[] replyBuffer = new byte[DFHackHandshakeReply.Length];
int received = 0;
while (received < replyBuffer.Length)
{
    int cnt = socket.Receive(replyBuffer, received, replyBuffer.Length - received, SocketFlags.None);
    if (cnt <= 0) break;
    received += cnt;
}

if (Encoding.ASCII.GetString(replyBuffer) != DFHackHandshakeReply)
{
    Console.WriteLine("Handshake reply mismatch. Expected: " + DFHackHandshakeReply.Replace("\n", "\\n") + ", got: " + Encoding.ASCII.GetString(replyBuffer).Replace("\n", "\\n"));
    return;
}


// Read 4 more bytes. Assembled together (as an int32) they're supposed to be DFHack's protocol version. See documentation
byte[] versionBuf = new byte[4];
int versionReceived = 0;
while (versionReceived < versionBuf.Length)
{
    int cnt = socket.Receive(versionBuf, versionReceived, versionBuf.Length - versionReceived, SocketFlags.None);
    if (cnt <= 0) return;
    versionReceived += cnt;
}

if (versionBuf[0] != 1 || versionBuf[1] != 0 || versionBuf[2] != 0 || versionBuf[3] != 0) // Version [1,0,0,0] (i.e., version 1)
{
    Console.WriteLine($"Unexpected protocol version: [{versionBuf[0]}, {versionBuf[1]}, {versionBuf[2]}, {versionBuf[3]}]");
    return;
}

Console.WriteLine("DFHack replied what we expected.");


Console.WriteLine("\nThis example program successfuly connected to DFHack and it responded!");


socket.Close();
