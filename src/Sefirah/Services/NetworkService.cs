using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CommunityToolkit.WinUI;
using NetCoreServer;
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
    private Sefirah.Services.Socket.TcpServer? server;
    public int ServerPort { get; private set; }
    private bool isRunning;
    private X509Certificate2? certificate;
    private readonly IEnumerable<int> PORT_RANGE = Enumerable.Range(23333, 1); // Only use port 23333

    private readonly Lazy<IMessageHandler> messageHandler = new(messageHandlerFactory);

    private string bufferedData = string.Empty;
    
    private ObservableCollection<PairedDevice> PairedDevices => deviceManager.PairedDevices;

    /// <summary>
    /// Event fired when a device connection status changes
    /// </summary>
    public event EventHandler<(PairedDevice Device, bool IsConnected)>? ConnectionStatusChanged;

    public async Task<bool> StartServerAsync()
    {
        if (isRunning)
        {
            logger.LogWarning("Server is already running");
            return false;
        }
        try
        {
            // Notify-Relay-pc不使用SSL，直接使用普通TCP连接
            foreach (int port in PORT_RANGE)
            {
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

            logger.LogError("Failed to start server");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError("Error starting server {ex}", ex);
            return false;
        }
    }

    public void SendMessage(ServerSession session, string message)
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
            logger.LogError("Error sending message {ex}", ex);
        }
    }

    public void BroadcastMessage(string message)
    {
        if (PairedDevices.Count == 0) return;
        try
        {
            foreach (var device in PairedDevices.Where(d => d.Session != null))
            {
                SendMessage(device.Session!, message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error sending message to all {ex}", ex);
        }
    }

    // Server side methods
    public void OnConnected(ServerSession session)
    {

    }

    public void OnDisconnected(ServerSession session)
    {
        DisconnectSession(session);
    }

    public void OnError(SocketError error)
    {
        logger.LogError("Error on socket {error}", error);
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

                // Check if newlineIndex is at the end of the string
                if (newlineIndex + 1 >= bufferedData.Length)
                {
                    bufferedData = string.Empty;
                }
                else
                {
                    bufferedData = bufferedData[(newlineIndex + 1)..];
                }

                if (string.IsNullOrEmpty(message)) continue;
        
                var device = PairedDevices.FirstOrDefault(d => d.Session?.Id == session.Id);
                if (device is null)
                {
                    await HandleVerification(session, message);
                    return;
                }
                ProcessMessage(device, message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error in OnReceived for session {id}: {ex}", session.Id, ex);
            DisconnectSession(session);
        }
    }

    private async Task HandleVerification(ServerSession session, string message)
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
            else if (SocketMessageSerializer.DeserializeMessage(message) is DeviceInfo deviceInfo)
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

    private async void ProcessMessage(PairedDevice device, string message)
    {
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
                var socketMessage = SocketMessageSerializer.DeserializeMessage(message);
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

    public void DisconnectSession(ServerSession session)
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
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in Disconnecting: {exMessage}", ex.Message);
        }
    }
}
