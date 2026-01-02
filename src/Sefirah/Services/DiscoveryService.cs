using System.Net;
using System.Text;
using CommunityToolkit.WinUI;
using MeaMod.DNS.Multicast;
using Microsoft.UI.Dispatching;
using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Sefirah.Data.EventArguments;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using Sefirah.Services.Socket;
using Sefirah.Utils;
using Sefirah.Utils.Serialization;

namespace Sefirah.Services;
public class DiscoveryService(
    ILogger logger,
    IMdnsService mdnsService,
    IDeviceManager deviceManager,
    INetworkService networkService
    ) : IDiscoveryService, IUdpClientProvider
{
    private MulticastClient? udpClient; 
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private const string DEFAULT_BROADCAST = "255.255.255.255";
    private LocalDeviceEntity? localDevice;
    private readonly int port = 23334;
    public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = [];
    public List<DiscoveredMdnsServiceArgs> DiscoveredMdnsServices { get; } = [];
    private List<IPEndPoint> broadcastEndpoints = [];
    private const int DiscoveryPort = 23334;
    private bool isBroadcasting;

    public async Task StartDiscoveryAsync()
    {
        try
        {
            localDevice = await deviceManager.GetLocalDeviceAsync();
            var localAddresses = NetworkHelper.GetAllValidAddresses();

            logger.LogInformation($"将广播的地址：{string.Join(", ", localAddresses)}");

            var (name, _) = await UserInformation.GetCurrentUserInfoAsync();
            var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
            var serverPort = networkService.ServerPort == 0 ? 23333 : networkService.ServerPort;
            var discoverMessage = $"NOTIFYRELAY_DISCOVER:{localDevice.DeviceId}:{encodedName}:{serverPort}";

            broadcastEndpoints = [.. localAddresses.Select(ipInfo =>
            {
                var network = new Data.Models.IPNetwork(ipInfo.Address, ipInfo.SubnetMask);
                var broadcastAddress = network.BroadcastAddress;

                return broadcastAddress.Equals(IPAddress.Broadcast) && ipInfo.Gateway is not null
                    ? new IPEndPoint(ipInfo.Gateway, DiscoveryPort)
                    : new IPEndPoint(broadcastAddress, DiscoveryPort);

            }).Distinct()];

            broadcastEndpoints.Add(new IPEndPoint(IPAddress.Parse(DEFAULT_BROADCAST), DiscoveryPort));

            var ipAddresses = deviceManager.GetRemoteDeviceIpAddresses();
            broadcastEndpoints.AddRange(ipAddresses.Select(ip => new IPEndPoint(IPAddress.Parse(ip), DiscoveryPort)));

            logger.LogInformation("当前广播端点：{endpoints}", string.Join(", ", broadcastEndpoints));


            udpClient = new MulticastClient("0.0.0.0", port, this, logger)
            {
                OptionDualMode = false,
                OptionMulticast = true,
                OptionReuseAddress = true,
            };
            udpClient.SetupMulticast(true);

            if (udpClient.Connect())
            {
                udpClient.Socket.EnableBroadcast = true;
                logger.LogInformation("UDP 客户端连接成功（端口：{port}）", port);

                BroadcastDeviceInfoAsync(discoverMessage);
            }
            else
            {
                logger.LogError("UDP 客户端连接失败");
            }
            mdnsService.DiscoveredMdnsService += OnDiscoveredMdnsService;
            mdnsService.ServiceInstanceShutdown += OnServiceInstanceShutdown;

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动发现服务时出错");
        }
    }

    private async void BroadcastDeviceInfoAsync(string discoverMessage)
    {
        if (isBroadcasting) return;
        isBroadcasting = true;

        var messageBytes = Encoding.UTF8.GetBytes(discoverMessage);

        while (udpClient is not null)
        {
            var endpointsSnapshot = broadcastEndpoints.ToArray();
            foreach (var endPoint in endpointsSnapshot)
            {
                try
                {
                    udpClient.Socket.SendTo(messageBytes, endPoint);
                }
                catch
                {
                    // ignore send errors
                }
            }

            try
            {
                await Task.Delay(1000);
            }
            catch
            {
                break;
            }
        }

        isBroadcasting = false;
    }

    private async void OnDiscoveredMdnsService(object? sender, DiscoveredMdnsServiceArgs service)
    {
        if (DiscoveredMdnsServices.Any(s => s.DeviceId == service.DeviceId)) return;

        DiscoveredMdnsServices.Add(service);
        
        logger.LogInformation("发现服务实例：{deviceId}，{deviceName}", service.DeviceId, service.DeviceName);

        DiscoveredDevice device = new(
            service.DeviceId,
            null,
            service.DeviceName,
            null,
            DateTimeOffset.UtcNow,
            DeviceOrigin.MdnsService,
            23333);

        await dispatcher.EnqueueAsync(() =>
        {
            var existing = DiscoveredDevices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
            if (existing is not null)
            {
                DiscoveredDevices[DiscoveredDevices.IndexOf(existing)] = device;
            }
            else
            {
                DiscoveredDevices.Add(device);
            }
        });
    }

    private async void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        var deviceId = e.ServiceInstanceName.ToString().Split('.')[0];

        await dispatcher.EnqueueAsync(() =>
        {
            // Remove from MDNS services list
            DiscoveredMdnsServices.RemoveAll(s => s.DeviceId == deviceId);
            
            // Remove from discovered devices
            try
            {
                var deviceToRemove = DiscoveredDevices
                    .Where(d => d.Origin is DeviceOrigin.MdnsService)
                    .FirstOrDefault(d => d.DeviceId == deviceId);

                if (deviceToRemove is not null)
                {
                    DiscoveredDevices.Remove(deviceToRemove);
                }
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                logger.LogWarning("移除设备时：{Message}", ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning("移除设备时出现意外错误：{Message}", ex.Message);
            }
        });
    }

    public async void OnReceived(EndPoint endpoint, byte[] buffer, long offset, long size)
    {
        try
        {
            var message = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
            if (!message.StartsWith("NOTIFYRELAY_DISCOVER:")) return;

            var parts = message.Split(':');
            if (parts.Length < 4) return;

            var deviceId = parts[1];
            if (deviceId == localDevice?.DeviceId) return;

            string decodedName;
            try
            {
                decodedName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
            }
            catch
            {
                decodedName = parts[2];
            }

            int devicePort = int.TryParse(parts[3], out var parsedPort) ? parsedPort : 23333;

            if (endpoint is IPEndPoint ipEndPoint)
            {
                var newEndpoint = new IPEndPoint(ipEndPoint.Address, DiscoveryPort);
                if (!broadcastEndpoints.Contains(newEndpoint))
                {
                    broadcastEndpoints.Add(newEndpoint);
                }
            }

            var discovered = new DiscoveredDevice(
                deviceId,
                null,
                decodedName,
                null,
                DateTimeOffset.UtcNow,
                DeviceOrigin.UdpBroadcast,
                devicePort);

            await dispatcher.EnqueueAsync(() =>
            {
                var existingDevice = DiscoveredDevices.FirstOrDefault(d => d.DeviceId == discovered.DeviceId);
                if (existingDevice is not null)
                {
                    var index = DiscoveredDevices.IndexOf(existingDevice);
                    DiscoveredDevices[index] = discovered;
                }
                else
                {
                    DiscoveredDevices.Add(discovered);
                }
            });

            StartCleanupTimer();
        }
        catch (Exception ex)
        {
            logger.LogWarning("处理 UDP 消息时出错：{message}", ex.Message);
        }
    }

    private DispatcherQueueTimer? _cleanupTimer;
    private readonly Lock _timerLock = new();
    
    private void StartCleanupTimer()
    {
        lock (_timerLock)
        {
            if (_cleanupTimer is not null) return;

            _cleanupTimer = dispatcher.CreateTimer();
            _cleanupTimer.Interval = TimeSpan.FromSeconds(1);
            _cleanupTimer.Tick += (s, e) => CleanupStaleDevices();
            _cleanupTimer.Start();
        }
    }

    private void StopCleanupTimer()
    {
        lock (_timerLock)
        {
            _cleanupTimer?.Stop();
            _cleanupTimer = null;
        }
    }

    private async void CleanupStaleDevices()
    {
        try
        {
            await dispatcher.EnqueueAsync(() =>
            {
                var now = DateTimeOffset.UtcNow;
                var staleThreshold = TimeSpan.FromSeconds(5);

                var staleDevices = DiscoveredDevices
                    .Where(d => d.Origin is DeviceOrigin.UdpBroadcast &&
                              now - d.LastSeen > staleThreshold)
                    .ToList();

                foreach (var device in staleDevices)
                {
                        DiscoveredDevices.Remove(device);
                }

                // Stop timer if no UDP devices left
                if (!DiscoveredDevices.Any(d => d.Origin is DeviceOrigin.UdpBroadcast))
                {
                    StopCleanupTimer();
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "清理过期设备时出错");
        }
    }

    public void StopDiscovery()
    {
        StopCleanupTimer();

        try
        {
            udpClient?.Dispose();
            udpClient = null;
            mdnsService.UnAdvertiseService();
        }
        catch (Exception ex)
        {
            logger.LogError("释放默认 UDP 客户端时出错：{message}", ex.Message);
        }
    }
}
