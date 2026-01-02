using Sefirah.Data.Contracts;
using Sefirah.Data.Models;
using Sefirah.Utils.Serialization;

namespace Sefirah.Utils;

public static class NotificationActionUtils
{
    public static void ProcessReplyAction(ISessionManager sessionManager, ILogger logger, PairedDevice device, string notificationKey, string replyResultKey, string replyText)
    {
        if (!device.ConnectionStatus) return;

        var replyAction = new ReplyAction
        {
            NotificationKey = notificationKey,
            ReplyResultKey = replyResultKey,
            ReplyText = replyText,
        };

        sessionManager.SendMessage(device.Id, SocketMessageSerializer.Serialize(replyAction));
        logger.LogDebug("已向设备 {DeviceId} 发送回复动作（通知键：{NotificationKey}）", device.Id, notificationKey);
    }

    public static void ProcessClickAction(ISessionManager sessionManager, ILogger logger, PairedDevice device, string notificationKey, int actionIndex)
    {        
        if (!device.ConnectionStatus) return;

        var notificationAction = new NotificationAction
        {
            NotificationKey = notificationKey,
            ActionIndex = actionIndex,
            IsReplyAction = false
        };

        sessionManager.SendMessage(device.Id, SocketMessageSerializer.Serialize(notificationAction));
        logger.LogDebug("已向设备 {DeviceId} 发送点击动作（通知键：{NotificationKey}）", device.Id, notificationKey);
    }
} 
