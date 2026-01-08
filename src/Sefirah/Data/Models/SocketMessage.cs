using Sefirah.Data.Enums;

namespace Sefirah.Data.Models;
/// <summary>
/// Socket消息多态类型定义，基于数字类型鉴别器路由
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
/// <summary>
/// 命令消息 - 类型: "0"
/// 路径: Sefirah.Data.Models.CommandMessage
/// 功能: 用于发送设备控制命令，如清除通知、请求应用列表、断开连接等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
[JsonDerivedType(typeof(CommandMessage), typeDiscriminator: "0")]
/// <summary>
/// 设备信息 - 类型: "1"
/// 路径: Sefirah.Data.Models.DeviceInfo
/// 功能: 包含设备的基本信息，如设备ID、名称、型号、公钥等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
[JsonDerivedType(typeof(DeviceInfo), typeDiscriminator: "1")]
/// <summary>
/// 设备状态 - 类型: "2"
/// 路径: Sefirah.Data.Models.DeviceStatus
/// 功能: 包含设备的状态信息，如电量、充电状态、WiFi状态等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → DeviceManager.UpdateDeviceStatus
/// </summary>
[JsonDerivedType(typeof(DeviceStatus), typeDiscriminator: "2")]
/// <summary>
/// 剪贴板消息 - 类型: "3"
/// 路径: Sefirah.Data.Models.ClipboardMessage
/// 功能: 用于在设备间同步剪贴板内容
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → ClipboardService.SetContentAsync
/// </summary>
[JsonDerivedType(typeof(ClipboardMessage), typeDiscriminator: "3")]
/// <summary>
/// 通知消息 - 类型: "4"
/// 路径: Sefirah.Data.Models.NotificationMessage
/// 功能: 包含通知的详细信息，如应用名称、标题、内容等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → NotificationService.HandleNotificationMessage
/// </summary>
[JsonDerivedType(typeof(NotificationMessage), typeDiscriminator: "4")]

/// <summary>
/// 媒体播放会话 - 类型: "7"
/// 路径: Sefirah.Data.Models.PlaybackSession
/// 功能: 包含媒体播放的详细信息，如歌曲标题、艺术家、播放状态等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
[JsonDerivedType(typeof(PlaybackSession), typeDiscriminator: "7")]
/// <summary>
/// 文件传输 - 类型: "8"
/// 路径: Sefirah.Data.Models.FileTransfer
/// 功能: 用于在设备间传输单个文件
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → FileTransferService.ReceiveFile
/// </summary>
[JsonDerivedType(typeof(FileTransfer), typeDiscriminator: "8")]
/// <summary>
/// 批量文件传输 - 类型: "9"
/// 路径: Sefirah.Data.Models.BulkFileTransfer
/// 功能: 用于在设备间批量传输文件
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → FileTransferService.ReceiveBulkFiles
/// </summary>
[JsonDerivedType(typeof(BulkFileTransfer), typeDiscriminator: "9")]
/// <summary>
/// 应用信息消息 - 类型: "10"
/// 路径: Sefirah.Data.Models.ApplicationInfoMessage
/// 功能: 包含单个应用的详细信息，如包名、应用名称等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → RemoteAppRepository.AddOrUpdateApplicationForDevice
/// </summary>
[JsonDerivedType(typeof(ApplicationInfoMessage), typeDiscriminator: "10")]
/// <summary>
/// SFTP服务器信息 - 类型: "11"
/// 路径: Sefirah.Data.Models.SftpServerInfo
/// 功能: 包含SFTP服务器的连接信息，如IP地址、端口、用户名、密码等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → SftpService.InitializeAsync
/// </summary>
[JsonDerivedType(typeof(SftpServerInfo), typeDiscriminator: "11")]
/// <summary>
/// UDP广播消息 - 类型: "12"
/// 路径: Sefirah.Data.Models.UdpBroadcast
/// 功能: 用于设备发现和广播设备信息
/// 处理服务: Sefirah.Services.DiscoveryService
/// </summary>
[JsonDerivedType(typeof(UdpBroadcast), typeDiscriminator: "12")]
/// <summary>
/// 设备铃声模式 - 类型: "13"
/// 路径: Sefirah.Data.Models.DeviceRingerMode
/// 功能: 包含设备的铃声模式信息
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
[JsonDerivedType(typeof(DeviceRingerMode), typeDiscriminator: "13")]
/// <summary>
/// 音频设备 - 类型: "17"
/// 路径: Sefirah.Data.Models.AudioDevice
/// 功能: 包含音频设备的详细信息，如设备ID、名称、音量等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
[JsonDerivedType(typeof(AudioDevice), typeDiscriminator: "17")]
/// <summary>
/// 媒体播放操作 - 类型: "18"
/// 路径: Sefirah.Data.Models.PlaybackAction
/// 功能: 用于控制媒体播放，如播放、暂停、下一曲等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → PlaybackService.HandleMediaActionAsync
/// </summary>
[JsonDerivedType(typeof(PlaybackAction), typeDiscriminator: "18")]
/// <summary>
/// 应用列表 - 类型: "19"
/// 路径: Sefirah.Data.Models.ApplicationList
/// 功能: 包含设备上的应用列表
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → RemoteAppRepository.UpdateApplicationList
/// </summary>
[JsonDerivedType(typeof(ApplicationList), typeDiscriminator: "19")]
/// <summary>
/// 动作消息 - 类型: "20"
/// 路径: Sefirah.Data.Models.ActionMessage
/// 功能: 用于执行设备上的动作，如锁屏、关机等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → ActionService.HandleActionMessage
/// </summary>
[JsonDerivedType(typeof(ActionMessage), typeDiscriminator: "20")]
public class SocketMessage { }
/// <summary>
/// 命令消息类
/// 路径: Sefirah.Data.Models.CommandMessage
/// 功能: 用于发送设备控制命令，如清除通知、请求应用列表、断开连接等
/// 调用位置: MainPageViewModel.cs, DevicesViewModel.cs
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
public class CommandMessage : SocketMessage
{
    [JsonPropertyName("commandType")]
    public required CommandType CommandType { get; set; }
}

