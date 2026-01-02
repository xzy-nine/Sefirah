using Sefirah.Data.Models;
using Sefirah.Services.Socket;

namespace Sefirah.Data.Contracts;
public interface ISessionManager
{
    /// <summary>
    /// Event fired when a device connection status changes
    /// </summary>
    event EventHandler<(PairedDevice Device, bool IsConnected)> ConnectionStatusChanged;

    /// <summary>
    /// Sends a message to the specified device.
    /// </summary>
    /// <param name="deviceId">Target device ID.</param>
    /// <param name="message">The message to send.</param>
    void SendMessage(string deviceId, string message);

    /// <summary>
    /// Sends a message to all connected devices.
    /// </summary>
    /// <param name="message">The message to send.</param>
    void BroadcastMessage(string message);

    /// <summary>
    /// Disconnects the specified device session (if any).
    /// </summary>
    void DisconnectDevice(string deviceId);
}
