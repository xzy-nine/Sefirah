using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Sefirah.Data.Models;
using Sefirah.Utils;
using Sefirah.Utils.Serialization;
using System.ComponentModel;
using System.Diagnostics;

namespace Sefirah.ViewModels;
public sealed partial class MainPageViewModel : BaseViewModel
{
    #region Services
    private IDeviceManager DeviceManager { get; } = Ioc.Default.GetRequiredService<IDeviceManager>();
    private IScreenMirrorService ScreenMirrorService { get; } = Ioc.Default.GetRequiredService<IScreenMirrorService>();
    public INotificationService NotificationService { get; } = Ioc.Default.GetRequiredService<INotificationService>();
    private RemoteAppRepository RemoteAppsRepository { get; } = Ioc.Default.GetRequiredService<RemoteAppRepository>();
    private ISessionManager SessionManager { get; } = Ioc.Default.GetRequiredService<ISessionManager>();
    private IUpdateService UpdateService { get; } = Ioc.Default.GetRequiredService<IUpdateService>();
    private IFileTransferService FileTransferService { get; } = Ioc.Default.GetRequiredService<IFileTransferService>();
    private IMessageHandler MessageHandler { get; } = Ioc.Default.GetRequiredService<IMessageHandler>();
    #endregion

    #region Properties
    public ObservableCollection<PairedDevice> PairedDevices => DeviceManager.PairedDevices;
    public ReadOnlyObservableCollection<Notification> Notifications => NotificationService.NotificationHistory;
    public PairedDevice? Device => DeviceManager.ActiveDevice;
    
    /// <summary>
    /// 当前显示的音乐媒体块列表（支持多个设备同时显示）
    /// </summary>
    public ReadOnlyObservableCollection<MusicMediaBlock> CurrentMusicMediaBlocks => NotificationService.CurrentMusicMediaBlocks;

    [ObservableProperty]
    public partial bool LoadingScrcpy { get; set; } = false;

    public bool IsUpdateAvailable => UpdateService.IsUpdateAvailable;
    
    /// <summary>
    /// 当前设备是否正在运行仅音频模式的 scrcpy
    /// </summary>
    public bool IsAudioOnlyRunning
    {
        get
        {
            if (Device == null)
                return false;
            return ScreenMirrorService.IsAudioOnlyRunning(Device.Id);
        }
    }
    
    /// <summary>
    /// 获取当前音频状态的图标
    /// </summary>
    public string AudioStatusIcon
    {
        get
        {
            return IsAudioOnlyRunning ? "\uE995" : "\uE74F";
        }
    }
    
    /// <summary>
    /// 获取当前音频状态的文本描述
    /// </summary>
    public string AudioStatusText
    {
        get
        {
            return IsAudioOnlyRunning ? "仅音频播放中" : "未转发音频";
        }
    }
    #endregion

