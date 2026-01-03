using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Media.Imaging;
using Sefirah.Data.Enums;
using Sefirah.Extensions;
using Sefirah.Helpers;
using Sefirah.Utils;

namespace Sefirah.Data.Models;

public partial class Notification : ObservableObject
{
    public string Key { get; set; } = string.Empty;
    
    [ObservableProperty]
    private bool pinned = false;
    
    public string? TimeStamp { get; set; }
    public NotificationType Type { get; set; }
    public string? AppName { get; set; }
    public string? AppPackage { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public List<NotificationGroup>? GroupedMessages { get; set; }
    public bool HasGroupedMessages => GroupedMessages?.Count > 0;
    public string? Tag { get; set; }
    public string? GroupKey { get; set; }
    public List<NotificationAction> Actions { get; set; } = [];
    public string? ReplyResultKey { get; set; }
    
    [ObservableProperty]
    private BitmapImage? icon;
    
    [ObservableProperty]
    private string? iconPath;
    
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }

    public bool ShouldShowTitle
    {
        get
        {
            if (GroupedMessages != null && GroupedMessages.Count != 0)
            {
                return !string.Equals(Title, GroupedMessages.First().Sender, StringComparison.OrdinalIgnoreCase);
            }

            return !string.Equals(AppName, Title, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string FlyoutFilterString => string.Format("NotificationFilterButton".GetLocalizedResource(), AppName);

    #region Helpers
    public static async Task<Notification> FromMessage(NotificationMessage message)
    {
        var notification = new Notification
        {
            Key = message.NotificationKey,
            TimeStamp = message.TimeStamp,
            Type = message.NotificationType,
            AppName = message.AppName,
            AppPackage = message.AppPackage,
            Title = message.Title,
            Text = message.Text,
            GroupedMessages = GroupBySender(message.Messages),
            Tag = message.Tag,
            GroupKey = message.GroupKey,
            Actions = message.Actions.Where(a => a != null).ToList()!,
            ReplyResultKey = message.ReplyResultKey
        };

        // 设置图标路径，复用应用列表的已有图标
        if (!string.IsNullOrEmpty(message.AppPackage))
        {
            notification.IconPath = IconUtils.GetAppIconPath(message.AppPackage);
            await notification.LoadIconAsync();
        }

        return notification;
    }

    /// <summary>
    /// 从本地加载图标
    /// </summary>
    public async Task LoadIconAsync()
    {
        if (string.IsNullOrEmpty(IconPath)) return;
        
        try
        {
            // 尝试从本地加载图标
            var bitmapImage = new BitmapImage();
            bitmapImage.UriSource = new Uri(IconPath);
            Icon = bitmapImage;
        }
        catch (Exception ex)
        {
            // 忽略加载错误
        }
    }

    internal static List<NotificationGroup> GroupBySender(List<NotificationTextMessage>? messages)
    {
        if (messages == null || messages.Count == 0) return [];

        List<NotificationGroup> result = [];
        NotificationGroup? currentGroup = null;

        foreach (var message in messages)
        {
            if (currentGroup?.Sender != message.Sender)
            {
                currentGroup = new NotificationGroup(message.Sender, []);
                result.Add(currentGroup);
            }
            if (!string.IsNullOrEmpty(message.Text))
            {
                currentGroup.Messages.Add(message.Text);
            }
        }
        return result;
    }
    #endregion
}
