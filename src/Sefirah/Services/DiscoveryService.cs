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
    
    /// <summary>
    /// 可发现设备列表
    /// </summary>
    public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = [];
    
    public List<DiscoveredMdnsServiceArgs> DiscoveredMdnsServices { get; } = [];
    private List<IPEndPoint> broadcastEndpoints = [];
    private const int DiscoveryPort = 23334;
    private bool isBroadcasting;
    private bool isInitialized = false;

    public async Task StartDiscoveryAsync()
    {
        try
        {
            // 1. 首先在UI线程上清理设备列表，确保初始状态为空
            await dispatcher.EnqueueAsync(() =>
            {
                DiscoveredDevices.Clear();
                DiscoveredMdnsServices.Clear();
                logger.LogInformation("设备列表已清理");
            });
            
            // 2. 确保localDevice完全初始化
            localDevice = await deviceManager.GetLocalDeviceAsync();
            logger.LogInformation("本地设备初始化完成：{deviceId}, {deviceName}", localDevice.DeviceId, localDevice.DeviceName);
            
            // 3. 设置事件处理程序，但此时isInitialized仍为false，不会添加设备
            mdnsService.DiscoveredMdnsService += OnDiscoveredMdnsService;
            mdnsService.ServiceInstanceShutdown += OnServiceInstanceShutdown;
            deviceManager.LocalDeviceNameChanged += OnLocalDeviceNameChanged;
            logger.LogInformation("事件处理程序已设置");
            
            // 4. 获取本地地址和构建设备发现消息
            var localAddresses = NetworkHelper.GetAllValidAddresses();
            logger.LogInformation($"将广播的地址：{string.Join(", ", localAddresses)}");
            var discoverMessage = BuildDiscoverMessage(localDevice.DeviceName);
            
            // 5. 构建广播端点列表
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
            
            // 6. 初始化UDP客户端
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
            }
            else
            {
                logger.LogError("UDP 客户端连接失败");
            }
            
            // 7. 立即发布mDNS服务广告
            var serverPort = networkService.ServerPort == 0 ? 23333 : networkService.ServerPort;
            var udpBroadcast = new UdpBroadcast
            {
                DeviceId = localDevice.DeviceId,
                DeviceName = localDevice.DeviceName,
                PublicKey = Encoding.UTF8.GetString(localDevice.PublicKey ?? Array.Empty<byte>()),
                Port = serverPort,
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            mdnsService.AdvertiseService(udpBroadcast, serverPort);
            logger.LogInformation("mDNS服务广告已发布");
            
            // 8. 最后设置初始化标志为true，允许添加设备
            isInitialized = true;
            logger.LogInformation("发现服务初始化标志已设置为true，开始接受设备发现事件");
            
            // 9. 开始广播设备信息
            BroadcastDeviceInfoAsync(discoverMessage);
            
            logger.LogInformation("发现服务已完全初始化，开始接受设备发现事件");

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动发现服务时出错");
            // 出错时确保初始化标志为false
            isInitialized = false;
            
            // 出错时清理设备列表
            await dispatcher.EnqueueAsync(() =>
            {
                DiscoveredDevices.Clear();
                DiscoveredMdnsServices.Clear();
            });
        }
    }
    
    /// <summary>
    /// 构建设备发现消息
    /// </summary>
    private string BuildDiscoverMessage(string deviceName)
    {
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(deviceName));
        var serverPort = networkService.ServerPort == 0 ? 23333 : networkService.ServerPort;
        return $"NOTIFYRELAY_DISCOVER:{localDevice.DeviceId}:{encodedName}:{serverPort}";
    }
    
    /// <summary>
    /// 处理本地设备名更改事件
    /// </summary>
    private void OnLocalDeviceNameChanged(object? sender, string newName)
    {
        try
        {
            logger.LogInformation("本地设备名已更改，重新广播设备信息：{newName}", newName);
            localDevice.DeviceName = newName;
            var discoverMessage = BuildDiscoverMessage(newName);
            BroadcastDeviceInfoAsync(discoverMessage);
            
            // 重新发布mDNS服务广告
            mdnsService.UnAdvertiseService();
            var serverPort = networkService.ServerPort == 0 ? 23333 : networkService.ServerPort;
            var udpBroadcast = new UdpBroadcast
            {
                DeviceId = localDevice.DeviceId,
                DeviceName = newName,
                PublicKey = Encoding.UTF8.GetString(localDevice.PublicKey ?? Array.Empty<byte>()),
                Port = serverPort,
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            mdnsService.AdvertiseService(udpBroadcast, serverPort);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理本地设备名更改时出错");
        }
    }

    private async void BroadcastDeviceInfoAsync(string discoverMessage)
    {
        if (isBroadcasting) return;
        isBroadcasting = true;

        var messageBytes = Encoding.UTF8.GetBytes(discoverMessage);

        while (udpClient is not null)
        {
            try
            {
                var endpointsSnapshot = broadcastEndpoints.ToArray();
                foreach (var endPoint in endpointsSnapshot)
                {
                    try
                    {
                        if (udpClient is not null)
                        {
                            udpClient.Socket.SendTo(messageBytes, endPoint);
                        }
                        else
                        {
                            logger.LogWarning("UDP 客户端已释放，停止广播");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("发送广播消息时出错：{ex}", ex);
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
            catch (Exception ex)
            {
                logger.LogError(ex, "广播设备信息时出错");
                break;
            }
        }

        isBroadcasting = false;
    }

    private async void OnDiscoveredMdnsService(object? sender, DiscoveredMdnsServiceArgs service)
    {
        // 跳过本机设备
        if (service.DeviceId == localDevice?.DeviceId) return;
        
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
            // 确保服务已初始化
            if (!isInitialized) return;
            
            // 最终检查：确保不是本机设备
            if (device.DeviceId == localDevice?.DeviceId) return;
            
            var existing = DiscoveredDevices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
            if (existing is not null)
            {
                var index = DiscoveredDevices.IndexOf(existing);
                DiscoveredDevices[index] = device;
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
            
            // 处理心跳消息
            if (message.StartsWith("HEARTBEAT:"))
            {
                logger.LogDebug("收到 UDP 心跳消息: {message} 来自 {endpoint}");
                
                // 心跳格式：HEARTBEAT:<deviceUuid><设备电量%>
                const string heartbeatPrefix = "HEARTBEAT:";
                if (message.Length > heartbeatPrefix.Length + 36 && endpoint is IPEndPoint heartbeatIpEndPoint)
                {
                    // 提取设备UUID
                    var deviceUuid = message.Substring(heartbeatPrefix.Length, 36);
                    
                    // 查找已配对的设备
                    var device = deviceManager.PairedDevices.FirstOrDefault(d => d.Id == deviceUuid);
                    if (device is not null)
                    {
                        // 直接调用NetworkService的ProcessProtocolMessageAsync方法处理心跳
                        _ = networkService.ProcessProtocolMessageAsync(device, message);
                    }
                }
                return;
            }
            
            // 处理设备发现消息
            if (!message.StartsWith("NOTIFYRELAY_DISCOVER:")) return;

            var parts = message.Split(':');
            if (parts.Length < 4) return;

            var deviceId = parts[1];
            
            // 1. 首先检查消息发送设备是否为本机，使用本地设备ID直接比较
            if (deviceId == localDevice?.DeviceId)
            {
                //logger.LogDebug("收到来自本机的UDP发现消息，忽略");
                return;
            }

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
                // 2. 确保服务已初始化
                if (!isInitialized)
                {
                    logger.LogDebug("发现服务未初始化，忽略设备添加");
                    return;
                }
                
                // 3. 再次检查，确保不是本机设备
                if (discovered.DeviceId == localDevice?.DeviceId)
                {
                    logger.LogDebug("尝试添加本机设备，忽略");
                    return;
                }
                
                // 4. 检查设备是否已经存在
                var existingDevice = DiscoveredDevices.FirstOrDefault(d => d.DeviceId == discovered.DeviceId);
                if (existingDevice is not null)
                {
                    // 更新现有设备
                    var index = DiscoveredDevices.IndexOf(existingDevice);
                    DiscoveredDevices[index] = discovered;
                }
            // 兼容处理旧格式的UDP广播
            else if (SocketMessageSerializer.DeserializeMessage(message) is UdpBroadcast broadcast)
            {
                if (broadcast.DeviceId == localDevice?.DeviceId) return;
                
                IPEndPoint? deviceEndpoint = broadcast.IpAddresses.Select(ip => new IPEndPoint(IPAddress.Parse(ip), DiscoveryPort)).FirstOrDefault();
                
                if (deviceEndpoint is not null && !broadcastEndpoints.Contains(deviceEndpoint))
                {
                    // 添加新设备
                    DiscoveredDevices.Add(discovered);
                }
                
                // Skip if we already have this device via mDNS
                if (DiscoveredMdnsServices.Any(s => s.PublicKey == broadcast.PublicKey)) return;
                
                var sharedSecret = EcdhHelper.DeriveKey(broadcast.PublicKey, localDevice!.PrivateKey);
                
                await dispatcher.EnqueueAsync(() =>
                {
                    var existingDevice = DiscoveredDevices.FirstOrDefault(d => d.DeviceId == broadcast.DeviceId);
                    if (existingDevice is not null)
                    {
                        // 更新现有设备
                        existingDevice.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(broadcast.TimeStamp);
                        existingDevice.IsOnline = true;
                    }
                    else
                    {
                        // 添加新设备
                        DiscoveredDevice device = new(
                            broadcast.DeviceId,
                            broadcast.PublicKey,
                            broadcast.DeviceName,
                            sharedSecret,
                            DateTimeOffset.FromUnixTimeMilliseconds(broadcast.TimeStamp),
                            DeviceOrigin.UdpBroadcast);
                        
                        DiscoveredDevices.Add(device);
                    }
                });
                
                StartCleanupTimer();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("处理 UDP 消息时出错：{message}", ex.Message);
        }
    }

    private DispatcherQueueTimer? _cleanupTimer;
    private readonly Lock _timerLock = new();
    
    public void OnDisconnected()
    {
        try
        {
            logger.LogInformation("UDP 客户端已断开连接，重新启动广播");
            isBroadcasting = false;
            
            // 重新启动广播
            var discoverMessage = BuildDiscoverMessage(localDevice.DeviceName);
            BroadcastDeviceInfoAsync(discoverMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 UDP 客户端断开连接时出错");
        }
    }
    
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

                // 更新离线设备的状态，不再直接删除
                foreach (var device in DiscoveredDevices.Where(d => d.Origin is DeviceOrigin.UdpBroadcast))
                {
                    DiscoveredDevices.Remove(device);
                }

                // 保留定时器运行，以便持续检查设备状态
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
            deviceManager.LocalDeviceNameChanged -= OnLocalDeviceNameChanged;
            
            // 清理设备列表，确保下次启动发现时不会显示旧设备
            dispatcher.TryEnqueue(() =>
            {
                DiscoveredDevices.Clear();
                DiscoveredMdnsServices.Clear();
                isInitialized = false;
            });
        }
        catch (Exception ex)
        {
            logger.LogError("释放默认 UDP 客户端时出错：{message}", ex.Message);
        }
    }
}
