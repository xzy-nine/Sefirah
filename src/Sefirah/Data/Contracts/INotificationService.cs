using System.Collections.ObjectModel;
using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;
public interface INotificationService
{
    /// <summary>
    /// Gets notifications for the currently active device session
    /// </summary>
    ReadOnlyObservableCollection<Notification> NotificationHistory { get; }

    /// <summary>
    /// Initializes the notification service
    /// </summary>  
    void Initialize();

    Task HandleNotificationMessage(PairedDevice device, NotificationMessage notificationMessage);
    Task HandleMediaPlayNotification(PairedDevice device, NotificationMessage notificationMessage);
    void RemoveNotification(PairedDevice device, Notification notification);
    
    /// <summary>
    /// Toggles pin status for a notification in the active session
    /// </summary>
    void TogglePinNotification(PairedDevice device, Notification notification);
    
    /// <summary>
    /// Clears all notifications for the specified device
    /// </summary>
    void ClearAllNotification(PairedDevice device);
    
    /// <summary>
    /// Clears all notifications for all devices
    /// </summary>
    void ClearAllNotifications();
    
    void ClearHistory(PairedDevice device);
    void HandleIconResponse(string deviceId, string packageName);
    
    /// <summary>
    /// 当前显示的音乐媒体块列表（只读，支持多个设备同时显示）
    /// </summary>
    ReadOnlyObservableCollection<MusicMediaBlock> CurrentMusicMediaBlocks { get; }
    
    /// <summary>
    /// 处理音乐媒体块超时
    /// </summary>
    void CheckMusicMediaBlockTimeout();
}
