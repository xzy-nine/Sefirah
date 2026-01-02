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
    private string? localPublicKey;
    private string? localDeviceId;
    private Timer? heartbeatTimer;
    private readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan heartbeatTimeout = TimeSpan.FromSeconds(60);
    
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

    public void SendMessage(ServerSession session, string message)
    {
        try
        {
            if (session is null)
            {
                logger.LogDebug("跳过发送：会话为空");
                return;
            }

            if (deviceManager.PairedDevices is null)
            {
                logger.LogWarning("无法发送消息：配对设备列表未初始化");
                return;
            }

            PairedDevice? device = null;
            try
            {
                foreach (var d in deviceManager.PairedDevices)
                {
                    if (d?.Session is null) continue;
                    if (session.Id == d.Session.Id)
                    {
                        device = d;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "扫描配对设备以查找会话时出错");
                return;
            }
            if (device is null)
            {
                logger.LogDebug("跳过发送：未找到与会话 {id} 对应的设备", session.Id);
                return;
            }

            if (device.SharedSecret is null)
            {
                logger.LogWarning("无法发送加密消息：会话 {id} 缺少共享密钥", session.Id);
                return;
            }

            if (localPublicKey is null || localDeviceId is null)
            {
                logger.LogWarning("本地身份未初始化，跳过发送");
                return;
            }

            if (session.Socket is not null && session.Socket.Connected)
            {
                var encryptedPayload = NotifyCryptoHelper.Encrypt(message, device.SharedSecret);
                var framedMessage = $"DATA_JSON:{localDeviceId}:{localPublicKey}:{encryptedPayload}\n";
                byte[] messageBytes = Encoding.UTF8.GetBytes(framedMessage);

                session.Send(messageBytes, 0, messageBytes.Length);
            }
            else
            {
                logger.LogDebug("跳过发送：设备 {id} 的会话未连接", device.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError("发送消息时出错：{ex}", ex);
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
            logger.LogError("向所有设备发送消息时出错：{ex}", ex);
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

                var device = PairedDevices.FirstOrDefault(d => d.Session?.Id == session.Id);
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

            ConnectionStatusChanged?.Invoke(this, (device, true));

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

                var encryptedPayload = string.Join(":", parts.Skip(3));
                var decryptedPayload = NotifyCryptoHelper.Decrypt(encryptedPayload, device.SharedSecret);
                logger.LogDebug("收到来自设备 {id} 的 DATA 有效负载，长度={len}", device.Id, decryptedPayload.Length);

                MarkDeviceAlive(device);
                await DispatchPayloadAsync(device, decryptedPayload);
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
            var socketMessage = SocketMessageSerializer.DeserializeMessage(payload);
            if (socketMessage is not null && socketMessage is not SocketMessage)
            {
                await messageHandler.Value.HandleMessageAsync(device, socketMessage);
                return;
            }

            if (TryParseNotifyRelayNotification(payload, out var notificationMessage))
            {
                await messageHandler.Value.HandleMessageAsync(device, notificationMessage);
                return;
            }
        }
        catch (JsonException jsonEx)
        {
            logger.Error($"解析 JSON 消息时出错：{jsonEx.Message}");
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

    public void DisconnectSession(ServerSession session)
    {
        try
        {
            sessionBuffers.Remove(session.Id);
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
        var device = PairedDevices.FirstOrDefault(d => d.Session == session);
        if (device is null) return;

        App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            device.Session = null;
            logger.LogDebug($"设备 {device.Name} 的会话已解绑");
        });
    }

    private void MarkDeviceAlive(PairedDevice device)
    {
        device.LastHeartbeat = DateTime.UtcNow;
        if (!device.ConnectionStatus)
        {
            device.ConnectionStatus = true;
            ConnectionStatusChanged?.Invoke(this, (device, true));
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
            if (localDeviceId is null || localPublicKey is null) return;

            var payload = $"HEARTBEAT:{localDeviceId}:{localPublicKey}\n";
            var bytes = Encoding.UTF8.GetBytes(payload);

            foreach (var device in PairedDevices.ToList())
            {
                // 发送心跳（如果当前有持久连接）
                if (device.Session is not null)
                {
                    try
                    {
                        device.Session.Send(bytes, 0, bytes.Length);
                    }
                    catch
                    {
                        // best-effort heartbeat send
                    }
                }

                // 超时判定：无论是否有会话，只要超过超时时间就标记离线
                var last = device.LastHeartbeat;
                if (last.HasValue && DateTime.UtcNow - last.Value > heartbeatTimeout && device.ConnectionStatus)
                {
                    App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                    {
                        device.ConnectionStatus = false;
                        device.Session = null;
                        ConnectionStatusChanged?.Invoke(this, (device, false));
                    });
                }
            }
        }
        catch
        {
            // best-effort heartbeat
        }
    }
}
