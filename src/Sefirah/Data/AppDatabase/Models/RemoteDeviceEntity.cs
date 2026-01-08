using System.Text.Json;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using SQLite;

namespace Sefirah.Data.AppDatabase.Models;

public partial class RemoteDeviceEntity
{
    [PrimaryKey]
    public string DeviceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    // Notify 协议远端公钥（用于 HKDF 派生对称密钥）
    public string? PublicKey { get; set; }

    public byte[]? SharedSecret { get; set; }

    public byte[]? WallpaperBytes { get; set; }

    public DateTime? LastConnected { get; set; }

    public bool HasSentSftpRequest { get; set; } = false;

    [Column("IpAddresses")]
    public string? IpAddressesJson { get; set; }
    
    [Ignore]
    public List<string> IpAddresses
    {
        get => string.IsNullOrEmpty(IpAddressesJson) ? [] : JsonSerializer.Deserialize<List<string>>(IpAddressesJson) ?? [];
        set => IpAddressesJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    #region Helpers
    internal async Task<PairedDevice> ToPairedDevice()
    {
        return new PairedDevice(DeviceId)
        {
            Name = Name,
            Model = Model,
            IpAddresses = IpAddresses,
            Wallpaper = await ImageHelper.ToBitmapAsync(WallpaperBytes),
            SharedSecret = SharedSecret,
            RemotePublicKey = PublicKey,
            HasSentSftpRequest = HasSentSftpRequest,
        };
    }
    #endregion
}
