using System.Text;
using CommunityToolkit.WinUI;
using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Sefirah.Data.Models;
using Sefirah.Dialogs;
using Sefirah.Extensions;
using Sefirah.Utils;
using Sefirah.Views.Settings;
using Uno.Logging;
using Windows.ApplicationModel.DataTransfer;

namespace Sefirah.Services;
public class ScreenMirrorService(
    ILogger<ScreenMirrorService> logger,
    IUserSettingsService userSettingsService,
    IAdbService adbService
) : IScreenMirrorService
{
    private readonly ObservableCollection<AdbDevice> devices = adbService.AdbDevices;

    private Dictionary<string, Process> scrcpyProcesses = [];
    private CancellationTokenSource? cts;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = App.MainWindow?.DispatcherQueue;
    
    // Password cache: deviceId -> (password, cachedTime, timeoutMinutes)
    private readonly Dictionary<string, (string Password, DateTime CachedAt, int TimeoutMinutes)> passwordCache = [];
    
    public async Task<bool> StartScrcpy(PairedDevice device, string? customArgs = null, string? iconPath = null)
    {
        logger.LogDebug("[调试] StartScrcpy 请求: deviceId={DeviceId} customArgs={CustomArgs} iconPath={IconPath}", device?.Id, customArgs, iconPath);
        try
        {
            // 额外记录设备会话和连接状态以便诊断
            logger.LogDebug("[调试] 设备信息: Name={Name} ConnectionStatus={ConnectionStatus}", device?.Name, device?.ConnectionStatus);
        }
        catch { }

        Process? process = null;
        CancellationTokenSource? processCts = null;

        var deviceSettings = device.DeviceSettings;
        try
        {
            var scrcpyPath = userSettingsService.GeneralSettingsService.ScrcpyPath;
            if (!File.Exists(scrcpyPath))
            {
                logger.LogError("未在路径找到 scrcpy：{ScrcpyPath}", scrcpyPath);
                var result = await dispatcher!.EnqueueAsync(async () =>
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = App.MainWindow.Content!.XamlRoot,
                        Title = "ScrcpyNotFound".GetLocalizedResource(),
                        Content = "ScrcpyNotFoundDescription".GetLocalizedResource(),
                        PrimaryButtonText = "SelectLocation".GetLocalizedResource(),
                        DefaultButton = ContentDialogButton.Primary,
                        CloseButtonText = "Dismiss".GetLocalizedResource()
                    };

                    var dialogResult = await dialog.ShowAsync();
                    if (dialogResult is ContentDialogResult.Primary)
                    {
                        scrcpyPath = await SelectScrcpyLocationClick();
                        return !string.IsNullOrEmpty(scrcpyPath) && File.Exists(scrcpyPath);
                    }
                    return false;
                });

                if (!result) return false;
            }

            var devicePreferenceType = deviceSettings.ScrcpyDevicePreference;
            string? selectedDeviceSerial = null;

            List<string> argBuilder = [];
            if (!string.IsNullOrEmpty(customArgs))
            {
                argBuilder.Add(customArgs);
            }

            // 根据已配对设备信息优先匹配 ADB 设备：
            // 1. 优先匹配 AndroidId == PairedDevice.Id（绑定映射）
            // 2. 否则通过型号匹配
            // 3. 在候选中优先选择 USB（有线）设备
            var adbOnlineDevices = devices.Where(d => d != null && d.IsOnline).ToList();
            var matchedDevices = adbOnlineDevices.Where(d => !string.IsNullOrEmpty(d.AndroidId) && d.AndroidId == device.Id).ToList();

            if (matchedDevices.Count == 0 && !string.IsNullOrEmpty(device.Model))
            {
                matchedDevices = adbOnlineDevices.Where(d => string.IsNullOrEmpty(d.AndroidId) &&
                    !string.IsNullOrEmpty(d.Model) &&
                    (device.Model.Equals(d.Model, StringComparison.OrdinalIgnoreCase) ||
                     device.Model.Contains(d.Model, StringComparison.OrdinalIgnoreCase) ||
                     d.Model.Contains(device.Model, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            var pairedDevices = matchedDevices.Count > 0 ? matchedDevices : devices.Where(d => d != null && d.Model == device.Model).ToList();

            // 如果存在多个候选 ADB 设备，弹窗选择并预选最优（USB 优先），而不是直接选择第一个
            if (pairedDevices.Count > 1)
            {
                var preferredSerial = pairedDevices.FirstOrDefault(d => d.Type == DeviceType.USB)?.Serial
                                      ?? pairedDevices.First().Serial;
                selectedDeviceSerial = await ShowDeviceSelectionDialog(pairedDevices, preferredSerial);
                if (string.IsNullOrEmpty(selectedDeviceSerial))
                {
                    logger.LogWarning("用户在设备选择弹窗中取消或未选择设备");
                    return false;
                }
            }
            else if (pairedDevices.Count > 0)
            {
                switch (devicePreferenceType)
                {
                    case ScrcpyDevicePreferenceType.Usb:
                        // 优先选择已匹配设备中的 USB，否则从匹配列表中选择第一个
                        selectedDeviceSerial = pairedDevices.FirstOrDefault(d => d.Type == DeviceType.USB)?.Serial
                                            ?? pairedDevices.FirstOrDefault()?.Serial;
                        break;
                    case ScrcpyDevicePreferenceType.Tcpip:
                        selectedDeviceSerial = pairedDevices.FirstOrDefault(d => d.Type == DeviceType.WIFI)?.Serial
                                            ?? pairedDevices.FirstOrDefault()?.Serial;
                        break;
                    case ScrcpyDevicePreferenceType.Auto:
                        // 优先选择 USB，如果找到了 USB 且启用了 ADB TCP/IP 模式，则追加参数
                        var usbDevice = pairedDevices.FirstOrDefault(d => d.Type == DeviceType.USB);
                        if (usbDevice != null)
                        {
                            if (deviceSettings.AdbTcpipModeEnabled)
                            {
                                argBuilder.Add("--tcpip");
                            }
                            selectedDeviceSerial = usbDevice.Serial;
                        }
                        else
                        {
                            selectedDeviceSerial = pairedDevices.FirstOrDefault(d => d.Type == DeviceType.WIFI)?.Serial
                                                ?? pairedDevices.FirstOrDefault()?.Serial;
                        }
                        break;
                            case ScrcpyDevicePreferenceType.AskEverytime:
                                // 计算首选序列号：优先 USB 设备
                                var preferred = pairedDevices.FirstOrDefault(d => d.Type == DeviceType.USB)?.Serial
                                                ?? pairedDevices.FirstOrDefault()?.Serial;
                                selectedDeviceSerial = await ShowDeviceSelectionDialog(pairedDevices, preferred);
                        if (string.IsNullOrEmpty(selectedDeviceSerial))
                        {
                            logger.LogWarning("未选择用于 scrcpy 的设备");
                            return false;
                        }
                        break;
                }
                var commands = deviceSettings.UnlockCommands?.Trim()
                    .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim())
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToList();
                var adbDevice = pairedDevices.FirstOrDefault(d => d.Serial == selectedDeviceSerial);
                if (adbDevice is null || !adbDevice.DeviceData.HasValue) return false;

                if (commands?.Count > 0 && await adbService.IsLocked(adbDevice.DeviceData.Value))
                {
                    // Check if any command contains password placeholder
                    var hasPasswordPlaceholder = commands.Any(c => c.Contains("%pwd%"));
                    string? password = null;
                    
                    if (hasPasswordPlaceholder)
                    {
                        // Only use password caching if timeout is greater than 0
                        var timeoutSeconds = deviceSettings.UnlockTimeout;
                        if (timeoutSeconds > 0)
                        {
                            // Try to get cached password first
                            password = GetCachedPassword(device.Id, timeoutSeconds);
                        }
                        
                        // If no cached password or caching is disabled, ask user for password
                        if (password is null)
                        {
                            password = await ShowPasswordInputDialog();
                            if (password is null) return false;
                            
                            // Only cache the password if timeout is greater than 0
                            if (timeoutSeconds > 0)
                            {
                                CachePassword(device.Id, password, timeoutSeconds);
                            }
                        }
                        
                        // Replace password placeholders with actual password
                        commands = commands.Select(c => c.Replace("%pwd%", password)).ToList();
                    }
                    
                    adbService.UnlockDevice(adbDevice.DeviceData.Value, commands);
                }
            }
            else if(deviceSettings.AdbTcpipModeEnabled && device.Session != null)
            {
                var connectedSessionIpAddress = device.Session.Socket.RemoteEndPoint?.ToString()?.Split(':')[0];
                if (await adbService.ConnectWireless(connectedSessionIpAddress))
                {
                    selectedDeviceSerial = $"{connectedSessionIpAddress}:5555";
                }
            }
            else if (devices.Any(d => d.IsOnline && !string.IsNullOrEmpty(d.Serial)))
            {
                // If no paired devices found, show dialog to select from online devices
                var online = devices.Where(d => d.IsOnline).ToList();
                var preferred = online.FirstOrDefault(d => d.Type == DeviceType.USB)?.Serial
                                ?? online.FirstOrDefault()?.Serial;
                selectedDeviceSerial = await ShowDeviceSelectionDialog(online, preferred);
            }
            else
            {
                logger.LogWarning("未在 adb 中找到在线设备");
                _ = dispatcher?.EnqueueAsync(async () =>
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = App.MainWindow.Content!.XamlRoot,
                        Title = "AdbDeviceOffline".GetLocalizedResource(),
                        Content = "AdbDeviceOfflineDescription".GetLocalizedResource(),
                        CloseButtonText = "Dismiss".GetLocalizedResource()
                    };
                    await dialog.ShowAsync();
                });
                return false;
            }

            // Validate that we have a selected device
            if (string.IsNullOrEmpty(selectedDeviceSerial)) return false;
            
            argBuilder.Add($"-s {selectedDeviceSerial}");

            // Build arguments for scrcpy with the selected device
            var (args, deviceSerial) = BuildScrcpyArguments(argBuilder, selectedDeviceSerial!, deviceSettings);
            
            cts?.Cancel();
            cts?.Dispose();
            processCts = new CancellationTokenSource();
            cts = processCts;
            
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = scrcpyPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            if (!string.IsNullOrEmpty(iconPath))
            {
                process.StartInfo.EnvironmentVariables["SCRCPY_ICON_PATH"] = iconPath;
                logger.Info($"正在使用自定义 scrcpy 图标：{iconPath}");
            }

            bool started;
            try
            {
                started = process.Start();
                logger.LogDebug("[调试] scrcpy 进程 Start() 返回: {Started}", started);
            }
            catch (Exception ex)
            {
                logger.LogError($"启动 scrcpy 失败：{ex.Message}", ex);
                process?.Dispose();
                processCts.Dispose();
                if (ReferenceEquals(cts, processCts)) cts = null;
                return false;
            }

            if (!started)
            {
                logger.LogError("启动 scrcpy 进程失败");
                process?.Dispose();
                processCts.Dispose();
                if (ReferenceEquals(cts, processCts)) cts = null;
                return false;
            }

            // 传递设备名称和仅音频模式标志给监控方法
            // 判断是否为仅音频模式：检查设置或自定义参数中是否包含--no-video
            // 检查完整的参数列表，包括自定义参数和构建的参数
            var isAudioOnly = deviceSettings.DisableVideoForwarding || 
                              (customArgs?.Contains("--no-video") ?? false) || 
                              args.Contains("--no-video");
            StartProcessMonitoring(process, processCts, deviceSerial, device.Name, isAudioOnly);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError("StartScrcpy 中出错：{ex}", ex);
            processCts?.Dispose();
            if (ReferenceEquals(cts, processCts)) cts = null;
            process?.Dispose();
            return false;
        }
    }

    // 进程窗口映射，用于管理scrcpy进程和对应的窗口
    private Dictionary<string, (Process Process, Window? Window)> scrcpyProcessesWithWindows = [];

    private void StartProcessMonitoring(Process process, CancellationTokenSource processCts, string deviceSerial, string deviceName, bool isAudioOnly)
    {
        var errorOutput = new StringBuilder();
        
        process.OutputDataReceived += (_, e) => 
        {
            if (!string.IsNullOrEmpty(e.Data))
                logger.LogInformation($"scrcpy：{e.Data}");
        };
        
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                logger.LogError($"scrcpy 错误：{e.Data}");
                lock (errorOutput)
                {
                    errorOutput.AppendLine(e.Data);
                }
            }
        };
        
        process.Exited += (_, _) => 
        {
            logger.LogInformation("scrcpy 进程已终止");
            // 进程退出时关闭对应的窗口
            if (scrcpyProcessesWithWindows.TryGetValue(deviceSerial, out var processWithWindow))
            {
                processWithWindow.Window?.Close();
            }
        };
        
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        logger.LogInformation("scrcpy 进程已启动（pid：{pid}）", process.Id);

        Window? window = null;
        // 仅在音频模式下创建音频播放窗口
        if (isAudioOnly)
        {
            window = CreateAudioWindow(deviceName, deviceSerial, processCts);
        }
        
        // 保存进程和窗口的映射关系
        scrcpyProcessesWithWindows[deviceSerial] = (process, window);

        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync(processCts.Token);
                logger.LogInformation("scrcpy 进程退出，代码：{exitCode}", process.ExitCode);
                
                if (process.ExitCode != 0 && process.ExitCode != 2)
                {
                    string errorMessage;
                    lock (errorOutput)
                    {
                        errorMessage = $"Scrcpy 进程以代码 {process.ExitCode} 退出\n\n错误输出：\n{errorOutput.ToString().TrimEnd()}";
                    }
                    logger.LogError("scrcpy 失败：{error}", errorMessage);

                    await dispatcher!.EnqueueAsync(async () =>
                    {
                        var scrollViewer = new ScrollViewer
                        {
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                            MaxHeight = 300,
                            Content = new TextBlock
                            {
                                Text = errorMessage,
                                IsTextSelectionEnabled = true,
                                TextWrapping = TextWrapping.Wrap
                            }
                        };
                        
                        var errorDialog = new ContentDialog
                        {
                            XamlRoot = App.MainWindow.Content!.XamlRoot,
                            Title = "ScrcpyErrorTitle".GetLocalizedResource(),
                            Content = scrollViewer,
                            CloseButtonText = "Dismiss".GetLocalizedResource(),
                            SecondaryButtonText = "CopyError".GetLocalizedResource()
                        };
                        
                        var result = await errorDialog.ShowAsync();
                        if (result is ContentDialogResult.Secondary)
                        {
                            var dataPackage = new DataPackage();
                            dataPackage.SetText(errorMessage);
                            Clipboard.SetContent(dataPackage);
                            logger.LogInformation("scrcpy 错误输出已复制到剪贴板");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    logger.LogError("监控 scrcpy 进程时出错：{ex}", ex);
                }
            }
            finally
            {
                process.Dispose();
                scrcpyProcessesWithWindows.Remove(deviceSerial);

                processCts.Dispose();
                if (ReferenceEquals(cts, processCts))
                {
                    cts = null;
                }
            }
        }, processCts.Token);
    }

    private Window CreateAudioWindow(string deviceName, string deviceSerial, CancellationTokenSource processCts)
    {
        // 创建新窗口
        var window = new Window
        {
            Title = $"正在播放{deviceName}的设备声音",
            Content = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                Children = 
                {
                    new TextBlock
                    {
                        Text = $"正在播放{deviceName}的设备声音",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10)
                    }
                }
            }
        };

        // 设置窗口大小
        window.ExtendsContentIntoTitleBar = false;
        var appWindow = window.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 300, Height = 150 });

        // 窗口关闭事件处理
        window.Closed += (_, _) =>
        {
            logger.LogInformation("音频播放窗口已关闭");
            
            // 关闭窗口时终止对应的scrcpy进程
            if (scrcpyProcessesWithWindows.TryGetValue(deviceSerial, out var processWithWindow))
            {
                var process = processWithWindow.Process;
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill();
                        logger.LogInformation("已终止scrcpy进程");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("终止scrcpy进程时出错：{ex}", ex);
                    }
                }
                
                // 取消进程监控任务
                processCts.Cancel();
            }
        };

        // 显示窗口
        window.Activate();
        
        return window;
    }

    private async Task<string?> ShowDeviceSelectionDialog(List<AdbDevice> onlineDevices, string? preferredSerial = null)
    {
        string? selectedDeviceSerial = null;
        
        await dispatcher!.EnqueueAsync(async () =>
        {
            var deviceOptions = new List<ComboBoxItem>();
            foreach (var device in onlineDevices)
            {
                var displayName = device.Model ?? "Unknown";
                var item = new ComboBoxItem
                {
                    Content = $"{displayName} - {device.Type} ({device.Serial})",
                    Tag = device.Serial
                };
                deviceOptions.Add(item);
            }

            var deviceSelector = new ComboBox
            {
                ItemsSource = deviceOptions,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0
            };

            // 如果提供了首选序列号，尝试设置为选中项
            if (!string.IsNullOrEmpty(preferredSerial))
            {
                for (int i = 0; i < deviceOptions.Count; i++)
                {
                    if ((deviceOptions[i].Tag as string) == preferredSerial)
                    {
                        deviceSelector.SelectedIndex = i;
                            logger.LogDebug("[调试] 在设备选择弹窗中预选设备：{Serial}", preferredSerial);
                        break;
                    }
                }
            }

            var dialog = new ContentDialog
            {
                XamlRoot = App.MainWindow.Content!.XamlRoot,
                Title = "SelectDevice".GetLocalizedResource(),
                Content = deviceSelector,
                PrimaryButtonText = "Start".GetLocalizedResource(),
                CloseButtonText = "Cancel".GetLocalizedResource(),
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();

            if (result is ContentDialogResult.Primary && deviceSelector.SelectedItem is ComboBoxItem selected)
            {
                selectedDeviceSerial = selected.Tag as string;
            }
        });

        return selectedDeviceSerial;
    }

    private (string, string) BuildScrcpyArguments(List<string> args, string deviceSerial, IDeviceSettingsService settings)
    {

        var preDefinedArgs = settings.CustomArguments;

        if (!string.IsNullOrEmpty(preDefinedArgs))
        {
            args.Add(preDefinedArgs);
        }
        
        // General settings
        if (settings.ScreenOff)
        {
            args.Add("--turn-screen-off");
        }
        
        if (settings.PhysicalKeyboard)
        {
            args.Add("--keyboard=uhid");
        }
        
        // Video settings
        if (settings.DisableVideoForwarding)
        {
            args.Add("--no-video");
        }

        if (settings.VideoCodec != 0)
        {
            args.Add($"{adbService.VideoCodecOptions[settings.VideoCodec].Command}");
        }   
        
        if (!string.IsNullOrEmpty(settings.VideoResolution))
        {
            args.Add($"--max-size={settings.VideoResolution}");
        }
        
        if (!string.IsNullOrEmpty(settings.VideoBitrate))
        {
            args.Add($"--video-bit-rate={settings.VideoBitrate}");
        }
        
        if (!string.IsNullOrEmpty(settings.VideoBuffer))
        {
            args.Add($"--video-buffer={settings.VideoBuffer}");
        }
        
        if (!string.IsNullOrEmpty(settings.FrameRate))
        {
            args.Add($"--max-fps={settings.FrameRate}");
        }
        
        if (!string.IsNullOrEmpty(settings.Crop))
        {
            args.Add($"--crop={settings.Crop}");
        }

        if (settings.DisplayOrientation != 0)
        {
            args.Add($"--orientation={adbService.DisplayOrientationOptions[settings.DisplayOrientation].Command}");
        }
        
        if (!string.IsNullOrEmpty(settings.Display))
        {
            args.Add($"--display-id={settings.Display}");
        }

        // Audio settings
        if (!string.IsNullOrEmpty(settings.AudioBitrate))
        {
            args.Add($"--audio-bit-rate={settings.AudioBitrate}");
        }

        if (!string.IsNullOrEmpty(settings.AudioBuffer))
        {
            args.Add($"--audio-buffer={settings.AudioBuffer}");
        }

        if (!string.IsNullOrEmpty(settings.AudioOutputBuffer))
        {
            args.Add($"--audio-output-buffer={settings.AudioOutputBuffer}");
        }
        
        if (settings.ForwardMicrophone)
        {
            args.Add("--audio-source=mic");
        }

        switch (settings.AudioOutputMode)
        {
            case AudioOutputModeType.Remote:
                args.Add("--no-audio");
                break;
            case AudioOutputModeType.Both:
                args.Add("--audio-dup");
                break;
        }

        if (settings.AudioCodec != 0)
        {
            args.Add($"{adbService.AudioCodecOptions[settings.AudioCodec].Command}");
        }

        if (args[0].StartsWith ("--start-app"))
        {
            if (!string.IsNullOrEmpty(settings.VirtualDisplaySize) && settings.IsVirtualDisplayEnabled)
            {
                args.Add($"--new-display={settings.VirtualDisplaySize}");
            }
            else if (settings.IsVirtualDisplayEnabled)
            {
                args.Add("--new-display");
            }
            else if (scrcpyProcesses.Count > 0)
            {
                // Check for existing processes for this device and terminate them
                // when virtual display is not enabled
                if (scrcpyProcesses.TryGetValue(deviceSerial, out var existingProcess))
                {
                    try
                    {
                        if (!existingProcess.HasExited)
                        {
                            existingProcess.Kill();
                        }
                        scrcpyProcesses.Remove(deviceSerial);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"终止现有进程失败：{ex.Message}", ex);
                    }
                }
            }
        }

        return (string.Join(" ", args), deviceSerial);
    }

    public async Task<string> SelectScrcpyLocationClick()
    {
        var file = await PickerHelper.PickFileAsync();
        if (file?.Path is string path)
        {
            userSettingsService.GeneralSettingsService.ScrcpyPath = path;
            GeneralPage.TrySetCompanionTool(path, "adb.exe", p => userSettingsService.GeneralSettingsService.AdbPath = p);
            await adbService.StartAsync();
            return path;
        }
        return string.Empty;
    }

    private async Task<string?> ShowPasswordInputDialog()
    {
        string? password = null;
        
        await dispatcher!.EnqueueAsync(async () =>
        {
            var dialog = new PasswordInputDialog
            {
                XamlRoot = App.MainWindow.Content!.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                password = dialog.Password;
            }
        });

        return password;
    }

    private string? GetCachedPassword(string deviceId, int currentTimeout)
    {
        if (passwordCache.TryGetValue(deviceId, out var cacheEntry))
        {
            var (password, cachedAt, cachedTimeout) = cacheEntry;

            if (currentTimeout == cachedTimeout && DateTime.Now <= cachedAt.AddMinutes(cachedTimeout))
            {
                return password;
            }
            passwordCache.Remove(deviceId);
        }
        
        return null;
    }

    private void CachePassword(string deviceId, string password, int timeoutMinutes)
    {
        passwordCache[deviceId] = (password, DateTime.Now, timeoutMinutes);
    }
}
