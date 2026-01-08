using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;

public interface INetworkService
{
    Task<bool> StartServerAsync();
    int ServerPort { get; }
    void SendMessage(string deviceId, string message);
    void SendAppListRequest(string deviceId);
    void SendIconRequest(string deviceId, string packageName);
    void SendIconRequest(string deviceId, List<string> packageNames);
    
    void SendMediaControlRequest(string deviceId, string controlType);
    
    void SendMediaPlayNotification(string deviceId, NotificationMessage mediaInfo);
    
    /// <summary>
    /// 发送媒体播放通知，支持全量包和差异包
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="mediaInfo">媒体播放信息</param>
    /// <param name="mediaType">媒体类型，FULL 表示全量包，DELTA 表示差异包</param>
    void SendMediaPlayNotification(string deviceId, NotificationMessage mediaInfo, string mediaType);
    
    Task ProcessProtocolMessageAsync(PairedDevice device, string message);
}
