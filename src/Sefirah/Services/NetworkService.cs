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
    private Server? server;
    public int ServerPort { get; private set; } = 23333;
    private bool isRunning;

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
        // 构建应用列表请求对象
        var requestObj = new
        {
            type = "APP_LIST_REQUEST",
            scope = "user",
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        // 序列化为 JSON
        string requestJson = JsonSerializer.Serialize(requestObj);
        
        // 调用通用发送方法
        SendRequest(deviceId, "DATA_APP_LIST_REQUEST", requestJson, "应用列表请求");
    }
    
    /// <summary>
    /// 发送图标请求
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="packageNames">应用包名列表</param>
    public void SendIconRequest(string deviceId, List<string> packageNames)
    {
        logger.LogInformation("开始发送图标请求：deviceId={deviceId}, packageCount={packageCount}", deviceId, packageNames.Count);

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
        
        // 调用通用发送方法
        SendRequest(deviceId, "DATA_ICON_REQUEST", requestJson, $"图标请求，packageCount={packageNames.Count}");
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

    /// <summary>
    /// 通用发送请求方法
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="messageType">消息类型（如 DATA_APP_LIST_REQUEST, DATA_ICON_REQUEST, DATA_MEDIA_CONTROL 等）</param>
    /// <param name="requestJson">请求内容的 JSON 字符串</param>
    /// <param name="description">请求描述，用于日志</param>
    private void SendRequest(string deviceId, string messageType, string requestJson, string description)
    {
        logger.LogInformation("开始发送请求：{description}，deviceId={deviceId}", description, deviceId);
        
        _ = Task.Run(async () =>
        {
            try
            {
                var device = PairedDevices.FirstOrDefault(d => d.Id == deviceId);
                if (device is null)
                {
                    logger.LogWarning("跳过发送：未找到设备 {deviceId}", deviceId);
                    return;
                }
                
                if (localPublicKey is null || localDeviceId is null)
                {
                    logger.LogWarning("本地身份未初始化，跳过发送");
                    return;
                }
                
                // 使用统一的协议发送器发送消息
                await ProtocolSender.SendEncryptedAsync(
                    logger, 
                    device, 
                    messageType, 
                    requestJson, 
                    localDeviceId, 
                    localPublicKey
                );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发送请求时出错：deviceId={deviceId}", deviceId);
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
        // 调用重载方法，默认发送全量包
        SendMediaPlayNotification(deviceId, mediaInfo, "FULL");
    }
    
    /// <summary>
    /// 发送媒体播放通知
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="mediaInfo">媒体播放信息</param>
    /// <param name="mediaType">媒体类型，FULL 表示全量包，DELTA 表示差异包</param>
    public void SendMediaPlayNotification(string deviceId, NotificationMessage mediaInfo, string mediaType)
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
            mediaType = mediaType // Android端需要的字段，支持FULL和DELTA
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
        else if (message.Contains("DATA_SFTP", StringComparison.OrdinalIgnoreCase))
        {
            messageType = "DATA_SFTP";
            logger.LogDebug("识别到 DATA_SFTP 消息类型");
        }
        else if (message.Contains("SftpServerInfo", StringComparison.OrdinalIgnoreCase))
        {
            messageType = "DATA_SFTP";
            logger.LogDebug("识别到 SFTP_SERVER_INFO 消息类型");
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
            return;
        }

        var parts = message.Split(':');
        if (parts.Length < 6)
        {
            logger.LogWarning("握手格式无效，期望至少6个部分");
            SendRaw(session, $"REJECT:{localDeviceId ?? string.Empty}");
            DisconnectSession(session);
            return;
        }

        var remoteDeviceId = parts[1];
        var remotePublicKey = parts[2];
        var remoteIpAddress = parts[3];
        var remoteBattery = parts[4];
        var remoteDeviceType = parts[5];
        var discoveredName = PairedDevices.FirstOrDefault(d => d.Id == remoteDeviceId)?.Name;

        if (discoveredName is null)
        {
            var discovery = Ioc.Default.GetService<IDiscoveryService>();
            discoveredName = discovery?.DiscoveredDevices.FirstOrDefault(d => d.DeviceId == remoteDeviceId)?.DeviceName;
        }
        var connectedSessionIpAddress = session.Socket.RemoteEndPoint?.ToString()?.Split(':')[0];
        logger.Info($"收到握手来自 {connectedSessionIpAddress} (类型: {remoteDeviceType}, 电量: {remoteBattery})");

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
                connectedDevice.RemoteIpAddress = remoteIpAddress;
                connectedDevice.RemoteDeviceType = remoteDeviceType;
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
                var localBattery = GetLocalBatteryStatus();
                var localIp = GetLocalIpAddress();
                SendRaw(session, $"ACCEPT:{localDeviceId}:{localPublicKey}:{localIp}:{localBattery}:pc");
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

    private static string GetLocalBatteryStatus()
    {
        try
        {
            return "100+";
        }
        catch
        {
            return "";
        }
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
            return "0.0.0.0";
        }
        catch
        {
            return "0.0.0.0";
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
            var parts = message.Split(':');
            if (parts.Length < 4)
            {
                logger.LogWarning("无效的 DATA 帧格式: {message}", message.Length > 50 ? message[..50] + "..." : message);
                return;
            }

            if (device.SharedSecret is null)
            {
                logger.LogWarning("设备 {id} 缺少共享密钥，无法处理加密消息", device.Id);
                return;
            }

            var messageType = parts[0];
            var encryptedPayload = string.Join(":", parts.Skip(3));
            var decryptedPayload = NotifyCryptoHelper.Decrypt(encryptedPayload, device.SharedSecret);
            
            // 更新设备活跃时间
            MarkDeviceAlive(device);
            
            // 根据具体的DATA_*报文头进行分流处理
            switch (messageType)
            {
                case "DATA_APP_LIST_REQUEST":
                    // 应用列表请求
                    await HandleAppListRequestAsync(device, decryptedPayload);
                    break;
                    
                case "DATA_ICON_REQUEST":
                    // 图标请求
                    await HandleIconRequestAsync(device, decryptedPayload);
                    break;
                    
                case "DATA_NOTIFICATION":
                case "DATA_JSON":
                    // 普通通知和通用JSON数据
                    await DispatchPayloadAsync(device, decryptedPayload);
                    break;
                    
                case "DATA_MEDIAPLAY":
                    // 媒体播放信息，直接调用媒体播放通知处理
                    logger.LogTrace("收到DATA_MEDIAPLAY消息，设备：{deviceId}", device.Id);
                    // logger.LogDebug("DATA_MEDIAPLAY消息内容：{payload}", decryptedPayload.Length > 100 ? decryptedPayload[..100] + "..." : decryptedPayload);
                    try
                    {
                        // 直接使用JsonDocument解析DATA_MEDIAPLAY消息
                        using JsonDocument doc = JsonDocument.Parse(decryptedPayload);
                        JsonElement root = doc.RootElement;
                        
                        // 提取time字段，处理Number和String两种类型
                        string timeStamp;
                        if (root.TryGetProperty("time", out JsonElement timeElement))
                        {
                            if (timeElement.ValueKind == JsonValueKind.String)
                            {
                                timeStamp = timeElement.GetString() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                            }
                            else if (timeElement.ValueKind == JsonValueKind.Number)
                            {
                                timeStamp = timeElement.GetInt64().ToString();
                            }
                            else
                            {
                                timeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                            }
                        }
                        else if (root.TryGetProperty("timeStamp", out JsonElement timeStampElement))
                        {
                            if (timeStampElement.ValueKind == JsonValueKind.String)
                            {
                                timeStamp = timeStampElement.GetString() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                            }
                            else if (timeStampElement.ValueKind == JsonValueKind.Number)
                            {
                                timeStamp = timeStampElement.GetInt64().ToString();
                            }
                            else
                            {
                                timeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                            }
                        }
                        else
                        {
                            timeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                        }
                        
                        // 直接构造NotificationMessage对象
                        var notificationMessage = new NotificationMessage
                        {
                            NotificationKey = Guid.NewGuid().ToString(),
                            TimeStamp = timeStamp,
                            NotificationType = NotificationType.New,
                            AppPackage = root.TryGetProperty("packageName", out JsonElement packageNameElement) && packageNameElement.ValueKind == JsonValueKind.String ? packageNameElement.GetString() : null,
                            AppName = root.TryGetProperty("appName", out JsonElement appNameElement) && appNameElement.ValueKind == JsonValueKind.String ? appNameElement.GetString() : null,
                            Title = root.TryGetProperty("title", out JsonElement titleElement) && titleElement.ValueKind == JsonValueKind.String ? titleElement.GetString() : null,
                            Text = root.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String ? textElement.GetString() : null,
                            BigPicture = root.TryGetProperty("bigPicture", out JsonElement bigPictureElement) && bigPictureElement.ValueKind == JsonValueKind.String ? bigPictureElement.GetString() : null,
                            LargeIcon = root.TryGetProperty("largeIcon", out JsonElement largeIconElement) && largeIconElement.ValueKind == JsonValueKind.String ? largeIconElement.GetString() : null,
                            CoverUrl = root.TryGetProperty("coverUrl", out JsonElement coverUrlElement) && coverUrlElement.ValueKind == JsonValueKind.String ? coverUrlElement.GetString() : null
                        };
                        
                        // logger.LogDebug("成功构造NotificationMessage对象");
                        var notificationService = Ioc.Default.GetRequiredService<INotificationService>();
                        // logger.LogDebug("调用HandleMediaPlayNotification处理媒体播放通知");
                        await notificationService.HandleMediaPlayNotification(device, notificationMessage);
                        // logger.LogDebug("媒体播放通知处理完成");
                    }
                    catch (JsonException jsonEx)
                    {
                        logger.LogError(jsonEx, "解析DATA_MEDIAPLAY消息JSON时出错，消息内容：{payload}", decryptedPayload.Length > 100 ? decryptedPayload[..100] + "..." : decryptedPayload);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "处理DATA_MEDIAPLAY消息时出错");
                    }
                    break;
                    
                case "DATA_APP_LIST_RESPONSE":
                case "DATA_ICON_RESPONSE":
                case "DATA_AUDIO_REQUEST":
                    // 应用列表响应、图标响应和音频请求
                    await DispatchPayloadAsync(device, decryptedPayload);
                    break;
                    
                case "DATA_SUPERISLAND":
                    // 超级岛通知，直接忽略
                    break;
                    
                case "DATA_MEDIA_CONTROL":
                    // 媒体控制指令
                    await DispatchPayloadAsync(device, decryptedPayload);
                    break;
                
                case "DATA_SFTP":
                    // SFTP 消息
                    await DispatchPayloadAsync(device, decryptedPayload);
                    break;
                    
                default:
                    logger.LogWarning("不支持的 DATA 消息类型: {messageType}", messageType);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理DATA消息时出错");
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
            string remoteDeviceId;
            string remotePublicKey = string.Empty;
            
            if (message.StartsWith("HEARTBEAT:"))
            {
                // 处理新的心跳格式：HEARTBEAT:<deviceUuid><设备电量%>
                const string heartbeatPrefix = "HEARTBEAT:";
                if (message.Length > heartbeatPrefix.Length + 36)
                {
                    remoteDeviceId = message.Substring(heartbeatPrefix.Length, 36);
                }
                else
                {
                    return null;
                }
            }
            else
            {
                // 处理其他格式
                var parts = message.Split(':');
                if (parts.Length < 3) return null;
                remoteDeviceId = parts[1];
                remotePublicKey = parts[2];
            }

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

            // 获取会话的远程IP地址
            var connectedSessionIpAddress = session.Socket.RemoteEndPoint?.ToString()?.Split(':')[0];
            
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                device.Session = session;
                device.ConnectionStatus = true;
                device.RemotePublicKey = remotePublicKey;
                device.SharedSecret ??= NotifyCryptoHelper.GenerateSharedSecretBytes(localPublicKey, remotePublicKey);
                device.LastHeartbeat = DateTime.UtcNow;
                deviceManager.ActiveDevice ??= device;
                
                // 更新设备IP地址
                if (!string.IsNullOrEmpty(connectedSessionIpAddress))
                {
                    // 如果IP地址已存在且不同，或者IP地址列表为空，更新IP地址
                    if (device.IpAddresses == null || device.IpAddresses.Count == 0 || !device.IpAddresses.Contains(connectedSessionIpAddress))
                    {
                        logger.LogInformation("更新设备 {deviceName} 的IP地址为 {newIp}", device.Name, connectedSessionIpAddress);
                        logger.LogInformation("会话远程IP地址：{ipAddress}", connectedSessionIpAddress);
                        
                        // 清空旧的IP地址列表，只保留新的IP地址
                        device.IpAddresses = [connectedSessionIpAddress];
                    }
                }
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
                logger.LogWarning("跳过非 JSON 载荷：{payload}", payload.Length > 50 ? payload[..50] + "..." : payload);
                return;
            }
            
            // 首先尝试解析JSON
            JsonDocument doc;
            JsonElement root;
            try
            {
                doc = JsonDocument.Parse(payload);
                root = doc.RootElement;
            }
            catch (JsonException ex)
            {
                logger.LogWarning("解析JSON时出错：{ex.Message}");
                return;
            }
            
            // 检查是否包含type字段
            if (root.TryGetProperty("type", out var typeProp))
            {
                var messageType = typeProp.GetString();
                logger.LogDebug("识别到JSON消息类型：{messageType}");
                
                // 根据type字段值进行分流处理
                switch (messageType)
                {
                    case "APP_LIST_RESPONSE":
                    case "ICON_RESPONSE":
                    case "AUDIO_REQUEST":
                        // Notify-Relay-pc特定消息类型，使用反射调用处理方法
                        var handleJsonMethod = messageHandler.Value.GetType().GetMethod("HandleJsonMessageAsync", 
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (handleJsonMethod != null)
                        {
                            await (Task)handleJsonMethod.Invoke(messageHandler.Value, new object[] { device, payload });
                        }
                        return;
                        
                    case "MEDIA_CONTROL":
                        // 媒体控制消息，直接处理
                        await HandleMediaControlMessageAsync(device, payload);
                        return;
                    
                    case "DATA_SFTP":
                        // DATA_SFTP消息，使用反射调用处理方法
                        handleJsonMethod = messageHandler.Value.GetType().GetMethod("HandleJsonMessageAsync", 
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (handleJsonMethod != null)
                        {
                            await (Task)handleJsonMethod.Invoke(messageHandler.Value, new object[] { device, payload });
                        }
                        return;
                }
            }
            
            // 检查是否为SFTP响应（没有type字段但有action字段）
            if (root.TryGetProperty("action", out var actionProp))
            {
                var action = actionProp.GetString();
                if (action is "started" or "stopped" or "error")
                {
                    logger.LogDebug("识别到SFTP响应：action={action}", action);
                    // 直接调用MessageHandler的HandleJsonMessageAsync方法处理SFTP响应
                    var handleJsonMethod = messageHandler.Value.GetType().GetMethod("HandleJsonMessageAsync", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (handleJsonMethod != null)
                    {
                        await (Task)handleJsonMethod.Invoke(messageHandler.Value, new object[] { device, payload });
                    }
                    return;
                }
            }

            // 尝试作为普通SocketMessage处理
            try
            {
                var socketMessage = SocketMessageSerializer.DeserializeMessage(payload);
                if (socketMessage is not null && socketMessage is not SocketMessage)
                {
                    logger.LogDebug("处理为普通SocketMessage");
                    await messageHandler.Value.HandleMessageAsync(device, socketMessage);
                    return;
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug("解析SocketMessage时出错：{ex.Message}");
            }

            // 尝试作为通知消息处理
            if (TryParseNotifyRelayNotification(payload, out var notificationMessage))
            {
                logger.LogDebug("处理为通知消息");
                await messageHandler.Value.HandleMessageAsync(device, notificationMessage);
                return;
            }
            
            logger.LogWarning("无法处理的JSON载荷：{payload}", payload.Length > 100 ? payload[..100] + "..." : payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "分发载荷时出错");
        }
    }
    
    /// <summary>
    /// 处理媒体控制消息
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="payload">消息内容</param>
    private async Task HandleMediaControlMessageAsync(PairedDevice device, string payload)
    {
        try
        {
            logger.LogDebug("处理媒体控制消息：{payload}", payload.Length > 100 ? payload[..100] + "..." : payload);
            
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            
            // 获取action字段
            if (root.TryGetProperty("action", out var actionProp))
            {
                var action = actionProp.GetString();
                logger.LogDebug("媒体控制动作：{action}");
                
                // 处理不同的媒体控制动作
                switch (action)
                {
                    case "playPause":
                    case "next":
                    case "previous":
                        // 执行媒体控制动作
                        logger.LogDebug("执行媒体控制动作：{action}");
                        try
                        {
                            var playbackService = Ioc.Default.GetRequiredService<IPlaybackService>();
                            PlaybackActionType actionType = action switch
                            {
                                "playPause" => PlaybackActionType.Play,
                                "next" => PlaybackActionType.Next,
                                "previous" => PlaybackActionType.Previous,
                                _ => PlaybackActionType.Play
                            };
                            await playbackService.HandleMediaActionAsync(new PlaybackAction
                            {
                                PlaybackActionType = actionType,
                                Source = "MediaControl"
                            });
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "执行媒体控制动作时出错：{action}", action);
                        }
                        break;
                        
                    case "audioRequest":
                        // 处理音频转发请求
                        logger.LogDebug("收到音频转发请求");
                        try
                        {
                            // 构建仅音频转发的 scrcpy 参数
                            string customArgs = "--no-video --no-control";
                            
                            // 启动 scrcpy 仅音频转发
                            bool success = await screenMirrorService.StartScrcpy(device, customArgs);
                            
                            // 构造响应
                            var response = new
                            {
                                type = "MEDIA_CONTROL",
                                action = "audioResponse",
                                result = success ? "accepted" : "rejected"
                            };
                            string responseJson = JsonSerializer.Serialize(response);
                            // 发送响应，使用 DATA_MEDIA_CONTROL 协议头
                            SendRequest(device.Id, "DATA_MEDIA_CONTROL", responseJson, "音频转发响应");
                            
                            logger.LogDebug("音频转发请求处理完成，结果：{result}", success ? "accepted" : "rejected");
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "处理音频转发请求时出错");
                            
                            // 发送拒绝响应，使用 DATA_MEDIA_CONTROL 协议头
                            var errorResponse = new
                            {
                                type = "MEDIA_CONTROL",
                                action = "audioResponse",
                                result = "rejected"
                            };
                            string errorResponseJson = JsonSerializer.Serialize(errorResponse);
                            SendRequest(device.Id, "DATA_MEDIA_CONTROL", errorResponseJson, "音频转发响应");
                        }
                        break;
                        
                    case "audioResponse":
                        // 处理音频转发响应
                        logger.LogDebug("收到音频转发响应");
                        break;
                        
                    default:
                        logger.LogWarning("未知媒体控制动作：{action}", action);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理媒体控制消息时出错");
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
                // 注释掉丢弃超级岛通知的调试日志
                // logger.LogDebug("丢弃超级岛通知: {PackageName}", packageName);
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
                LargeIcon = root.TryGetProperty("largeIcon", out var largeIconProp) ? largeIconProp.GetString() : null,
                CoverUrl = root.TryGetProperty("coverUrl", out var coverUrlProp) ? coverUrlProp.GetString() : null
            };

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void DisconnectSession(ServerSession session)
    {
        try
        {
            sessionBuffers.TryRemove(session.Id, out _);
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
            // 不要在TCP会话断开时立即标记为离线，由心跳超时决定
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
            logger.LogWarning("获取系统电量失败：{ex}", ex);
            // 异常情况下返回默认值100%
            return 100;
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
            
            // 心跳格式：HEARTBEAT:<deviceUuid><设备电量%><设备类型>
            var payload = $"HEARTBEAT:{localDeviceId}{batteryLevel}pc";
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