/// <summary>
/// 动作消息类
/// 路径: Sefirah.Data.Models.ActionMessage
/// 功能: 用于执行设备上的预定义动作，如锁屏、关机等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → ActionService.HandleActionMessage
/// </summary>
public class ActionMessage : SocketMessage
{
    [JsonPropertyName("actionId")]
    public required string ActionId { get; set; }

    [JsonPropertyName("actionName")]
    public required string ActionName { get; set; }
}

/// <summary>
/// 自定义动作消息类
/// 路径: Sefirah.Data.Models.CustomActionMessage
/// 功能: 用于执行自定义路径的程序，支持传递参数
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → ActionService.HandleActionMessage
/// </summary>
public class CustomActionMessage : SocketMessage
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; } = null;
}

/// <summary>
/// 剪贴板消息类
/// 路径: Sefirah.Data.Models.ClipboardMessage
/// 功能: 用于在设备间同步剪贴板内容，支持多种内容类型
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → ClipboardService.SetContentAsync
/// </summary>
public class ClipboardMessage : SocketMessage
{
    [JsonPropertyName("clipboardType")]
    public string ClipboardType { get; set; } = "text/plain";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// 通知消息类
/// 路径: Sefirah.Data.Models.NotificationMessage
/// 功能: 包含通知的详细信息，如应用名称、标题、内容、操作等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → NotificationService.HandleNotificationMessage
/// </summary>
public class NotificationMessage : SocketMessage
{
    [JsonPropertyName("notificationKey")]
    public required string NotificationKey { get; set; }

    [JsonPropertyName("timestamp")]
    public string? TimeStamp { get; set; }

    [JsonPropertyName("notificationType")]
    public required NotificationType NotificationType { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("appPackage")]
    public string? AppPackage { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("messages")]
    public List<NotificationTextMessage>? Messages { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("groupKey")]
    public string? GroupKey { get; set; }

    [JsonPropertyName("appIcon")]
    public string? AppIcon { get; set; }

    [JsonPropertyName("bigPicture")]
    public string? BigPicture { get; set; }

