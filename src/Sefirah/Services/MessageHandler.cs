using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Data.Contracts;
using Sefirah.Data.Models;

namespace Sefirah.Services;
public class MessageHandler(
    RemoteAppRepository remoteAppRepository,
    IDeviceManager deviceManager,
    INotificationService notificationService,
    IClipboardService clipboardService,
    SmsHandlerService smsHandlerService,
    IFileTransferService fileTransferService,
    IPlaybackService playbackService,
    IActionService actionService,
    ISftpService sftpService,
    ILogger<MessageHandler> logger) : IMessageHandler
{
    public async Task HandleMessageAsync(PairedDevice device, SocketMessage message)
    {
        try
        {
            switch (message)
            {
                case ApplicationInfoMessage applicationInfo:
                    await remoteAppRepository.AddOrUpdateApplicationForDevice(await ApplicationInfoEntity.FromApplicationInfoMessage(applicationInfo, device.Id), device.Id);
                    break;

                case ApplicationList applicationList:
                    remoteAppRepository.UpdateApplicationList(device, applicationList);
                    break;

                case NotificationMessage notificationMessage:
                    await notificationService.HandleNotificationMessage(device, notificationMessage);
                    break;

                case PlaybackAction action:
                    await playbackService.HandleMediaActionAsync(action);
                    break;

                case DeviceStatus deviceStatus:
                    deviceManager.UpdateDeviceStatus(device, deviceStatus);
                    break;

                case ClipboardMessage clipboardMessage:
                    await clipboardService.SetContentAsync(clipboardMessage.Content, device);
                    break;

                case TextConversation textConversation:
                    await smsHandlerService.HandleTextMessage(device.Id, textConversation);
                    break;

                case ContactMessage contactMessage:
                    await smsHandlerService.HandleContactMessage(device.Id, contactMessage);
                    break;

                case ActionMessage action:
                    actionService.HandleActionMessage(action);
                    break;

                case SftpServerInfo sftpServerInfo:
                    await sftpService.InitializeAsync(device, sftpServerInfo);
                    break;

                case FileTransfer fileTransfer:
                    await fileTransferService.ReceiveFile(fileTransfer, device);
                    break;
                case BulkFileTransfer fileTransfer:
                    await fileTransferService.ReceiveBulkFiles(fileTransfer, device);
                    break;
                default:
                    logger.LogWarning("Unknown message type received: {type}", message.GetType().Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling message");
        }
    }
    
    public async Task HandleNotifyRelayNotificationAsync(PairedDevice device, string decryptedPayload)
    {
        try
        {
            // 解析Notify-Relay-pc格式的通知数据
            // 示例格式：{"packageName":"com.example.app","appName":"示例App","title":"通知标题","text":"通知内容","time":1690000000000,"isLocked":false}
            var notificationData = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(decryptedPayload);
            if (notificationData == null)
            {
                logger.LogWarning("Failed to deserialize Notify-Relay notification payload: {payload}", decryptedPayload);
                return;
            }
            
            // 提取字段
            string packageName = notificationData.TryGetValue("packageName", out var pkgVal) ? pkgVal.ToString() : string.Empty;
            string appName = notificationData.TryGetValue("appName", out var appNameVal) ? appNameVal.ToString() : string.Empty;
            string title = notificationData.TryGetValue("title", out var titleVal) ? titleVal.ToString() : string.Empty;
            string text = notificationData.TryGetValue("text", out var textVal) ? textVal.ToString() : string.Empty;
            long time = notificationData.TryGetValue("time", out var timeVal) ? Convert.ToInt64(timeVal) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool isLocked = notificationData.TryGetValue("isLocked", out var isLockedVal) && Convert.ToBoolean(isLockedVal);
            
            // 创建NotificationMessage对象
            var notificationMessage = new NotificationMessage
            {
                NotificationKey = Guid.NewGuid().ToString(),
                TimeStamp = time.ToString(),
                NotificationType = Data.Enums.NotificationType.New,
                AppName = appName,
                AppPackage = packageName,
                Title = title,
                Text = text,
                Actions = [],
                // 其他字段根据需要设置默认值
            };
            
            // 调用现有的通知处理逻辑
            await notificationService.HandleNotificationMessage(device, notificationMessage);
            
            logger.LogDebug("Processed Notify-Relay notification: {title} from {appName}", title, appName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Notify-Relay notification");
        }
    }
}
