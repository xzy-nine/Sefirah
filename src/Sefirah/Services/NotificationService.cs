using System.Collections.ObjectModel;
using CommunityToolkit.WinUI;
using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Sefirah.Data.Models;
using Sefirah.Utils;
using Sefirah.Utils.Serialization;
using Windows.Data.Xml.Dom;
using Windows.System;
using Windows.UI.Notifications;
using Notification = Sefirah.Data.Models.Notification;

namespace Sefirah.Services;
public class NotificationService(
    ILogger logger,
    ISessionManager sessionManager,
    IDeviceManager deviceManager,
    IPlatformNotificationHandler platformNotificationHandler,
    RemoteAppRepository remoteAppsRepository,
    NotificationRepository notificationRepository,
    INetworkService networkService) : INotificationService, INotifyPropertyChanged
{
    private readonly Dictionary<string, ObservableCollection<Notification>> deviceNotifications = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueue dispatcher = App.MainWindow.DispatcherQueue;
    
    private readonly ObservableCollection<Notification> activeNotifications = [];
    private readonly HashSet<string> loadedDeviceIds = [];
    
    // 音乐媒体块相关（支持多个设备同时显示）
    private readonly ObservableCollection<MusicMediaBlock> _currentMusicMediaBlocks = new();
    private ReadOnlyObservableCollection<MusicMediaBlock>? _currentMusicMediaBlocksReadOnly;
    private System.Threading.Timer? _musicMediaBlockTimer;
    private const int MUSIC_MEDIA_BLOCK_TIMEOUT = 30; // 30秒超时
    
    // 跟踪图标请求状态的字典，key: packageName|deviceId, value: TaskCompletionSource<bool>
    private readonly Dictionary<string, TaskCompletionSource<bool>> pendingIconRequests = [];
    private const int ICON_REQUEST_TIMEOUT = 3000; // 图标请求最长等待时间：3秒

    /// <summary>
    /// 属性变更事件
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets all notifications from all devices
    /// </summary>
    public ReadOnlyObservableCollection<Notification> NotificationHistory => new(activeNotifications);
    
    /// <summary>
    /// 当前显示的音乐媒体块列表（只读）
    /// </summary>
    public ReadOnlyObservableCollection<MusicMediaBlock> CurrentMusicMediaBlocks => _currentMusicMediaBlocksReadOnly ??= new ReadOnlyObservableCollection<MusicMediaBlock>(_currentMusicMediaBlocks);

    // Initialize the service - call this after DI container creates the instance
    public void Initialize()
    {
        ClearBadge();
        // Listen to device connection status changes
        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        
        // When active device changes, ensure that device's notifications are loaded
        ((INotifyPropertyChanged)deviceManager).PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(IDeviceManager.ActiveDevice) && deviceManager.ActiveDevice is not null)
            {
                _ = EnsureNotificationsLoadedAsync(deviceManager.ActiveDevice);
            }
        };

        // Ensure notifications are loaded for all currently paired devices at startup
        try
        {
            foreach (var d in deviceManager.PairedDevices)
            {
                _ = EnsureNotificationsLoadedAsync(d);
            }

            // Subscribe to collection changes so newly paired devices get their notifications loaded automatically
            deviceManager.PairedDevices.CollectionChanged += (s, e) =>
            {
                if (e.NewItems is not null)
                {
                    foreach (var item in e.NewItems)
                    {
                        if (item is PairedDevice newDevice)
                        {
                            _ = EnsureNotificationsLoadedAsync(newDevice);
                        }
                    }
                }
            };
        }
        catch
        {
            // ignore any issue enumerating paired devices during startup
        }

        // 初始化音乐媒体块超时检查定时器，每1秒检查一次
        _musicMediaBlockTimer = new System.Threading.Timer(
            _ => CheckMusicMediaBlockTimeout(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Gets or creates the notification collection for a device session
    /// </summary>
    private ObservableCollection<Notification> GetOrCreateNotificationCollection(PairedDevice device)
    {
        if (!deviceNotifications.TryGetValue(device.Id, out var notifications))
        {
            notifications = [];
            deviceNotifications[device.Id] = notifications;
        }
        return notifications;
    }

    private void OnConnectionStatusChanged(object? sender, (PairedDevice Device, bool IsConnected) e)
    {
        if (e.IsConnected)
        {
            _ = EnsureNotificationsLoadedAsync(e.Device);
        }
    }

    public async Task HandleNotificationMessage(PairedDevice device, NotificationMessage message)
    {
        // Check if device has notification sync enabled
        if (!device.DeviceSettings.NotificationSyncEnabled) return;
        
        try
        { 
            if (message.Title is not null && message.AppPackage is not null)
            {
                // 过滤超级岛通知，识别段是'superisland:'
                if (message.AppPackage.StartsWith("superisland:"))
                {
                    // 注释掉丢弃超级岛通知的调试日志
                    // logger.LogDebug("丢弃超级岛通知: {AppPackage}", message.AppPackage);
                    return;
                }

                var filter = remoteAppsRepository.GetAppNotificationFilterAsync(message.AppPackage, device.Id)
                ?? await remoteAppsRepository.AddOrUpdateApplicationForDevice(device.Id, message.AppPackage, message.AppName!, message.AppIcon);

                if (filter == NotificationFilter.Disabled) return;

                // 检查是否需要请求图标
                // 处理mediaplay:前缀，移除前缀后再检查图标
                string actualPackageName = message.AppPackage?.StartsWith("mediaplay:") == true ? message.AppPackage.Substring("mediaplay:".Length) : message.AppPackage;
                bool needIconRequest = !string.IsNullOrEmpty(actualPackageName) && !IconUtils.AppIconExists(actualPackageName);
                TaskCompletionSource<bool>? iconRequestTcs = null;
                string? requestKey = null;
                
                if (needIconRequest)
                {
                    // 创建 TaskCompletionSource 用于跟踪图标请求状态
                    requestKey = $"{message.AppPackage}|{device.Id}";
                    iconRequestTcs = new TaskCompletionSource<bool>();
                    pendingIconRequests[requestKey] = iconRequestTcs;
                    
                    // 发送图标请求，使用实际包名
                    networkService.SendIconRequest(device.Id, actualPackageName);
                }

                // 等待图标请求完成，最长等待 3 秒
                if (iconRequestTcs != null)
                {
                    var timeoutTask = Task.Delay(ICON_REQUEST_TIMEOUT);
                    var completedTask = await Task.WhenAny(iconRequestTcs.Task, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        // 超时
                        logger.LogDebug("图标请求超时，继续显示通知：{AppPackage}", message.AppPackage);
                    }
                    else
                    {
                        // 图标请求完成
                        logger.LogDebug("图标请求完成，显示通知：{AppPackage}", message.AppPackage);
                    }
                    
                    // 清理请求状态
                    if (requestKey != null)
                    {
                        pendingIconRequests.Remove(requestKey);
                    }
                }

                await dispatcher.EnqueueAsync(async () =>
                        {
                            await EnsureNotificationsLoadedAsync(device);
                            var notifications = GetOrCreateNotificationCollection(device);
                            
                            // 先创建一个临时notification对象，仅用于检查是否存在相同内容的通知
                            var tempNotification = new Notification
                            {
                                AppPackage = message.AppPackage,
                                Title = message.Title,
                                Text = message.Text,
                                Type = NotificationType.New
                            };
                            
                            // 检查是否存在内容相同的现有通知（跨设备聚合）
                            var contentMatchNotification = activeNotifications.FirstOrDefault(n => 
                                n.AppPackage == tempNotification.AppPackage &&
                                n.Title == tempNotification.Title &&
                                n.Text == tempNotification.Text &&
                                n.Type == tempNotification.Type);
                            
                            // 只有当通知是新的（即之前没有相同内容的通知）时，才会发送到Windows通知中心
                            bool shouldSendNotification = contentMatchNotification is null;
                            
                            // 创建正式notification对象并加载图标（仅当需要发送到Windows通知中心时）
                            Notification? notification = null;
                            if (shouldSendNotification || message.NotificationType != NotificationType.New)
                            {
                                notification = await Notification.FromMessage(message);
                                notification.AddSourceDevice(device.Id, device.Name);
                                
                                // 确保图标路径和图标正确设置，无论设备是否活跃
                                if (!string.IsNullOrEmpty(message.AppPackage))
                                {
                                    string iconPath = IconUtils.GetAppIconPath(message.AppPackage);
                                    notification.IconPath = iconPath;
                                    
                                    // 确保图标文件存在
                                    if (IconUtils.AppIconExists(message.AppPackage))
                                    {
                                        // 立即同步加载图标，确保通知能显示图标
                                        await notification.LoadIconAsync();
                                    }
                                }
                            }
                            
                            bool shouldSaveNotification = true;
                            
                            if (message.NotificationType == NotificationType.New && filter == NotificationFilter.ToastFeed)
                            {
                                if (contentMatchNotification is not null)
                                {
                                    // 聚合通知：添加设备到现有通知的来源设备列表
                                    contentMatchNotification.AddSourceDevice(device.Id, device.Name);
                                    
                                    // 更新设备本地通知集合中的对应通知
                                    var existingNotification = notifications.FirstOrDefault(n => n.Key == contentMatchNotification.Key);
                                    if (existingNotification is not null)
                                    {
                                        var index = notifications.IndexOf(existingNotification);
                                        notifications[index] = contentMatchNotification;
                                    }
                                    else
                                    {
                                        notifications.Insert(0, contentMatchNotification);
                                    }
                                    
                                    // 使用已存在通知的tag和groupKey
                                    message.NotificationKey = contentMatchNotification.Key;
                                    message.Tag = contentMatchNotification.Tag;
                                    message.GroupKey = contentMatchNotification.GroupKey;
                                    
                                    // 只更新本地存储，不发送到Windows通知中心
                                    logger.LogDebug("相同内容的通知已存在，仅存储不发送到Windows通知中心");
                                }
                                else
                                {
                                    // 确保notification对象已创建
                                    if (notification == null)
                                    {
                                        notification = await Notification.FromMessage(message);
                                        notification.AddSourceDevice(device.Id, device.Name);
                                    }
                                    
                                    // 检查设备本地是否已有相同Key的通知
                                    var existingNotification = notifications.FirstOrDefault(n => n.Key == notification.Key);

                                    if (existingNotification is not null)
                                    {
                                        // 更新现有通知
                                        var index = notifications.IndexOf(existingNotification);
                                        if (existingNotification.Pinned)
                                        {
                                            notification.Pinned = true;
                                        }
                                        notifications[index] = notification;
                                    }
                                    else
                                    {
                                        // 添加新通知
                                        notifications.Insert(0, notification);
                                    }
                                    
                                    // 如果是新通知，使用内容生成唯一的tag和groupKey
                                    string contentHash = $"{notification.AppPackage}|{notification.Title}|{notification.Text}";
                                    message.Tag = contentHash;
                                    message.GroupKey = contentHash;
                                }
                            }
                            else if ((message.NotificationType is NotificationType.Active || message.NotificationType is NotificationType.New)
                                && (filter is NotificationFilter.Feed || filter is NotificationFilter.ToastFeed))
                            {
                                // 确保notification对象已创建
                                if (notification == null)
                                {
                                    notification = await Notification.FromMessage(message);
                                    notification.AddSourceDevice(device.Id, device.Name);
                                }
                                notifications.Add(notification);
                            }
                            else
                            {
                                shouldSaveNotification = false;
                            }
                            
                            if (shouldSaveNotification && notification != null)
                            {
                                notificationRepository.UpsertNotification(device.Id, message, notification.Pinned);
                                
                                SortNotifications(device.Id);
                                
                                // Update all notifications
                                UpdateActiveNotifications();
                                
                                #if WINDOWS
                                // Check if the app is active before showing the notification
                                if (device.DeviceSettings.IgnoreWindowsApps && await IsAppActiveAsync(message.AppName!)) return;
                                #endif
                                
                                // 只有当通知是新的时，才会发送到Windows通知中心
                                if (shouldSendNotification)
                                {
                                    // 发送通知到Windows通知中心
                                    await platformNotificationHandler.ShowRemoteNotification(message, device.Id);
                                }
                            }
                        });
            }
            else if (message.NotificationType == NotificationType.Removed)
            {
                if (!deviceNotifications.TryGetValue(device.Id, out var notifications)) return;
                var notification = notifications.FirstOrDefault(n => n.Key == message.NotificationKey);
                if (notification is not null && !notification.Pinned)
                {
                    await dispatcher.EnqueueAsync(() => notifications.Remove(notification));
                    notificationRepository.DeleteNotification(device.Id, message.NotificationKey);
                    // Update all notifications
                    UpdateActiveNotifications();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理通知消息时出错");
        }
    }

    public void RemoveNotification(PairedDevice device, Notification notification)
    {
        try
        {
            if (!deviceNotifications.TryGetValue(device.Id, out var notifications)) return;
                
            if (!notification.Pinned)
            {
                _ = dispatcher.EnqueueAsync(() => notifications.Remove(notification));
                logger.LogDebug("已从设备 {DeviceId} 移除通知：{NotificationKey}", device.Id, notification);

                notificationRepository.DeleteNotification(device.Id, notification.Key);

                platformNotificationHandler.RemoveNotificationsByTagAndGroup(notification.Tag, notification.GroupKey);

                // Always update the aggregated notifications after a removal
                UpdateActiveNotifications();

                // 不再向设备发送单条通知删除消息；本地已删除并持久化
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "移除通知时出错");
        }
    }

    public void TogglePinNotification(PairedDevice device, Notification notification)
    {
            _ = dispatcher.EnqueueAsync(() =>
        {
            if (!deviceNotifications.TryGetValue(device.Id, out var notifications)) return;
            
            notification.Pinned = !notification.Pinned;
            // Update existing notification: try to find by reference first, then by Key
            var index = notifications.IndexOf(notification);
            if (index < 0)
            {
                index = notifications.ToList().FindIndex(n => n.Key == notification.Key);
            }
            if (index >= 0)
            {
                notifications[index] = notification;
            }
            else
            {
                // If not found, insert at the front
                notifications.Insert(0, notification);
            }
            SortNotifications(device.Id);
            notificationRepository.UpdatePinned(device.Id, notification.Key, notification.Pinned);
                
            // Always update the aggregated notifications after pin/unpin
            UpdateActiveNotifications();
        });
    }

    public void ClearAllNotification(PairedDevice device)
    {
        try
        {
            ClearHistory(device);
            // Only clear non-pinned notifications from the DB for this device
            notificationRepository.ClearDeviceNotificationsExceptPinned(device.Id);
            // Ensure UI reflects the cleared device notifications (pinned remain)
            UpdateActiveNotifications();
            // 不再向设备发送清除命令；本地仅清除未置顶通知
            if (!device.ConnectionStatus) return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "清除全部通知时出错");
        }
    }

    /// <summary>
    /// 清除所有设备的全部通知
    /// </summary>
    public void ClearAllNotifications()
    {
        _ = dispatcher.EnqueueAsync(() =>
        {
            try
            {
                // 清除所有设备的通知集合
                foreach (var (deviceId, notifications) in deviceNotifications)
                {
                    // 移除集合中所有未置顶的通知，保留置顶
                    for (int i = notifications.Count - 1; i >= 0; i--)
                    {
                        if (!notifications[i].Pinned)
                        {
                            notifications.RemoveAt(i);
                        }
                    }
                }

                // 从聚合活跃列表中移除所有未置顶通知
                for (int i = activeNotifications.Count - 1; i >= 0; i--)
                {
                    if (!activeNotifications[i].Pinned)
                    {
                        activeNotifications.RemoveAt(i);
                    }
                }
                
                // 清除所有设备的未置顶通知历史（保留置顶）
                foreach (var device in deviceManager.PairedDevices)
                {
                    notificationRepository.ClearDeviceNotificationsExceptPinned(device.Id);
                }
                
                ClearBadge();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "清除所有设备通知时出错");
            }
        });
    }

    public void ClearHistory(PairedDevice device)
    {
        _ = dispatcher.EnqueueAsync(() =>
        {
            try
            {
                if (deviceNotifications.TryGetValue(device.Id, out var notifications))
                {
                    // 移除未置顶的通知，保留置顶
                    for (int i = notifications.Count - 1; i >= 0; i--)
                    {
                        if (!notifications[i].Pinned)
                        {
                            notifications.RemoveAt(i);
                        }
                    }
                    ClearBadge();
                }
                // 只清除数据库中未置顶的通知
                notificationRepository.ClearDeviceNotificationsExceptPinned(device.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "清除通知历史时出错");
            }
        });
    }

    private void SortNotifications(string deviceId)
    {
        dispatcher.EnqueueAsync(() =>
        {
            if (!deviceNotifications.TryGetValue(deviceId, out var notifications)) return;

            var sorted = notifications.OrderByDescending(n => n.Pinned)
                .ThenByDescending(n => n.TimeStamp)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                int currentIndex = notifications.IndexOf(sorted[i]);
                if (currentIndex != i)
                {
                    notifications.Move(currentIndex, i);
                }
            }
        });
    }

    public void ProcessReplyAction(PairedDevice device, string notificationKey, string ReplyResultKey, string replyText)
    {
        var replyAction = new ReplyAction
        {
            NotificationKey = notificationKey,
            ReplyResultKey = ReplyResultKey,
            ReplyText = replyText,
        };

        if (!device.ConnectionStatus) return;

        sessionManager.SendMessage(device.Id, SocketMessageSerializer.Serialize(replyAction));
        logger.LogDebug("已向设备 {DeviceId} 发送回复动作（通知键：{NotificationKey}）", device.Id, notificationKey);
    }

    public void ProcessClickAction(PairedDevice device, string notificationKey, int actionIndex)
    {
        var notificationAction = new NotificationAction
        {
            NotificationKey = notificationKey,
            ActionIndex = actionIndex,
            IsReplyAction = false
        };

        if (!device.ConnectionStatus) return;

        sessionManager.SendMessage(device.Id, SocketMessageSerializer.Serialize(notificationAction));
        logger.LogDebug("已向设备 {DeviceId} 发送点击动作（通知键：{NotificationKey}）", device.Id, notificationKey);
    }

    private void UpdateActiveNotifications(PairedDevice? activeDevice = null)
    {
        dispatcher.EnqueueAsync(() =>
        {
            activeNotifications.Clear();

            // 聚合所有设备的通知，相同内容的通知只保留一个，添加多个设备来源
            Dictionary<string, Notification> aggregatedNotifications = [];
            int totalNotifications = 0;

            // 遍历所有设备的通知
            foreach (var (deviceId, notifications) in deviceNotifications)
            {
                totalNotifications += notifications.Count;
                
                // 遍历每个设备的通知
                foreach (var notification in notifications)
                {
                    // 使用 AppPackage + Title + Text 作为聚合键
                    string aggregationKey = $"{notification.AppPackage}|{notification.Title}|{notification.Text}|{notification.Type}";
                    
                    if (aggregatedNotifications.TryGetValue(aggregationKey, out var existingNotification))
                    {
                        // 如果已存在相同内容的通知，合并设备来源
                        foreach (var sourceDevice in notification.SourceDevices)
                        {
                            existingNotification.AddSourceDevice(sourceDevice.DeviceId, sourceDevice.DeviceName);
                        }
                        
                        // 确保现有通知有图标，如果新通知有图标而现有通知没有
                        if (existingNotification.Icon == null && notification.Icon != null)
                        {
                            existingNotification.Icon = notification.Icon;
                            existingNotification.IconPath = notification.IconPath;
                        }
                        // 如果现有通知没有图标路径，从新通知获取
                        if (string.IsNullOrEmpty(existingNotification.IconPath) && !string.IsNullOrEmpty(notification.IconPath))
                        {
                            existingNotification.IconPath = notification.IconPath;
                            // 尝试加载图标
                            _ = existingNotification.LoadIconAsync();
                        }
                    }
                    else
                    {
                        // 如果不存在，添加到聚合字典中
                        aggregatedNotifications[aggregationKey] = notification;
                    }
                }
            }

            // 确保所有聚合后的通知都能正确加载图标
            foreach (var notification in aggregatedNotifications.Values)
            {
                if (notification.Icon == null && !string.IsNullOrEmpty(notification.AppPackage))
                {
                    // 处理mediaplay:前缀，移除前缀后再处理图标
                    string actualPackageName = notification.AppPackage.StartsWith("mediaplay:") ? notification.AppPackage.Substring("mediaplay:".Length) : notification.AppPackage;
                    // 确保图标路径正确
                    notification.IconPath = IconUtils.GetAppIconPath(actualPackageName);
                    // 检查图标文件是否存在，如果存在则尝试加载图标
                    if (IconUtils.AppIconExists(actualPackageName))
                    {
                        // 异步加载图标，不阻塞主线程
                        _ = notification.LoadIconAsync();
                    }
                }
            }

            // 将聚合后的通知添加到活跃通知列表
            activeNotifications.AddRange(aggregatedNotifications.Values);

            // Update badge if there's an active device and it has badge enabled
            if (activeDevice?.DeviceSettings.ShowBadge == true)
            {
                // Get the blank badge XML payload for a badge number
                XmlDocument badgeXml =
                    BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);

                // Set the value of the badge in the XML to our number
                XmlElement badgeElement = badgeXml.SelectSingleNode("/badge") as XmlElement;
                badgeElement.SetAttribute("value", totalNotifications.ToString());

                // Create the badge notification
                BadgeNotification badge = new(badgeXml);

                // Create the badge updater for the application
                BadgeUpdater badgeUpdater =
                    BadgeUpdateManager.CreateBadgeUpdaterForApplication();

                // And update the badge
                badgeUpdater.Update(badge);
            }

            // Sort all notifications by timestamp (newest first)
            var sortedNotifications = activeNotifications.OrderByDescending(n => n.TimeStamp).ToList();
            activeNotifications.Clear();
            activeNotifications.AddRange(sortedNotifications);
        });
    }

    private async Task EnsureNotificationsLoadedAsync(PairedDevice device)
    {
        if (loadedDeviceIds.Contains(device.Id)) return;

        try
        {
            var stored = notificationRepository.GetDeviceNotifications(device.Id);
            if (stored.Count == 0)
            {
                loadedDeviceIds.Add(device.Id);
                return;
            }

            var notifications = new ObservableCollection<Notification>();

            foreach (var entity in stored)
                {
                    var msg = SocketMessageSerializer.DeserializeMessage(entity.MessageJson) as NotificationMessage;
                    if (msg is null) continue;

                    var notif = await Notification.FromMessage(msg);
                    notif.Pinned = entity.Pinned;
                    notif.AddSourceDevice(device.Id, device.Name);
                    
                    // 确保图标路径正确设置
                        if (!string.IsNullOrEmpty(msg.AppPackage))
                        {
                            // 处理mediaplay:前缀，移除前缀后再处理图标
                            string actualPackageName = msg.AppPackage.StartsWith("mediaplay:") ? msg.AppPackage.Substring("mediaplay:".Length) : msg.AppPackage;
                            // 直接设置图标路径，确保所有设备的通知都能访问到正确的图标路径
                            string iconPath = IconUtils.GetAppIconPath(actualPackageName);
                            notif.IconPath = iconPath;
                            
                            // 确保图标文件存在
                            if (IconUtils.AppIconExists(actualPackageName))
                            {
                                // 立即同步加载图标，确保历史通知能显示图标
                                await notif.LoadIconAsync();
                            }
                            else
                            {
                                // 如果图标不存在，尝试异步请求图标
                                logger.LogDebug("通知图标不存在，尝试请求图标: {AppPackage}", actualPackageName);
                                // 发送图标请求，使用实际包名
                                networkService.SendIconRequest(device.Id, actualPackageName);
                                // 延迟一段时间后再次尝试加载图标
                                await Task.Delay(500);
                                await notif.LoadIconAsync();
                            }
                        }
                    
                    notifications.Add(notif);
                }

            deviceNotifications[device.Id] = notifications;
            loadedDeviceIds.Add(device.Id);

            // Update all notifications, not just for the active device
            UpdateActiveNotifications();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载设备 {DeviceId} 的通知时出错", device.Id);
        }
    }

    /// <summary>
    /// Clears the badge number on the app tile
    /// </summary>
    private void ClearBadge()
    {
        try
        {
            _ = dispatcher.EnqueueAsync(() =>
            {
                BadgeUpdater badgeUpdater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                badgeUpdater.Clear();
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动时清除角标失败");
        }
    }

#if WINDOWS
    private async Task<bool> IsAppActiveAsync(string appName)
    {
        try
        {
            // Get all running apps
            var diagnosticInfo = await AppDiagnosticInfo.RequestInfoAsync();
            var isAppActive = diagnosticInfo.Any(info =>
                info.AppInfo.DisplayInfo.DisplayName.Equals(appName, StringComparison.OrdinalIgnoreCase));
            return isAppActive;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "检查应用 '{AppName}' 是否处于活动状态时出错", appName);
            return false;
        }
    }
#endif

    /// <summary>
    /// 处理媒体播放通知
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="notificationMessage">通知消息</param>
    public async Task HandleMediaPlayNotification(PairedDevice device, NotificationMessage notificationMessage)
    {
        // logger.LogDebug("进入HandleMediaPlayNotification方法，设备：{deviceId}", device.Id);
        try
        {
            // 检查设备是否启用了通知同步
            // logger.LogDebug("检查设备通知同步设置，设备ID：{deviceId}，是否启用：{enabled}", device.Id, device.DeviceSettings.NotificationSyncEnabled);
            if (!device.DeviceSettings.NotificationSyncEnabled)
            {
                // logger.LogDebug("设备通知同步未启用，跳过处理媒体播放通知");
                return;
            }
            
            // 解析媒体播放通知的标题和文本
            // 注意：对于差异包，我们需要保留现有值，而不是将缺失的字段置空
            string title = notificationMessage.Title ?? "";
            string text = notificationMessage.Text ?? "";
            
            // logger.LogDebug("媒体播放通知内容：标题='{title}', 文本='{text}'", title, text);
            
            // 从通知消息中提取封面URL
            string? coverUrl = null;
            if (!string.IsNullOrEmpty(notificationMessage.CoverUrl))
            {
                coverUrl = notificationMessage.CoverUrl;
                // logger.LogDebug("从CoverUrl提取封面：{coverUrl}", coverUrl);
            }
            else if (!string.IsNullOrEmpty(notificationMessage.BigPicture))
            {
                coverUrl = notificationMessage.BigPicture;
                // logger.LogDebug("从BigPicture提取封面：{coverUrl}", coverUrl);
            }
            else if (!string.IsNullOrEmpty(notificationMessage.LargeIcon))
            {
                coverUrl = notificationMessage.LargeIcon;
                // logger.LogDebug("从LargeIcon提取封面：{coverUrl}", coverUrl);
            }
            else
            {
                // logger.LogDebug("未找到封面URL");
            }
            
            // 所有对_currentMusicMediaBlocks集合的访问都必须在UI线程上进行
            await dispatcher.EnqueueAsync(() =>
            {
                try
                {
                    // 更新或创建音乐媒体块（支持多个设备）
                    // logger.LogDebug("当前MusicMediaBlocks 数量：{count}", _currentMusicMediaBlocks.Count);
                    var existingBlock = _currentMusicMediaBlocks.FirstOrDefault(b => b.DeviceId == device.Id);
                    if (existingBlock == null)
                    {
                        // 创建新的音乐媒体块并加入集合
                        // logger.LogDebug("创建新的音乐媒体块，设备：{deviceId}", device.Id);
                        var newBlock = new MusicMediaBlock(
                            device.Id,
                            device.Name,
                            title,
                            text,
                            coverUrl
                        );
                        _currentMusicMediaBlocks.Add(newBlock);
                        // logger.LogDebug("新音乐媒体块已加入集合");
                    }
                    else
                    {
                        // 处理差异包：只更新那些在通知消息中明确提供的字段
                        // logger.LogDebug("更新现有音乐媒体块，设备：{deviceId}", device.Id);
                        string updatedTitle = existingBlock.Title;
                        string updatedText = existingBlock.Text;
                        string? updatedCoverUrl = existingBlock.CoverUrl;

                        if (!string.IsNullOrEmpty(notificationMessage.Title))
                        {
                            updatedTitle = notificationMessage.Title;
                            // logger.LogDebug("更新标题：{updatedTitle}", updatedTitle);
                        }

                        if (!string.IsNullOrEmpty(notificationMessage.Text))
                        {
                            updatedText = notificationMessage.Text;
                            // logger.LogDebug("更新文本：{updatedText}", updatedText);
                        }

                        if (!string.IsNullOrEmpty(coverUrl))
                        {
                            updatedCoverUrl = coverUrl;
                            // logger.LogDebug("更新封面URL：{updatedCoverUrl}", updatedCoverUrl);
                        }

                        // 直接更新音乐媒体块的属性
                        existingBlock.Update(updatedTitle, updatedText, updatedCoverUrl);
                        // logger.LogDebug("音乐媒体块更新完成");
                    }
                    
                    // logger.LogDebug("媒体播放通知处理完成");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "在UI线程上处理媒体播放通知时出错");
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理媒体播放通知时出错");
        }
    }
    
    /// <summary>
    /// 检查音乐媒体块是否超时
    /// </summary>
    public void CheckMusicMediaBlockTimeout()
    {
        dispatcher.EnqueueAsync(() =>
        {
            // 检查集合中每个媒体块是否超时，超时则移除
            var toRemove = _currentMusicMediaBlocks.Where(b => b.IsTimeout(MUSIC_MEDIA_BLOCK_TIMEOUT)).ToList();
            foreach (var b in toRemove)
            {
                try
                {
                    _currentMusicMediaBlocks.Remove(b);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "移除超时的音乐媒体块时出错，设备：{deviceId}", b.DeviceId);
                }
            }
        });
    }
    
    /// <summary>
    /// 处理图标响应，通知等待的图标请求任务
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="packageName">应用包名</param>
    public void HandleIconResponse(string deviceId, string packageName)
    {
        try
        {
            string requestKey = $"{packageName}|{deviceId}";
            if (pendingIconRequests.TryGetValue(requestKey, out var tcs))
            {
                // 完成等待的任务
                tcs.TrySetResult(true);
                logger.LogDebug("已通知图标请求完成：{PackageName}", packageName);
            }
            
            // 更新所有使用该包名的通知的图标
            dispatcher.EnqueueAsync(async () =>
            {
                // 遍历所有设备的通知
                foreach (var (deviceIdKey, notifications) in deviceNotifications)
                {
                    // 查找使用该包名的通知
                    var notificationsToUpdate = notifications.Where(n => n.AppPackage == packageName).ToList();
                    foreach (var notification in notificationsToUpdate)
                    {
                        // 更新图标路径和图标
                        notification.IconPath = IconUtils.GetAppIconPath(packageName);
                        await notification.LoadIconAsync();
                    }
                }
                
                // 刷新所有通知
                UpdateActiveNotifications();
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理图标响应通知时出错");
        }
    }
}
