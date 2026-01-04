using System.Text.Json;
using Windows.Storage.Streams;

namespace Sefirah.Utils;

/// <summary>
/// Utility class for image operations
/// </summary>
public static class IconUtils
{
    private const string AppIconsFolderName = "AppIcons";
    private const string NotifyRelayPackageName = "com.xzyht.notifyrelay";
    private const string NotifyRelayAppIconPath = "ms-appx:///Assets/NotifyRelayAppIcon.png";

    /// <summary>
    /// Gets or creates the AppIcons folder in the local app data
    /// </summary>
    /// <returns>The AppIcons folder</returns>
    public static async Task<StorageFolder> GetAppIconsFolderAsync()
    {
        var localFolder = ApplicationData.Current.LocalFolder;
        try
        {
            return await localFolder.GetFolderAsync(AppIconsFolderName);
        }
        catch (FileNotFoundException)
        {
            return await localFolder.CreateFolderAsync(AppIconsFolderName);
        }
    }

    /// <summary>
    /// Saves a base64 encoded image to a file and returns the URI
    /// </summary>
    /// <param name="base64">Base64 encoded image data</param>
    /// <param name="fileName">Name of the file to save</param>
    /// <returns>URI to the saved file</returns>
    public static async Task<Uri> SaveBase64ToFileAsync(string base64, string fileName)
    {
        var bytes = Convert.FromBase64String(base64);
        var localFolder = ApplicationData.Current.LocalFolder;
        var file = await localFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        using var dataWriter = new DataWriter(stream);
        dataWriter.WriteBytes(bytes);
        await dataWriter.StoreAsync();

        return new Uri($"ms-appdata:///local/{fileName}");
    }


    /// <summary>
    /// Gets the URI for an app icon file in the AppIcons folder
    /// </summary>
    /// <param name="packageName">Name of the app icon file</param>
    /// <returns>URI to the app icon file</returns>
    public static async Task<Uri?> GetAppIconUriAsync(string packageName)
    {
        // 处理mediaplay:前缀，移除前缀后再处理图标
        string actualPackageName = packageName.StartsWith("mediaplay:") ? packageName.Substring("mediaplay:".Length) : packageName;
        
        // 对于 Notify-Relay 应用包名，返回内置图标 URI
        if (actualPackageName == NotifyRelayPackageName)
        {
            return new Uri(NotifyRelayAppIconPath);
        }
        
        try
        {
            var appIconsFolder = await GetAppIconsFolderAsync();
            // First try with .png extension (most icons are saved as {package}.png)
            string fileNameWithExt = actualPackageName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? actualPackageName : actualPackageName + ".png";
            try
            {
                await appIconsFolder.GetFileAsync(fileNameWithExt);
                return new Uri($"ms-appdata:///local/{AppIconsFolderName}/{fileNameWithExt}");
            }
            catch (FileNotFoundException)
            {
                // Fallback: try without extension (legacy)
                await appIconsFolder.GetFileAsync(actualPackageName);
                return new Uri($"ms-appdata:///local/{AppIconsFolderName}/{actualPackageName}");
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public static string GetAppIconFilePath(string packageName)
    {
        // 处理mediaplay:前缀，移除前缀后再处理图标
        string actualPackageName = packageName.StartsWith("mediaplay:") ? packageName.Substring("mediaplay:".Length) : packageName;
        
        // 对于 Notify-Relay 应用包名，返回内置图标路径
        if (actualPackageName == NotifyRelayPackageName)
        {
            return NotifyRelayAppIconPath;
        }
        
        return $@"{ApplicationData.Current.LocalFolder.Path}\{AppIconsFolderName}\{actualPackageName}.png";
    }

    public static string GetAppIconPath(string packageName)
    {
        // 处理mediaplay:前缀，移除前缀后再处理图标
        string actualPackageName = packageName.StartsWith("mediaplay:") ? packageName.Substring("mediaplay:".Length) : packageName;
        
        // 对于 Notify-Relay 应用包名，返回内置图标路径
        if (actualPackageName == NotifyRelayPackageName)
        {
            return NotifyRelayAppIconPath;
        }
        
        return $"ms-appdata:///local/{AppIconsFolderName}/{actualPackageName}.png";
    }

    /// <summary>
    /// Checks if an app icon file exists for the given package name
    /// </summary>
    /// <param name="packageName">App package name</param>
    /// <returns>True if the icon file exists, false otherwise</returns>
    public static bool AppIconExists(string packageName)
    {
        // 处理mediaplay:前缀，移除前缀后再检查图标
        string actualPackageName = packageName.StartsWith("mediaplay:") ? packageName.Substring("mediaplay:".Length) : packageName;
        
        // 对于 Notify-Relay 应用包名，始终返回 true，使用内置图标
        if (actualPackageName == NotifyRelayPackageName)
        {
            return true;
        }
        
        try
        {
            string iconFilePath = GetAppIconFilePath(actualPackageName);
            return File.Exists(iconFilePath);
        }
        catch (Exception)
        {
            // If any error occurs, assume the icon doesn't exist
            return false;
        }
    }

    /// <summary>
    /// Saves app icon bytes to the AppIcons folder and returns the file system path
    /// </summary>
    /// <param name="appIconBase64">Base64 encoded app icon data</param>
    /// <param name="appPackage">App package name</param>
    public static async Task SaveAppIconToPathAsync(string? appIconBase64, string appPackage)
    {
        // 处理mediaplay:前缀，移除前缀后再保存图标
        string actualPackageName = appPackage.StartsWith("mediaplay:") ? appPackage.Substring("mediaplay:".Length) : appPackage;
        
        // 对于 Notify-Relay 应用包名，跳过保存，使用内置图标
        if (actualPackageName == NotifyRelayPackageName)
        {
            return;
        }
        
        try
        {
            if (string.IsNullOrEmpty(appIconBase64)) return;
            
            // 处理 base64 编码的图标数据
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(appIconBase64);
            }
            catch (FormatException)
            {
                // 忽略无效的 base64 字符串
                return;
            }
            
            var appIconsFolder = await GetAppIconsFolderAsync();
            var file = await appIconsFolder.CreateFileAsync($"{actualPackageName}.png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            using var dataWriter = new DataWriter(stream);
            dataWriter.WriteBytes(bytes);
            await dataWriter.StoreAsync();
        }
        catch (Exception)
        {
            // 忽略保存错误
        }
    }
    
    /// <summary>
    /// 构建图标请求对象
    /// </summary>
    /// <param name="packageName">应用包名</param>
    /// <returns>图标请求 JSON 字符串</returns>
    public static string BuildIconRequest(string packageName)
    {
        // 处理mediaplay:前缀，移除前缀后再构建请求
        string actualPackageName = packageName.StartsWith("mediaplay:") ? packageName.Substring("mediaplay:".Length) : packageName;
        
        var requestObj = new
        {
            type = "ICON_REQUEST",
            packageName = actualPackageName,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        return JsonSerializer.Serialize(requestObj);
    }
} 