    public MainPageViewModel()
    {
        // 当 DeviceManager.ActiveDevice 变化时，让 x:Bind 的 Device 属性重新求值
        if (DeviceManager is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(IDeviceManager.ActiveDevice))
                {
                    OnPropertyChanged(nameof(Device));
                    OnPropertyChanged(nameof(IsAudioOnlyRunning));
                    OnPropertyChanged(nameof(AudioStatusIcon));
                    OnPropertyChanged(nameof(AudioStatusText));
                }
            };
        }
        
        // 监听 NotificationService 的 PropertyChanged 事件，当 MediaBlocks 列表变化时触发 UI 更新（集合自身变更由集合通知）
        if (NotificationService is INotifyPropertyChanged npc2)
        {
            npc2.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(INotificationService.CurrentMusicMediaBlocks))
                {
                    OnPropertyChanged(nameof(CurrentMusicMediaBlocks));
                }
            };
        }
    }
    
    /// <summary>
    /// 手动刷新音频状态属性，用于UI更新
    /// </summary>
    public void RefreshAudioStatus()
    {
        OnPropertyChanged(nameof(IsAudioOnlyRunning));
        OnPropertyChanged(nameof(AudioStatusIcon));
        OnPropertyChanged(nameof(AudioStatusText));
    }

    /// <summary>
    /// 根据设备ID获取设备名称
    /// </summary>
    public string GetDeviceName(string deviceId)
    {
        var device = PairedDevices.FirstOrDefault(d => d.Id == deviceId);
        return device?.Name ?? deviceId;
    }

    #region Commands

    [RelayCommand]
    public async Task ToggleConnection(PairedDevice? device)
    {
        if (Device!.ConnectionStatus)
        {
            var message = new CommandMessage { CommandType = CommandType.Disconnect };
            SessionManager.SendMessage(Device.Id, SocketMessageSerializer.Serialize(message));
            await Task.Delay(50);
            SessionManager.DisconnectDevice(Device.Id);
            Device.ConnectionStatus = false;
        }
    }

    [RelayCommand]
    public async Task StartScrcpy()
    {
        try
        {
            LoadingScrcpy = true;
            await ScreenMirrorService.StartScrcpy(Device!);
        }
        finally
        {
            await Task.Delay(1000);
            LoadingScrcpy = false;
        }
    }

    [RelayCommand]
    public void SwitchToNextDevice(int delta)
    {
        if (PairedDevices.Count <= 1)
            return;

        var currentIndex = -1;
        for (int i = 0; i < PairedDevices.Count; i++)
        {
            if (PairedDevices[i].Id == Device?.Id)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex == -1)
            return;

        int nextIndex;
        if (delta < 0)
        {
            // Move to next device (or loop back to first)
            nextIndex = (currentIndex + 1) % PairedDevices.Count;
        }
        else
        {
            // Move to previous device (or loop to last)
            nextIndex = (currentIndex - 1 + PairedDevices.Count) % PairedDevices.Count;
        }

        DeviceManager.ActiveDevice = PairedDevices[nextIndex];
    }

    [RelayCommand]
    public async void SetRingerMode(string? modeStr)
    {
        if (int.TryParse(modeStr, out int mode))
        {
            // 模式2：仅音频播放中，模式0：未转发音频
            if (mode == 2)
            {
                // 启动仅音频模式的 scrcpy
                await ScreenMirrorService.StartScrcpy(Device!, "--no-video");
            }
            else if (mode == 0)
            {
                // 停止 scrcpy 进程
                ScreenMirrorService.StopScrcpyByDeviceId(Device!.Id);
            }
            // 不再发送铃声模式消息到设备
            
            // 刷新音频状态属性，更新UI
            RefreshAudioStatus();
        }
    }

    [RelayCommand]
    public void ClearAllNotifications()
    {
        NotificationService.ClearAllNotifications();
    }

    [RelayCommand]
    public void Update()
    {
        UpdateService.DownloadUpdatesAsync();
    }

    [RelayCommand]
    public void RemoveNotification(Notification notification)
    {
        NotificationService.RemoveNotification(Device!, notification);
    }

    [RelayCommand]
    public void HandleNotificationAction(NotificationAction action)
    {
        NotificationService.ProcessClickAction(Device!, action.NotificationKey, action.ActionIndex);
    }
    
    [RelayCommand]
    public void SendMediaControl(string mediaControlParam)
    {
        if (string.IsNullOrEmpty(mediaControlParam))
        {
            return;
        }
        
        // 解析参数：格式为 "deviceId:action"
        var parts = mediaControlParam.Split(':');
        if (parts.Length != 2)
        {
            return;
        }
        
        string deviceId = parts[0];
        string action = parts[1];
        
        // 发送媒体控制请求到指定设备
        SessionManager.SendMediaControlRequest(deviceId, action);
    }

    [RelayCommand]
    public void StartSftpConnection()
    {
        if (Device != null)
        {
            // 设置手动发送过SFTP请求的标记
            Device.HasSentSftpRequest = true;
            MessageHandler.SendSftpCommand(Device, "start");
        }
    }

    #endregion

    #region Methods

    public async Task OpenApp(Notification notification, string? deviceId = null)
    {
        Debug.WriteLine($"[调试] MainPageViewModel.OpenApp 被调用：notification.Key={notification?.Key} deviceId={deviceId}");

        // 如果未指定设备ID，使用当前活跃设备
        var targetDevice = deviceId != null ? DeviceManager.FindDeviceById(deviceId) : Device;
        if (targetDevice == null)
        {
            Debug.WriteLine("[警告] 找不到目标设备（targetDevice 为 null），取消打开应用。请检查 deviceId 是否正确或设备是否已配对。");
            return;
        }

        var notificationToInvoke = new NotificationMessage
        {
            NotificationType = NotificationType.Invoke,
            NotificationKey = notification.Key,
        };
        string? appIcon = string.Empty;
        if (!string.IsNullOrEmpty(notification.AppPackage))
        {
            appIcon = IconUtils.GetAppIconFilePath(notification.AppPackage);
        }

        Debug.WriteLine($"[调试] 调用 ScreenMirrorService.StartScrcpy: deviceId={targetDevice.Id} appPackage={notification.AppPackage} appIcon={appIcon}");
        var started = await ScreenMirrorService.StartScrcpy(targetDevice, $"--new-display --start-app={notification.AppPackage}", appIcon);

        Debug.WriteLine($"[调试] ScreenMirrorService.StartScrcpy 返回: started={started}");

        // Scrcpy doesn't have a way of opening notifications afaik, so we will just have the notification listener on Android to open it for us
        // Plus we have to wait (2s will do ig?) until the app is actually launched to send the intent for launching the notification since Google added a lot more restrictions in this particular case
        if (started && targetDevice.ConnectionStatus)
        {
            Debug.WriteLine($"[调试] scrcpy 已启动且设备连接，等待 2s 然后发送通知调用到设备 {targetDevice.Id}");
            await Task.Delay(2000);
            SessionManager.SendMessage(targetDevice.Id, SocketMessageSerializer.Serialize(notificationToInvoke));
        }
    }

    public void UpdateNotificationFilter(string appPackage)
    {
        RemoteAppsRepository.UpdateAppNotificationFilter(Device!.Id, appPackage, NotificationFilter.Disabled);
    }

    public void ToggleNotificationPin(Notification notification)
    {
        if (Device != null)
        {
            NotificationService.TogglePinNotification(Device, notification);
        }
    }

    public void SendFiles(IReadOnlyList<IStorageItem> storageItems)
    {
        FileTransferService.SendFiles(storageItems);
    }

    public void HandleNotificationReply(Notification notification, string replyText)
    {
        NotificationService.ProcessReplyAction(Device!, notification.Key, notification.ReplyResultKey!, replyText);
    }

    #endregion

}
