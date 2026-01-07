using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
using Windows.Devices.Power;

namespace Sefirah.Services;
public class NetworkService(
    Func<IMessageHandler> messageHandlerFactory,
    ILogger<NetworkService> logger,
    IDeviceManager deviceManager,
    IAdbService adbService,
    IScreenMirrorService screenMirrorService) : INetworkService, ISessionManager, ITcpServerProvider
{
    private Sefirah.Services.Socket.TcpServer? server;
    public int ServerPort { get; private set; }
    private bool isRunning;
    private X509Certificate2? certificate;
    private readonly IEnumerable<int> PORT_RANGE = Enumerable.Range(23333, 1); // Only use port 23333

    private readonly Lazy<IMessageHandler> messageHandler = new(messageHandlerFactory);
    private readonly ConcurrentDictionary<Guid, string> sessionBuffers = new();
    private readonly Dictionary<string, ServerSession> deviceSessions = new();
    private readonly Dictionary<Guid, string> sessionDeviceMap = new();
    private readonly object sessionLock = new();
    private string? localPublicKey;
    private string? localDeviceId;
    private Timer? heartbeatTimer;
    private readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(4);
    private readonly TimeSpan heartbeatTimeout = TimeSpan.FromSeconds(15);
    
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
            // Notify-Relay-pc不使用SSL，直接使用普通TCP连接
            foreach (int port in PORT_RANGE)
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
                
                logger.LogInformation("请求 JSON：{requestJson}", requestJson);
                
                try
                {
                    // 使用新的TcpServer类，不使用SSL
                    server = new Sefirah.Services.Socket.TcpServer(IPAddress.Any, port, this, logger)
                    {
                        OptionReuseAddress = true,
                    };

                    if (server.Start())
                    {
                        ServerPort = port;
                        isRunning = true;
                        logger.LogInformation("Server started on port: {port}", port);
                        return true;
                    }
                    else
                    {
                        server.Dispose();
                        server = null;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error starting server on port {port}", port);
                    server?.Dispose();
                    server = null;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发送请求时出错：deviceId={deviceId}, 异常类型：{exceptionType}, 异常消息：{message}", deviceId, ex.GetType().Name, ex.Message);
            }
            finally
            {
                logger.LogInformation("请求发送流程结束：{description}，deviceId={deviceId}", description, deviceId);
            }
        });
    }

    /// <summary>
    /// 发送媒体控制请求
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="controlType">控制类型（如 play, pause, next 等）</param>
    public void SendMediaControlRequest(string deviceId, string controlType)
    {
        // 构建媒体控制请求对象，与 Android 端保持一致（移除time字段）
        var requestObj = new
        {
            type = "MEDIA_CONTROL",
            action = controlType
        };
        
        // 序列化为 JSON
        string requestJson = JsonSerializer.Serialize(requestObj);
        
        // 调用通用发送方法，使用 DATA_MEDIA_CONTROL 协议头，与 Android 端保持一致
        SendRequest(deviceId, "DATA_MEDIA_CONTROL", requestJson, $"媒体控制请求，controlType={controlType}");
    }
    
    /// <summary>
    /// 发送媒体播放通知
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="mediaInfo">媒体播放信息</param>
    public void SendMediaPlayNotification(string deviceId, NotificationMessage mediaInfo)
    {
        // 构建媒体播放通知对象，与 Android 端保持一致
        var requestObj = new
        {
            type = "MEDIA_PLAY",
            packageName = mediaInfo.AppPackage,
            appName = mediaInfo.AppName,
            title = mediaInfo.Title,
            text = mediaInfo.Text,
            coverUrl = mediaInfo.CoverUrl,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            isLocked = false, // Android端需要的字段，默认设为false
            mediaType = "FULL" // Android端需要的字段，PC端发送全量包
        };
        
        // 序列化为 JSON
        string requestJson = JsonSerializer.Serialize(requestObj);
        
        // 调用通用发送方法，使用 DATA_MEDIAPLAY 协议头，与 Android 端保持一致
        SendRequest(deviceId, "DATA_MEDIAPLAY", requestJson, "媒体播放通知");
    }

    public void SendMessage(string deviceId, string message)
    {
        logger.LogDebug("原始消息内容：{message}", message);
        
        // 根据消息内容选择消息类型
        string messageType = "DATA_JSON";
        
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
        else if (message.Contains("AUDIO_RESPONSE", StringComparison.OrdinalIgnoreCase))
        {
            messageType = "DATA_AUDIO_RESPONSE";
            logger.LogDebug("识别到 AUDIO_RESPONSE 消息类型");
        }
        else if (message.Contains("MEDIA_CONTROL", StringComparison.OrdinalIgnoreCase))
        {
            messageType = "DATA_MEDIA_CONTROL";
            logger.LogDebug("识别到 MEDIA_CONTROL 消息类型");
        }
        else
        {
            logger.LogDebug("使用默认 DATA_JSON 消息类型");
        }

        // 调用通用发送方法
        SendRequest(deviceId, messageType, message, "通用消息");
    }

    public void BroadcastMessage(string message)
    {
        try
        {
            string messageWithNewline = message + "\n";
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageWithNewline);

            // 直接调用ServerSession的Send方法，内部会处理不同类型的会话
            session.Send(messageBytes, 0, messageBytes.Length);
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
        sessionBuffers.TryRemove(session.Id, out _);
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

            logger.LogDebug("Processing message: {message}", newData.Length > 100 ? string.Concat(newData.AsSpan(0, Math.Min(100, newData.Length)), "...") : newData);

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

            if (string.IsNullOrEmpty(bufferedData))
            {
                sessionBuffers.TryRemove(session.Id, out _);
            }
            else
            {
                sessionBuffers[session.Id] = bufferedData;
            }
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

    public async Task ProcessProtocolMessageAsync(PairedDevice device, string message)
    {
        try
        {
            // 根据报文头类型进行分流处理
            if (message.StartsWith("HEARTBEAT:"))
            {
                // 处理心跳包
                // 心跳格式：HEARTBEAT:<deviceUuid><设备电量%>
                // UUID固定为36个字符（8-4-4-4-12格式），电量在UUID后直接拼接
                const string heartbeatPrefix = "HEARTBEAT:";
                if (message.Length > heartbeatPrefix.Length + 36)
                {
                    // 提取电量信息
                    var batteryStr = message.Substring(heartbeatPrefix.Length + 36);
                    var batteryLevel = 0;
                    if (int.TryParse(batteryStr, out var parsedBattery))
                    {
                        batteryLevel = Math.Clamp(parsedBattery, 0, 100);
                    }
                    
                    // 更新设备状态
                    var deviceStatus = new DeviceStatus
                    {
                        BatteryStatus = batteryLevel
                    };
                    
                    // 调用设备管理器更新设备状态
                    deviceManager.UpdateDeviceStatus(device, deviceStatus);
                }
                
                MarkDeviceAlive(device);
                return;
            }
            
            if (message.StartsWith("DATA_"))
            {
                // 处理DATA_*加密业务消息
                await ProcessDataMessageAsync(device, message);
                return;
            }
            
            if (message.TrimStart().StartsWith('{') || message.TrimStart().StartsWith('['))
            {
                // 处理直接的JSON格式消息
                MarkDeviceAlive(device);
                await DispatchPayloadAsync(device, message);
                return;
            }
            
            logger.LogWarning("收到不支持的消息格式: {message}", message.Length > 50 ? message[..50] + "..." : message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理协议消息时出错");
        }
    }
    
    /// <summary>
    /// 处理DATA_*加密业务消息
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="message">完整消息</param>
    private async Task ProcessDataMessageAsync(PairedDevice device, string message)
    {
        try
        {
            // 处理Notify-Relay-pc格式的HANDSHAKE消息：HANDSHAKE:{uuid}:{localPublicKey}
            if (message.StartsWith("HANDSHAKE:"))
            {
                var parts = message.Split(':');
                if (parts.Length < 3)
                {
                    logger.LogWarning("Invalid HANDSHAKE message format: {message}", message);
                    SendMessage(session, $"REJECT:{string.Empty}");
                    DisconnectSession(session);
                    return;
                }
                
                string remoteUuid = parts[1];
                string remotePublicKey = parts[2];
                
                var connectedSessionIpAddress = session.Socket.RemoteEndPoint?.ToString()?.Split(':')[0];
                logger.LogInformation("Received HANDSHAKE from {connectedSessionIpAddress}, UUID: {remoteUuid}", connectedSessionIpAddress, remoteUuid);
                
                var localDevice = await deviceManager.GetLocalDeviceAsync();
                string localPublicKeyBase64 = Convert.ToBase64String(localDevice.PublicKey);
                
                // 检查设备是否已经被拒绝
                bool isRejected = false; // TODO这里需要实现拒绝列表逻辑
                
                if (isRejected)
                {
                    logger.LogWarning("Device {remoteUuid} is in rejected list", remoteUuid);
                    SendMessage(session, $"REJECT:{remoteUuid}");
                    DisconnectSession(session);
                    return;
                }
                
                // 检查设备是否已经存在
                var existingDevice = PairedDevices.FirstOrDefault(d => d.Id == remoteUuid);
                
                // 自动信任条件：设备已存在且具有已保存的publicKey（匹配远端公钥）
                bool canAutoTrust = existingDevice != null && !string.IsNullOrEmpty(existingDevice.PublicKey) && 
                                   string.Equals(existingDevice.PublicKey, remotePublicKey, StringComparison.OrdinalIgnoreCase);
                
                if (existingDevice != null && existingDevice.IsAuthenticated)
                {
                    // 已认证设备，直接接受
                    logger.LogInformation("Device {remoteUuid} is already authenticated, accepting connection", remoteUuid);
                    SendMessage(session, $"ACCEPT:{localDevice.DeviceId}:{localPublicKeyBase64}");
                    
                    // 更新设备信息
                    existingDevice.ConnectionStatus = true;
                    existingDevice.Session = session;
                    existingDevice.IsOnline = true;
                    existingDevice.LastSeen = DateTimeOffset.UtcNow;
                    
                    deviceManager.ActiveDevice = existingDevice;
                    ConnectionStatusChanged?.Invoke(this, (existingDevice, true));
                    return;
                }
                else if (canAutoTrust)
                {
                    // 自动信任复连
                    logger.LogInformation("Auto-trusting device {remoteUuid} based on existing public key", remoteUuid);
                    SendMessage(session, $"ACCEPT:{localDevice.DeviceId}:{localPublicKeyBase64}");
                    
                    // 更新设备信息
                    existingDevice!.PublicKey = remotePublicKey;
                    existingDevice.ConnectionStatus = true;
                    existingDevice.Session = session;
                    existingDevice.IsAuthenticated = true;
                    existingDevice.IsOnline = true;
                    existingDevice.LastSeen = DateTimeOffset.UtcNow;
                    
                    // 生成共享密钥，使用Notify-Relay-pc的HKDF-SHA256算法
                    string localPublicKeyStr = Convert.ToBase64String(localDevice.PublicKey);
                    string sharedSecretBase64 = CryptoService.GenerateSharedSecret(localPublicKeyStr, remotePublicKey);
                    existingDevice.SharedSecret = Convert.FromBase64String(sharedSecretBase64);
                    
                    deviceManager.ActiveDevice = existingDevice;
                    ConnectionStatusChanged?.Invoke(this, (existingDevice, true));
                    return;
                }
                else
                {
                    // 新设备或公钥不匹配，需要用户确认
                    logger.LogInformation("New device {remoteUuid} requesting connection, needs user approval", remoteUuid);
                    
                    // TODO这里需要实现用户确认逻辑，暂时先自动接受
                    // TODO后续需要添加UI确认流程
                    
                    // 生成共享密钥，使用Notify-Relay-pc的HKDF-SHA256算法
                    string localPublicKeyStr = Convert.ToBase64String(localDevice.PublicKey);
                    string sharedSecretBase64 = CryptoService.GenerateSharedSecret(localPublicKeyStr, remotePublicKey);
                    byte[] sharedSecret = Convert.FromBase64String(sharedSecretBase64);
                    
                    PairedDevice device;
                    if (existingDevice != null)
                    {
                        // 更新现有设备
                        device = existingDevice;
                        device.PublicKey = remotePublicKey;
                        device.SharedSecret = sharedSecret;
                        device.IsAuthenticated = true;
                        device.IsOnline = true;
                        device.LastSeen = DateTimeOffset.UtcNow;
                        device.ConnectionStatus = true;
                        device.Session = session;
                    }
                    else
                    {
                        // 创建新设备
                        device = new PairedDevice(remoteUuid)
                        {
                            Name = "Unknown Device", // 后续可以通过其他方式获取设备名称
                            PublicKey = remotePublicKey,
                            SharedSecret = sharedSecret,
                            IsAuthenticated = true,
                            IsOnline = true,
                            LastSeen = DateTimeOffset.UtcNow,
                            ConnectionStatus = true,
                            Session = session,
                            Origin = Sefirah.Data.Enums.DeviceOrigin.UdpBroadcast
                        };
                        PairedDevices.Add(device);
                    }
                    
                    // 发送接受响应
                    SendMessage(session, $"ACCEPT:{localDevice.DeviceId}:{localPublicKeyBase64}");
                    
                    deviceManager.ActiveDevice = device;
                    ConnectionStatusChanged?.Invoke(this, (device, true));
                    logger.LogInformation("Device {remoteUuid} connected and authenticated", remoteUuid);
                    return;
                }
            }
            // 兼容处理旧格式的DeviceInfo JSON消息
            else if (SocketMessageSerializer.DeserializeMessage(message, logger) is DeviceInfo deviceInfo)
            {
                if (string.IsNullOrEmpty(deviceInfo.Nonce) || string.IsNullOrEmpty(deviceInfo.Proof))
                {
                    logger.LogWarning("Missing authentication data for old format message");
                    SendMessage(session, "Rejected");
                    DisconnectSession(session);
                    return;
                }
                
                var connectedSessionIpAddress = session.Socket.RemoteEndPoint?.ToString()?.Split(':')[0];
                logger.LogInformation("Received old format connection from {connectedSessionIpAddress}", connectedSessionIpAddress);
                
                var device = await deviceManager.VerifyDevice(deviceInfo, connectedSessionIpAddress);
                
                if (device is not null)
                {
                    logger.LogInformation("Old format device {deviceId} connected", device.Id);
                    
                    deviceManager.UpdateOrAddDevice(device, connectedDevice =>
                    {
                        connectedDevice.ConnectionStatus = true;
                        connectedDevice.Session = session;
                        
                        deviceManager.ActiveDevice = connectedDevice;
                        device = connectedDevice;
                        
                        if (device.DeviceSettings.AdbAutoConnect && !string.IsNullOrEmpty(connectedSessionIpAddress))
                        {
                            adbService.TryConnectTcp(connectedSessionIpAddress);
                        }
                    });
                    
                    var (_, avatar) = await UserInformation.GetCurrentUserInfoAsync();
                    var localDevice = await deviceManager.GetLocalDeviceAsync();
                    
                    // Generate our authentication proof for old format
                    var sharedSecret = EcdhHelper.DeriveKey(deviceInfo.PublicKey!, localDevice.PrivateKey);
                    var nonce = EcdhHelper.GenerateNonce();
                    var proof = EcdhHelper.GenerateProof(sharedSecret, nonce);
                    
                    SendMessage(session, SocketMessageSerializer.Serialize(new DeviceInfo
                    {
                        DeviceId = localDevice.DeviceId,
                        DeviceName = localDevice.DeviceName,
                        Avatar = avatar,
                        PublicKey = Convert.ToBase64String(localDevice.PublicKey),
                        Nonce = nonce,
                        Proof = proof
                    }));
                    
                    ConnectionStatusChanged?.Invoke(this, (device, true));
                }
                else
                {
                    SendMessage(session, "Rejected");
                    await Task.Delay(50);
                    logger.LogInformation("Old format device verification failed or was declined");
                    DisconnectSession(session);
                }
                return;
            }
            
            logger.LogWarning("First message was not a HANDSHAKE or DeviceInfo: {message}", message);
            SendMessage(session, $"REJECT:{string.Empty}");
            DisconnectSession(session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing first message for session {sessionId}: {ex}", session.Id, ex);
            DisconnectSession(session);
        }
    }

    private bool TryParseNotifyRelayNotification(string payload, out NotificationMessage notificationMessage)
    {
        notificationMessage = null!;

        try
        {
            // 处理Notify-Relay-pc格式的消息
            if (message.StartsWith("HANDSHAKE:"))
            {
                // 处理握手请求，这部分会在HandleVerification方法中处理
                logger.LogDebug("Received HANDSHAKE message, forwarding to HandleVerification");
                // 由于Handshake消息应该在设备未验证时处理，这里不应该收到已验证设备的Handshake消息
                return;
            }
            else if (message.StartsWith("HEARTBEAT:"))
            {
                // 处理心跳消息
                var parts = message.Split(':');
                if (parts.Length >= 3)
                {
                    string remoteUuid = parts[1];
                    // 更新设备在线状态
                    await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                    {
                        device.IsOnline = true;
                        device.LastSeen = DateTimeOffset.UtcNow;
                    });
                    logger.LogDebug("Received HEARTBEAT from device {remoteUuid}", remoteUuid);
                }
                return;
            }
            else if (message.StartsWith("DATA_JSON:"))
            {
                // 处理通知数据消息
                var parts = message.Split(':');
                if (parts.Length >= 4)
                {
                    string remoteUuid = parts[1];
                    string remotePublicKey = parts[2];
                    string encryptedPayload = string.Join(":", parts.Skip(3));
                    
                    // 解密payload并处理通知
                    string sharedSecretBase64 = Convert.ToBase64String(device.SharedSecret);
                    string decryptedPayload = CryptoService.Decrypt(encryptedPayload, sharedSecretBase64);
                    
                    logger.LogDebug("Received DATA_JSON from device {remoteUuid}, decrypted payload: {decryptedPayload}", remoteUuid, decryptedPayload);
                    
                    // 调用messageHandler处理Notify-Relay-pc格式的通知数据
                    await messageHandler.Value.HandleNotifyRelayNotificationAsync(device, decryptedPayload);
                }
                return;
            }
            else if (message.StartsWith("DATA_ICON_REQUEST:"))
            {
                // 处理图标请求
                logger.LogDebug("Received DATA_ICON_REQUEST message");
                return;
            }
            else if (message.StartsWith("DATA_ICON_RESPONSE:"))
            {
                // 处理图标响应
                logger.LogDebug("Received DATA_ICON_RESPONSE message");
                return;
            }
            else if (message.StartsWith("DATA_APP_LIST_REQUEST:"))
            {
                // 处理应用列表请求
                logger.LogDebug("Received DATA_APP_LIST_REQUEST message");
                return;
            }
            else if (message.StartsWith("DATA_APP_LIST_RESPONSE:"))
            {
                // 处理应用列表响应
                logger.LogDebug("Received DATA_APP_LIST_RESPONSE message");
                return;
            }
            else if (message.StartsWith("ACCEPT:"))
            {
                // 处理握手接受响应
                logger.LogDebug("Received ACCEPT message");
                return;
            }
            else if (message.StartsWith("REJECT:"))
            {
                // 处理握手拒绝响应
                logger.LogDebug("Received REJECT message");
                return;
            }
            // 兼容处理旧格式的JSON消息
            else if (message.TrimStart().StartsWith('{') || message.TrimStart().StartsWith('['))
            {
                var socketMessage = SocketMessageSerializer.DeserializeMessage(message, logger);
                if (socketMessage is not null)
                {
                    await messageHandler.Value.HandleMessageAsync(device, socketMessage);
                }
                return;
            }
            logger.LogDebug("Received unknown message format: {message}", message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing message: {exMessage}", ex.Message);
        }
    }

    private void DisconnectSession(ServerSession session)
    {
        try
        {
            bufferedData = string.Empty;
            
            // 直接调用ServerSession的Disconnect和Dispose方法，内部会处理不同类型的会话
            session.Disconnect();
            session.Dispose();
            
            var device = PairedDevices.FirstOrDefault(d => d.Session == session);   
            if (device is not null)
            {
                App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    device.ConnectionStatus = false;
                    device.Session = null;
                    logger.LogInformation("Device {deviceName} session disconnected, status updated", device.Name);
                });
            }
        });
    }

    /// <summary>
    /// 获取系统电量百分比
    /// </summary>
    private int GetSystemBatteryLevel()
    {
        try
        {
            // 使用Windows.Devices.Power API获取电量
            var batteryReport = Battery.AggregateBattery.GetReport();
            
            // 检查电量信息是否可用
            if (batteryReport.RemainingCapacityInMilliwattHours.HasValue &&
                batteryReport.FullChargeCapacityInMilliwattHours.HasValue &&
                batteryReport.FullChargeCapacityInMilliwattHours.Value > 0)
            {
                // 计算电量百分比
                var remainingCapacity = batteryReport.RemainingCapacityInMilliwattHours.Value;
                var fullCapacity = batteryReport.FullChargeCapacityInMilliwattHours.Value;
                var batteryLevel = (int)Math.Round((double)remainingCapacity / fullCapacity * 100);
                
                // 确保电量值在0-100之间
                return Math.Clamp(batteryLevel, 0, 100);
            }
            
            // 如果无法获取电量信息，返回默认值100%
            return 100;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in Disconnecting: {exMessage}", ex.Message);
        }
    }

    private void StartHeartbeat()
    {
        heartbeatTimer ??= new Timer(_ => HeartbeatTick(), null, heartbeatInterval, heartbeatInterval);
    }

    private void HeartbeatTick()
    {
        try
        {
            if (localDeviceId is null) return;

            // 获取PC设备电量百分比
            var batteryLevel = GetSystemBatteryLevel(); // 接入系统电量API
            
            // 心跳格式：HEARTBEAT:<deviceUuid><设备电量%>
            var payload = $"HEARTBEAT:{localDeviceId}{batteryLevel}";
            var bytes = Encoding.UTF8.GetBytes(payload);
            const int udpPort = 23334; // 使用与Android端相同的UDP端口

            // 使用UDP发送心跳到所有已配对设备
            using var udpClient = new System.Net.Sockets.UdpClient();
            foreach (var device in PairedDevices)
            {
                if (device.IpAddresses is not null && device.IpAddresses.Count > 0)
                {
                    foreach (var ipAddress in device.IpAddresses)
                    {
                        try
                        {
                            var endPoint = new IPEndPoint(IPAddress.Parse(ipAddress), udpPort);
                            udpClient.Send(bytes, bytes.Length, endPoint);
                        }
                        catch
                        {
                            // best-effort UDP heartbeat send
                        }
                    }
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
