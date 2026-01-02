namespace Sefirah.Data.Contracts;

public interface INetworkService
{
    Task<bool> StartServerAsync();
    int ServerPort { get; }
    void SendMessage(string deviceId, string message);
    void SendAppListRequest(string deviceId);
}