    [JsonPropertyName("largeIcon")]
    public string? LargeIcon { get; set; }
    
    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }
}





/// <summary>
/// 通知文本消息类
/// 路径: Sefirah.Data.Models.NotificationTextMessage
/// 功能: 包含通知中的单条文本消息，如聊天消息
/// 用于: NotificationMessage.Messages 列表中
/// </summary>
public class NotificationTextMessage
{
    [JsonPropertyName("sender")]
    public required string Sender { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

/// <summary>
/// 设备信息类
/// 路径: Sefirah.Data.Models.DeviceInfo
/// 功能: 包含设备的基本信息，如设备ID、名称、型号、公钥等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
public class DeviceInfo : SocketMessage
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; }

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    [JsonPropertyName("proof")]
    public string? Proof { get; set; }

    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; }
}

/// <summary>
/// 设备状态类
/// 路径: Sefirah.Data.Models.DeviceStatus
/// 功能: 包含设备的实时状态信息，如电量、充电状态、WiFi状态等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → DeviceManager.UpdateDeviceStatus
/// </summary>
public class DeviceStatus : SocketMessage
{
    [JsonPropertyName("batteryStatus")]
    public int BatteryStatus { get; set; }

    [JsonPropertyName("chargingStatus")]
    public bool ChargingStatus { get; set; }

    [JsonPropertyName("wifiStatus")]
    public bool WifiStatus { get; set; }

    [JsonPropertyName("bluetoothStatus")]
    public bool BluetoothStatus { get; set; }

    [JsonPropertyName("isDndEnabled")]
    public bool IsDndEnabled { get; set; }

    [JsonPropertyName("ringerMode")]
    public int RingerMode { get; set; }
}

/// <summary>
/// 媒体播放会话类
/// 路径: Sefirah.Data.Models.PlaybackSession
/// 功能: 包含媒体播放的详细信息，如歌曲标题、艺术家、播放状态等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
public class PlaybackSession : SocketMessage
{
    [JsonPropertyName("sessionType")]
    public SessionType SessionType { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("trackTitle")]
    public string? TrackTitle { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("isPlaying")]
    public bool IsPlaying { get; set; }

    [JsonPropertyName("isShuffleActive")]
    public bool? IsShuffleActive { get; set; }

    [JsonPropertyName("repeatMode")]
    public int? RepeatMode { get; set; }

    [JsonPropertyName("playbackRate")]
    public double? PlaybackRate { get; set; }

    [JsonPropertyName("position")]
    public double? Position { get; set; }

    [JsonPropertyName("maxSeekTime")]
    public double? MaxSeekTime { get; set; }

    [JsonPropertyName("minSeekTime")]
    public double? MinSeekTime { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }
}

/// <summary>
/// 媒体播放操作类
/// 路径: Sefirah.Data.Models.PlaybackAction
/// 功能: 用于控制媒体播放，如播放、暂停、下一曲等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → PlaybackService.HandleMediaActionAsync
/// </summary>
public class PlaybackAction : SocketMessage
{
    [JsonPropertyName("playbackActionType")]
    public PlaybackActionType PlaybackActionType { get; set; }

    [JsonPropertyName("source")]
    public required string Source { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }
}

/// <summary>
/// 音频设备类
/// 路径: Sefirah.Data.Models.AudioDevice
/// 功能: 包含音频设备的详细信息，如设备ID、名称、音量、静音状态等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
public class AudioDevice : SocketMessage
{
    [JsonPropertyName("audioDeviceType")]
    public AudioMessageType AudioDeviceType { get; set; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("volume")]
    public float Volume { get; set; }

    [JsonPropertyName("isMuted")]   
    public bool IsMuted { get; set; }

    [JsonPropertyName("isSelected")]
    public bool IsSelected { get; set; }
}

/// <summary>
/// 文件传输类
/// 路径: Sefirah.Data.Models.FileTransfer
/// 功能: 用于在设备间传输单个文件，包含文件元数据和服务器信息
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → FileTransferService.ReceiveFile
/// </summary>
public class FileTransfer : SocketMessage
{
    [JsonPropertyName("transferType")]
    public FileTransferType TransferType { get; set; }

