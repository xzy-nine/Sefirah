using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Media.Imaging;
using Sefirah.Data.Enums;
using Sefirah.Extensions;
using Sefirah.Helpers;
using Sefirah.Utils;
using System.Collections.ObjectModel;
using System.Net.Http;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Sefirah.Data.Models;

// 用于存储通知来源设备的类
public class SourceDevice
{
    public string DeviceId { get; set; }
    public string DeviceName { get; set; }
    
    public SourceDevice(string deviceId, string deviceName)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
    }
}

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
    
    // 存储通知来自的设备列表，用于聚合相同内容的通知
    public ObservableCollection<SourceDevice> SourceDevices { get; set; } = new ObservableCollection<SourceDevice>();
    
    // 为了兼容现有代码，保留DeviceId和DeviceName属性，但它们现在返回第一个设备的信息
    public string? DeviceId
    {
        get => SourceDevices.FirstOrDefault()?.DeviceId;
        set
        {
            if (value != null && SourceDevices.Count == 0)
            {
                SourceDevices.Add(new SourceDevice(value, string.Empty));
            }
        }
    }
    
    public string? DeviceName
    {
        get => SourceDevices.FirstOrDefault()?.DeviceName;
        set
        {
            if (value != null && SourceDevices.Count > 0)
            {
                SourceDevices[0].DeviceName = value;
            }
        }
    }
    
    // 添加设备到通知的来源设备列表
    public void AddSourceDevice(string deviceId, string deviceName)
    {
        // 检查设备是否已经存在
        if (!SourceDevices.Any(d => d.DeviceId == deviceId))
        {
            SourceDevices.Add(new SourceDevice(deviceId, deviceName));
        }
    }

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
            // 使用异步方式加载图像，确保图标能正确显示
            await bitmapImage.SetSourceAsync(await GetStreamForUriAsync(new Uri(IconPath)));
            Icon = bitmapImage;
        }
        catch (Exception)
        {
            // 忽略加载错误
        }
    }
    
    /// <summary>
    /// 获取URI对应的流
    /// </summary>
    private async Task<IRandomAccessStream> GetStreamForUriAsync(Uri uri)
    {
        if (uri.Scheme == "ms-appdata")
        {
            // 处理本地应用数据文件
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            return await file.OpenAsync(FileAccessMode.Read);
        }
        else if (uri.Scheme == "ms-appx")
        {
            // 处理应用包内文件
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            return await file.OpenAsync(FileAccessMode.Read);
        }
        else
        {
            // 处理其他URI（如http/https）
            var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync();
            return stream.AsRandomAccessStream();
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
