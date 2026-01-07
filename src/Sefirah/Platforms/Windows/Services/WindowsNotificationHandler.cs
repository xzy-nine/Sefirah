using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Sefirah.Data.Contracts;
using Sefirah.Data.Models;
using Sefirah.Extensions;
using Sefirah.Services;
using Sefirah.Utils;
using Uno.Logging;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Streams;
using static Sefirah.Constants;
using System.IO;

namespace Sefirah.Platforms.Windows.Services;

/// <summary>
/// Windows implementation of the platform notification handler
/// </summary>
public class WindowsNotificationHandler(ILogger logger, ISessionManager sessionManager, IDeviceManager deviceManager) : IPlatformNotificationHandler
{
    private static readonly TimeSpan TempIconMaxAge = TimeSpan.FromDays(1); // 清理 1 天以前的临时图标
    private const string TempIconsFolderName = "Sefirah-pc-icons";

    private static string GetTempIconsDirectory()
    {
        string tempPath = Path.GetTempPath();
        string tempIconsDirectory = Path.Combine(tempPath, TempIconsFolderName);
        try
        {
            Directory.CreateDirectory(tempIconsDirectory);

            // 清理超过阈值的旧文件
            try
            {
                var files = Directory.GetFiles(tempIconsDirectory);
                var expireBefore = DateTime.UtcNow - TempIconMaxAge;
                foreach (var f in files)
                {
                    try
                    {
                        var info = new FileInfo(f);
                        if (info.Exists && info.LastWriteTimeUtc < expireBefore)
                        {
                            info.Delete();
                        }
                    }
                    catch
                    {
                        // 忽略单个文件删除错误
                    }
                }
            }
            catch
            {
                // 忽略清理错误
            }
        }
        catch
        {
            // ignore
        }

        return tempIconsDirectory;
    }

    /// <inheritdoc />
    public async Task ShowRemoteNotification(NotificationMessage message, string deviceId)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(message.AppName, new AppNotificationTextProperties().SetMaxLines(1))
                .AddText(message.Title)
                .AddText(message.Text)
                .SetTag(message.Tag ?? string.Empty)
                .SetGroup(message.GroupKey ?? string.Empty);

