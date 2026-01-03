using Sefirah.Data.Enums;
using Sefirah.Data.Models;

namespace Sefirah.Utils.Serialization;

public static class SocketMessageSerializer
{
    private static readonly JsonSerializerOptions options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(object message) => 
        JsonSerializer.Serialize(message, options);

    public static T? Deserialize<T>(string json) => 
        JsonSerializer.Deserialize<T>(json, options);

    public static SocketMessage? DeserializeMessage(string json) 
    {
        try
        {
            // 尝试直接反序列化
            return JsonSerializer.Deserialize<SocketMessage>(json, options);
        }
        catch (JsonException)
        {
            // 如果直接反序列化失败，尝试修复JSON格式
            // 对于媒体通知，我们知道它是NotificationMessage类型
            // 检查是否包含"mediaplay:"前缀
            if (json.Contains("mediaplay:"))
            {
                // 尝试手动构造NotificationMessage
                try
                {
                    // 解析JSON为JObject
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;
                    
                    // 提取必要的字段
                    string notificationKey = root.TryGetProperty("notificationKey", out JsonElement notificationKeyElement) ? notificationKeyElement.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                    string? appPackage = root.TryGetProperty("appPackage", out JsonElement appPackageElement) ? appPackageElement.GetString() : null;
                    string? appName = root.TryGetProperty("appName", out JsonElement appNameElement) ? appNameElement.GetString() : null;
                    string? title = root.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() : null;
                    string? text = root.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() : null;
                    string? bigPicture = root.TryGetProperty("bigPicture", out JsonElement bigPictureElement) ? bigPictureElement.GetString() : null;
                    string? largeIcon = root.TryGetProperty("largeIcon", out JsonElement largeIconElement) ? largeIconElement.GetString() : null;
                    
                    // 创建NotificationMessage对象
                    return new NotificationMessage
                    {
                        NotificationKey = notificationKey,
                        NotificationType = NotificationType.New,
                        AppPackage = appPackage,
                        AppName = appName,
                        Title = title,
                        Text = text,
                        BigPicture = bigPicture,
                        LargeIcon = largeIcon
                    };
                }
                catch (Exception ex)
                {
                    // 如果手动构造也失败，返回null
                    Console.WriteLine($"手动构造NotificationMessage失败: {ex.Message}");
                    return null;
                }
            }
            
            // 如果不是媒体通知，返回null
            return null;
        }
    }
}

