using Sefirah.Data.Contracts;
using Sefirah.Data.Models;
using Sefirah.Utils;

namespace Sefirah.Platforms.Desktop.Services;

public class DesktopSftpService(ILogger<DesktopSftpService> logger) : ISftpService
{
    private readonly Dictionary<string, string> _mountedDevices = [];

    public async Task InitializeAsync(PairedDevice device, SftpServerInfo info)
    {
        logger.LogInformation("正在为设备 {DeviceName} 初始化 SFTP 服务，IP：{IpAddress}，端口：{Port}，密码：{Password}", 
            device.Name, info.IpAddress, info.Port, info.Password);

        var sftpUri = $"sftp://{info.Username}@{info.IpAddress}:{info.Port}/";
        
        logger.LogInformation("正在为设备 {DeviceName} 挂载 SFTP", device.Name);

        ProcessExecutor.ExecuteProcess("gio", $"mount -s \"{sftpUri}\"");

        // Use gio mount with password input via stdin
        var (exitCode, errorOutput) = await ExecuteProcessWithPasswordAsync("gio", $"mount \"{sftpUri}\"", info.Password);
        
        if (exitCode != 0)
        {
            logger.LogError("为设备 {DeviceName} 挂载 SFTP 失败：{Error}", device.Name, errorOutput);
            return;
        }
        
        _mountedDevices[device.Id] = sftpUri;
        logger.LogInformation("为设备 {DeviceName} 成功挂载 SFTP", device.Name);
    }

    private static async Task<(int ExitCode, string ErrorOutput)> ExecuteProcessWithPasswordAsync(string fileName, string arguments, string password)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return (-1, "启动进程失败");
        }

        // Send password to stdin when prompted
        await process.StandardInput.WriteLineAsync(password);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        await process.WaitForExitAsync();
        var errorOutput = await process.StandardError.ReadToEndAsync();
        
        return (process.ExitCode, errorOutput);
    }

    public void Remove(string deviceId)
    {
        if (!_mountedDevices.TryGetValue(deviceId, out var sftpUri))
        {
            logger.LogDebug("设备 {DeviceId} 未挂载", deviceId);
            return;
        }
        
        logger.LogInformation("正在卸载设备 {DeviceId} 的 SFTP 挂载", deviceId);
        ProcessExecutor.ExecuteProcess("gio", $"mount -u \"{sftpUri}\"");
        _mountedDevices.Remove(deviceId);
    }
}