            // 优先使用通知的包名对应的图标（已有的 AppIcons 文件或内置图标）
            if (!string.IsNullOrEmpty(message.AppPackage))
            {
                var iconUri = await IconUtils.GetAppIconUriAsync(message.AppPackage);
                var appIconExists = IconUtils.AppIconExists(message.AppPackage);
                

                if (iconUri is not null)
                {
                    // 如果图标是本地 ms-appdata 文件，尝试复制到系统临时目录并使用 file:// URI
                    try
                    {
                        if (iconUri.Scheme.Equals("ms-appdata", StringComparison.OrdinalIgnoreCase) || iconUri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var storageFile = await StorageFile.GetFileFromApplicationUriAsync(iconUri);
                                
                                // 创建临时图标目录并清理旧文件
                                string tempIconsDirectory = GetTempIconsDirectory();

                                // 构建临时文件路径
                                string tempFileName = $"{message.AppPackage}_{DateTime.UtcNow.Ticks}.png";
                                string tempFilePath = Path.Combine(tempIconsDirectory, tempFileName);

                                // 复制图标文件到临时目录
                                var destFolder = await StorageFolder.GetFolderFromPathAsync(tempIconsDirectory);
                                await storageFile.CopyAsync(destFolder, tempFileName, NameCollisionOption.ReplaceExisting);

                                // 使用 file:// URI 引用临时图标文件
                                var fileUri = new Uri($"file://{tempFilePath}");

                                builder.SetAppLogoOverride(fileUri, AppNotificationImageCrop.Circle);
                            }
                            catch (Exception exLocal)
                            {
                                logger.LogWarning(exLocal, "无法读取本地图标 URI，回退使用原始 URI：{IconUri}", iconUri);
                                builder.SetAppLogoOverride(iconUri, AppNotificationImageCrop.Circle);
                            }
                        }
                        else
                        {
                            logger.LogDebug("设置通知图标为 {IconUri}，通知键：{NotificationKey}", iconUri, message.NotificationKey);
                            builder.SetAppLogoOverride(iconUri, AppNotificationImageCrop.Circle);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "设置通知图标时出错，通知键：{NotificationKey}", message.NotificationKey);
                    }
                }
                else if (!string.IsNullOrEmpty(message.LargeIcon))
                {
                    // 包名图标不存在时回退：保存消息内的 base64 大图到临时目录并使用它
                    try
                    {
                        // 创建临时图标目录并清理旧文件
                        string tempIconsDirectory = GetTempIconsDirectory();

                        // 构建临时文件路径
                        string tempFileName = $"largeIcon_{message.NotificationKey}_{DateTime.UtcNow.Ticks}.png";
                        string tempFilePath = Path.Combine(tempIconsDirectory, tempFileName);

                        // 直接将base64转换为字节数组并保存到临时文件
                        var bytes = Convert.FromBase64String(message.LargeIcon);
                        await File.WriteAllBytesAsync(tempFilePath, bytes);

                        // 使用 file:// URI 引用临时图标文件
                        var fileUri = new Uri($"file://{tempFilePath}");
                        logger.LogDebug("包名图标不存在，已保存大图标到临时目录：{FileUri}，通知键：{NotificationKey}", fileUri, message.NotificationKey);
                        builder.SetAppLogoOverride(fileUri, AppNotificationImageCrop.Circle);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "保存大图标到临时目录时出错，通知键：{NotificationKey}", message.NotificationKey);
                    }
                }
                else
                {
                    logger.LogDebug("未找到应用图标或大图标，通知键：{NotificationKey}，包名：{AppPackage}", message.NotificationKey, message.AppPackage);
                }
            }
            else if (!string.IsNullOrEmpty(message.LargeIcon))
            {
                // 当没有包名时，使用消息内的大图（base64）作为回退，保存到临时目录
                try
                {
                    // 创建临时图标目录并清理旧文件
                    string tempIconsDirectory = GetTempIconsDirectory();

                    // 构建临时文件路径
                    string tempFileName = $"largeIcon_{message.NotificationKey}_{DateTime.UtcNow.Ticks}.png";
                    string tempFilePath = Path.Combine(tempIconsDirectory, tempFileName);

                    // 直接将base64转换为字节数组并保存到临时文件
                    var bytes = Convert.FromBase64String(message.LargeIcon);
                    await File.WriteAllBytesAsync(tempFilePath, bytes);

                    // 使用 file:// URI 引用临时图标文件
                    var fileUri = new Uri($"file://{tempFilePath}");
                    logger.LogDebug("未设置包名，已保存大图标到临时目录：{FileUri}，通知键：{NotificationKey}", fileUri, message.NotificationKey);
                    builder.SetAppLogoOverride(fileUri, AppNotificationImageCrop.Circle);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "保存大图标到临时目录时出错，通知键：{NotificationKey}", message.NotificationKey);
                }
            }
            else
            {
                logger.LogDebug("未设置图标：通知 {NotificationKey}，包名={AppPackage}，LargeIcon 为空", message.NotificationKey, message.AppPackage);
            }

            // Handle actions
            foreach (var action in message.Actions)
            {
                if (action is null) continue;

                if (action.IsReplyAction)
                {
                    builder
                        .AddTextBox("textBox", "ReplyPlaceholder".GetLocalizedResource(), "")
                        .AddButton(new AppNotificationButton("SendButton".GetLocalizedResource())
                            .AddArgument("notificationType", ToastNotificationType.RemoteNotification)
                            .AddArgument("tag", message.NotificationKey)
                            .AddArgument("replyResultKey", message.ReplyResultKey)
                            .AddArgument("action", "Reply")
                            .AddArgument("deviceId", deviceId)
                                .SetInputId("textBox"));
                }
                else
                {
                    builder.AddButton(new AppNotificationButton(action.Label)
                        .AddArgument("notificationType", ToastNotificationType.RemoteNotification)
                        .AddArgument("action", "Click")
                        .AddArgument("actionIndex", action.ActionIndex.ToString())
                        .AddArgument("tag", message.NotificationKey)
                        .AddArgument("deviceId", deviceId));
                }
            }

            var notification = builder.BuildNotification();
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示远程通知失败");
        }
    }

    public async void ShowFileTransferNotification(string subtitle, string fileName, string transferId, uint notificationSequence, double? progress = null)
    {
        try
        {
            // if transfer is in progress, update existing notification
            if (progress.HasValue && progress > 0 && progress < 100)
            {
                var progressData = new AppNotificationProgressData(notificationSequence)
                {
                    Title = fileName,
                    Value = progress.Value / 100,
                    ValueStringOverride = $"{progress.Value:F0}%",
                    Status = subtitle 
                };
                await AppNotificationManager.Default.UpdateAsync(progressData, transferId, Constants.Notification.FileTransferGroup);
            }
            else
            {
                var builder = new AppNotificationBuilder()
                    .AddText("FileTransferNotification.Title".GetLocalizedResource())
                    .SetTag(transferId)
                    .SetGroup(Constants.Notification.FileTransferGroup)
                    .MuteAudio()
                    .AddButton(new AppNotificationButton("FileTransferNotificationAction.Cancel".GetLocalizedResource())
                        .AddArgument("notificationType", ToastNotificationType.FileTransfer)
                        .AddArgument("action", "cancel"))
                    .AddProgressBar(new AppNotificationProgressBar()
                        .BindTitle()
                        .BindValue()
                        .BindValueStringOverride()
                        .BindStatus());

                var notification = builder.BuildNotification();
                notification.ExpiresOnReboot = true;

                // Set initial progress data
                notification.Progress = new AppNotificationProgressData(notificationSequence)
                {
                    Title = fileName,
                    Value = 0,
                    ValueStringOverride = "0%",
                    Status = subtitle
                };

                AppNotificationManager.Default.Show(notification);
            }
        }
        catch (Exception ex)
        {
            logger.Error($"通知失败，进度：{progress}, 序列：{notificationSequence}", ex);
        }
    }


    /// <inheritdoc />
    public async void ShowCompletedFileTransferNotification(string subtitle, string transferId, string? filePath = null, string? folderPath = null)
    {
        // TODO: show hero image if available   
        try
        {
            await Task.Delay(500);
            var builder = new AppNotificationBuilder()
                .AddText("FileTransferNotification.Completed".GetLocalizedResource())
                .AddText(subtitle)
                .SetTag(transferId)
                .SetGroup(Constants.Notification.FileTransferGroup);

            if (!string.IsNullOrEmpty(filePath))
            {
                builder.AddButton(new AppNotificationButton("FileTransferNotificationAction.OpenFile".GetLocalizedResource())
                    .AddArgument("notificationType", ToastNotificationType.FileTransfer)
                    .AddArgument("action", "openFile")
                    .AddArgument("filePath", filePath));
            }

            if (!string.IsNullOrEmpty(folderPath))
            {
                builder.AddButton(new AppNotificationButton("FileTransferNotificationAction.OpenFolder".GetLocalizedResource())
                    .AddArgument("notificationType", ToastNotificationType.FileTransfer)
                    .AddArgument("action", "openFolder")
                    .AddArgument("folderPath", folderPath));
            }

            var notification = builder.BuildNotification();
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示文件传输通知失败");
        }
    }


    /// <inheritdoc />
    public void ShowClipboardNotification(string title, string text, string? iconPath = null)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(text)
                .SetTag($"clipboard_{DateTime.Now.Ticks}")
                .SetGroup("clipboard");

            var notification = builder.BuildNotification();
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示简单通知失败");
        }
    }

    /// <inheritdoc />
    public void ShowClipboardNotificationWithActions(string title, string text, string? actionLabel = null, string? actionData = null)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(text)
                .SetTag($"clipboard_{DateTime.Now.Ticks}")
                .SetGroup("clipboard");

            if (!string.IsNullOrEmpty(actionLabel) && !string.IsNullOrEmpty(actionData))
            {
                builder.AddButton(new AppNotificationButton(actionLabel)
                    .AddArgument("notificationType", ToastNotificationType.Clipboard)
                    .AddArgument("uri", actionData));
            }

            var notification = builder.BuildNotification();
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示剪贴板通知失败");
        }
    }

    /// <inheritdoc />
    public async Task RegisterForNotifications()
    {
        AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;

        try
        {
            await Task.Run(() => AppNotificationManager.Default.Register());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "无法注册通知，继续不显示通知");
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            logger.LogInformation("通知被触发 - 参数：{Arguments}", string.Join(", ", args.Arguments.Select(x => $"{x.Key}={x.Value}")));

            if (!args.Arguments.TryGetValue("notificationType", out var notificationType)) return;
            
            switch (notificationType)
            {
                case ToastNotificationType.FileTransfer:
                    HandleFileTransferNotification(args);
                    break;
                
                case ToastNotificationType.RemoteNotification:
                    HandleMessageNotification(args);
                    break;

                case ToastNotificationType.Clipboard:
                    HandleClipboardNotification(args);
                    break;
                
                default:
                    logger.LogWarning("未处理的通知类型：{NotificationType}", notificationType);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理通知操作时出错");
        }
    }

    private static async void HandleClipboardNotification(AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("uri", out var uriString) && Uri.TryCreate(uriString, UriKind.Absolute, out Uri? uri) && ClipboardService.IsValidWebUrl(uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private static void HandleFileTransferNotification(AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out string? action))
        {
            switch (action)
            {
                case "openFile":
                    if (args.Arguments.TryGetValue("filePath", out string? filePath) && File.Exists(filePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = filePath,
                            UseShellExecute = true
                        });
                    }
                    break;
                case "openFolder":
                    if (args.Arguments.TryGetValue("folderPath", out string? folderPath) && Directory.Exists(folderPath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{folderPath}\"",
                            UseShellExecute = true
                        });
                    }
                    break;
                case "cancel":
                    var fileTransferService = Ioc.Default.GetRequiredService<IFileTransferService>();
                    fileTransferService.CancelTransfer();
                    break;
            }
        }
    }

    private void HandleMessageNotification(AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("action", out var actionType))
            return;
        
        if (!args.Arguments.TryGetValue("deviceId", out var deviceId))
            return;

        var device = deviceManager.FindDeviceById(deviceId);
        if (device is null) return;

        var notificationKey = args.Arguments["tag"];
        switch (actionType)
        {
            case "Reply" when args.UserInput.TryGetValue("textBox", out var replyText):
                if (args.Arguments.TryGetValue("replyResultKey", out var replyResultKey))
                {
                    NotificationActionUtils.ProcessReplyAction(sessionManager, logger, device, notificationKey, replyResultKey, replyText);
                }
                break;
            case "Click":
                if (args.Arguments.TryGetValue("actionIndex", out var actionIndexStr))
                {
                    NotificationActionUtils.ProcessClickAction(sessionManager, logger, device, notificationKey, int.Parse(actionIndexStr));
                }
                break;
        }
    }

    /// <inheritdoc />
    public async Task RemoveNotificationByTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        await AppNotificationManager.Default.RemoveByTagAsync(tag);
    }

    /// <inheritdoc />
    public async Task RemoveNotificationsByGroup(string? groupKey)
    {
        if (string.IsNullOrEmpty(groupKey)) return;
        await AppNotificationManager.Default.RemoveByGroupAsync(groupKey);
    }

    public async Task RemoveNotificationsByTagAndGroup(string? tag, string? groupKey)
    {
        if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(groupKey)) return;
        await AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, groupKey);
    }

    /// <inheritdoc />
    public async Task ClearAllNotifications()
    {
        await AppNotificationManager.Default.RemoveAllAsync();
    }
}