    [JsonPropertyName("fileMetadata")]
    public required FileMetadata FileMetadata { get; set; }

    [JsonPropertyName("serverInfo")]
    public required ServerInfo ServerInfo { get; set; }
}

/// <summary>
/// 批量文件传输类
/// 路径: Sefirah.Data.Models.BulkFileTransfer
/// 功能: 用于在设备间批量传输文件，包含多个文件的元数据和服务器信息
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → FileTransferService.ReceiveBulkFiles
/// </summary>
public class BulkFileTransfer : SocketMessage
{
    [JsonPropertyName("files")]
    public required List<FileMetadata> Files { get; set; }

    [JsonPropertyName("serverInfo")]
    public required ServerInfo ServerInfo { get; set; }
}

/// <summary>
/// 服务器信息类
/// 路径: Sefirah.Data.Models.ServerInfo
/// 功能: 包含文件传输服务器的连接信息，如IP地址、端口、密码等
/// 用于: FileTransfer, BulkFileTransfer 类中
/// </summary>
public class ServerInfo
{
    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public required int Port { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 文件元数据类
/// 路径: Sefirah.Data.Models.FileMetadata
/// 功能: 包含文件的基本信息，如文件名、MIME类型、文件大小等
/// 用于: FileTransfer, BulkFileTransfer 类中
/// </summary>
public class FileMetadata
{
    [JsonPropertyName("fileName")]
    public required string FileName { get; set; }

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; set; }

    [JsonPropertyName("fileSize")]
    public required long FileSize { get; set; }
}

/// <summary>
/// 应用列表类
/// 路径: Sefirah.Data.Models.ApplicationList
/// 功能: 包含设备上的应用列表
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → RemoteAppRepository.UpdateApplicationList
/// </summary>
public class ApplicationList : SocketMessage
{
    public required List<ApplicationInfoMessage> AppList { get; set; }
}

/// <summary>
/// 应用信息消息类
/// 路径: Sefirah.Data.Models.ApplicationInfoMessage
/// 功能: 包含单个应用的详细信息，如包名、应用名称、图标等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → RemoteAppRepository.AddOrUpdateApplicationForDevice
/// </summary>
public class ApplicationInfoMessage : SocketMessage
{
    [JsonPropertyName("packageName")]
    public required string PackageName { get; set; }

    [JsonPropertyName("appName")]
    public required string AppName { get; set; }

    [JsonPropertyName("appIcon")]
    public string? AppIcon { get; set; }

}

/// <summary>
/// SFTP服务器信息类
/// 路径: Sefirah.Data.Models.SftpServerInfo
/// 功能: 包含SFTP服务器的连接信息，如用户名、密码、IP地址、端口等
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync → SftpService.InitializeAsync
/// </summary>
public class SftpServerInfo : SocketMessage
{
    [JsonPropertyName("username")]
    public required string Username { get; set; }

    [JsonPropertyName("password")]
    public required string Password { get; set; }

    [JsonPropertyName("ipAddress")]
    public required string IpAddress { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

/// <summary>
/// UDP广播消息类
/// 路径: Sefirah.Data.Models.UdpBroadcast
/// 功能: 用于设备发现和广播设备信息，包含设备ID、名称、公钥等
/// 处理服务: Sefirah.Services.DiscoveryService
/// </summary>
public class UdpBroadcast : SocketMessage
{
    [JsonPropertyName("ipAddresses")]
    public List<string> IpAddresses { get; set; } = [];

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }

    [JsonPropertyName("deviceName")]
    public required string DeviceName { get; set; }

    [JsonPropertyName("publicKey")]
    public required string PublicKey { get; set; }

    [JsonPropertyName("timestamp")]
    public long TimeStamp { get; set; }
}

/// <summary>
/// 设备铃声模式类
/// 路径: Sefirah.Data.Models.DeviceRingerMode
/// 功能: 包含设备的铃声模式信息
/// 处理服务: Sefirah.Services.MessageHandler.HandleMessageAsync
/// </summary>
public class DeviceRingerMode : SocketMessage
{
    [JsonPropertyName("ringerMode")]
    public int RingerMode { get; set; }
}






