using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
    private Server? server;
    public int ServerPort { get; private set; } = 23333;
    private bool isRunning;

    private readonly Lazy<IMessageHandler> messageHandler = new(messageHandlerFactory);
    private readonly Dictionary<Guid, string> sessionBuffers = new();
    private string? localPublicKey;
    private string? localDeviceId;
    private Timer? heartbeatTimer;
    
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
                logger.Info($"Server started on port: {ServerPort}");
                StartHeartbeat();
                return true;
            }

            server.Dispose();
            server = null;

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
            var device = PairedDevices.FirstOrDefault(d => d.Session?.Id == session.Id);
            if (device?.SharedSecret is null)
            {
                logger.LogWarning("Cannot send encrypted message, shared secret missing for session {id}", session.Id);
                return;
            }

            if (localPublicKey is null || localDeviceId is null)
            {
                logger.LogWarning("Local identity not initialized, skip sending");
                return;
            }

            var encryptedPayload = NotifyCryptoHelper.Encrypt(message, device.SharedSecret);
            var framedMessage = $"DATA_JSON:{localDeviceId}:{localPublicKey}:{encryptedPayload}\n";
            byte[] messageBytes = Encoding.UTF8.GetBytes(framedMessage);

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
            logger.LogError("Error sending raw message {ex}", ex);
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
            logger.LogError("Error in OnReceived for session {id}: {ex}", session.Id, ex);
            DisconnectSession(session);
        }
    }

    private async Task HandleHandshakeAsync(ServerSession session, string message)
    {
        if (!message.StartsWith("HANDSHAKE:"))
        {
            logger.LogWarning("Unexpected pre-handshake message from {id}: {message}", session.Id, message);
            // 非握手报文不处理，等待合法握手再次到来
            return;
        }

        var parts = message.Split(':');
        if (parts.Length < 3)
        {
            logger.LogWarning("Invalid handshake format");
            SendRaw(session, $"REJECT:{localDeviceId ?? string.Empty}");
            DisconnectSession(session);
            return;
        }

        var remoteDeviceId = parts[1];
        var remotePublicKey = parts[2];
        var connectedSessionIpAddress = session.Socket.RemoteEndPoint?.ToString()?.Split(':')[0];
        logger.Info($"Received handshake from {connectedSessionIpAddress}");

        var device = await deviceManager.VerifyHandshakeAsync(remoteDeviceId, remotePublicKey, null, connectedSessionIpAddress);

        if (device is not null)
        {
            logger.Info($"Device {device.Id} connected");

            deviceManager.UpdateOrAddDevice(device, connectedDevice  =>
            {
                connectedDevice.ConnectionStatus = true;
                connectedDevice.Session = session;
                connectedDevice.RemotePublicKey = remotePublicKey;
                connectedDevice.SharedSecret ??= NotifyCryptoHelper.GenerateSharedSecretBytes(localPublicKey ?? string.Empty, remotePublicKey);
                deviceManager.ActiveDevice = connectedDevice;
                device = connectedDevice;

                if (device.DeviceSettings.AdbAutoConnect && !string.IsNullOrEmpty(connectedSessionIpAddress))
                {
                    adbService.TryConnectTcp(connectedSessionIpAddress);
                }
            });

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
            logger.Info("Device verification failed or was declined");
            DisconnectSession(session);
        }
    }

    private async Task ProcessProtocolMessageAsync(PairedDevice device, string message)
    {
        try
        {
            if (message.StartsWith("HEARTBEAT:"))
            {
                device.ConnectionStatus = true;
                return;
            }

            if (message.StartsWith("DATA_"))
            {
                var parts = message.Split(':');
                if (parts.Length < 4)
                {
                    logger.LogWarning("Invalid DATA frame");
                    return;
                }

                if (device.SharedSecret is null)
                {
                    logger.LogWarning("Shared secret missing for device {id}", device.Id);
                    return;
                }

                var encryptedPayload = string.Join(":", parts.Skip(3));
                var decryptedPayload = NotifyCryptoHelper.Decrypt(encryptedPayload, device.SharedSecret);

                await DispatchPayloadAsync(device, decryptedPayload);
                return;
            }

            if (message.TrimStart().StartsWith('{') || message.TrimStart().StartsWith('['))
            {
                await DispatchPayloadAsync(device, message);
                return;
            }

            logger.Debug("Received unsupported message format");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling protocol message");
        }
    }

    private async Task DispatchPayloadAsync(PairedDevice device, string payload)
    {
        try
        {
            var socketMessage = SocketMessageSerializer.DeserializeMessage(payload);
            if (socketMessage is null) return;
            await messageHandler.Value.HandleMessageAsync(device, socketMessage);
        }
        catch (JsonException jsonEx)
        {
            logger.Error($"Error parsing JSON message: {jsonEx.Message}");
        }
    }

    public void DisconnectSession(ServerSession session)
    {
        try
        {
            sessionBuffers.Remove(session.Id);
            session.Disconnect();
            session.Dispose();
            var device = PairedDevices.FirstOrDefault(d => d.Session == session);   
            if (device is not null)
            {
                App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    device.ConnectionStatus = false;
                    device.Session = null;
                    logger.Info($"Device {device.Name} session disconnected, status updated");
                });
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Error in Disconnecting: {ex.Message}");
        }
    }

    private void StartHeartbeat()
    {
        heartbeatTimer ??= new Timer(_ => SendHeartbeat(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    private void SendHeartbeat()
    {
        try
        {
            if (localDeviceId is null || localPublicKey is null) return;

            var payload = $"HEARTBEAT:{localDeviceId}:{localPublicKey}\n";
            var bytes = Encoding.UTF8.GetBytes(payload);

            List<ServerSession> sessions;
            lock (PairedDevices)
            {
                sessions = PairedDevices
                    .Where(d => d.Session is not null)
                    .Select(d => d.Session!)
                    .ToList();
            }

            foreach (var session in sessions)
            {
                try
                {
                    session.Send(bytes, 0, bytes.Length);
                }
                catch
                {
                    // Ignore transient send errors
                }
            }
        }
        catch
        {
            // best-effort heartbeat
        }
    }
}
