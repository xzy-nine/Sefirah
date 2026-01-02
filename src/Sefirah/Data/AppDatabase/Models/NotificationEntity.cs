using SQLite;

namespace Sefirah.Data.AppDatabase.Models;

public class NotificationEntity
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty; // deviceId|notificationKey

    public string DeviceId { get; set; } = string.Empty;
    public string NotificationKey { get; set; } = string.Empty;

    // Raw serialized NotificationMessage payload for replay
    public string MessageJson { get; set; } = string.Empty;

    // Local-only flags
    public bool Pinned { get; set; } = false;

    // For ordering when timestamp is missing/duplicate
    public long CreatedAt { get; set; }
}
