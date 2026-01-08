using Sefirah.Data.Enums;

namespace Sefirah.Data.Models;
//TODO 确定以下功能的使用
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CommandMessage), typeDiscriminator: "0")]
[JsonDerivedType(typeof(DeviceInfo), typeDiscriminator: "1")]
[JsonDerivedType(typeof(DeviceStatus), typeDiscriminator: "2")]
[JsonDerivedType(typeof(ClipboardMessage), typeDiscriminator: "3")]
[JsonDerivedType(typeof(NotificationMessage), typeDiscriminator: "4")]
[JsonDerivedType(typeof(NotificationAction), typeDiscriminator: "5")]
[JsonDerivedType(typeof(ReplyAction), typeDiscriminator: "6")]
[JsonDerivedType(typeof(PlaybackSession), typeDiscriminator: "7")]
[JsonDerivedType(typeof(FileTransfer), typeDiscriminator: "8")]
[JsonDerivedType(typeof(BulkFileTransfer), typeDiscriminator: "9")]
[JsonDerivedType(typeof(ApplicationInfoMessage), typeDiscriminator: "10")]
[JsonDerivedType(typeof(SftpServerInfo), typeDiscriminator: "11")]
[JsonDerivedType(typeof(UdpBroadcast), typeDiscriminator: "12")]
[JsonDerivedType(typeof(DeviceRingerMode), typeDiscriminator: "13")]
[JsonDerivedType(typeof(AudioDevice), typeDiscriminator: "17")]
[JsonDerivedType(typeof(PlaybackAction), typeDiscriminator: "18")]
[JsonDerivedType(typeof(ApplicationList), typeDiscriminator: "19")]
[JsonDerivedType(typeof(ActionMessage), typeDiscriminator: "20")]
public class SocketMessage { }
public class CommandMessage : SocketMessage
{
    [JsonPropertyName("commandType")]
    public required CommandType CommandType { get; set; }
}

public class ActionMessage : SocketMessage
{
    [JsonPropertyName("actionId")]
    public required string ActionId { get; set; }

    [JsonPropertyName("actionName")]
    public required string ActionName { get; set; }
}

public class CustomActionMessage : SocketMessage
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; } = null;
}

public class ClipboardMessage : SocketMessage
{
    [JsonPropertyName("clipboardType")]
    public string ClipboardType { get; set; } = "text/plain";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

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

    [JsonPropertyName("actions")]
    public List<NotificationAction?> Actions { get; set; } = [];

    [JsonPropertyName("replyResultKey")]
    public string? ReplyResultKey { get; set; }

    [JsonPropertyName("appIcon")]
    public string? AppIcon { get; set; }

    [JsonPropertyName("bigPicture")]
    public string? BigPicture { get; set; }

    [JsonPropertyName("largeIcon")]
    public string? LargeIcon { get; set; }
    
    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }
}

public class ReplyAction : SocketMessage
{
    [JsonPropertyName("notificationKey")]
    public required string NotificationKey { get; set; }

    [JsonPropertyName("replyResultKey")]
    public required string ReplyResultKey { get; set; }

    [JsonPropertyName("replyText")]
    public required string ReplyText { get; set; }
}

public class NotificationAction : SocketMessage
{
    [JsonPropertyName("notificationKey")]
    public required string NotificationKey { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; } = string.Empty;

    [JsonPropertyName("actionIndex")]
    public required int ActionIndex { get; set; }

    [JsonPropertyName("isReplyAction")]
    public bool IsReplyAction { get; set; }
}

public class NotificationTextMessage
{
    [JsonPropertyName("sender")]
    public required string Sender { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

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

    [JsonPropertyName("phoneNumbers")]
    public List<PhoneNumber> PhoneNumbers { get; set; } = [];
}

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

public class PlaybackAction : SocketMessage
{
    [JsonPropertyName("playbackActionType")]
    public PlaybackActionType PlaybackActionType { get; set; }

    [JsonPropertyName("source")]
    public required string Source { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }
}

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

public class FileTransfer : SocketMessage
{
    [JsonPropertyName("transferType")]
    public FileTransferType TransferType { get; set; }

    [JsonPropertyName("fileMetadata")]
    public required FileMetadata FileMetadata { get; set; }

    [JsonPropertyName("serverInfo")]
    public required ServerInfo ServerInfo { get; set; }
}

public class BulkFileTransfer : SocketMessage
{
    [JsonPropertyName("files")]
    public required List<FileMetadata> Files { get; set; }

    [JsonPropertyName("serverInfo")]
    public required ServerInfo ServerInfo { get; set; }
}

public class ServerInfo
{
    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public required int Port { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public class FileMetadata
{
    [JsonPropertyName("fileName")]
    public required string FileName { get; set; }

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; set; }

    [JsonPropertyName("fileSize")]
    public required long FileSize { get; set; }
}

public class ApplicationList : SocketMessage
{
    public required List<ApplicationInfoMessage> AppList { get; set; }
}

public class ApplicationInfoMessage : SocketMessage
{
    [JsonPropertyName("packageName")]
    public required string PackageName { get; set; }

    [JsonPropertyName("appName")]
    public required string AppName { get; set; }

    [JsonPropertyName("appIcon")]
    public string? AppIcon { get; set; }

}

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

public class DeviceRingerMode : SocketMessage
{
    [JsonPropertyName("ringerMode")]
    public int RingerMode { get; set; }
}



public class PhoneNumber
{
    [JsonPropertyName("number")]
    public string Number { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionId")]
    public int SubscriptionId { get; set; } = -1;
}


