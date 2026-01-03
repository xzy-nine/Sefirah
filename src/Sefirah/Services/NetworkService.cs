using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using NetCoreServer;
using Sefirah.Data.Enums;
using Sefirah.Data.Contracts;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using Sefirah.Services.Socket;
using Sefirah.Utils;
using Sefirah.Utils.Serialization;
using Uno.Logging;

namespace Sefirah.Services;
public class NetworkService(
    Func<IMessageHandler> messageHandlerFactory,
    ILogger<NetworkService> logger,
    IDeviceManager deviceManager,
    IAdbService adbService) : INetworkService, ISessionManager, ITcpServerProvider
{
    private Server? server;
    public int ServerPort { get; private set; } = 23333;
    private bool isRunning;

    private readonly Lazy<IMessageHandler> messageHandler = new(messageHandlerFactory);
    private readonly Dictionary<Guid, string> sessionBuffers = new();
    private readonly Dictionary<string, ServerSession> deviceSessions = new();
    private readonly Dictionary<Guid, string> sessionDeviceMap = new();
    private readonly object sessionLock = new();
    private string? localPublicKey;
    private string? localDeviceId;
    private Timer? heartbeatTimer;
    private readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(4);
    private readonly TimeSpan heartbeatTimeout = TimeSpan.FromSeconds(25);
    
    private ObservableCollection<PairedDevice> PairedDevices => deviceManager.PairedDevices;

    /// <summary>
    /// Event fired when a device connection status changes
    /// </summary>
    public event EventHandler<(PairedDevice Device, bool IsConnected)>? ConnectionStatusChanged;

    public async Task<bool> StartServerAsync()
    {
        if (isRunning)
        {
            logger.LogWarning("服务器已在运行");
            return false;
        }
        try
        {
            var localDevice = await deviceManager.GetLocalDeviceAsync();
            localPublicKey = Encoding.UTF8.GetString(localDevice.PublicKey ?? Array.Empty<byte>());
            localDeviceId = localDevice.DeviceId;

            server = new Server(IPAddress.Any, ServerPort, this, logger)
            {
                OptionReuseAddress = true,
            };

            if (server.Start())
            {
                isRunning = true;
                logger.Info($"服务器已在端口 {ServerPort} 启动");
                StartHeartbeat();
                return true;
            }

            server.Dispose();
            server = null;

            logger.LogError("启动服务器失败");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError("启动服务器时发生错误：{ex}", ex);
            return false;
        }
    }

    /// <summary>
    /// 发送应用列表请求
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    public void SendAppListRequest(string deviceId)
    {
        logger.LogInformation("开始发送应用列表请求：deviceId={deviceId}", deviceId);
        
        Task.Run(async () =>
        {
            try
            {
                var device = PairedDevices.FirstOrDefault(d => d.Id == deviceId);
                if (device is null)
                {
                    logger.LogWarning("跳过发送：未找到设备 {deviceId}", deviceId);
                    return;
                }
                
                logger.LogDebug("找到设备：deviceId={deviceId}, name={deviceName}", deviceId, device.Name);

                if (device.SharedSecret is null)
                {
                    logger.LogWarning("无法发送加密消息：设备 {deviceId} 缺少共享密钥", deviceId);
                    return;
                }
                
                logger.LogDebug("设备有共享密钥，继续发送");

                if (localPublicKey is null || localDeviceId is null)
                {
                    logger.LogWarning("本地身份未初始化，跳过发送");
                    return;
                }
                
                logger.LogDebug("本地身份已初始化，继续发送");

                // 尝试获取设备的IP地址
                if (device.IpAddresses is null || device.IpAddresses.Count == 0)
                {
                    logger.LogWarning("跳过发送：设备 {deviceId} 没有IP地址", deviceId);
                    return;
                }
                
                string ipAddress = device.IpAddresses.First();
                const int notifyRelayPort = 23333; // 使用与本机相同的端口
                logger.LogDebug("使用设备IP地址：{ipAddress}:{port}", ipAddress, notifyRelayPort);

                // 构建应用列表请求对象
                var requestObj = new
                {
                    type = "APP_LIST_REQUEST",
                    scope = "user",
                    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                
                // 序列化为 JSON
                string requestJson = JsonSerializer.Serialize(requestObj);
                
                logger.LogInformation("应用列表请求 JSON：{requestJson}", requestJson);
                
                try
                {
                    logger.LogDebug("开始加密消息");
                    var encryptedPayload = NotifyCryptoHelper.Encrypt(requestJson, device.SharedSecret);
                    logger.LogDebug("消息加密成功，长度={length}", encryptedPayload.Length);
                    
                    var framedMessage = $"DATA_APP_LIST_REQUEST:{localDeviceId}:{localPublicKey}:{encryptedPayload}\n";
                    logger.LogDebug("构建的完整消息：{framedMessage}", framedMessage.Length > 100 ? framedMessage[..100] + "..." : framedMessage);
                    
                    byte[] messageBytes = Encoding.UTF8.GetBytes(framedMessage);
                    logger.LogDebug("消息字节长度：{length}", messageBytes.Length);

                    // 使用TCP客户端主动连接并发送消息
                    using var tcpClient = new System.Net.Sockets.TcpClient();
                    logger.LogDebug("正在连接到设备：{ipAddress}:{port}", ipAddress, notifyRelayPort);
                    await tcpClient.ConnectAsync(ipAddress, notifyRelayPort);
                    logger.LogDebug("连接成功");
                    
                    using var networkStream = tcpClient.GetStream();
                    await networkStream.WriteAsync(messageBytes, 0, messageBytes.Length);
                    logger.LogInformation("成功发送应用列表请求：deviceId={deviceId}", deviceId);
                    
                    // 关闭连接
                    tcpClient.Close();
                }
                catch (ObjectDisposedException ex)
                {
                    logger.LogError(ex, "发送应用列表请求时 Socket 已释放：deviceId={deviceId}", deviceId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "发送应用列表请求时出错：deviceId={deviceId}", deviceId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发送应用列表请求时出错：deviceId={deviceId}", deviceId);
            }
            finally
            {
                logger.LogInformation("应用列表请求发送流程结束：deviceId={deviceId}", deviceId);
            }
        });
    }
    
    /// <summary>
    /// 发送图标请求
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="packageNames">应用包名列表</param>
    public void SendIconRequest(string deviceId, List<string> packageNames)
    {
        logger.LogInformation("开始发送图标请求：deviceId={deviceId}, packageCount={packageCount}", deviceId, packageNames.Count);
        
        Task.Run(async () =>
        {
            try
            {
                var device = PairedDevices.FirstOrDefault(d => d.Id == deviceId);
                if (device is null)
                {
                    logger.LogWarning("跳过发送：未找到设备 {deviceId}", deviceId);
                    return;
                }
                
                logger.LogDebug("找到设备：deviceId={deviceId}, name={deviceName}", deviceId, device.Name);

                if (device.SharedSecret is null)
                {
                    logger.LogWarning("无法发送加密消息：设备 {deviceId} 缺少共享密钥", deviceId);
                    return;
                }
                
                logger.LogDebug("设备有共享密钥，继续发送");

                if (localPublicKey is null || localDeviceId is null)
                {
                    logger.LogWarning("本地身份未初始化，跳过发送");
                    return;
                }
                
                logger.LogDebug("本地身份已初始化，继续发送");

                // 尝试获取设备的IP地址
                if (device.IpAddresses is null || device.IpAddresses.Count == 0)
                {
                    logger.LogWarning("跳过发送：设备 {deviceId} 没有IP地址", deviceId);
                    return;
                }
                
                string ipAddress = device.IpAddresses.First();
                const int notifyRelayPort = 23333; // 使用与本机相同的端口
                logger.LogDebug("使用设备IP地址：{ipAddress}:{port}", ipAddress, notifyRelayPort);

                // 构建图标请求对象（支持单个或多个包名）
                object requestObj;
                if (packageNames.Count == 1)
                {
                    requestObj = new
                    {
                        type = "ICON_REQUEST",
                        packageName = packageNames.First(),
                        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }
                else
                {
                    requestObj = new
                    {
                        type = "ICON_REQUEST",
                        packageNames = packageNames,
                        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }
                
                // 序列化为 JSON
                string requestJson = JsonSerializer.Serialize(requestObj);
                
                logger.LogInformation("图标请求 JSON：{requestJson}", requestJson);
                
                try
                {
                    logger.LogDebug("开始加密消息");
                    var encryptedPayload = NotifyCryptoHelper.Encrypt(requestJson, device.SharedSecret);
                    logger.LogDebug("消息加密成功，长度={length}", encryptedPayload.Length);
                    
                    var framedMessage = $"DATA_ICON_REQUEST:{localDeviceId}:{localPublicKey}:{encryptedPayload}\n";
                    logger.LogDebug("构建的完整消息：{framedMessage}", framedMessage.Length > 100 ? framedMessage[..100] + "..." : framedMessage);
                    
                    byte[] messageBytes = Encoding.UTF8.GetBytes(framedMessage);
                    logger.LogDebug("消息字节长度：{length}", messageBytes.Length);

                    // 使用TCP客户端主动连接并发送消息
                    using var tcpClient = new System.Net.Sockets.TcpClient();
                    logger.LogDebug("正在连接到设备：{ipAddress}:{port}", ipAddress, notifyRelayPort);
                    await tcpClient.ConnectAsync(ipAddress, notifyRelayPort);
                    logger.LogDebug("连接成功");
                    
                    using var networkStream = tcpClient.GetStream();
                    await networkStream.WriteAsync(messageBytes, 0, messageBytes.Length);
                    logger.LogInformation("成功发送图标请求：deviceId={deviceId}, packageCount={packageCount}", deviceId, packageNames.Count);
                    
                    // 关闭连接
                    tcpClient.Close();
                }
                catch (ObjectDisposedException ex)
                {
                    logger.LogError(ex, "发送图标请求时 Socket 已释放：deviceId={deviceId}", deviceId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "发送图标请求时出错：deviceId={deviceId}", deviceId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发送图标请求时出错：deviceId={deviceId}", deviceId);
            }
            finally
            {
                logger.LogInformation("图标请求发送流程结束：deviceId={deviceId}", deviceId);
            }
        });
    }
    
    /// <summary>
    /// 发送图标请求（单个包名）
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="packageName">应用包名</param>
    public void SendIconRequest(string deviceId, string packageName)
    {
        SendIconRequest(deviceId, new List<string> { packageName });
    }

    public void SendMessage(string deviceId, string message)
    {
        try
        {
            var device = PairedDevices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null)
            {
                logger.LogWarning("跳过发送：未找到设备 {deviceId}", deviceId);
                return;
            }

            if (device.SharedSecret is null)
            {
                logger.LogWarning("无法发送加密消息：设备 {deviceId} 缺少共享密钥", deviceId);
                return;
            }

            if (localPublicKey is null || localDeviceId is null)
            {
                logger.LogWarning("本地身份未初始化，跳过发送");
                return;
            }

            if (!TryGetSession(deviceId, out var session))
            {
                logger.LogTrace("跳过发送：设备 {id} 未找到会话", deviceId);
                return;
            }
            
            if (session == null)
            {
                logger.LogTrace("跳过发送：设备 {id} 会话为 null", deviceId);
                return;
            }
            
            if (session.Socket == null)
            {
                logger.LogTrace("跳过发送：设备 {id} Socket 为 null", deviceId);
                return;
            }
            
            try
            {
                if (!session.Socket.Connected)
                {
                    logger.LogTrace("跳过发送：设备 {id} Socket 未连接", deviceId);
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                logger.LogTrace("跳过发送：设备 {id} Socket 已释放", deviceId);
                return;
            }

            // 根据消息内容选择消息类型
            string messageType = "DATA_JSON";
            
            logger.LogDebug("原始消息内容：{message}", message);
            
            // 直接检查消息中的 type 字段值，支持不同的引号格式
            if (message.Contains("APP_LIST_REQUEST", StringComparison.OrdinalIgnoreCase))
            {
                messageType = "DATA_APP_LIST_REQUEST";
                logger.LogDebug("识别到 APP_LIST_REQUEST 消息类型");
            }
            else if (message.Contains("ICON_REQUEST", StringComparison.OrdinalIgnoreCase))
            {
                messageType = "DATA_ICON_REQUEST";
                logger.LogDebug("识别到 ICON_REQUEST 消息类型");
            }
            else
            {
                logger.LogDebug("使用默认 DATA_JSON 消息类型");
            }

            try
            {
                var encryptedPayload = NotifyCryptoHelper.Encrypt(message, device.SharedSecret);
                var framedMessage = $"{messageType}:{localDeviceId}:{localPublicKey}:{encryptedPayload}\n";
                byte[] messageBytes = Encoding.UTF8.GetBytes(framedMessage);

                session.Send(messageBytes, 0, messageBytes.Length);
                logger.LogDebug("已发送消息：{messageType}，deviceId={deviceId}", messageType, deviceId);
            }
            catch (ObjectDisposedException ex)
            {
                logger.LogError(ex, "发送消息时 Socket 已释放：deviceId={deviceId}", deviceId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发送消息时出错：deviceId={deviceId}", deviceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送消息时出错：deviceId={deviceId}", deviceId);
        }
    }

    public void BroadcastMessage(string message)
    {
        try
        {
            var targets = GetConnectedDeviceIds();
            foreach (var deviceId in targets)
            {
                SendMessage(deviceId, message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError("向所有设备发送消息时出错：{ex}", ex);
        }
    }

    public void DisconnectDevice(string deviceId)
    {
        if (TryGetSession(deviceId, out var session) && session is not null)
        {
            DisconnectSession(session);
        }
    }

    private bool TryGetSession(string deviceId, out ServerSession? session)
    {
        lock (sessionLock)
        {
            return deviceSessions.TryGetValue(deviceId, out session);
        }
    }

    private List<string> GetConnectedDeviceIds()
    {
        lock (sessionLock)
        {
            return deviceSessions.Keys.ToList();
        }
    }

    private PairedDevice? GetDeviceBySession(ServerSession session)
    {
        string? deviceId = null;
        lock (sessionLock)
        {
            sessionDeviceMap.TryGetValue(session.Id, out deviceId);
        }

        return deviceId is null ? null : PairedDevices.FirstOrDefault(d => d.Id == deviceId);
    }

    private void BindSession(string deviceId, ServerSession session)
    {
        lock (sessionLock)
        {
            if (deviceSessions.TryGetValue(deviceId, out var existing) && existing.Id != session.Id)
            {
                try
                {
                    existing.Disconnect();
                    existing.Dispose();
                }
                catch
                {
                    // best-effort cleanup
                }

                // remove old mapping entry
                sessionDeviceMap.Remove(existing.Id);
            }

            deviceSessions[deviceId] = session;
            sessionDeviceMap[session.Id] = deviceId;
        }
    }

    private void UnbindSession(ServerSession session)
    {
        lock (sessionLock)
        {
            if (sessionDeviceMap.TryGetValue(session.Id, out var deviceId))
            {
                sessionDeviceMap.Remove(session.Id);
                if (deviceSessions.TryGetValue(deviceId, out var existing) && existing.Id == session.Id)
                {
                    deviceSessions.Remove(deviceId);
                }
            }
        }
    }

    private List<(string DeviceId, ServerSession Session)> GetSessionSnapshot()
    {
        lock (sessionLock)
        {
            return deviceSessions.Select(kvp => (kvp.Key, kvp.Value)).ToList();
        }
    }

    private void SendRaw(ServerSession session, string message)
    {
        try
        {
            string messageWithNewline = message + "\n";
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageWithNewline);
            session.Send(messageBytes, 0, messageBytes.Length);
        }
        catch (Exception ex)
        {
            logger.LogError("发送原始消息时出错：{ex}", ex);
        }
    }

    // Server side methods
    public void OnConnected(ServerSession session)
    {

    }

    public void OnDisconnected(ServerSession session)
    {
        sessionBuffers.Remove(session.Id);
        UnbindSession(session);
        DetachSession(session);
    }

    public void OnError(SocketError error)
    {
        logger.LogError("Socket 错误：{error}", error);
    }

    public async void OnReceived(ServerSession session, byte[] buffer, long offset, long size)
    {
        try
        {
            string newData = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);

            if (!sessionBuffers.TryGetValue(session.Id, out var bufferedData))
            {
                bufferedData = string.Empty;
            }

            bufferedData += newData;
            while (true)
            {
                int newlineIndex = bufferedData.IndexOf('\n');
                if (newlineIndex == -1)
                {
                    break;
                }

                string message = bufferedData[..newlineIndex].Trim();

                bufferedData = newlineIndex + 1 >= bufferedData.Length
                    ? string.Empty
                    : bufferedData[(newlineIndex + 1)..];

                if (string.IsNullOrEmpty(message)) continue;

                var device = GetDeviceBySession(session);
                if (device is null)
                {
                    await HandleHandshakeAsync(session, message);
                }
                else
                {
                    await ProcessProtocolMessageAsync(device, message);
                }
            }

            sessionBuffers[session.Id] = bufferedData;
        }
        catch (Exception ex)
        {
            logger.LogError("接收会话 {id} 数据时出错：{ex}", session.Id, ex);
            DisconnectSession(session);
        }
    }

    private async Task HandleHandshakeAsync(ServerSession session, string message)
    {
        if (!message.StartsWith("HANDSHAKE:"))
        {
            if (message.StartsWith("DATA_") || message.StartsWith("HEARTBEAT:"))
            {
                var attachedDevice = await TryAttachExistingDeviceSessionAsync(session, message);
                if (attachedDevice is not null)
                {
                    await ProcessProtocolMessageAsync(attachedDevice, message);
                    return;
                }
            }

            logger.LogWarning("收到意外的预握手消息，来源：{id}，消息：{message}", session.Id, message);
            // 非握手报文不处理，等待合法握手再次到来
            return;
        }

        var parts = message.Split(':');
        if (parts.Length < 3)
        {
            logger.LogWarning("握手格式无效");
            SendRaw(session, $"REJECT:{localDeviceId ?? string.Empty}");
            DisconnectSession(session);
            return;
        }

        var remoteDeviceId = parts[1];
        var remotePublicKey = parts[2];
        var discoveredName = PairedDevices.FirstOrDefault(d => d.Id == remoteDeviceId)?.Name;

        if (discoveredName is null)
        {
            var discovery = Ioc.Default.GetService<IDiscoveryService>();
            discoveredName = discovery?.DiscoveredDevices.FirstOrDefault(d => d.DeviceId == remoteDeviceId)?.DeviceName;
        }
        var connectedSessionIpAddress = session.Socket.RemoteEndPoint?.ToString()?.Split(':')[0];
        logger.Info($"收到握手来自 {connectedSessionIpAddress}");

        var device = await deviceManager.VerifyHandshakeAsync(remoteDeviceId, remotePublicKey, discoveredName, connectedSessionIpAddress);

        if (device is not null)
        {
            logger.Info($"设备 {device.Id} 已连接");

            device = await deviceManager.UpdateOrAddDeviceAsync(device, connectedDevice  =>
            {
                connectedDevice.ConnectionStatus = true;
                connectedDevice.Session = session;
                connectedDevice.RemotePublicKey = remotePublicKey;
                connectedDevice.SharedSecret ??= NotifyCryptoHelper.GenerateSharedSecretBytes(localPublicKey ?? string.Empty, remotePublicKey);
                deviceManager.ActiveDevice = connectedDevice;
                connectedDevice.LastHeartbeat = DateTime.UtcNow;

                if (connectedDevice.DeviceSettings.AdbAutoConnect && !string.IsNullOrEmpty(connectedSessionIpAddress))
                {
                    adbService.TryConnectTcp(connectedSessionIpAddress);
                }
            });

            BindSession(device.Id, session);

            if (localDeviceId is not null && localPublicKey is not null)
            {
                SendRaw(session, $"ACCEPT:{localDeviceId}:{localPublicKey}");
            }

            ConnectionStatusChanged?.Invoke(this, (device, true));
        }
        else
        {
            SendRaw(session, $"REJECT:{localDeviceId ?? string.Empty}");
            await Task.Delay(50);
            logger.Info("设备验证失败或被拒绝");
            DisconnectSession(session);
        }
    }

    private async Task ProcessProtocolMessageAsync(PairedDevice device, string message)
    {
        try
        {
            if (message.StartsWith("HEARTBEAT:"))
            {
                MarkDeviceAlive(device);
                return;
            }

            if (message.StartsWith("DATA_"))
            {
                var parts = message.Split(':');
                if (parts.Length < 4)
                {
                    logger.LogWarning("无效的 DATA 帧");
                    return;
                }

                if (device.SharedSecret is null)
                {
                    logger.LogWarning("设备 {id} 缺少共享密钥", device.Id);
                    return;
                }

                var messageType = parts[0];
                var encryptedPayload = string.Join(":", parts.Skip(3));
                var decryptedPayload = NotifyCryptoHelper.Decrypt(encryptedPayload, device.SharedSecret);
                logger.LogDebug("收到来自设备 {id} 的 {messageType} 有效负载，长度={len}", device.Id, messageType, decryptedPayload.Length);

                MarkDeviceAlive(device);
                
                // 处理不同类型的 DATA 消息
                switch (messageType)
                {
                    case "DATA_APP_LIST_REQUEST":
                        await HandleAppListRequestAsync(device, decryptedPayload);
                        break;
                    case "DATA_APP_LIST_RESPONSE":
                        await DispatchPayloadAsync(device, decryptedPayload);
                        break;
                    case "DATA_ICON_REQUEST":
                        await HandleIconRequestAsync(device, decryptedPayload);
                        break;
                    case "DATA_ICON_RESPONSE":
                        await DispatchPayloadAsync(device, decryptedPayload);
                        break;
                    case "DATA_JSON":
                        await DispatchPayloadAsync(device, decryptedPayload);
                        break;
                    default:
                        logger.LogWarning("不支持的 DATA 消息类型: {messageType}", messageType);
                        break;
                }
                return;
            }

            if (message.TrimStart().StartsWith('{') || message.TrimStart().StartsWith('['))
            {
                MarkDeviceAlive(device);
                await DispatchPayloadAsync(device, message);
                return;
            }

            logger.Debug("收到不支持的消息格式");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理协议消息时出错");
        }
    }

    private async Task HandleAppListRequestAsync(PairedDevice device, string decryptedPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(decryptedPayload);
            var root = doc.RootElement;
            
            // 检查是否为 APP_LIST_REQUEST
            if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "APP_LIST_REQUEST")
            {
                // 获取设备会话
                if (TryGetSession(device.Id, out var session) && session?.Socket?.Connected == true)
                {
                    // 获取应用列表（目前返回空列表，实际实现需要采集本机应用）
                    var apps = new List<object>();
                    
                    // 构建响应对象
                    var responseObj = new
                    {
                        type = "APP_LIST_RESPONSE",
                        scope = "user",
                        apps = apps,
                        total = apps.Count,
                        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    
                    string respJson = JsonSerializer.Serialize(responseObj);
                    string encryptedResp = NotifyCryptoHelper.Encrypt(respJson, device.SharedSecret);
                    string payload = $"DATA_APP_LIST_RESPONSE:{localDeviceId}:{localPublicKey}:{encryptedResp}";
                    byte[] payloadBytes = Encoding.UTF8.GetBytes(payload + "\n");
                    
                    session.Send(payloadBytes, 0, payloadBytes.Length);
                    logger.LogDebug("已发送 APP_LIST_RESPONSE 给设备 {deviceId}", device.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理应用列表请求时出错");
        }
    }

    private async Task HandleIconRequestAsync(PairedDevice device, string decryptedPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(decryptedPayload);
            var root = doc.RootElement;
            
            // 检查是否为 ICON_REQUEST
            if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "ICON_REQUEST")
            {
                // 获取设备会话
                if (TryGetSession(device.Id, out var session) && session?.Socket?.Connected == true)
                {
                    // 获取包名
                    string packageName = root.TryGetProperty("packageName", out var packageProp) ? packageProp.GetString() : string.Empty;
                    if (string.IsNullOrEmpty(packageName))
                    {
                        logger.LogWarning("图标请求缺少 packageName");
                        return;
                    }
                    
                    // 构建响应对象（目前返回空图标，实际实现需要获取应用图标）
                    var responseObj = new
                    {
                        type = "ICON_RESPONSE",
                        packageName = packageName,
                        iconData = string.Empty,
                        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    
                    string respJson = JsonSerializer.Serialize(responseObj);
                    string encryptedResp = NotifyCryptoHelper.Encrypt(respJson, device.SharedSecret);
                    string payload = $"DATA_ICON_RESPONSE:{localDeviceId}:{localPublicKey}:{encryptedResp}";
                    byte[] payloadBytes = Encoding.UTF8.GetBytes(payload + "\n");
                    
                    session.Send(payloadBytes, 0, payloadBytes.Length);
                    logger.LogDebug("已发送 ICON_RESPONSE 给设备 {deviceId}，包名: {packageName}", device.Id, packageName);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理图标请求时出错");
        }
    }

    private async Task<PairedDevice?> TryAttachExistingDeviceSessionAsync(ServerSession session, string message)
    {
        try
        {
            var parts = message.Split(':');
            if (parts.Length < 3) return null;

            var remoteDeviceId = parts[1];
            var remotePublicKey = parts[2];

            var device = PairedDevices.FirstOrDefault(d => d.Id == remoteDeviceId);
            if (device is null) return null;

            if (device.RemotePublicKey is not null && !string.Equals(device.RemotePublicKey, remotePublicKey, StringComparison.Ordinal))
            {
                logger.LogWarning("设备 {id} 的远端公钥不匹配", remoteDeviceId);
                return null;
            }

            if (localPublicKey is null)
            {
                logger.LogWarning("本地公钥未初始化，无法绑定会话");
                return null;
            }

            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                device.Session = session;
                device.ConnectionStatus = true;
                device.RemotePublicKey = remotePublicKey;
                device.SharedSecret ??= NotifyCryptoHelper.GenerateSharedSecretBytes(localPublicKey, remotePublicKey);
                device.LastHeartbeat = DateTime.UtcNow;
                deviceManager.ActiveDevice ??= device;
            });

            BindSession(device.Id, session);

            ConnectionStatusChanged?.Invoke(this, (device, true));
            return device;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "绑定预握手 DATA 会话时出错");
            return null;
        }
    }

    private async Task DispatchPayloadAsync(PairedDevice device, string payload)
    {
        try
        {
            if (!payload.TrimStart().StartsWith('{') && !payload.TrimStart().StartsWith('['))
            {
                logger.LogDebug("跳过非 JSON 载荷：{payload}", payload.Length > 50 ? payload[..50] + "..." : payload);
                return;
            }
            
            logger.LogDebug("正在处理 JSON 载荷：{payload}", payload.Length > 100 ? payload[..100] + "..." : payload);

            // 首先尝试处理Notify-Relay-pc格式的JSON消息，如APP_LIST_RESPONSE和ICON_RESPONSE
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                
                // 检查是否包含type字段
                if (root.TryGetProperty("type", out var typeProp))
                {
                    var messageType = typeProp.GetString();
                    logger.LogDebug("识别到Notify-Relay-pc消息类型：{messageType}");
                    
                    // 如果是Notify-Relay-pc特定消息类型，直接处理
                    if (messageType is "APP_LIST_RESPONSE" or "ICON_RESPONSE")
                    {
                        // 使用反射获取HandleJsonMessageAsync方法并调用
                        var handleJsonMethod = messageHandler.Value.GetType().GetMethod("HandleJsonMessageAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (handleJsonMethod != null)
                        {
                            await (Task)handleJsonMethod.Invoke(messageHandler.Value, new object[] { device, payload });
                        }
                        return;
                    }
                }
            }
            catch (JsonException jsonEx)
            {
                logger.LogDebug(jsonEx, "解析Notify-Relay-pc消息时出错，尝试作为普通SocketMessage处理");
            }

            // 尝试作为普通SocketMessage处理
            try
            {
                var socketMessage = SocketMessageSerializer.DeserializeMessage(payload);
                if (socketMessage is not null && socketMessage is not SocketMessage)
                {
                    await messageHandler.Value.HandleMessageAsync(device, socketMessage);
                    return;
                }
            }
            catch (JsonException jsonEx)
            {
                logger.LogDebug(jsonEx, "解析SocketMessage时出错，尝试作为通知消息处理");
            }

            // 尝试作为通知消息处理
            if (TryParseNotifyRelayNotification(payload, out var notificationMessage))
            {
                await messageHandler.Value.HandleMessageAsync(device, notificationMessage);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "分发载荷时出错");
        }
    }

    private bool TryParseNotifyRelayNotification(string payload, out NotificationMessage notificationMessage)
    {
        notificationMessage = null!;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind is not JsonValueKind.Object) return false;

            var root = doc.RootElement;

            if (!root.TryGetProperty("packageName", out var packageProp)) return false;

            var packageName = packageProp.GetString();
            if (string.IsNullOrWhiteSpace(packageName)) return false;
            
            // 过滤超级岛通知，识别段是'superisland:'
            if (packageName.StartsWith("superisland:"))
            {
                logger.LogDebug("丢弃超级岛通知: {PackageName}", packageName);
                return false;
            }

            var timeMs = root.TryGetProperty("time", out var timeProp)
                ? timeProp.GetInt64()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string notificationKey = root.TryGetProperty("id", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString())
                ? idProp.GetString()!
                : root.TryGetProperty("key", out var keyProp) && !string.IsNullOrWhiteSpace(keyProp.GetString())
                    ? keyProp.GetString()!
                    : $"{packageName}:{timeMs}";

            notificationMessage = new NotificationMessage
            {
                NotificationKey = notificationKey,
                TimeStamp = timeMs.ToString(),
                NotificationType = NotificationType.New,
                AppPackage = packageName,
                AppName = root.TryGetProperty("appName", out var appNameProp) ? appNameProp.GetString() : null,
                Title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null,
                Text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null,
                AppIcon = root.TryGetProperty("appIcon", out var appIconProp) ? appIconProp.GetString() : null,
                LargeIcon = root.TryGetProperty("largeIcon", out var largeIconProp) ? largeIconProp.GetString() : null
            };

            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "解析 Notify-Relay 通知载荷失败");
            return false;
        }
    }

    private void DisconnectSession(ServerSession session)
    {
        try
        {
            sessionBuffers.Remove(session.Id);
            UnbindSession(session);
            session.Disconnect();
            session.Dispose();
            DetachSession(session);
        }
        catch (Exception ex)
        {
            logger.Error($"断开连接时出错：{ex.Message}");
        }
    }

    private void DetachSession(ServerSession session)
    {
        var device = GetDeviceBySession(session) ?? PairedDevices.FirstOrDefault(d => d.Session == session);
        if (device is null) return;

        UpdateDeviceState(device, d =>
        {
            d.Session = null;
            if (d.ConnectionStatus)
            {
                d.ConnectionStatus = false;
                ConnectionStatusChanged?.Invoke(this, (d, false));
            }
            logger.LogTrace($"设备 {d.Name} 的会话已解绑");
        });
    }

    private void MarkDeviceAlive(PairedDevice device)
    {
        var now = DateTime.UtcNow;
        UpdateDeviceState(device, d =>
        {
            d.LastHeartbeat = now;
            if (!d.ConnectionStatus)
            {
                d.ConnectionStatus = true;
                ConnectionStatusChanged?.Invoke(this, (d, true));
            }
        });
    }

    private void StartHeartbeat()
    {
        heartbeatTimer ??= new Timer(_ => HeartbeatTick(), null, heartbeatInterval, heartbeatInterval);
    }

    private void HeartbeatTick()
    {
        try
        {
            if (localDeviceId is null || localPublicKey is null) return;

            var payload = $"HEARTBEAT:{localDeviceId}:{localPublicKey}\n";
            var bytes = Encoding.UTF8.GetBytes(payload);

            foreach (var (deviceId, session) in GetSessionSnapshot())
            {
                try
                {
                    session.Send(bytes, 0, bytes.Length);
                }
                catch
                {
                    // best-effort heartbeat send
                }
            }

            foreach (var device in PairedDevices.ToList())
            {
                var last = device.LastHeartbeat;
                if (last.HasValue && DateTime.UtcNow - last.Value > heartbeatTimeout && device.ConnectionStatus)
                {
                    UpdateDeviceState(device, d =>
                    {
                        d.ConnectionStatus = false;
                        d.Session = null;
                        if (TryGetSession(d.Id, out var staleSession) && staleSession is not null)
                        {
                            UnbindSession(staleSession);
                        }
                        ConnectionStatusChanged?.Invoke(this, (d, false));
                    });
                }
            }
        }
        catch
        {
            // best-effort heartbeat
        }
    }

    private void UpdateDeviceState(PairedDevice device, Action<PairedDevice> update)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;

        if (dispatcher is null)
        {
            update(device);
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            update(device);
            return;
        }

        dispatcher.TryEnqueue(() => update(device));
    }
}
