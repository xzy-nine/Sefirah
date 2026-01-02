using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.Models;
using Sefirah.Utils.Serialization;

namespace Sefirah.Data.AppDatabase.Repository;

public class NotificationRepository(DatabaseContext context, ILogger logger)
{
    public List<NotificationEntity> GetDeviceNotifications(string deviceId, int take = 200)
    {
        try
        {
            return context.Database.Table<NotificationEntity>()
                .Where(n => n.DeviceId == deviceId)
                .OrderByDescending(n => n.CreatedAt)
                .Take(take)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load notifications for device {DeviceId}", deviceId);
            return [];
        }
    }

    public void UpsertNotification(string deviceId, NotificationMessage message, bool pinned)
    {
        try
        {
            var entity = new NotificationEntity
            {
                Id = $"{deviceId}|{message.NotificationKey}",
                DeviceId = deviceId,
                NotificationKey = message.NotificationKey,
                MessageJson = SocketMessageSerializer.Serialize(message),
                Pinned = pinned,
                CreatedAt = ParseTimestamp(message.TimeStamp)
            };

            context.Database.InsertOrReplace(entity);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upsert notification {Key} for device {DeviceId}", message.NotificationKey, deviceId);
        }
    }

    public void DeleteNotification(string deviceId, string notificationKey)
    {
        try
        {
            var id = $"{deviceId}|{notificationKey}";
            var entity = context.Database.Find<NotificationEntity>(id);
            if (entity is not null)
            {
                context.Database.Delete(entity);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete notification {Key} for device {DeviceId}", notificationKey, deviceId);
        }
    }

    public void ClearDeviceNotifications(string deviceId)
    {
        try
        {
            context.Database.Execute("DELETE FROM NotificationEntity WHERE DeviceId = ?", deviceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear notifications for device {DeviceId}", deviceId);
        }
    }

    public void UpdatePinned(string deviceId, string notificationKey, bool pinned)
    {
        try
        {
            var id = $"{deviceId}|{notificationKey}";
            var entity = context.Database.Find<NotificationEntity>(id);
            if (entity is null) return;

            entity.Pinned = pinned;
            context.Database.InsertOrReplace(entity);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update pinned for notification {Key} on device {DeviceId}", notificationKey, deviceId);
        }
    }

    private static long ParseTimestamp(string? timestamp)
    {
        if (long.TryParse(timestamp, out var ts)) return ts;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
