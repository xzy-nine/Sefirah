using NetCoreServer;
using System.Net;
using SocketError = System.Net.Sockets.SocketError;
using UdpClient = NetCoreServer.UdpClient;
using TcpClient = NetCoreServer.TcpClient;
using TcpServer = NetCoreServer.TcpServer;

namespace Sefirah.Services.Socket;

public partial class ServerSession(TcpServer server, ITcpServerProvider socketProvider) : TcpSession(server)
{

    protected override void OnDisconnected()
    {
        socketProvider.OnDisconnected(this);
    }

    protected override void OnConnected()
    {
        socketProvider.OnConnected(this);
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        socketProvider.OnReceived(this, buffer, offset, size);
    }

    protected override void OnError(SocketError error)
    {
        socketProvider.OnError(error);
    }
}

public partial class Server(IPAddress address, int port, ITcpServerProvider socketProvider, ILogger logger) : TcpServer(address, port)
{
    protected override TcpSession CreateSession()
    {
        logger.LogDebug("Creating new session");
        return new ServerSession(this, socketProvider);
    }

    protected override void OnError(SocketError error)
    {
        socketProvider.OnError(error);
    }
}

public partial class Client(string address, int port, ITcpClientProvider socketProvider) : TcpClient(address, port)
{
    protected override void OnConnected()
    {
        socketProvider.OnConnected();
    }

    protected override void OnDisconnected()
    {
        socketProvider.OnDisconnected();
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        socketProvider.OnReceived(buffer, offset, size);
    }

    protected override void OnError(SocketError error)
    {
        socketProvider.OnError(error);
    }
}


public partial class MulticastClient(string address, int port, IUdpClientProvider socketProvider, ILogger logger) : UdpClient(address, port)
{

    protected override void OnConnected()
    {
        ReceiveAsync();
    }

    protected override void OnDisconnected()
    {
    }

    protected override void OnReceived(EndPoint endpoint, byte[] buffer, long offset, long size)
    {
        socketProvider.OnReceived(endpoint, buffer, offset, size);
        ReceiveAsync();
    }
    protected override void OnError(SocketError error)
    {
        logger.LogError("Session {Id} encountered error: {error}", Id, error);
    }
}
