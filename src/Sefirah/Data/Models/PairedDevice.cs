using System.Collections.Specialized;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Data.Contracts;
using Sefirah.Services.Socket;

namespace Sefirah.Data.Models;

public partial class PairedDevice : ObservableObject
{
    public string Id { get; private set; }

    private string name = string.Empty;
    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    public string Model { get; set; } = string.Empty;

    public List<string>? IpAddresses { get; set; } = [];

    private ImageSource? wallpaper;
    public ImageSource? Wallpaper
    {
        get => wallpaper;
        set => SetProperty(ref wallpaper, value);
    }

    private bool connectionStatus;
    public bool ConnectionStatus 
    {
        get => connectionStatus;
        set
        {
            // 只有当连接状态真正改变时才执行操作
            if (connectionStatus == value)
            {
                // 移除连接状态未变化的调试日志
                return;
            }
            
            var wasConnected = connectionStatus;
            logger.LogDebug("连接状态设置：值={value}, 之前已连接={wasConnected}, 当前连接状态={connectionStatus}", value, wasConnected, connectionStatus);
            
            if (value)
            {
                // 如果设置为true，取消任何挂起的断开连接操作
                disconnectDebounceTimer?.Stop();
                disconnectDebounceTimer?.Dispose();
                disconnectDebounceTimer = null;
                pendingDisconnect = false;
                
                SetProperty(ref connectionStatus, true);
                logger.LogDebug("连接状态已更新为：True");
                
                // 如果设备之前未连接，并且已经发送过SFTP请求，启动自动SFTP请求计时器
                if (!wasConnected)
                {
                    logger.LogDebug("设备 {Name} ({Id}) 已连接，检查HasSentSftpRequest属性", Name, Id);
                    
                    // 只有当HasSentSftpRequest为true时才启动计时器
                    if (HasSentSftpRequest)
                    {
                        logger.LogDebug("HasSentSftpRequest为true，启动自动SFTP计时器", Name, Id);
                        
                        // 确保之前的计时器已被释放
                        autoSftpTimer?.Stop();
                        autoSftpTimer?.Dispose();
                        
                        // 启动5秒自动SFTP请求计时器
                        autoSftpTimer = new System.Timers.Timer(5000);
                        autoSftpTimer.AutoReset = false; // 只触发一次
                        autoSftpTimer.Elapsed += (s, e) =>
                        {
                            logger.LogDebug("设备 {Name} ({Id}) 的自动SFTP计时器已触发", Name, Id);
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                    // 再次检查连接状态和HasSentSftpRequest属性
                                    if (ConnectionStatus && HasSentSftpRequest)
                                    {
                                        logger.LogDebug("设备仍然连接且HasSentSftpRequest为true，发送SFTP命令");
                                        
                                        // 从DI获取messageHandler并发送SFTP命令
                                        var messageHandler = Ioc.Default.GetRequiredService<IMessageHandler>();
                                        messageHandler.SendSftpCommand(this, "start");
                                        logger.LogDebug("SFTP命令发送成功");
                                    }
                                    else
                                    {
                                        logger.LogDebug("设备已断开连接或HasSentSftpRequest为false，跳过发送SFTP命令");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "设备 {Name} ({Id}) 的自动SFTP请求失败", Name, Id);
                                }
                                finally
                                {
                                    // 确保计时器被释放
                                    autoSftpTimer?.Dispose();
                                    autoSftpTimer = null;
                                }
                            });
                        };
                        autoSftpTimer.Start();
                        logger.LogDebug("设备 {Name} ({Id}) 的自动SFTP计时器已启动", Name, Id);
                    }
                    else
                    {
                        logger.LogDebug("HasSentSftpRequest为false，跳过启动自动SFTP计时器");
                    }
                }
            }
            else if (connectionStatus && !pendingDisconnect)
            {
                // If setting to false and currently true, debounce
                pendingDisconnect = true;
                disconnectDebounceTimer?.Stop();
                disconnectDebounceTimer?.Dispose();
                disconnectDebounceTimer = new System.Timers.Timer(5000); // 5 second debounce
                disconnectDebounceTimer.Elapsed += (s, e) =>
                {
                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (pendingDisconnect)
                        {
                            SetProperty(ref connectionStatus, false);
                            pendingDisconnect = false;
                        }
                        disconnectDebounceTimer?.Dispose();
                        disconnectDebounceTimer = null;
                    });
                };
                disconnectDebounceTimer.Start();
            }
            else if (!connectionStatus)
            {
                // Already false, do nothing
            }
        }
    }

    private ServerSession? session;
    public ServerSession? Session
    {
        get => session;
        set => SetProperty(ref session, value);
    }

    private DeviceStatus? status;
    public DeviceStatus? Status
    {
        get => status;
        set => SetProperty(ref status, value);
    }

    // Notify 协议会话所需信息
    public byte[]? SharedSecret { get; set; }
    public string? RemotePublicKey { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public string? RemoteIpAddress { get; set; }
    public string? RemoteDeviceType { get; set; }
    public string? RemoteBattery { get; set; }

    private System.Timers.Timer? disconnectDebounceTimer;
    private System.Timers.Timer? autoSftpTimer;
    private bool pendingDisconnect;

    private readonly IAdbService adbService;
    private readonly IUserSettingsService userSettingsService;
    private readonly ILogger<PairedDevice> logger;

    private IDeviceSettingsService deviceSettings;
    public IDeviceSettingsService DeviceSettings
    {
        get => deviceSettings;
        private set => SetProperty(ref deviceSettings, value);
    }

    private bool hasSentSftpRequest;
    public bool HasSentSftpRequest
    {
        get => hasSentSftpRequest;
        set
        {
            if (SetProperty(ref hasSentSftpRequest, value))
            {
                logger.LogDebug("HasSentSftpRequest属性值已更新为：{value}", value);
                // 当属性变化时保存到数据库
                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var deviceRepository = Ioc.Default.GetRequiredService<DeviceRepository>();
                        var deviceEntity = new RemoteDeviceEntity
                        {
                            DeviceId = Id,
                            Name = Name,
                            Model = Model,
                            IpAddresses = IpAddresses,
                            SharedSecret = SharedSecret,
                            PublicKey = RemotePublicKey,
                            HasSentSftpRequest = value
                        };
                        deviceRepository.AddOrUpdateRemoteDevice(deviceEntity);
                        logger.LogDebug("HasSentSftpRequest属性已保存到数据库");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "保存HasSentSftpRequest属性到数据库失败");
                    }
                });
            }
        }
    }


    public PairedDevice(string Id)
    {
        this.Id = Id;
        userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
        adbService = Ioc.Default.GetRequiredService<IAdbService>();
        logger = Ioc.Default.GetRequiredService<ILogger<PairedDevice>>();
        adbService.AdbDevices.CollectionChanged += OnAdbDevicesChanged;
        deviceSettings = userSettingsService.GetDeviceSettings(Id);
    }

    public ObservableCollection<AdbDevice> ConnectedAdbDevices { get; set; } = [];

    public bool HasAdbConnection
    {
        get
        {
            try
            {
                if (adbService == null)
                {
                    return false;
                }
                
                // 添加日志，便于调试
                var pairedDeviceId = Id;
                var pairedDeviceModel = Model;
                var adbDevicesCount = adbService.AdbDevices.Count;
                
                logger.LogDebug("检查 ADB 连接：已配对设备 ID='{pairedDeviceId}'，型号='{pairedDeviceModel}'", pairedDeviceId, pairedDeviceModel);
                logger.LogDebug("当前 ADB 设备数量：{adbDevicesCount}", adbDevicesCount);
                
                foreach (var adbDevice in adbService.AdbDevices)
                {
                    logger.LogDebug("ADB 设备：序列号='{adbDevice.Serial}'，型号='{adbDevice.Model}'，Android ID='{adbDevice.AndroidId}'，在线状态='{adbDevice.IsOnline}'", adbDevice.Serial, adbDevice.Model, adbDevice.AndroidId, adbDevice.IsOnline);
                    
                    // 检查匹配条件
                    var isOnline = adbDevice.IsOnline;
                    var androidIdMatch = !string.IsNullOrEmpty(adbDevice.AndroidId) && adbDevice.AndroidId == pairedDeviceId;
                    var modelMatch = string.IsNullOrEmpty(adbDevice.AndroidId) && 
                                     !string.IsNullOrEmpty(adbDevice.Model) && 
                                     !string.IsNullOrEmpty(pairedDeviceModel) &&
                                     (pairedDeviceModel.Equals(adbDevice.Model, StringComparison.OrdinalIgnoreCase) ||
                                      pairedDeviceModel.Contains(adbDevice.Model, StringComparison.OrdinalIgnoreCase) ||
                                      adbDevice.Model.Contains(pairedDeviceModel, StringComparison.OrdinalIgnoreCase));
                    
                    logger.LogDebug("  - 在线：{isOnline}，Android ID 匹配：{androidIdMatch}，型号匹配：{modelMatch}", isOnline, androidIdMatch, modelMatch);
                    
                    if (isOnline && (androidIdMatch || modelMatch))
                    {
                        logger.LogDebug("  - 设备匹配成功！");
                        return true;
                    }
                }
                
                logger.LogDebug("  - 未找到匹配的 ADB 设备");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "检查 ADB 连接时出错");
                return false;
            }
        }
    }

    private void OnAdbDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshConnectedAdbDevices();
        OnPropertyChanged(nameof(HasAdbConnection));
    }

    private async void RefreshConnectedAdbDevices()
    {
        try
        {
            // Use UI thread if available
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                ConnectedAdbDevices.Clear();

                var devices = adbService.AdbDevices
                    .Where(adbDevice => adbDevice.IsOnline && 
                        (
                            (!string.IsNullOrEmpty(adbDevice.AndroidId) && adbDevice.AndroidId == Id) ||
                            (string.IsNullOrEmpty(adbDevice.AndroidId) && 
                                !string.IsNullOrEmpty(adbDevice.Model) && 
                                !string.IsNullOrEmpty(Model) &&
                                (Model.Equals(adbDevice.Model, StringComparison.OrdinalIgnoreCase) ||
                                Model.Contains(adbDevice.Model, StringComparison.OrdinalIgnoreCase) ||
                                adbDevice.Model.Contains(Model, StringComparison.OrdinalIgnoreCase)))
                        ))
                    .ToList();

                ConnectedAdbDevices.AddRange(devices);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in RefreshConnectedAdbDevices: {ex.Message}");
        }
    }
}
