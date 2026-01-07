using NetCoreServer;
using System.Net;
using SocketError = System.Net.Sockets.SocketError;
using UdpClient = NetCoreServer.UdpClient;
using TcpClient = NetCoreServer.TcpClient;
using TcpServer = NetCoreServer.TcpServer;

namespace Sefirah.Services.Socket;

// 统一的ServerSession类，用于包装NetCoreServer的会话对象
public class ServerSession
{
    private readonly NetCoreServer.TcpSession? _tcpSession;
    private readonly NetCoreServer.SslSession? _sslSession;
    
    public ServerSession(NetCoreServer.TcpSession tcpSession)
    {
        _tcpSession = tcpSession;
    }
    
    public ServerSession(NetCoreServer.SslSession sslSession)
    {
        _sslSession = sslSession;
    }
    
    // 会话ID
    public string Id => _tcpSession?.Id.ToString() ?? _sslSession?.Id.ToString() ?? string.Empty;
    
    // 会话状态
    public bool IsConnected => _tcpSession?.IsConnected ?? _sslSession?.IsConnected ?? false;
    
    // 会话的Socket对象
    public System.Net.Sockets.Socket Socket => _tcpSession?.Socket ?? _sslSession?.Socket ?? throw new InvalidOperationException("Session has no socket");
    
    // 断开会话
    public void Disconnect()
    {
        if (_tcpSession != null)
            _tcpSession.Disconnect();
        else if (_sslSession != null)
            _sslSession.Disconnect();
    }
    
    // 释放会话资源
    public void Dispose()
    {
        if (_tcpSession != null)
            _tcpSession.Dispose();
        else if (_sslSession != null)
            _sslSession.Dispose();
    }
    
    // 发送消息，适配不同的Send方法重载
    public void Send(byte[] buffer, long offset, long size)
    {
        if (_tcpSession != null)
            _tcpSession.Send(buffer, (int)offset, (int)size);
        else if (_sslSession != null)
            _sslSession.Send(buffer, (int)offset, (int)size);
    }
    
    // 获取内部会话对象
    public T GetInternalSession<T>() where T : class
    {
        if (_tcpSession is T tcpSession) return tcpSession;
        if (_sslSession is T sslSession) return sslSession;
        throw new InvalidCastException($"Cannot cast session to {typeof(T).Name}");
    }
}

// SSL Session and Server
public class SslServerSession : NetCoreServer.SslSession
{
    private readonly ITcpServerProvider _socketProvider;
    
    public SslServerSession(NetCoreServer.SslServer server, ITcpServerProvider socketProvider) : base(server)
    {
        _socketProvider = socketProvider;
    }
    
    protected override void OnDisconnected()
    {
        _socketProvider.OnDisconnected(new ServerSession(this));
    }

    protected override void OnConnected()
    {
        _socketProvider.OnConnected(new ServerSession(this));
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        _socketProvider.OnReceived(new ServerSession(this), buffer, offset, size);
    }

    protected override void OnError(SocketError error)
    {
        _socketProvider.OnError(error);
    }
}

public class SslServer : NetCoreServer.SslServer
{
    private readonly ITcpServerProvider _socketProvider;
    private readonly ILogger _logger;
    
    public SslServer(SslContext context, IPAddress address, int port, ITcpServerProvider socketProvider, ILogger logger) : base(context, address, port)
    {
        _socketProvider = socketProvider;
        _logger = logger;
    }
    
    protected override NetCoreServer.SslSession CreateSession()
    {
        _logger.LogDebug("Creating new SSL session");
        return new SslServerSession(this, _socketProvider);
    }

    protected override void OnError(SocketError error)
    {
        _socketProvider.OnError(error);
    }
}

// Plain TCP Session and Server
public class TcpServerSession : NetCoreServer.TcpSession
{
    private readonly ITcpServerProvider _socketProvider;
    
    public TcpServerSession(NetCoreServer.TcpServer server, ITcpServerProvider socketProvider) : base(server)
    {
        _socketProvider = socketProvider;
    }
    
    protected override void OnDisconnected()
    {
        _socketProvider.OnDisconnected(new ServerSession(this));
    }

    protected override void OnConnected()
    {
        _socketProvider.OnConnected(new ServerSession(this));
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        _socketProvider.OnReceived(new ServerSession(this), buffer, offset, size);
    }

    protected override void OnError(SocketError error)
    {
        _socketProvider.OnError(error);
    }
}

public class TcpServer : NetCoreServer.TcpServer
{
    private readonly ITcpServerProvider _socketProvider;
    private readonly ILogger _logger;
    
    public TcpServer(IPAddress address, int port, ITcpServerProvider socketProvider, ILogger logger) : base(address, port)
    {
        _socketProvider = socketProvider;
        _logger = logger;
    }
    
    protected override NetCoreServer.TcpSession CreateSession()
    {
        _logger.LogDebug("Creating new TCP session");
        return new TcpServerSession(this, _socketProvider);
    }

    protected override void OnError(SocketError error)
    {
        _socketProvider.OnError(error);
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
        socketProvider.OnDisconnected();
    }

    protected override void OnReceived(EndPoint endpoint, byte[] buffer, long offset, long size)
    {
        socketProvider.OnReceived(endpoint, buffer, offset, size);
        ReceiveAsync();
    }
    protected override void OnError(SocketError error)
    {
        logger.LogError("会话 {Id} 遇到错误：{error}", Id, error);
    }
}
