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
    INetworkService networkService) : INotificationService
{
    private readonly Dictionary<string, ObservableCollection<Notification>> deviceNotifications = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueue dispatcher = App.MainWindow.DispatcherQueue;
    
    private readonly ObservableCollection<Notification> activeNotifications = [];
    private readonly HashSet<string> loadedDeviceIds = [];
    
    // 跟踪图标请求状态的字典，key: packageName|deviceId, value: TaskCompletionSource<bool>
    private readonly Dictionary<string, TaskCompletionSource<bool>> pendingIconRequests = [];
    private const int ICON_REQUEST_TIMEOUT = 3000; // 图标请求最长等待时间：3秒

    /// <summary>
    /// Gets notifications for the currently active device session
    /// </summary>
    public ReadOnlyObservableCollection<Notification> NotificationHistory => new(activeNotifications);

    // Initialize the service - call this after DI container creates the instance
    public void Initialize()
    {
        ClearBadge();
        // Listen to device connection status changes
        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        
        ((INotifyPropertyChanged)deviceManager).PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(IDeviceManager.ActiveDevice) && deviceManager.ActiveDevice is not null)
            {
                _ = EnsureNotificationsLoadedAsync(deviceManager.ActiveDevice);
                UpdateActiveNotifications(deviceManager.ActiveDevice);
            }
        };

        if (deviceManager.ActiveDevice is not null)
        {
            _ = EnsureNotificationsLoadedAsync(deviceManager.ActiveDevice);
        }
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
                    logger.LogDebug("丢弃超级岛通知: {AppPackage}", message.AppPackage);
                    return;
                }

                var filter = remoteAppsRepository.GetAppNotificationFilterAsync(message.AppPackage, device.Id)
                ?? await remoteAppsRepository.AddOrUpdateApplicationForDevice(device.Id, message.AppPackage, message.AppName!, message.AppIcon);

                if (filter == NotificationFilter.Disabled) return;

                // 检查是否需要请求图标
                bool needIconRequest = !string.IsNullOrEmpty(message.AppPackage) && !IconUtils.AppIconExists(message.AppPackage);
                TaskCompletionSource<bool>? iconRequestTcs = null;
                string? requestKey = null;
                
                if (needIconRequest)
                {
                    // 创建 TaskCompletionSource 用于跟踪图标请求状态
                    requestKey = $"{message.AppPackage}|{device.Id}";
                    iconRequestTcs = new TaskCompletionSource<bool>();
                    pendingIconRequests[requestKey] = iconRequestTcs;
                    
                    // 发送图标请求
                    networkService.SendIconRequest(device.Id, message.AppPackage);
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
                    var notification = await Notification.FromMessage(message);
                    
                    if (message.NotificationType == NotificationType.New && filter == NotificationFilter.ToastFeed)
                    {
                        // Check for existing notification in this device's collection
                        var existingNotification = notifications.FirstOrDefault(n => n.Key == notification.Key);

                        if (existingNotification is not null)
                        {
                            // Update existing notification
                            var index = notifications.IndexOf(existingNotification);
                            if (existingNotification.Pinned)
                            {
                                notification.Pinned = true;
                            }
                            notifications[index] = notification;
                        }
                        else
                        {
                            // Add new notification
                            notifications.Insert(0, notification);
                        }
#if WINDOWS
                        // Check if the app is active before showing the notification
                        if (device.DeviceSettings.IgnoreWindowsApps && await IsAppActiveAsync(message.AppName!)) return;
#endif
                        await platformNotificationHandler.ShowRemoteNotification(message, device.Id);
                    }
                    else if ((message.NotificationType is NotificationType.Active || message.NotificationType is NotificationType.New)
                        && (filter is NotificationFilter.Feed || filter is NotificationFilter.ToastFeed))
                    {
                        notifications.Add(notification);
                    }
                    else
                    {
                        return;
                    }

                    notificationRepository.UpsertNotification(device.Id, message, notification.Pinned);
                    
                    SortNotifications(device.Id);
                    
                    // Update active notifications if this is for the active device
                    if (deviceManager.ActiveDevice?.Id == device.Id)
                    {
                        UpdateActiveNotifications(deviceManager.ActiveDevice);
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
                    // Update active notifications if this is for the active device
                    if (deviceManager.ActiveDevice?.Id == device.Id)
                    {
                        UpdateActiveNotifications(deviceManager.ActiveDevice);
                    }
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
                dispatcher.EnqueueAsync(() => notifications.Remove(notification));
                logger.LogDebug("已从设备 {DeviceId} 移除通知：{NotificationKey}", device.Id, notification);

                notificationRepository.DeleteNotification(device.Id, notification.Key);

                platformNotificationHandler.RemoveNotificationsByTagAndGroup(notification.Tag, notification.GroupKey);

                // Update active notifications if this is for the active device
                if (deviceManager.ActiveDevice?.Id == device.Id)
                {   
                    UpdateActiveNotifications(deviceManager.ActiveDevice);
                }

                var notificationToRemove = new NotificationMessage
                {
                    NotificationKey = notification.Key,
                    NotificationType = NotificationType.Removed
                };
                string jsonMessage = SocketMessageSerializer.Serialize(notificationToRemove);
                if (device.ConnectionStatus)
                {
                    sessionManager.SendMessage(device.Id, jsonMessage);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "移除通知时出错");
        }
    }

    public void TogglePinNotification(Notification notification)
    {
        var activeDevice = deviceManager.ActiveDevice!;

        dispatcher.EnqueueAsync(() =>
        {
            if (!deviceNotifications.TryGetValue(activeDevice.Id, out var notifications)) return;
            
            notification.Pinned = !notification.Pinned;
            // Update existing notification
            var index = notifications.IndexOf(notification);
            notifications[index] = notification;
            SortNotifications(activeDevice.Id);
            notificationRepository.UpdatePinned(activeDevice.Id, notification.Key, notification.Pinned);
                
            // Update active notifications since this is for the active device
            UpdateActiveNotifications(activeDevice);
        });
    }

    public void ClearAllNotification()
    {
        var activeDevice = deviceManager.ActiveDevice!;
        try
        {
            ClearHistory(activeDevice);
            activeNotifications.Clear();
            notificationRepository.ClearDeviceNotifications(activeDevice.Id);
            if (!activeDevice.ConnectionStatus) return;

            var command = new CommandMessage { CommandType = CommandType.ClearNotifications };
            string jsonMessage = SocketMessageSerializer.Serialize(command);
            sessionManager.SendMessage(activeDevice.Id, jsonMessage);
            logger.LogInformation("已清除设备 {DeviceId} 的全部通知", activeDevice.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "清除全部通知时出错");
        }
    }

    public void ClearHistory(PairedDevice device)
    {
        dispatcher.EnqueueAsync(() =>
        {
            try
            {
                if (deviceNotifications.TryGetValue(device.Id, out var notifications))
                {
                    notifications.Clear();
                    ClearBadge();
                }
                notificationRepository.ClearDeviceNotifications(device.Id);
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

    private void UpdateActiveNotifications(PairedDevice activeDevice)
    {
        dispatcher.EnqueueAsync(() =>
        {
            activeNotifications.Clear();

            if (deviceNotifications.TryGetValue(activeDevice.Id, out var deviceNotifs))
            {
                activeNotifications.AddRange(deviceNotifs);

                if (activeDevice.DeviceSettings.ShowBadge)
                {
                    // Get the blank badge XML payload for a badge number
                    XmlDocument badgeXml =
                        BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);

                    // Set the value of the badge in the XML to our number
                    XmlElement badgeElement = badgeXml.SelectSingleNode("/badge") as XmlElement;
                    badgeElement.SetAttribute("value", deviceNotifs.Count.ToString());

                    // Create the badge notification
                    BadgeNotification badge = new(badgeXml);

                    // Create the badge updater for the application
                    BadgeUpdater badgeUpdater =
                        BadgeUpdateManager.CreateBadgeUpdaterForApplication();

                    // And update the badge
                    badgeUpdater.Update(badge);
                }

            }
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
                notif.DeviceId = device.Id;
                notifications.Add(notif);
            }

            deviceNotifications[device.Id] = notifications;
            loadedDeviceIds.Add(device.Id);

            if (deviceManager.ActiveDevice?.Id == device.Id)
            {
                UpdateActiveNotifications(device);
            }
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
            dispatcher.EnqueueAsync(() =>
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
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理图标响应通知时出错");
        }
    }
}
