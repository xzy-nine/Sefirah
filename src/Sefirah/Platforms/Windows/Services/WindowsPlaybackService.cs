using System.Runtime.InteropServices;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Utils;
using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using Sefirah.Platforms.Windows.Interop;
using Sefirah.Utils.Serialization;
using Windows.Media;
using Windows.Media.Control;

namespace Sefirah.Platforms.Windows.Services;
public class WindowsPlaybackService(
    ILogger<WindowsPlaybackService> logger,
    ISessionManager sessionManager,
    IDeviceManager deviceManager) : IPlaybackService, IMMNotificationClient
{
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> activeSessions = [];
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    public List<AudioDevice> AudioDevices { get; private set; } = [];
    private readonly MMDeviceEnumerator enumerator = new();

    private readonly Dictionary<string, double> lastTimelinePosition = [];
    private readonly Dictionary<string, DateTime> lastSessionUpdateTime = [];
    private const int MinUpdateIntervalMs = 5000; // 最小更新间隔，5秒
    
    // 媒体播放状态跟踪，用于实现差异包发送
    private readonly Dictionary<string, MediaPlayState> lastMediaState = [];
    
    // 媒体播放状态数据类，用于跟踪上次发送的媒体状态
    private class MediaPlayState
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Thumbnail { get; set; }
        public DateTime SentTime { get; set; }
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (manager is null)
            {
                logger.LogError("初始化系统媒体传输控制会话管理器失败");
                return;
            }

            GetAllAudioDevices();
            UpdateActiveSessions();

            enumerator.RegisterEndpointNotificationCallback(this);

            manager.SessionsChanged += SessionsChanged;

            sessionManager.ConnectionStatusChanged += async (sender, args) =>
            {
                //if (args.IsConnected && args.Device.DeviceSettings?.MediaSessionSyncEnabled == true)
                //{
                    //foreach (var session in activeSessions.Values)
                    //{
                    //    await UpdatePlaybackDataAsync(session);
                    //}
                    //foreach (var device in AudioDevices)
                    //{
                    //    device.AudioDeviceType = AudioMessageType.New;
                    //    string jsonMessage = SocketMessageSerializer.Serialize(device);
                    //    sessionManager.SendMessage(args.Device.Id, jsonMessage);
                   // }
                //}
            };

            // 定期发送媒体消息（每9秒发送一次）
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(9));
                    try
                    {
                        // 获取当前活跃的媒体会话
                        var currentSession = manager.GetCurrentSession();
                        if (currentSession != null && activeSessions.ContainsKey(currentSession.SourceAppUserModelId))
                        {
                            // 定期发送媒体数据，与Android端保持一致
                            await UpdatePlaybackDataAsync(currentSession);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "定期更新播放数据时出错");
                    }
                }
            });

            logger.LogInformation("播放服务初始化成功");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "初始化播放服务失败");
        }
    }

    public async Task HandleMediaActionAsync(PlaybackAction mediaAction)
    {
        // 尝试根据Source字段查找对应的媒体会话
        var session = activeSessions.Values.FirstOrDefault(s => s.SourceAppUserModelId == mediaAction.Source);
        
        // 如果找不到匹配的会话，或者Source是"MediaControl"（来自外部设备的控制指令），则使用当前活动的媒体会话
        if (session == null || mediaAction.Source == "MediaControl")
        {
            session = manager?.GetCurrentSession();
        }

        // 检查是否是本应用自身的媒体会话，如果是则不执行控制指令
        if (session != null)
        {
            // 获取当前进程的名称，用于识别本应用的媒体会话
            string currentProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            
            // 检查媒体会话的SourceAppUserModelId是否包含当前进程名称
            // 如果包含则认为是本应用自身的媒体会话，不执行控制指令
            if (!string.IsNullOrEmpty(session.SourceAppUserModelId) && 
                session.SourceAppUserModelId.Contains(currentProcessName, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("忽略对本应用自身媒体会话的控制指令");
                return;
            }
        }

        await ExecuteSessionActionAsync(session, mediaAction);
    }

    private async Task ExecuteSessionActionAsync(GlobalSystemMediaTransportControlsSession? session, PlaybackAction mediaAction)
    {
        await dispatcher.EnqueueAsync(async () =>
        {
            try
            {
                switch (mediaAction.PlaybackActionType)
                {
                    case PlaybackActionType.Play:
                        // 如果是来自外部设备的playPause指令（Source为"MediaControl"），则根据当前状态切换播放/暂停
                        if (mediaAction.Source == "MediaControl")
                        {
                            var playbackInfo = session?.GetPlaybackInfo();
                            if (playbackInfo != null)
                            {
                                if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                                {
                                    await session?.TryPauseAsync();
                                }
                                else
                                {
                                    await session?.TryPlayAsync();
                                }
                            }
                            else
                            {
                                // 如果无法获取播放状态，默认尝试播放
                                await session?.TryPlayAsync();
                            }
                        }
                        else
                        {
                            // 普通Play指令，直接播放
                            await session?.TryPlayAsync();
                        }
                        break;
                    case PlaybackActionType.Pause:
                        await session?.TryPauseAsync();
                        break;
                    case PlaybackActionType.Next:
                        await session?.TrySkipNextAsync();
                        break;
                    case PlaybackActionType.Previous:
                        await session?.TrySkipPreviousAsync();
                        break;
                    case PlaybackActionType.Seek:
                        if (mediaAction.Value.HasValue)
                        {
                            // We need to use Ticks here
                            TimeSpan position = TimeSpan.FromMilliseconds(mediaAction.Value.Value);
                            await session?.TryChangePlaybackPositionAsync(position.Ticks);
                        }
                        break;
                    case PlaybackActionType.Shuffle:
                        await session?.TryChangeShuffleActiveAsync(true);
                        break;
                    case PlaybackActionType.Repeat:
                        if (mediaAction.Value.HasValue)
                        {
                            if (mediaAction.Value == 1.0)
                            {
                                await session?.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.Track);
                            }
                            else if (mediaAction.Value == 2.0)
                            {
                                await session?.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.List);
                            }
                        }
                        break;
                    case PlaybackActionType.DefaultDevice:
                        SetDefaultAudioDevice(mediaAction.Source);
                        break;
                    case PlaybackActionType.VolumeUpdate:
                        if (mediaAction.Value.HasValue)
                        {
                            SetVolume(mediaAction.Source, Convert.ToSingle(mediaAction.Value.Value));
                        }
                        break;
                    case PlaybackActionType.ToggleMute:
                        ToggleMute(mediaAction.Source);
                        break;
                    default:
                        logger.LogWarning("未处理的媒体操作：{PlaybackActionType}", mediaAction.PlaybackActionType);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "执行媒体操作时出错：{PlaybackActionType}", mediaAction.PlaybackActionType);
            }
        });
    }

    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager manager, SessionsChangedEventArgs args)
    {
        UpdateSessionsList(manager.GetSessions());
    }

    private void UpdateActiveSessions()
    {
        if (manager is null) return;

        try
        {
            var activeSessions = manager.GetSessions();
            UpdateSessionsList(activeSessions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新活动会话时出错");
        }
    }

    private void UpdateSessionsList(IReadOnlyList<GlobalSystemMediaTransportControlsSession> activeSessions)
    {
        lock (this.activeSessions)
        {
            var currentSessionIds = new HashSet<string>(activeSessions.Select(s => s.SourceAppUserModelId));

            foreach (var sessionId in this.activeSessions.Keys.ToList())
            {
                if (!currentSessionIds.Contains(sessionId))
                {
                    RemoveSession(sessionId);
                }
            }

            foreach (var session in activeSessions.Where(s => s is not null))
            {
                if (!this.activeSessions.ContainsKey(session.SourceAppUserModelId))
                {
                    AddSession(session);
                }
            }
        }
    }

    private void RemoveSession(string sessionId)
    {
        if (activeSessions.TryGetValue(sessionId, out var session))
        {
            activeSessions.Remove(sessionId);
            UnsubscribeFromSessionEvents(session);
        }
    }

    private void AddSession(GlobalSystemMediaTransportControlsSession session)
    {
        if (!activeSessions.ContainsKey(session.SourceAppUserModelId))
        {
            activeSessions[session.SourceAppUserModelId] = session;
            lastTimelinePosition[session.SourceAppUserModelId] = 0;
            SubscribeToSessionEvents(session);
        }
    }

    private void SubscribeToSessionEvents(GlobalSystemMediaTransportControlsSession session)
    {
        session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
        session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
    }

    private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        try
        {
            if (!activeSessions.ContainsKey(sender.SourceAppUserModelId)) return;
            var timelineProperties = sender.GetTimelineProperties();
            var isCurrentSession = manager?.GetCurrentSession()?.SourceAppUserModelId == sender.SourceAppUserModelId;

            if (timelineProperties is null || !isCurrentSession) return;

            if (lastTimelinePosition.TryGetValue(sender.SourceAppUserModelId, out var lastPosition))
            {
                double currentPosition = timelineProperties.Position.TotalMilliseconds;
                if (Math.Abs(currentPosition - lastPosition) < 1000) return; // Ignore minor changes under 1 second

                lastTimelinePosition[sender.SourceAppUserModelId] = currentPosition;

                // 时间线变化时只发送位置信息，不发送完整媒体信息以减少网络流量
                // 如果需要完整信息，会通过MediaPropertiesChanged事件发送
                var message = new PlaybackSession
                {
                    SessionType = SessionType.TimelineUpdate,
                    Source = sender.SourceAppUserModelId,
                    Position = currentPosition
                };
                SendPlaybackData(message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理时间线属性时出错：{SourceAppUserModelId}", sender.SourceAppUserModelId);
        }
    }

    private void UnsubscribeFromSessionEvents(GlobalSystemMediaTransportControlsSession session)
    {
        session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
        session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
        session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        lastTimelinePosition.Remove(session.SourceAppUserModelId);

        // 发送媒体结束通知，使用与Android兼容的DATA_MEDIAPLAY格式
        foreach (var device in deviceManager.PairedDevices)
        {
            if (device.ConnectionStatus && device.DeviceSettings.MediaSessionSyncEnabled)
            {
                // 构造与Android端buildMediaPlayEndPayload方法保持一致的媒体结束包
                string appName = session.SourceAppUserModelId ?? "Unknown App";
                string packageName = session.SourceAppUserModelId ?? "Unknown Package";
                long time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                
                // 构建JSON对象，与Android端保持一致
                var endPayload = new
                {
                    packageName = packageName,
                    appName = appName,
                    time = time,
                    isLocked = false, // PC端无法获取锁屏状态，默认设为false
                    type = "MEDIA_PLAY",
                    mediaType = "END", // 结束包标记
                    terminateValue = "__END__", // 统一结束标识
                    featureKeyName = "si_feature_id", // 与超级岛保持一致
                    featureKeyValue = "media_island_global" // 媒体会话全局特征ID
                };
                
                // 序列化为JSON字符串
                string endPayloadJson = JsonSerializer.Serialize(endPayload);
                
                // 使用NetworkService的SendRequest方法直接发送媒体结束包，使用DATA_MEDIAPLAY协议头
                var networkService = Ioc.Default.GetRequiredService<INetworkService>();
                networkService.SendMessage(device.Id, endPayloadJson);
            }
        }
    }

    private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        try
        {
            logger.LogInformation("媒体属性已变更：{SourceAppUserModelId}", sender.SourceAppUserModelId);
            await UpdatePlaybackDataAsync(sender);

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新播放数据时出错：{SourceAppUserModelId}", sender.SourceAppUserModelId);
        }
    }

    private async void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        try
        {
            // 播放状态变化时，获取完整的媒体信息
            await UpdatePlaybackDataAsync(sender);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新播放数据时出错：{SourceAppUserModelId}", sender.SourceAppUserModelId);
        }
    }

    private async Task UpdatePlaybackDataAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            await dispatcher.EnqueueAsync(async () =>
            {

                var playbackSession = await GetPlaybackSessionAsync(session);
                if (playbackSession is null || !activeSessions.ContainsKey(session.SourceAppUserModelId)) return;

                SendPlaybackData(playbackSession);
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新播放数据时出错：{SourceAppUserModelId}", session.SourceAppUserModelId);
        }
    }

    private async Task<PlaybackSession?> GetPlaybackSessionAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            // 只获取Android端需要的媒体字段
            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            var timelineProperties = session.GetTimelineProperties();
            
            lastTimelinePosition[session.SourceAppUserModelId] = timelineProperties.Position.TotalMilliseconds;

            var playbackSession = new PlaybackSession
            {
                Source = session.SourceAppUserModelId,
                TrackTitle = mediaProperties.Title,
                Artist = mediaProperties.Artist ?? "Unknown Artist"
            };

            // 只获取封面图片，其他字段不需要
            if (mediaProperties.Thumbnail is not null)
                playbackSession.Thumbnail = await mediaProperties.Thumbnail.ToBase64Async();

            return playbackSession;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取播放数据时出错：{SourceAppUserModelId}", session.SourceAppUserModelId);
            return null;
        }
    }


    private void SendPlaybackData(PlaybackSession playbackSession)
    {
        try
        {
            // 生成唯一键，用于区分不同会话的不同消息类型
            string key = $"{playbackSession.Source}|{playbackSession.SessionType}";
            
            // 检查是否需要节流
            if (lastSessionUpdateTime.TryGetValue(key, out var lastTime))
            {
                var elapsed = DateTime.Now - lastTime;
                if (elapsed.TotalMilliseconds < MinUpdateIntervalMs)
                {
                    // 发送频率过高，跳过本次发送
                    return;
                }
            }
            
            // 更新最后发送时间
            lastSessionUpdateTime[key] = DateTime.Now;
            
            // 获取NetworkService，用于发送与Android兼容的媒体会话
            var networkService = Ioc.Default.GetRequiredService<INetworkService>();
            
            // 构造当前媒体状态
            var currentState = new MediaPlayState
            {
                Title = playbackSession.TrackTitle,
                Artist = playbackSession.Artist,
                Thumbnail = playbackSession.Thumbnail,
                SentTime = DateTime.Now
            };
            
            // 获取上次发送的媒体状态
            bool sendFullPayload = true;
            string title = playbackSession.TrackTitle ?? string.Empty;
            string artist = playbackSession.Artist ?? string.Empty;
            string text = string.Empty;
            string coverUrl = playbackSession.Thumbnail ?? string.Empty;
            
            if (lastMediaState.TryGetValue(playbackSession.Source, out var oldState))
            {
                // 计算差异
                bool titleChanged = oldState.Title != currentState.Title;
                bool artistChanged = oldState.Artist != currentState.Artist;
                bool coverChanged = oldState.Thumbnail != currentState.Thumbnail;
                
                // 判断是否需要发送全量包：首次发送、封面变化或超过15秒
                var now = DateTime.Now;
                sendFullPayload = coverChanged || (now - oldState.SentTime).TotalSeconds > 15;
                
                // 如果只发送差异包，只包含变化的字段
                if (!sendFullPayload)
                {
                    // 差异包：只包含变化的字段，没有变化的字段使用空字符串
                    if (!titleChanged)
                    {
                        title = string.Empty; // 差异包中不包含未变化的标题
                    }
                    if (!artistChanged)
                    {
                        artist = string.Empty; // 差异包中不包含未变化的艺术家
                    }
                    if (!coverChanged)
                    {
                        coverUrl = string.Empty; // 差异包中不包含未变化的封面，使用空字符串而非null
                    }
                }
            }
            
            // 更新上次发送的媒体状态
            lastMediaState[playbackSession.Source] = currentState;
            
            // 构建文本内容
            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artist))
            {
                text = $"{artist} - {title}";
            }
            else if (!string.IsNullOrEmpty(title))
            {
                text = title;
            }
            else if (!string.IsNullOrEmpty(artist))
            {
                text = artist;
            }
            
            // 修复appName提取逻辑：如果Source是"QQMusic.exe"，则整个作为appName
            string appName = playbackSession.Source ?? "Unknown App";
            
            foreach (var device in deviceManager.PairedDevices)
            {
                if (device.ConnectionStatus && device.DeviceSettings.MediaSessionSyncEnabled)
                {
                    // 构造与Android兼容的NotificationMessage
                    var notificationMessage = new NotificationMessage
                    {
                        NotificationKey = Guid.NewGuid().ToString(),
                        TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                        NotificationType = NotificationType.New,
                        AppPackage = playbackSession.Source,
                        AppName = appName,
                        Title = title,
                        Text = text,
                        CoverUrl = coverUrl, // 始终包含封面URL，避免安卓端清理图标
                    };
                    
                    // 使用NetworkService发送与Android兼容的媒体会话，传递正确的mediaType参数
                    string mediaType = sendFullPayload ? "FULL" : "DELTA";
                    networkService.SendMediaPlayNotification(device.Id, notificationMessage, mediaType);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送播放数据时出错");
        }
    }

    public Task HandleRemotePlaybackMessageAsync(PlaybackSession data)
    {
        throw new NotImplementedException();
    }

    public void GetAllAudioDevices()
    {
        try
        {
            // Get the default device
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;

            // List all active devices
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                AudioDevices.Add(
                    new AudioDevice
                    {
                        DeviceId = device.ID,
                        DeviceName = device.FriendlyName,
                        Volume = device.AudioEndpointVolume.MasterVolumeLevelScalar,
                        IsMuted = device.AudioEndpointVolume.Mute,
                        IsSelected = device.ID == defaultDevice
                    }
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "枚举音频设备失败");
        }
    }

    public void ToggleMute(string deviceId)
    {
        try
        {
            var endpoint = enumerator.GetDevice(deviceId);
            if (endpoint is null || endpoint.State != DeviceState.Active) return;

            try
            {
                endpoint.AudioEndpointVolume.Mute = !endpoint.AudioEndpointVolume.Mute;
            }
            catch (COMException comEx) when (comEx.HResult == unchecked((int)0x8007001F))
            {
                logger.LogWarning("设备 {DeviceId} 在静音时无法正常工作", deviceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "静音设备 {DeviceId} 时出错", deviceId);
        }
    }

    public void SetVolume(string deviceId, float volume)
    {
        try
        {
            var endpoint = enumerator.GetDevice(deviceId);
            if (endpoint is null || endpoint.State is not DeviceState.Active) return;

            try
            {
                endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
            }
            catch (COMException comEx) when (comEx.HResult == unchecked((int)0x8007001F))
            {
                logger.LogWarning("设备 {DeviceId} 在设置音量时无法正常工作", deviceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "为设备 {DeviceId} 设置音量 {Volume} 时出错", volume, deviceId);
        }
    }


    public void SetDefaultAudioDevice(string deviceId)
    {
        object? policyConfigObject = null;
        try
        {
            Type? policyConfigType = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"));
            if (policyConfigType is null) return; 

            policyConfigObject = Activator.CreateInstance(policyConfigType);
            if (policyConfigObject is null) return;

            if (policyConfigObject is not IPolicyConfig policyConfig) return;

            int result1 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            int result2 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
            int result3 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);

            if (result1 != HResult.S_OK || result2 != HResult.S_OK || result3 != HResult.S_OK)
            {
                logger.LogError("SetDefaultEndpoint 返回错误代码：{Result1}, {Result2}, {Result3}", result1, result2, result3);
                return;
            }

            var index = AudioDevices.FindIndex(d => d.DeviceId == deviceId);

            if (index != -1)
            {
                AudioDevices.First().IsSelected = false;
                AudioDevices[index].IsSelected = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "设置默认设备时出错");
            return;
        }
        finally
        {
            if (policyConfigObject is not null)
            {
                Marshal.ReleaseComObject(policyConfigObject);
            }
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        logger.LogInformation("设备状态改变：{DeviceId} - {NewState}", deviceId, newState);
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        AudioDevices.Add(
            new AudioDevice
            {
                AudioDeviceType = AudioMessageType.New,
                DeviceId = pwstrDeviceId,
                DeviceName = enumerator.GetDevice(pwstrDeviceId).FriendlyName,
                Volume = enumerator.GetDevice(pwstrDeviceId).AudioEndpointVolume.MasterVolumeLevelScalar,
                IsMuted = enumerator.GetDevice(pwstrDeviceId).AudioEndpointVolume.Mute,
                IsSelected = false
            }
        );
        logger.LogInformation("设备已添加：{DeviceId}", pwstrDeviceId);
    }

    public void OnDeviceRemoved(string deviceId)
    {
        AudioDevices.RemoveAll(d => d.DeviceId == deviceId);
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        var index = AudioDevices.FindIndex(d => d.DeviceId == defaultDeviceId);

        if (index != -1)
        {
            var selectedIndex = AudioDevices.FindIndex(d => d.IsSelected == true);
            AudioDevices[selectedIndex].IsSelected = false;
            AudioDevices[index].IsSelected = true;
            logger.LogInformation("默认设备已更改：{DefaultDeviceId}", defaultDeviceId);
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        AudioDevice? device = AudioDevices.FirstOrDefault(d => d.DeviceId == pwstrDeviceId);
        device?.Volume = enumerator.GetDevice(pwstrDeviceId).AudioEndpointVolume.MasterVolumeLevelScalar;
    }
}
