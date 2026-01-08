using Sefirah.Data.Contracts;
using Sefirah.Data.Models;
using Sefirah.Extensions;
using Sefirah.Utils;
using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// Desktop implementation of the platform notification handler using D-Bus
/// </summary>
public class DesktopNotificationHandler(
    ILogger<DesktopNotificationHandler> logger,
    ISessionManager sessionManager,
    IDeviceManager deviceManager) : IPlatformNotificationHandler, IDisposable
{
    private Connection? _connection;
    private NotificationsService? _notificationService;
    private Notifications? _notifications;
    private bool _isInitialized = false;
    private readonly Dictionary<string, uint> _notificationIds = [];
    private readonly Dictionary<uint, NotificationActionData> _notificationActions = [];
    private IDisposable? _actionWatcher;

    private async Task<bool> EnsureInitializedAsync()
    {
        if (_isInitialized && _notifications != null)
            return true;

        try
        {
            // Check if we have a session bus address
            string? sessionBusAddress = Address.Session;
            if (sessionBusAddress is null)
            {
                logger.LogWarning("无法确定会话总线地址，系统可能不支持 D-Bus。");
                return false;
            }

            // Create connection to the session bus
            _connection = new Connection(sessionBusAddress);
            await _connection.ConnectAsync();
            logger.LogDebug("已连接到 D-Bus 会话总线");

            // Create the notifications service
            _notificationService = new NotificationsService(_connection, "org.freedesktop.Notifications");
            _notifications = _notificationService.CreateNotifications("/org/freedesktop/Notifications");

            // Test if the notification service is available
            var serverInfo = await _notifications.GetServerInformationAsync();
            logger.LogDebug("通知服务器：{Name} {Version}，厂商：{Vendor}", 
                serverInfo.Name, serverInfo.Version, serverInfo.Vendor);

            // Set up action watching
            _actionWatcher = await _notifications.WatchActionInvokedAsync(OnActionInvoked);
            logger.LogDebug("已为 D-Bus 通知注册动作监听");

            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "初始化 D-Bus 通知失败");
            return false;
        }
    }

    public async Task ShowRemoteNotification(NotificationMessage message, string deviceId)
    {
        if (!await EnsureInitializedAsync() || _notifications == null)
            return;

        try
        {
            var hints = new Dictionary<string, VariantValue>();
            var categoryHint = NotificationHints.Category("device.added");
            var urgencyHint = NotificationHints.NormalUrgency();
            var soundHint = NotificationHints.SuppressSound(false);
            var soundNameHint = NotificationHints.SoundName("message-new-instant"); 
            
            hints.Add(categoryHint.Key, categoryHint.Value);
            hints.Add(urgencyHint.Key, urgencyHint.Value);
            hints.Add(soundHint.Key, soundHint.Value);
            hints.Add(soundNameHint.Key, soundNameHint.Value);

            // Prepare actions (no actions supported since NotificationAction has been removed)
            var actions = new List<string>();
            var actionData = new NotificationActionData
            {
                NotificationType = "RemoteNotification",
                DeviceId = deviceId,
                NotificationKey = message.NotificationKey,
                Actions = []
            };

            var notificationId = await _notifications.NotifyAsync(
                appName: message.AppName ?? "Sefirah",
                replacesId: 0,
                appIcon: "smartphone", // Generic smartphone icon
                summary: message.Title ?? "Remote Notification",
                body: message.Text ?? "",
                actions: actions.ToArray(),
                hints: hints,
                expireTimeout: 8000 // 8 seconds
            );

            if (!string.IsNullOrEmpty(message.NotificationKey))
            {
                _notificationIds[message.NotificationKey] = notificationId;
            }

            // Store action data for this notification
            if (actionData.Actions.Count > 0)
            {
                _notificationActions[notificationId] = actionData;
            }

            logger.LogDebug("已发送远程通知：ID：{NotificationId}，动作数：{ActionCount}", 
                notificationId, actionData.Actions.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示远程通知失败");
        }
    }

    public async void ShowClipboardNotification(string title, string text, string? iconPath = null)
    {
        if (!await EnsureInitializedAsync() || _notifications == null)
            return;

        try
        {
            var hints = new Dictionary<string, VariantValue>();
            var categoryHint = NotificationHints.Category("");
            var urgencyHint = NotificationHints.NormalUrgency();
            var soundHint = NotificationHints.SuppressSound(false);
            
            hints.Add(categoryHint.Key, categoryHint.Value);
            hints.Add(urgencyHint.Key, urgencyHint.Value);
            hints.Add(soundHint.Key, soundHint.Value);

            string appIcon = "dialog-information";
            if (!string.IsNullOrEmpty(iconPath))
            {
                // Try to use the provided icon path, fallback to default if needed
                appIcon = iconPath.StartsWith("/") ? $"file://{iconPath}" : iconPath;
            }

            var notificationId = await _notifications.NotifyAsync(
                appName: "Sefirah",
                replacesId: 0,
                appIcon: appIcon,
                summary: title,
                body: text,
                actions: [],
                hints: hints,
                expireTimeout: 5000 // 5 seconds
            );

            logger.LogDebug("已发送简单通知：ID：{NotificationId}", notificationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示简单通知失败");
        }
    }

    public async void ShowClipboardNotificationWithActions(string title, string text, string? actionLabel = null, string? actionData = null)
    {
        if (!await EnsureInitializedAsync() || _notifications == null)
            return;

        try
        {
            var hints = new Dictionary<string, VariantValue>();
            var categoryHint = NotificationHints.Category("clipboard.action");
            var urgencyHint = NotificationHints.NormalUrgency();
            var soundHint = NotificationHints.SuppressSound(false);
            
            hints.Add(categoryHint.Key, categoryHint.Value);
            hints.Add(urgencyHint.Key, urgencyHint.Value);
            hints.Add(soundHint.Key, soundHint.Value);

            // Prepare actions for clipboard
            var actions = new List<string>();
            var notificationActionData = new NotificationActionData
            {
                NotificationType = "Clipboard",
                Actions = []
            };

            if (!string.IsNullOrEmpty(actionLabel) && !string.IsNullOrEmpty(actionData))
            {
                actions.Add("clipboard_action");
                actions.Add(actionLabel);

                notificationActionData.Actions.Add(new NotificationActionInfo
                {
                    ActionId = "clipboard_action",
                    ActionIndex = 0,
                    Label = actionLabel,
                    Data = actionData
                });
            }

            var notificationId = await _notifications.NotifyAsync(
                appName: "Sefirah",
                replacesId: 0,
                appIcon: "edit-copy", 
                summary: title,
                body: text,
                actions: actions.ToArray(), 
                hints: hints,
                expireTimeout: 4000 
            );

            // Store action data for this notification
            if (notificationActionData.Actions.Count > 0)
            {
                _notificationActions[notificationId] = notificationActionData;
            }

            logger.LogDebug("已发送剪贴板通知：ID：{NotificationId}，动作数：{ActionCount}", 
                notificationId, notificationActionData.Actions.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示剪贴板通知失败");
        }
    }

    public async void ShowCompletedFileTransferNotification(string subtitle, string transferId, string? filePath = null, string? folderPath = null)
    {
        if (!await EnsureInitializedAsync() || _notifications == null)
            return;

        try
        {
            var hints = new Dictionary<string, VariantValue>();
            var categoryHint = NotificationHints.Category("transfer.complete");
            var urgencyHint = NotificationHints.NormalUrgency();
            var soundHint = NotificationHints.SuppressSound(false);
            
            hints.Add(categoryHint.Key, categoryHint.Value);
            hints.Add(urgencyHint.Key, urgencyHint.Value);
            hints.Add(soundHint.Key, soundHint.Value);

            var notificationId = await _notifications.NotifyAsync(
                appName: "Sefirah",
                replacesId: 0,
                appIcon: "folder-download",
                summary: "FileTransferNotification.Completed".GetLocalizedResource(),
                body: subtitle,
                actions: [], 
                hints: hints,
                expireTimeout: 6000 // 6 seconds
            );

            logger.LogDebug("已发送文件传输通知：ID：{NotificationId}", notificationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示文件传输通知失败");
        }
    }

    public void ShowFileTransferNotification(string subtitle, string fileName, string transferId, uint notificationSequence, double? progress = null)
    {
        return; // Not implemented for D-Bus notifications
    }

    public async Task RegisterForNotifications()
    {
        await EnsureInitializedAsync();
    }

    public async Task RemoveNotificationByTag(string? notificationKey)
    {
        if (!_isInitialized || _notifications is null || string.IsNullOrEmpty(notificationKey))
            return;

        try
        {
            if (_notificationIds.TryGetValue(notificationKey, out uint notificationId))
            {
                await _notifications.CloseNotificationAsync(notificationId);
                _notificationIds.Remove(notificationKey);
                logger.LogDebug("已移除通知，键：{NotificationKey}", notificationKey);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "移除通知失败，键：{NotificationKey}", notificationKey);
        }
    }

    public Task RemoveNotificationsByGroup(string? groupKey)
    {
        // D-Bus notifications don't have group concept like Windows, so we ignore this
        return Task.CompletedTask;
    }

    public Task RemoveNotificationsByTagAndGroup(string? tag, string? groupKey)
    {
        // D-Bus notifications don't have group concept like Windows, so we ignore this
        return Task.CompletedTask;
    }

    public Task ClearAllNotifications()
    {
        // D-Bus doesn't have a clear all method, would need to track all IDs to close individually
        // For simplicity, we ignore this since notifications will expire anyway
        return Task.CompletedTask;
    }

    private async void OnActionInvoked(Exception? ex, (uint Id, string ActionKey) args)
    {
        if (ex != null)
        {
            logger.LogError(ex, "动作回调处理时出错");
            return;
        }

        try
        {
            logger.LogDebug("动作已触发 - ID：{NotificationId}，ActionKey：{ActionKey}", args.Id, args.ActionKey);

            if (!_notificationActions.TryGetValue(args.Id, out var actionData))
            {
                logger.LogWarning("未找到通知 ID 的操作数据：{NotificationId}", args.Id);
                return;
            }

            // Handle the action directly
            await HandleNotificationAction(args.Id, args.ActionKey, actionData);

            // Clean up action data
            _notificationActions.Remove(args.Id);
        }
        catch (Exception actionEx)
        {
            logger.LogError(actionEx, "处理通知操作时出错");
        }
    }

    private async Task HandleNotificationAction(uint notificationId, string actionKey, NotificationActionData actionData)
    {
        try
        {
            logger.LogDebug("正在处理通知动作 - ID：{NotificationId}，ActionKey：{ActionKey}", notificationId, actionKey);

            var action = actionData.Actions.FirstOrDefault(a => a.ActionId == actionKey);
            if (action == null)
            {
                logger.LogWarning("未找到动作：{ActionKey}（通知 ID：{NotificationId}）", actionKey, notificationId);
                return;
            }

            // Route to appropriate handler based on notification type
            switch (actionData.NotificationType)
            {
                case "RemoteNotification":
                    await HandleRemoteNotificationAction(actionData, action);
                    break;
                
                case "Clipboard":
                    await HandleClipboardAction(action);
                    break;
                
                default:
                    logger.LogWarning("未处理的通知类型：{NotificationType}", actionData.NotificationType);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理通知操作时出错");
        }
    }

    private async Task HandleRemoteNotificationAction(NotificationActionData actionData, NotificationActionInfo action)
    {
        if (string.IsNullOrEmpty(actionData.DeviceId) || string.IsNullOrEmpty(actionData.NotificationKey))
        {
            logger.LogWarning("缺少设备 ID 或通知键，无法处理远程通知动作");
            return;
        }

        // Find the device by ID
        var device = deviceManager.FindDeviceById(actionData.DeviceId);
        if (device == null)
        {
            logger.LogWarning("未找到设备，ID：{DeviceId}", actionData.DeviceId);
            return;
        }
    }

    private async Task HandleClipboardAction(NotificationActionInfo action)
    {
        if (string.IsNullOrEmpty(action.Data))
        {
            logger.LogWarning("剪贴板动作未提供数据");
            return;
        }

        // Handle clipboard action - open URL
        if (Uri.TryCreate(action.Data, UriKind.Absolute, out Uri? uri))
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = uri.ToString(),
                        UseShellExecute = true
                    }
                };
                process.Start();
                logger.LogDebug("已打开 URL：{Url}", uri);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "打开 URL 失败：{Url}", uri);
            }
        }
        else
        {
            logger.LogWarning("剪贴板操作中的 URL 无效：{Data}", action.Data);
        }
    }

    public void Dispose()
    {
        try
        {
            _actionWatcher?.Dispose();
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "释放 D-Bus 连接时出错");
        }
    }
}

/// <summary>
/// Data structure to store notification action information for D-Bus notifications
/// </summary>
internal class NotificationActionData
{
    public string NotificationType { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public string? NotificationKey { get; set; }
    public List<NotificationActionInfo> Actions { get; set; } = [];
}

/// <summary>
/// Information about a specific notification action
/// </summary>
internal class NotificationActionInfo
{
    public string ActionId { get; set; } = string.Empty;
    public int ActionIndex { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Data { get; set; }
}

