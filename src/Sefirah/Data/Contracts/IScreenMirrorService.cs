using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;

public interface IScreenMirrorService : IDisposable
{
    Task<bool> StartScrcpy(PairedDevice device, string? customArgs = null, string? iconPath = null);
    void StopScrcpy(string deviceSerial);
    void StopScrcpyByDeviceId(string deviceId);
    bool IsAudioOnlyRunning(string deviceId);
}
