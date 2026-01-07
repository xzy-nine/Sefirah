using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;
public interface IMessageHandler
{
    Task HandleMessageAsync(PairedDevice device, SocketMessage message);
    
    /// <summary>
    /// 处理Notify-Relay-pc格式的通知数据
    /// </summary>
    Task HandleNotifyRelayNotificationAsync(PairedDevice device, string decryptedPayload);
}
