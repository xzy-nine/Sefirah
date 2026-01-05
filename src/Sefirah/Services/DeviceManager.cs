using CommunityToolkit.WinUI;
using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Data.Contracts;
using Sefirah.Data.Models;
using Sefirah.Dialogs;
using Sefirah.Helpers;
using Sefirah.Utils;
using System.Text;
using Windows.Storage;

namespace Sefirah.Services;

public partial class DeviceManager(ILogger<DeviceManager> logger, DeviceRepository repository) : ObservableObject, IDeviceManager
{
    public ObservableCollection<PairedDevice> PairedDevices { get; set; } = [];

    [ObservableProperty]
    public partial PairedDevice? ActiveDevice { get; set; }

    /// <summary>
    /// Event fired when the active session changes
    /// </summary>

    /// <summary>
    /// Event fired when the local device name changes
    /// </summary>
    public event EventHandler<string>? LocalDeviceNameChanged;

    /// <summary>
    /// Finds a device session by device ID
    /// </summary>
    public PairedDevice? FindDeviceById(string deviceId)
    {
        return PairedDevices.FirstOrDefault(device => device.Id == deviceId);
    }

    /// <summary>
    /// Updates an existing device in the collection or adds it if it doesn't exist.
    /// Returns the live instance stored in the collection for further updates.
    /// </summary>
    public async Task<PairedDevice> UpdateOrAddDeviceAsync(PairedDevice device, Action<PairedDevice>? updateAction = null)
    {
        var tcs = new TaskCompletionSource<PairedDevice>();

        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            var existingDevice = PairedDevices.FirstOrDefault(d => d.Id == device.Id);
            if (existingDevice is not null)
            {
                existingDevice.Name = device.Name;
                existingDevice.Model = device.Model;
                existingDevice.IpAddresses = device.IpAddresses;
                existingDevice.PhoneNumbers = device.PhoneNumbers;
                existingDevice.Wallpaper = device.Wallpaper;
                existingDevice.Session = device.Session;
                existingDevice.SharedSecret = device.SharedSecret;
                existingDevice.RemotePublicKey = device.RemotePublicKey;
                updateAction?.Invoke(existingDevice);
                tcs.SetResult(existingDevice);
            }
            else
            {
                PairedDevices.Add(device);
                updateAction?.Invoke(device);
                tcs.SetResult(device);
            }
        });

        return await tcs.Task;
    }

    public Task<RemoteDeviceEntity> GetDeviceInfoAsync(string deviceId)
    {
        throw new NotImplementedException();
    }   

    public List<string> GetRemoteDeviceIpAddresses()
    {
        return repository.GetRemoteDeviceIpAddresses();
    }

    public async Task<PairedDevice?> GetLastConnectedDevice()
    {
        return await repository.GetLastConnectedDevice();
    }

    public void RemoveDevice(PairedDevice device)
    {
        App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            try
            {
                var existing = PairedDevices.FirstOrDefault(d => d.Id == device.Id);
                if (existing is null) return;

                PairedDevices.Remove(existing);
                repository.DeletePairedDevice(existing.Id);

                if (ActiveDevice?.Id == existing.Id)
                {
                    ActiveDevice = PairedDevices.FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "移除设备 {id} 时出错", device.Id);
            }
        });
    }

    public Task UpdateDevice(RemoteDeviceEntity device)
    {
        throw new NotImplementedException();
    }

    public void UpdateDeviceStatus(PairedDevice device, DeviceStatus deviceStatus)
    {
        var pairedDevice = PairedDevices.First(d => d.Id == device.Id);
        App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            pairedDevice.Status = deviceStatus;
        });
    }

    public async Task<PairedDevice?> VerifyHandshakeAsync(string deviceId, string remotePublicKey, string? deviceName, string? ipAddress)
    {
        try
        {
            var localDevice = await GetLocalDeviceAsync();
            var localKey = Encoding.UTF8.GetString(localDevice.PublicKey ?? Array.Empty<byte>());
            var sharedSecretBytes = NotifyCryptoHelper.GenerateSharedSecretBytes(localKey, remotePublicKey);
            var passkey = NotifyCryptoHelper.ComputePasskey(sharedSecretBytes);

            if (repository.HasDevice(deviceId, out var existingDevice))
            {
                existingDevice.LastConnected = DateTime.Now;
                existingDevice.Name = deviceName ?? existingDevice.Name;
                existingDevice.PublicKey = remotePublicKey;
                existingDevice.SharedSecret = sharedSecretBytes;

                if (ipAddress is not null && !existingDevice.IpAddresses.Contains(ipAddress))
                {
                    existingDevice.IpAddresses = [.. existingDevice.IpAddresses, ipAddress];
                }

                repository.AddOrUpdateRemoteDevice(existingDevice);

                var pairedDevice = await App.MainWindow.DispatcherQueue.EnqueueAsync(() => existingDevice.ToPairedDevice());
                return pairedDevice;
            }

            var tcs = new TaskCompletionSource<PairedDevice?>();
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                try
                {
                    var frame = (Frame)App.MainWindow.Content!;
                    var dialog = new ConnectionRequestDialog(deviceName ?? deviceId, passkey, frame)
                    {
                        XamlRoot = App.MainWindow.Content!.XamlRoot
                    };

                    var result = await dialog.ShowAsync();

                    if (result is not ContentDialogResult.Primary)
                    {
                        logger.LogInformation("用户拒绝了设备验证");
                        tcs.SetResult(null);
                        return;
                    }

                    var newDevice = new RemoteDeviceEntity
                    {
                        DeviceId = deviceId,
                        Name = deviceName ?? deviceId,
                        LastConnected = DateTime.Now,
                        Model = string.Empty,
                        SharedSecret = sharedSecretBytes,
                        PublicKey = remotePublicKey,
                        WallpaperBytes = null,
                        IpAddresses = ipAddress is not null ? [ipAddress] : [],
                        PhoneNumbers = [],
                    };

                    repository.AddOrUpdateRemoteDevice(newDevice);
                    tcs.SetResult(await newDevice.ToPairedDevice());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "验证设备时出错");
            return null;
        }
    }

    public async Task<LocalDeviceEntity> GetLocalDeviceAsync()
    {
        try
        {
            var localDevice = repository.GetLocalDevice();
            var localSettings = ApplicationData.Current.LocalSettings;
            var persistedId = localSettings.Values[nameof(LocalDeviceEntity.DeviceId)] as string;
            var persistedPublicKey = localSettings.Values["PublicKey"] as string;

            if (localDevice is null)
            {
                var (name, _) = await UserInformation.GetCurrentUserInfoAsync();
                var publicKey = persistedPublicKey ?? NotifyCryptoHelper.GeneratePublicKey();
                localDevice = new LocalDeviceEntity
                {
                    DeviceId = persistedId ?? Guid.NewGuid().ToString(),
                    DeviceName = name,
                    PublicKey = Encoding.UTF8.GetBytes(publicKey),
                    PrivateKey = Array.Empty<byte>(),
                };

                repository.AddOrUpdateLocalDevice(localDevice);
                localSettings.Values[nameof(LocalDeviceEntity.DeviceId)] = localDevice.DeviceId;
                localSettings.Values["PublicKey"] = publicKey;
            }
            else
            {
                var currentKey = Encoding.UTF8.GetString(localDevice.PublicKey ?? Array.Empty<byte>());
                var normalizedKey = NotifyCryptoHelper.NormalizePublicKey(currentKey);
                if (!string.Equals(currentKey, normalizedKey, StringComparison.Ordinal))
                {
                    localDevice.PublicKey = Encoding.UTF8.GetBytes(normalizedKey);
                    repository.AddOrUpdateLocalDevice(localDevice);
                }

                // 确保DeviceId在数据库和本地设置中保持一致
                bool deviceIdChanged = false;
                
                // 情况1：本地设备的DeviceId为空，优先使用本地设置中的persistedId
                if (string.IsNullOrWhiteSpace(localDevice.DeviceId))
                {
                    if (persistedId is not null)
                    {
                        localDevice.DeviceId = persistedId;
                        deviceIdChanged = true;
                    }
                    else
                    {
                        // 如果本地设置中也没有persistedId，生成一个新的GUID
                        localDevice.DeviceId = Guid.NewGuid().ToString();
                        deviceIdChanged = true;
                    }
                }
                // 情况2：本地设备的DeviceId不为空，但本地设置中的persistedId不同
                else if (persistedId is not null && persistedId != localDevice.DeviceId)
                {
                    // 以数据库中的DeviceId为准，更新本地设置
                    persistedId = localDevice.DeviceId;
                }
                
                // 如果DeviceId发生了变化，更新数据库和本地设置
                if (deviceIdChanged)
                {
                    repository.AddOrUpdateLocalDevice(localDevice);
                }
                
                // 确保本地设置中始终有DeviceId
                localSettings.Values[nameof(LocalDeviceEntity.DeviceId)] = localDevice.DeviceId;

                // 处理PublicKey
                if (persistedPublicKey is not null && persistedPublicKey != currentKey)
                {
                    localDevice.PublicKey = Encoding.UTF8.GetBytes(persistedPublicKey);
                    repository.AddOrUpdateLocalDevice(localDevice);
                }
                else if (persistedPublicKey is null)
                {
                    localSettings.Values["PublicKey"] = currentKey;
                }
            }
            return localDevice;
        }
        catch (Exception e)
        {
            logger.LogError(e, "获取本地设备时出错");
            throw;
        }
    }

    public void UpdateLocalDevice(LocalDeviceEntity device)
    {
        try
        {
            var existingDevice = repository.GetLocalDevice();
            repository.AddOrUpdateLocalDevice(device);
            
            // 检查设备名是否更改
            if (existingDevice != null && existingDevice.DeviceName != device.DeviceName)
            {
                LocalDeviceNameChanged?.Invoke(this, device.DeviceName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新本地设备时出错");
        }
    }

    public async Task Initialize()
    {
        var pairedDevicesList = await repository.GetPairedDevices();
        PairedDevices = pairedDevicesList.ToObservableCollection();
        ActiveDevice = PairedDevices.FirstOrDefault();
    }
}
