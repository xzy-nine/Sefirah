using System.Text.Json;
using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Data.Contracts;
using Sefirah.Data.Models;
using Sefirah.Utils;

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
                    // 检查是否为 JSON 字符串消息（Notify-Relay-pc 格式）
                    var jsonString = message.ToString();
                    if (jsonString.StartsWith("{") || jsonString.StartsWith("["))
                    {
                        await HandleJsonMessageAsync(device, jsonString);
                    }
                    else
                    {
                        logger.LogWarning("收到未知消息类型：{type}", message.GetType().Name);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理消息时出错");
        }
    }
    
    /// <summary>
    /// 处理 JSON 格式的消息（Notify-Relay-pc 格式）
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="jsonPayload">JSON 负载</param>
    private async Task HandleJsonMessageAsync(PairedDevice device, string jsonPayload)
    {
        try
        {
            logger.LogDebug("正在处理 JSON 消息：{jsonPayload}", jsonPayload.Length > 100 ? jsonPayload[..100] + "..." : jsonPayload);
            
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;
            
            // 检查消息类型
            if (!root.TryGetProperty("type", out var typeProp))
            {
                logger.LogWarning("JSON 消息缺少 type 属性");
                return;
            }
            
            var messageType = typeProp.GetString();
            logger.LogDebug("识别到 JSON 消息类型：{messageType}");
            
            switch (messageType)
            {
                case "APP_LIST_RESPONSE":
                    logger.LogDebug("处理 APP_LIST_RESPONSE 消息");
                    await HandleAppListResponseAsync(device, root);
                    break;
                case "ICON_RESPONSE":
                    logger.LogDebug("处理 ICON_RESPONSE 消息");
                    HandleIconResponse(device, root);
                    break;
                default:
                    logger.LogWarning("不支持的 JSON 消息类型：{messageType}", messageType);
                    break;
            }
        }
        catch (JsonException jsonEx)
        {
            logger.LogError(jsonEx, "解析 JSON 消息时出错：{jsonPayload}", jsonPayload.Length > 100 ? jsonPayload[..100] + "..." : jsonPayload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 JSON 消息时出错：{jsonPayload}", jsonPayload.Length > 100 ? jsonPayload[..100] + "..." : jsonPayload);
        }
    }
    
    /// <summary>
    /// 处理应用列表响应
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="root">JSON 根元素</param>
    private async Task HandleAppListResponseAsync(PairedDevice device, JsonElement root)
    {
        try
        {
            // 检查是否包含 apps 数组
            if (!root.TryGetProperty("apps", out var appsArray))
            {
                logger.LogWarning("APP_LIST_RESPONSE 缺少 apps 数组");
                return;
            }
            
            // 解析应用列表
            var apps = new List<ApplicationInfoMessage>();
            foreach (var appElement in appsArray.EnumerateArray())
            {
                if (!appElement.TryGetProperty("packageName", out var packageProp))
                    continue;
                
                var packageName = packageProp.GetString();
                if (string.IsNullOrEmpty(packageName))
                    continue;
                
                var appName = appElement.TryGetProperty("appName", out var appNameProp) ? appNameProp.GetString() : packageName;
                
                // 创建应用信息实体
                var appInfo = new ApplicationInfoMessage
                {
                    PackageName = packageName,
                    AppName = appName
                };
                
                apps.Add(appInfo);
            }
            
            // 更新应用列表
            var applicationList = new ApplicationList
            {
                AppList = apps
            };
            
            remoteAppRepository.UpdateApplicationList(device, applicationList);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 APP_LIST_RESPONSE 时出错");
        }
    }
    
    /// <summary>
    /// 处理图标响应
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="root">JSON 根元素</param>
    private void HandleIconResponse(PairedDevice device, JsonElement root)
    {
        try
        {
            // 获取包名
            if (!root.TryGetProperty("packageName", out var packageProp))
            {
                logger.LogWarning("ICON_RESPONSE 缺少 packageName 属性");
                return;
            }
            
            var packageName = packageProp.GetString();
            if (string.IsNullOrEmpty(packageName))
            {
                logger.LogWarning("ICON_RESPONSE 的 packageName 为空");
                return;
            }
            
            // 获取图标数据
            if (!root.TryGetProperty("iconData", out var iconDataProp))
            {
                logger.LogWarning("ICON_RESPONSE 缺少 iconData 属性");
                return;
            }
            
            var iconData = iconDataProp.GetString();
            if (string.IsNullOrEmpty(iconData))
            {
                logger.LogWarning("ICON_RESPONSE 的 iconData 为空");
                return;
            }
            
            // 保存图标
            IconUtils.SaveAppIconToPathAsync(iconData, packageName);
            logger.LogDebug("已保存应用 {packageName} 的图标", packageName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 ICON_RESPONSE 时出错");
        }
    }
}
