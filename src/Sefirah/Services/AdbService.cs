using System.Net;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using CommunityToolkit.WinUI;
using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Sefirah.Data.Items;
using Sefirah.Data.Models;

namespace Sefirah.Services;

public class AdbService(
    ILogger<AdbService> logger,
    IDeviceManager deviceManager,
    IUserSettingsService userSettingsService
) : IAdbService
{
    private CancellationTokenSource? cts;
    private DeviceMonitor? deviceMonitor;
    private readonly AdbClient adbClient = new();
    
    public ObservableCollection<AdbDevice> AdbDevices { get; } = [];
    public bool IsMonitoring => deviceMonitor != null && !(cts?.IsCancellationRequested ?? true);

    public AdbClient AdbClient => adbClient;

    // Initialize the codec option collections
    public ObservableCollection<ScrcpyPreferenceItem> DisplayOrientationOptions { get; } =
    [
        new(0, "", "Default"),
        new(1, "0", "0°"),
        new(2, "90", "90°"),
        new(3, "180", "180°"),
        new(4, "270", "270°"),
        new(5, "flip0", "flip-0°"),
        new(6, "flip90", "flip-90°"),
        new(7, "flip180", "flip-180°"),
        new(8, "flip270", "flip-270°")
    ];

    public ObservableCollection<ScrcpyPreferenceItem> VideoCodecOptions { get; } =
    [
        new(0, "", "Default"),
        new(1, "--video-codec=h264 --video-encoder=OMX.qcom.video.encoder.avc", "h264 & c2.qti.avc.encoder (hw)"),
        new(2, "--video-codec=h264 --video-encoder=c2.android.avc.encoder", "h264 & c2.android.avc.encoder (sw)"),
        new(4, "--video-codec=h264 --video-encoder=OMX.google.h264.encoder", "h264 & OMX.google.h264.encoder (sw)"),
        new(5, "--video-codec=h265 --video-encoder=OMX.qcom.video.encoder.hevc", "h265 & OMX.qcom.video.encoder.hevc (hw)"),
        new(6, "--video-codec=h265 --video-encoder=c2.android.hevc.encoder", "h265 & c2.android.hevc.encoder (sw)")
    ];

    public ObservableCollection<ScrcpyPreferenceItem> AudioCodecOptions { get; } =
    [
        new(0, "", "Default"),
        new(1, "--audio-codec=opus --audio-encoder=c2.android.opus.encoder", "opus & c2.android.opus.encoder (sw)"),
        new(2, "--audio-codec=aac --audio-encoder=c2.android.aac.encoder", "aac & c2.android.aac.encoder (sw)"),
        new(3, "--audio-codec=aac --audio-encoder=OMX.google.aac.encoder", "aac & OMX.google.aac.encoder (sw)"),
        new(4, "--audio-codec=raw", "raw")
    ];


    // TODO: To add new options dynamically
    public void AddVideoCodecOption(string command, string display)
    {
        int newId = VideoCodecOptions.Count > 0 ? VideoCodecOptions.Max(x => x.Id) + 1 : 0;
        VideoCodecOptions.Add(new ScrcpyPreferenceItem(newId, command, display));
    }

    public void AddAudioCodecOption(string command, string display)
    {
        int newId = AudioCodecOptions.Count > 0 ? AudioCodecOptions.Max(x => x.Id) + 1 : 0;
        AudioCodecOptions.Add(new ScrcpyPreferenceItem(newId, command, display));
    }


    
    public async Task StartAsync()
    {
        try
        {
            if (IsMonitoring) return;

            cts = new CancellationTokenSource();
            string adbPath = $"{userSettingsService.GeneralSettingsService.AdbPath}";

            // Start the ADB server if it's not running
            StartServerResult startServerResult = await AdbServer.Instance.StartServerAsync(adbPath, false, cts.Token);
            logger.LogTrace($"ADB 服务启动结果：{startServerResult}");
            
            // Create and configure the device monitor
            deviceMonitor = new DeviceMonitor(new AdbSocket(new IPEndPoint(IPAddress.Loopback, AdbClient.AdbServerPort)));
            
            deviceMonitor.DeviceConnected += DeviceConnected;
            deviceMonitor.DeviceDisconnected += DeviceDisconnected;
            deviceMonitor.DeviceChanged += DeviceChanged;

            await Task.Delay(50);
            
            await deviceMonitor.StartAsync();
            
            // Get initial list of devices
            await RefreshDevicesAsync();
            
            logger.LogTrace("ADB 设备监控已成功启动");
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            logger.LogError("启动 ADB 设备监控失败：{ex}", ex);
        }
    }
    
    public async Task StopAsync()
    {
        if (!IsMonitoring)
        {
            logger.LogWarning("ADB 监控未在运行");
            return;
        }
        
        await CleanupAsync();
        logger.LogInformation("ADB 设备监控已停止");
    }
    
    private async Task CleanupAsync()
    {
        if (deviceMonitor != null)
        {
            deviceMonitor.DeviceConnected -= DeviceConnected;
            deviceMonitor.DeviceDisconnected -= DeviceDisconnected;
            deviceMonitor.DeviceChanged -= DeviceChanged;
            
            await deviceMonitor.DisposeAsync();
            deviceMonitor = null;
        }
        
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
            cts = null;
        }
    }
    
    private async void DeviceConnected(object? sender, DeviceDataEventArgs e)
    {
        try
        {
            // Check if device already exists in collection
            var existingDevice = AdbDevices.FirstOrDefault(d => d.Serial == e.Device.Serial);
            if (existingDevice != null) return;

            // get the rudimentary data if it isn't online yet
            if (e.Device.State != DeviceState.Online)
            {
                logger.LogTrace($"设备 {e.Device.Serial} 已连接，但尚未在线，当前状态：{e.Device.State}");

                var adbDevice = new AdbDevice
                {
                    Serial = e.Device.Serial,
                    Model = e.Device.Model ?? "Unknown",
                    State = e.Device.State,
                    Type = e.Device.Serial.Contains(':') || e.Device.Serial.Contains("tcp") ? DeviceType.WIFI : DeviceType.USB,
                    DeviceData = e.Device,
                    AndroidId = "" // Will be populated when device comes online
                };

                await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    AdbDevices.Add(adbDevice);
                });
                return;
            }
            
            // Refresh the full device information
            var connectedDevice = await GetFullDeviceInfoAsync(e.Device);
            
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                AdbDevices.Add(connectedDevice);
            });
            logger.LogDebug($"设备已连接：{connectedDevice.Model} ({connectedDevice.Serial})");
        }
        catch (Exception ex)
        {
            logger.LogError($"处理设备连接时出错 {e.Device.Serial}：{ex.Message}", ex);
        }
    }
    
    private async void DeviceDisconnected(object? sender, DeviceDataEventArgs e)
    {
        logger.LogTrace($"设备已断开：{e.Device.Serial}");
        var existingDevice = AdbDevices.FirstOrDefault(d => d.Serial == e.Device.Serial);
        if (existingDevice != null)
        {
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                var index = AdbDevices.IndexOf(existingDevice);
                if (index != -1)
                {
                    AdbDevices.RemoveAt(index);
                }
            });
        }
    }
    
    private async void DeviceChanged(object? sender, DeviceDataChangeEventArgs e)
    {

        logger.LogTrace($"设备状态已更改：{e.Device.Serial} {e.OldState} -> {e.NewState}");
        var existingDevice = AdbDevices.FirstOrDefault(d => d.Serial == e.Device.Serial);
            
        if (e.NewState == DeviceState.Online)
        {
            var deviceInfo = await GetFullDeviceInfoAsync(e.Device);
                
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (existingDevice != null)
                {
                    // Update existing device
                    var index = AdbDevices.IndexOf(existingDevice);
                    if (index != -1)
                    {
                        AdbDevices[index] = deviceInfo;
                        logger.LogDebug($"设备已更新：{deviceInfo.Model} ({deviceInfo.Serial})");
                    }
                }
                else
                {
                    // Only add if device doesn't exist
                    AdbDevices.Add(deviceInfo);
                    logger.LogDebug($"设备已添加：{deviceInfo.Model} ({deviceInfo.Serial})");
                }
            });
                
            logger.LogDebug($"设备已连接：{deviceInfo.Model} ({deviceInfo.Serial})");
        }
        else
        {
            // Device is going offline/authorizing - just update the state if it exists
            if (existingDevice != null)
            {
                await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    var index = AdbDevices.IndexOf(existingDevice);
                    if (index != -1)
                    {
                        existingDevice.State = e.NewState;
                        AdbDevices[index] = existingDevice;
                    }
                });
            }
        }
    }
    
    private async Task RefreshDevicesAsync()
    {
        var devices = await adbClient.GetDevicesAsync();
        if (devices.Any())
        {
            logger.LogWarning("未找到设备");
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                AdbDevices.Clear();
            });
            return;
        }

        await App.MainWindow.DispatcherQueue.EnqueueAsync(async() =>
        {
            var adbDevices = new List<AdbDevice>();
            foreach (var device in devices)
            {
                AdbDevice adbDevice;
                if (device.State == DeviceState.Online)
                {
                    // Get full device info including AndroidId for online devices
                    adbDevice = await GetFullDeviceInfoAsync(device);
                }
                else
                {
                    // Create basic device info for non-online devices
                    adbDevice = new AdbDevice
                    {
                        Serial = device.Serial,
                        Model = device.Model ?? "Unknown",
                        State = device.State,
                        Type = device.Serial.Contains(':') || device.Serial.Contains("tcp") ? DeviceType.WIFI : DeviceType.USB,
                        DeviceData = device,
                        AndroidId = ""
                    };
                }
                AdbDevices.Add(adbDevice);
            }
        });
    }
    
    private async Task<AdbDevice> GetFullDeviceInfoAsync(DeviceData deviceData)
    {
        try
        {
            // Get full device information including model
            var devices = await adbClient.GetDevicesAsync();
            var fullDeviceData = devices.FirstOrDefault(d => d.Serial == deviceData.Serial);
            string androidId = string.Empty;
            try
            {
                logger.LogTrace($"开始获取设备 {deviceData.Serial} 的 UUID");
                var uuidReceiver = new ConsoleOutputReceiver();

                // adb shell cat /storage/emulated/0/Android/data/com.xzyht.notifyrelay/files/device_info.txt
                // Get the UUID from the device_info.txt file since we can't directly access the UUID of the App 
                string adbCommand = "cat /storage/emulated/0/Android/data/com.xzyht.notifyrelay/files/device_info.txt";
                logger.LogTrace($"执行 ADB 命令：{adbCommand}");
                await adbClient.ExecuteShellCommandAsync(deviceData, adbCommand, uuidReceiver);
                var rawOutput = uuidReceiver.ToString();
                var id = rawOutput.Trim();
                logger.LogTrace($"ADB 命令输出：'{rawOutput}'，处理后：'{id}'");
                if (!string.IsNullOrEmpty(id))
                {
                    // Extract the UUID from the output
                    androidId = id;
                    logger.LogTrace($"成功获取设备 {deviceData.Serial} 的 UUID：{androidId}");
                }
                else
                {
                    logger.LogWarning($"设备 {deviceData.Serial} 的 UUID 为空");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"获取 UUID 时出错：{deviceData.Serial}");
            }

            // Look for paired devices with matching model
            if (string.IsNullOrEmpty(androidId) && fullDeviceData.Model != null)
            {
                var deviceModel = fullDeviceData.Model;
                logger.LogTrace($"Android ID 为空，尝试通过设备型号 '{deviceModel}' 匹配已配对设备");

                var pairedDevices = deviceManager.PairedDevices;
                logger.LogTrace($"当前已配对设备数量：{pairedDevices.Count}");
                
                // (略) 不再逐条输出已配对设备，避免重复日志
                
                var matchingDevice = pairedDevices.FirstOrDefault(pd =>
                    !string.IsNullOrEmpty(pd.Model) &&
                    (pd.Model.Equals(deviceModel, StringComparison.OrdinalIgnoreCase) ||
                     pd.Model.Contains(deviceModel, StringComparison.OrdinalIgnoreCase) ||
                     deviceModel.Contains(pd.Model, StringComparison.OrdinalIgnoreCase)));

                if (matchingDevice != null)
                {
                    androidId = matchingDevice.Id;
                    logger.LogTrace($"通过型号匹配成功：设备型号 '{deviceModel}' 匹配到已配对设备 ID='{androidId}'，型号='{matchingDevice.Model}'");
                }
                else
                {
                    logger.LogWarning($"未找到与型号 '{deviceModel}' 匹配的配对设备");
                    androidId = string.Empty;
                }
            }

            var device = new AdbDevice
            {
                Serial = fullDeviceData.Serial,
                Model = fullDeviceData.Model ?? "Unknown",
                AndroidId = androidId,
                State = fullDeviceData.State,
                Type = fullDeviceData.Serial.Contains(':') || fullDeviceData.Serial.Contains("tcp") ? DeviceType.WIFI : DeviceType.USB,
                DeviceData = fullDeviceData
            };
            
            // 添加日志，便于调试
            logger.LogTrace($"生成 ADB 设备对象：序列号='{device.Serial}'，型号='{device.Model}'，Android ID='{device.AndroidId}'，在线状态='{device.IsOnline}'");
            
            // 检查是否有已配对设备匹配此 ADB 设备
            var allPairedDevices = deviceManager.PairedDevices;
            foreach (var pd in allPairedDevices)
            {
                logger.LogTrace($"检查已配对设备：ID='{pd.Id}'，型号='{pd.Model}'，是否匹配 ADB 设备：{pd.HasAdbConnection}");
            }
            
            return device;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"获取完整设备信息时出错：{deviceData.Serial}");
            // Return basic information if we can't get full details
            var device = new AdbDevice
            {
                Serial = deviceData.Serial,
                Model = "Unknown",
                AndroidId = "Unknown",
                State = deviceData.State,
                Type = deviceData.Serial.Contains(':') || deviceData.Serial.Contains("tcp") ? DeviceType.WIFI : DeviceType.USB,
                DeviceData = deviceData
            };
            
            return device;
        }
    }

    public async Task<bool> ConnectWireless(string? host, int port=5555)
    {
        if (string.IsNullOrEmpty(host)) return false;

        try
        {
            var result = await adbClient.ConnectAsync(host, port);
            if (result.Contains("failed") || result.Contains("refused"))
            {
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "连接默认无线设备时出错：{ex}");
            return false;
        }
    }

    public async Task<bool> Pair(AdbDevice device, string pairingCode, string host, int port=5555)
    {
        if (string.IsNullOrEmpty(host)) return false;
        try
        {
            var result = await adbClient.PairAsync(host, port, pairingCode);
            if (result.Contains("failed") || result.Contains("refused"))
            {
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "连接无线设备 {device} 时出错：{ex}", device.Serial, ex);
            return false;
        }
    }

    public async void UnlockDevice(DeviceData deviceData, List<string> commands)
    {
        try
        {
            logger.LogTrace("正在解锁设备");
            if (await IsLocked(deviceData))
            {
                foreach (var command in commands)
                {
                    logger.LogTrace("执行命令：{command}", command);
                    await adbClient.ExecuteShellCommandAsync(deviceData, command);
                    await Task.Delay(250);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解锁设备时出错：{ex}", ex);
        }
    }

    public async Task<bool> IsLocked(DeviceData deviceData)
    {
        ConsoleOutputReceiver consoleReceiver = new();
        await adbClient.ExecuteShellCommandAsync(deviceData, "dumpsys window policy | grep 'showing=' | cut -d '=' -f2", consoleReceiver);
        return consoleReceiver.ToString().Trim() == "true";
    }

    public async Task UninstallApp(string deviceId, string appPackage)
    {
        logger.LogInformation("正在从设备 {deviceId} 卸载应用 {appPackage}", appPackage, deviceId);

        var adbDevice = AdbDevices.FirstOrDefault(d => d.AndroidId == deviceId);
        if (adbDevice?.DeviceData == null) return;
        
        var deviceData = adbDevice.DeviceData.Value;
        await adbClient.UninstallPackageAsync(deviceData, appPackage);
    }

    /// <summary>
    /// Enables TCP/IP mode by restarting ADB with tcpip 5555 command
    /// </summary>
    private async Task<bool> EnableTcpipMode(string? targetSerial = null)
    {
        try
        {
            string adbPath = userSettingsService.GeneralSettingsService.AdbPath;
            if (string.IsNullOrEmpty(adbPath))
            {
                logger.LogError("ADB 路径未配置");
                return false;
            }

            logger.LogTrace("正在使用 ADB（{AdbPath}）启用 TCP/IP 模式，目标序列：{Target}", adbPath, targetSerial ?? "<any>");

            // 先列出当前 adb devices，帮助诊断多设备情况
            try
            {
                var listInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "devices -l",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var listProc = Process.Start(listInfo);
                if (listProc != null)
                {
                    var listOut = await listProc.StandardOutput.ReadToEndAsync();
                    var listErr = await listProc.StandardError.ReadToEndAsync();
                    await listProc.WaitForExitAsync();
                    logger.LogTrace("adb devices 输出:\n{Out}", listOut);
                    if (!string.IsNullOrEmpty(listErr)) logger.LogWarning("adb devices 错误输出: {Err}", listErr);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "执行 adb devices 时出错");
            }

            // Run "adb tcpip 5555" (如果提供了 targetSerial，则使用 -s 指定设备，避免 'more than one device' 错误)
            var tcpipArgs = string.IsNullOrEmpty(targetSerial) ? "tcpip 5555" : $"-s {targetSerial} tcpip 5555";
            logger.LogTrace("将执行 adb 命令: {Args}", tcpipArgs);

            var processInfo = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = tcpipArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                logger.LogError("启动 ADB 进程失败");
                return false;
            }

            await process.WaitForExitAsync();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            if (!string.IsNullOrEmpty(output)) logger.LogInformation("adb tcpip 输出: {Out}", output);
            if (!string.IsNullOrEmpty(error)) logger.LogWarning("adb tcpip 错误输出: {Err}", error);

            if (!string.IsNullOrEmpty(error) && error.Contains("more than one device", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("检测到多个设备：请在启用 tcpip 时指定目标设备序列号，或确保仅连接目标设备。错误信息：{Err}", error);
            }

            // Restart our ADB client to pick up the changes
            await RestartAdbClient();

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启用 TCP/IP 模式失败");
            return false;
        }
    }

    /// <summary>
    /// Restarts the ADB client to pick up TCP/IP mode changes
    /// </summary>
    private async Task RestartAdbClient()
    {
        try
        {
            logger.LogTrace("正在重启 ADB 客户端");
            var wasMonitoring = IsMonitoring;
            if (wasMonitoring)
            {
                await CleanupAsync();
            }
            await Task.Delay(200);

            if (wasMonitoring)
            {
                await StartAsync();
            }
            logger.LogTrace("ADB 客户端重启成功");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "重启 ADB 客户端失败");
        }
    }

    public async void TryConnectTcp(string host)
    {
        try
        {
            var result = await ConnectWireless(host);
            if (result)
            {
                logger.LogDebug("成功连接到 {Host}", host);
            }

            var usbDevice = AdbDevices.FirstOrDefault(d => d.Type == DeviceType.USB && d.IsOnline) ?? AdbDevices.FirstOrDefault(d => d.Type == DeviceType.USB);
            if (usbDevice == null) return;

            logger.LogDebug("尝试启用 TCP/IP 模式，首选 USB 设备序列: {Serial}", usbDevice.Serial);

            // If connection failed, try to enable TCP/IP mode using ADB if USB is connected
            var tcpipEnabled = await EnableTcpipMode(usbDevice.Serial);
            if (!tcpipEnabled)
            {
                logger.LogError("启用 TCP/IP 模式失败");
                return;
            }

            await Task.Delay(200);

            // Retry the connection after enabling TCP/IP mode
            result = await ConnectWireless(host);
            if (result)
            {
                logger.LogDebug("启用 TCP/IP 模式后成功连接到 {Host}", host);
                return;
            }

            logger.LogError("启用 TCP/IP 模式后 TCP/IP 连接仍然失败");
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "尝试连接 {Host} 时发生错误", host);
            return;
        }
    }
}
