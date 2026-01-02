using CommunityToolkit.WinUI;
using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Data.Contracts;
using Sefirah.Data.Models;
using Sefirah.Dialogs;
using Sefirah.Helpers;
using Sefirah.Utils;
using System.Text;

namespace Sefirah.Services;

public partial class DeviceManager(ILogger<DeviceManager> logger, DeviceRepository repository) : ObservableObject, IDeviceManager
{
    public ObservableCollection<PairedDevice> PairedDevices { get; set; } = [];

    [ObservableProperty]
    public partial PairedDevice? ActiveDevice { get; set; }

    /// <summary>
    /// Event fired when the active session changes
    /// </summary>
    public event EventHandler<PairedDevice?>? ActiveDeviceChanged;

    /// <summary>
    /// Finds a device session by device ID
    /// </summary>
    public PairedDevice? FindDeviceById(string deviceId)
    {
        return PairedDevices.FirstOrDefault(device => device.Id == deviceId);
    }

    /// <summary>
    /// Updates an existing device in the collection or adds it if it doesn't exist.
    /// </summary>
    public void UpdateOrAddDevice(PairedDevice device, Action<PairedDevice>? updateAction = null)
    {
        App.MainWindow?.DispatcherQueue.EnqueueAsync(() =>
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
            }
            else
            {
                PairedDevices.Add(device);
                updateAction?.Invoke(device);
            }
        });
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
            PairedDevices.Remove(device);
            repository.DeletePairedDevice(device.Id);
            if (ActiveDevice?.Id == device.Id)
            {
                ActiveDevice = PairedDevices.FirstOrDefault();
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
                        logger.LogInformation("User declined device verification");
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
            logger.LogError(ex, "Error verifying device");
            return null;
        }
    }

    public async Task<LocalDeviceEntity> GetLocalDeviceAsync()
    {
        try
        {
            var localDevice = repository.GetLocalDevice();
            if (localDevice is null)
            {
                var (name, _) = await UserInformation.GetCurrentUserInfoAsync();
                var publicKey = NotifyCryptoHelper.GeneratePublicKey();
                localDevice = new LocalDeviceEntity
                {
                    DeviceId = Guid.NewGuid().ToString(),
                    DeviceName = name,
                    PublicKey = Encoding.UTF8.GetBytes(publicKey),
                    PrivateKey = Array.Empty<byte>(),
                };

                repository.AddOrUpdateLocalDevice(localDevice);
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
            }
            return localDevice;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error getting local device");
            throw;
        }
    }

    public void UpdateLocalDevice(LocalDeviceEntity device)
    {
        try
        {
            repository.AddOrUpdateLocalDevice(device);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating local device");
        }
    }

    public async Task Initialize()
    {
        var pairedDevicesList = await repository.GetPairedDevices();
        PairedDevices = pairedDevicesList.ToObservableCollection();
        ActiveDevice = PairedDevices.FirstOrDefault();
    }
}
