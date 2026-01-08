using System.Text.Json;
using System.Text.Json.Nodes;
using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using Sefirah.Utils;

namespace Sefirah.Services;
public class MessageHandler(
    RemoteAppRepository remoteAppRepository,
    IDeviceManager deviceManager,
    INetworkService networkService,
    INotificationService notificationService,
    IClipboardService clipboardService,
    
    IFileTransferService fileTransferService,
    IPlaybackService playbackService,
    IActionService actionService,
    ISftpService sftpService,
    IScreenMirrorService screenMirrorService,
    ILogger<MessageHandler> logger) : IMessageHandler
{
    private const string DeviceTypeAndroid = "android";

    private static bool IsRemoteDeviceAndroid(PairedDevice? device)
    {
        // 允许RemoteDeviceType为null的情况，因为有些Android设备可能没有在握手时正确设置此属性
        return device != null && (device.RemoteDeviceType?.Equals(DeviceTypeAndroid, StringComparison.OrdinalIgnoreCase) ?? true);
    }

    /// <summary>
    /// 发送 SFTP 控制命令到 Android 设备
    /// </summary>
    /// <param name="device">目标设备</param>
    /// <param name="action">操作类型：start 或 stop</param>
    /// <param name="username">用户名（仅 start 时需要）</param>
    /// <param name="password">密码（仅 start 时需要）</param>
    public void SendSftpCommand(PairedDevice device, string action, string? username = null, string? password = null)
    {
        if (!IsRemoteDeviceAndroid(device))
        {
            logger.LogWarning("SFTP 命令被忽略：非 Android 设备 ({deviceType})", device.RemoteDeviceType);
            return;
        }

        try
        {
            var json = new JsonObject
            {
                ["type"] = "DATA_SFTP",
                ["action"] = action
            };

            if (action == "start")
            {
                if (!string.IsNullOrEmpty(username))
                    json["username"] = username;
                if (!string.IsNullOrEmpty(password))
                    json["password"] = password;
            }

            var message = JsonSerializer.Serialize(json);
            networkService.SendMessage(device.Id, message);
            logger.LogDebug("已发送 SFTP 命令：action={action}, device={deviceName}", action, device.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送 SFTP 命令失败：{deviceName}", device.Name);
        }
    }

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
                    // 普通通知处理
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

                // TextConversation and ContactMessage handling removed

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
                    var jsonString = message?.ToString();
                    if (string.IsNullOrWhiteSpace(jsonString))
                    {
                        logger.LogWarning("收到未知或空消息：{type}", message == null ? "null" : message.GetType().Name);
                        break;
                    }

                    if (jsonString.StartsWith("{") || jsonString.StartsWith("["))
                    {
                        await HandleJsonMessageAsync(device, jsonString);
                    }
                    else
                    {
                        logger.LogWarning("收到未知消息类型：{type}", message == null ? "null" : message.GetType().Name);
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
            if (string.IsNullOrWhiteSpace(jsonPayload))
            {
                logger.LogWarning("接收到空的 JSON 负载，忽略处理");
                return;
            }

            logger.LogDebug("正在处理 JSON 消息：{jsonPayload}", jsonPayload.Length > 100 ? jsonPayload[..100] + "..." : jsonPayload);

            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;
            
            string messageType = string.Empty;
            
            // 检查消息类型
            if (root.TryGetProperty("type", out var typeProp))
            {
                messageType = typeProp.GetString() ?? string.Empty;
                logger.LogDebug("识别到 JSON 消息类型：{messageType}", messageType);
            }
            
            // 检查是否为SFTP响应（没有type字段但有action字段）
            if (string.IsNullOrEmpty(messageType) && root.TryGetProperty("action", out var actionProp))
            {
                var action = actionProp.GetString();
                if (action is "started" or "stopped" or "error")
                {
                    logger.LogDebug("处理 SFTP 响应：action={action}", action);
                    await HandleDataSftpAsync(device, root);
                    return;
                }
            }
            
            switch (messageType)
            {
                case "APP_LIST_RESPONSE":
                    logger.LogDebug("处理 APP_LIST_RESPONSE 消息");
                    await HandleAppListResponseAsync(device, root);
                    break;
                case "ICON_RESPONSE":
                    logger.LogDebug("处理 ICON_RESPONSE 消息");
                    await HandleIconResponseAsync(device, root);
                    break;
                case "MEDIA_CONTROL":
                    logger.LogDebug("处理 MEDIA_CONTROL 消息");
                    await HandleMediaControlAsync(device, root);
                    break;
                case "DATA_SFTP":
                    logger.LogDebug("处理 DATA_SFTP 消息");
                    await HandleDataSftpAsync(device, root);
                    break;
                default:
                    if (!string.IsNullOrEmpty(messageType))
                    {
                        logger.LogWarning("不支持的 JSON 消息类型：{messageType}", messageType);
                    }
                    else
                    {
                        logger.LogWarning("JSON 消息缺少 type 属性且不是 SFTP 响应");
                    }
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
        private Task HandleAppListResponseAsync(PairedDevice device, JsonElement root)
    {
        try
        {
            // 检查是否包含 apps 数组
            if (!root.TryGetProperty("apps", out var appsArray))
            {
                logger.LogWarning("APP_LIST_RESPONSE 缺少 apps 数组");
                    return Task.CompletedTask;
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
                
                var appName = appElement.TryGetProperty("appName", out var appNameProp) ? appNameProp.GetString() ?? packageName : packageName;
                
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
            
            // 收集所有没有图标的应用的包名
            var packageNamesWithoutIcons = new List<string>();
            foreach (var appInfo in apps)
            {
                // 只有当应用没有图标时才添加到列表中
                if (!IconUtils.AppIconExists(appInfo.PackageName))
                {
                    packageNamesWithoutIcons.Add(appInfo.PackageName);
                }
            }
            
            // 发送批量图标请求
            if (packageNamesWithoutIcons.Count > 0)
            {
                networkService.SendIconRequest(device.Id, packageNamesWithoutIcons);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 APP_LIST_RESPONSE 时出错");
            return Task.CompletedTask;
        }
    }
    
    /// <summary>
    /// 处理图标响应（支持单个或批量）
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="root">JSON 根元素</param>
    private async Task HandleIconResponseAsync(PairedDevice device, JsonElement root)
    {
        try
        {
            // 检查是否为批量图标响应
            if (root.TryGetProperty("icons", out var iconsArray))
            {
                // 处理批量图标响应
                int savedCount = 0;
                foreach (var iconElement in iconsArray.EnumerateArray())
                {
                    // 获取包名
                    if (!iconElement.TryGetProperty("packageName", out var packageProp))
                    {
                        logger.LogWarning("批量 ICON_RESPONSE 中的图标缺少 packageName 属性");
                        continue;
                    }
                    
                    var packageName = packageProp.GetString();
                    if (string.IsNullOrEmpty(packageName))
                    {
                        logger.LogWarning("批量 ICON_RESPONSE 中的图标 packageName 为空");
                        continue;
                    }
                    
                    // 获取图标数据
                    if (!iconElement.TryGetProperty("iconData", out var iconDataProp))
                    {
                        logger.LogWarning("批量 ICON_RESPONSE 中的图标缺少 iconData 属性");
                        continue;
                    }
                    
                    var iconData = iconDataProp.GetString();
                    if (string.IsNullOrEmpty(iconData))
                    {
                        logger.LogWarning("批量 ICON_RESPONSE 中的图标 iconData 为空");
                        continue;
                    }
                    
                    // 保存图标
                    await IconUtils.SaveAppIconToPathAsync(iconData, packageName);
                    savedCount++;
                    
                    // 通知等待的图标请求任务
                    notificationService.HandleIconResponse(device.Id, packageName);
                }
                logger.LogDebug("已保存 {savedCount} 个应用图标", savedCount);
            }
            else
            {
                // 处理单个图标响应
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
                await IconUtils.SaveAppIconToPathAsync(iconData, packageName);
                logger.LogDebug("已保存应用 {packageName} 的图标", packageName);
                
                // 通知等待的图标请求任务
                notificationService.HandleIconResponse(device.Id, packageName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 ICON_RESPONSE 时出错");
        }
    }
    
    /// <summary>
    /// 处理媒体控制消息，包括音频转发请求和媒体播放控制
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="root">JSON 根元素</param>
    private async Task HandleMediaControlAsync(PairedDevice device, JsonElement root)
    {
        try
        {
            // 获取 action 属性
            if (!root.TryGetProperty("action", out var actionProp))
            {
                logger.LogWarning("MEDIA_CONTROL 消息缺少 action 属性");
                return;
            }
            
            var action = actionProp.GetString() ?? string.Empty;
            logger.LogDebug("处理 MEDIA_CONTROL action：{action}", action);
            
            switch (action)
            {
                case "audioRequest":
                    await HandleAudioRequestAsync(device, root);
                    break;
                case "playPause":
                    // 直接调用 Play 操作，与 Android 端保持一致
                    await playbackService.HandleMediaActionAsync(new PlaybackAction 
                    { 
                        PlaybackActionType = PlaybackActionType.Play, 
                        Source = "MediaControl"
                    });
                    break;
                case "next":
                    await playbackService.HandleMediaActionAsync(new PlaybackAction 
                    { 
                        PlaybackActionType = PlaybackActionType.Next, 
                        Source = "MediaControl"
                    });
                    break;
                case "previous":
                    await playbackService.HandleMediaActionAsync(new PlaybackAction 
                    { 
                        PlaybackActionType = PlaybackActionType.Previous, 
                        Source = "MediaControl"
                    });
                    break;
                default:
                    logger.LogWarning("不支持的 MEDIA_CONTROL action：{action}", action);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 MEDIA_CONTROL 消息时出错");
        }
    }
    
    /// <summary>
    /// 处理音频转发请求
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="root">JSON 根元素</param>
    private async Task HandleAudioRequestAsync(PairedDevice device, JsonElement root)
    {
        if (!IsRemoteDeviceAndroid(device))
        {
            logger.LogWarning("音频转发请求被忽略：非 Android 设备 ({deviceType})", device.RemoteDeviceType);
            return;
        }

        try
        {
            logger.LogDebug("收到音频转发请求，设备：{deviceName}", device.Name);
            
            // 构建仅音频转发的 scrcpy 参数
            string customArgs = "--no-video --no-control";
            
            // 启动 scrcpy 仅音频转发
            bool success = await screenMirrorService.StartScrcpy(device, customArgs);
            
            // 发送响应，使用新的 MEDIA_CONTROL 格式
            string response = success
                ? "{\"type\":\"MEDIA_CONTROL\",\"action\":\"audioResponse\",\"result\":\"accepted\"}"
                : "{\"type\":\"MEDIA_CONTROL\",\"action\":\"audioResponse\",\"result\":\"rejected\"}";
            
            // 通过 networkService 发送响应
            networkService.SendMessage(device.Id, response);
            
            logger.LogDebug("音频转发请求处理完成，结果：{result}", success ? "accepted" : "rejected");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理音频转发请求时出错");
            
            // 发送拒绝响应，使用新的 MEDIA_CONTROL 格式
            string errorResponse = "{\"type\":\"MEDIA_CONTROL\",\"action\":\"audioResponse\",\"result\":\"rejected\"}";
            networkService.SendMessage(device.Id, errorResponse);
        }
    }

    /// <summary>
    /// 处理 SFTP 消息（DATA_SFTP）
    /// </summary>
    /// <param name="device">设备</param>
    /// <param name="root">JSON 根元素</param>
    private async Task HandleDataSftpAsync(PairedDevice device, JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("action", out var actionProp))
            {
                logger.LogWarning("DATA_SFTP 消息缺少 action 属性");
                return;
            }

            var action = actionProp.GetString() ?? string.Empty;
            logger.LogDebug("处理 DATA_SFTP action：{action}", action);

            switch (action)
            {
                case "started":
                    if (!root.TryGetProperty("ipAddress", out var ipProp) ||
                        !root.TryGetProperty("port", out var portProp))
                    {
                        logger.LogWarning("DATA_SFTP started 消息缺少必要属性");
                        return;
                    }

                    if (device.SharedSecret == null)
                    {
                        logger.LogWarning("设备 {DeviceId} 没有 sharedSecret，无法生成 SFTP 凭据", device.Id);
                        return;
                    }

                    // 直接从 sharedSecret 生成 SFTP 凭据，不再依赖收到的凭据
                    var (expectedUsername, expectedPassword) = NotifyCryptoHelper.DeriveSftpCredentials(device.SharedSecret);

                    var sftpInfo = new SftpServerInfo
                    {
                        IpAddress = ipProp.GetString() ?? string.Empty,
                        Port = portProp.GetInt32(),
                        Username = expectedUsername,
                        Password = expectedPassword
                    };

                    await sftpService.InitializeAsync(device, sftpInfo);
                    logger.LogInformation("SFTP 连接已建立（凭据验证通过）：IP={IpAddress}, Port={Port}", sftpInfo.IpAddress, sftpInfo.Port);
                    break;

                case "stopped":
                    sftpService.Remove(device.Id);
                    logger.LogInformation("SFTP 连接已断开：{deviceId}", device.Id);
                    break;

                case "error":
                    var errorMessage = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "未知错误";
                    logger.LogWarning("SFTP 服务器启动失败：{errorMessage}", errorMessage);
                    break;

                default:
                    logger.LogWarning("不支持的 DATA_SFTP action：{action}", action);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 DATA_SFTP 消息时出错");
        }
    }
}
