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
            // 直接尝试反序列化为NotificationMessage，因为DATA_MEDIAPLAY消息应该是NotificationMessage类型
            return JsonSerializer.Deserialize<NotificationMessage>(json, options);
        }
        catch (JsonException jsonEx)
            {
                Console.WriteLine($"直接反序列化为NotificationMessage失败: {jsonEx.Message}");
                // 如果直接反序列化为NotificationMessage失败，尝试更简单的方式
                try
                {
                    // 解析JSON为JsonDocument
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;
                    
                    // 提取必要的字段，只使用最基本的字段
                    string notificationKey = Guid.NewGuid().ToString();
                    string timeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                    string? appPackage = root.TryGetProperty("packageName", out JsonElement appPackageElement) ? appPackageElement.GetString() : null;
                    string? appName = root.TryGetProperty("appName", out JsonElement appNameElement) ? appNameElement.GetString() : null;
                    string? title = root.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() : null;
                    string? text = root.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() : null;
                    
                    // 提取封面URL，尝试多种可能的字段名
                    string? coverUrl = null;
                    if (root.TryGetProperty("coverUrl", out JsonElement coverUrlElement))
                        coverUrl = coverUrlElement.GetString();
                    else if (root.TryGetProperty("bigPicture", out JsonElement bigPictureElement))
                        coverUrl = bigPictureElement.GetString();
                    else if (root.TryGetProperty("largeIcon", out JsonElement largeIconElement))
                        coverUrl = largeIconElement.GetString();
                    else if (root.TryGetProperty("icon", out JsonElement iconElement))
                        coverUrl = iconElement.GetString();
                    
                    // 创建NotificationMessage对象，只设置必要的字段
                    return new NotificationMessage
                    {
                        NotificationKey = notificationKey,
                        TimeStamp = timeStamp,
                        NotificationType = NotificationType.New,
                        AppPackage = appPackage,
                        AppName = appName,
                        Title = title,
                        Text = text,
                        CoverUrl = coverUrl
                    };
                }
                catch (JsonException innerJsonEx)
                {
                    Console.WriteLine($"解析JSON时失败: {innerJsonEx.Message}");
                    Console.WriteLine($"消息内容: {(json.Length > 100 ? json[..100] + "..." : json)}");
                    return null;
                }
                catch (Exception generalEx)
                {
                    Console.WriteLine($"手动构造NotificationMessage失败: {generalEx.Message}");
                    Console.WriteLine($"消息内容: {(json.Length > 100 ? json[..100] + "..." : json)}");
                    return null;
                }
            }
    }
}

