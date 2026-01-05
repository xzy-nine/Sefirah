namespace Sefirah.Data.Contracts;

public interface INetworkService
{
    Task<bool> StartServerAsync();
    int ServerPort { get; }
    void SendMessage(string deviceId, string message);
    void SendAppListRequest(string deviceId);
    void SendIconRequest(string deviceId, string packageName);
    void SendIconRequest(string deviceId, List<string> packageNames);
    void SendMediaControlRequest(string deviceId, string controlType);
}
