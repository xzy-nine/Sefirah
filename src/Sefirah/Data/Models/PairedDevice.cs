using System.Collections.Specialized;
using CommunityToolkit.WinUI;
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

    public List<PhoneNumber>? PhoneNumbers { get; set; } = [];

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
            if (value)
            {
                // If setting to true, cancel any pending disconnect
                disconnectDebounceTimer?.Stop();
                disconnectDebounceTimer?.Dispose();
                disconnectDebounceTimer = null;
                pendingDisconnect = false;
                SetProperty(ref connectionStatus, true);
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

    private DeviceStatus? status;
    public DeviceStatus? Status
    {
        get => status;
        set => SetProperty(ref status, value);
    }

    private ServerSession? session;
    public ServerSession? Session
    {
        get => session;
        set => SetProperty(ref session, value);
    }

    // Notify 协议会话所需信息
    public byte[]? SharedSecret { get; set; }
    public string? RemotePublicKey { get; set; }
    public DateTime? LastHeartbeat { get; set; }

    private System.Timers.Timer? disconnectDebounceTimer;
    private bool pendingDisconnect;

    private readonly IAdbService adbService;
    private readonly IUserSettingsService userSettingsService;

    private IDeviceSettingsService deviceSettings;
    public IDeviceSettingsService DeviceSettings
    {
        get => deviceSettings;
        private set => SetProperty(ref deviceSettings, value);
    }


    public PairedDevice(string Id)
    {
        this.Id = Id;
        userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
        adbService = Ioc.Default.GetRequiredService<IAdbService>();
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
                
                System.Diagnostics.Debug.WriteLine($"检查 ADB 连接：已配对设备 ID='{pairedDeviceId}'，型号='{pairedDeviceModel}'");
                System.Diagnostics.Debug.WriteLine($"当前 ADB 设备数量：{adbDevicesCount}");
                
                foreach (var adbDevice in adbService.AdbDevices)
                {
                    System.Diagnostics.Debug.WriteLine($"ADB 设备：序列号='{adbDevice.Serial}'，型号='{adbDevice.Model}'，Android ID='{adbDevice.AndroidId}'，在线状态='{adbDevice.IsOnline}'");
                    
                    // 检查匹配条件
                    var isOnline = adbDevice.IsOnline;
                    var androidIdMatch = !string.IsNullOrEmpty(adbDevice.AndroidId) && adbDevice.AndroidId == pairedDeviceId;
                    var modelMatch = string.IsNullOrEmpty(adbDevice.AndroidId) && 
                                     !string.IsNullOrEmpty(adbDevice.Model) && 
                                     !string.IsNullOrEmpty(pairedDeviceModel) &&
                                     (pairedDeviceModel.Equals(adbDevice.Model, StringComparison.OrdinalIgnoreCase) ||
                                      pairedDeviceModel.Contains(adbDevice.Model, StringComparison.OrdinalIgnoreCase) ||
                                      adbDevice.Model.Contains(pairedDeviceModel, StringComparison.OrdinalIgnoreCase));
                    
                    System.Diagnostics.Debug.WriteLine($"  - 在线：{isOnline}，Android ID 匹配：{androidIdMatch}，型号匹配：{modelMatch}");
                    
                    if (isOnline && (androidIdMatch || modelMatch))
                    {
                        System.Diagnostics.Debug.WriteLine($"  - 设备匹配成功！");
                        return true;
                    }
                }
                
                System.Diagnostics.Debug.WriteLine("  - 未找到匹配的 ADB 设备");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"检查 ADB 连接时出错：{ex.Message}");
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
