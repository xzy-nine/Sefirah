using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sefirah.Data.Models;

/// <summary>
/// 音乐媒体块数据模型
/// 用于在通知列表顶部显示音乐播放信息
/// </summary>
public class MusicMediaBlock : INotifyPropertyChanged
{
    private string _deviceId;
    private string _deviceName;
    private string _title;
    private string _text;
    private string? _coverUrl;
    private DateTime _lastUpdateTime;
    private bool _isVisible;

    /// <summary>
    /// 设备ID，作为特征值
    /// </summary>
    public string DeviceId
    {
        get => _deviceId;
        set
        { 
            _deviceId = value; 
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 设备名称
    /// </summary>
    public string DeviceName
    {
        get => _deviceName;
        set
        { 
            _deviceName = value; 
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 音乐标题
    /// </summary>
    public string Title
    {
        get => _title;
        set
        { 
            _title = value; 
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 音乐艺术家和专辑信息
    /// </summary>
    public string Text
    {
        get => _text;
        set
        { 
            _text = value; 
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 音乐封面URL（Data URL格式）
    /// </summary>
    public string? CoverUrl
    {
        get => _coverUrl;
        set
        { 
            _coverUrl = value; 
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime LastUpdateTime
    {
        get => _lastUpdateTime;
        set
        { 
            _lastUpdateTime = value; 
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 是否可见
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        { 
            _isVisible = value; 
            OnPropertyChanged();
        }
    }
    
    /// <summary>
    /// 前一首命令参数
    /// </summary>
    public string PreviousCommandParameter => $"{DeviceId}:previous";
    
    /// <summary>
    /// 播放/暂停命令参数
    /// </summary>
    public string PlayPauseCommandParameter => $"{DeviceId}:playPause";
    
    /// <summary>
    /// 下一首命令参数
    /// </summary>
    public string NextCommandParameter => $"{DeviceId}:next";

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="deviceId">设备ID</param>
    /// <param name="deviceName">设备名称</param>
    /// <param name="title">音乐标题</param>
    /// <param name="text">音乐艺术家和专辑信息</param>
    /// <param name="coverUrl">音乐封面URL</param>
    public MusicMediaBlock(string deviceId, string deviceName, string title, string text, string? coverUrl = null)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Title = title;
        Text = text;
        CoverUrl = coverUrl;
        LastUpdateTime = DateTime.Now;
        IsVisible = true;
    }

    /// <summary>
    /// 更新音乐媒体块信息
    /// </summary>
    /// <param name="title">音乐标题</param>
    /// <param name="text">音乐艺术家和专辑信息</param>
    /// <param name="coverUrl">音乐封面URL</param>
    public void Update(string title, string text, string? coverUrl = null)
    {
        Title = title;
        Text = text;
        if (!string.IsNullOrEmpty(coverUrl))
        {
            CoverUrl = coverUrl;
        }
        LastUpdateTime = DateTime.Now;
        IsVisible = true;
    }

    /// <summary>
    /// 检查是否超时
    /// </summary>
    /// <param name="timeoutSeconds">超时时间（秒）</param>
    /// <returns>是否超时</returns>
    public bool IsTimeout(int timeoutSeconds = 10)
    {
        TimeSpan elapsed = DateTime.Now - LastUpdateTime;
        return elapsed.TotalSeconds > timeoutSeconds;
    }

    /// <summary>
    /// 属性变更事件
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 触发属性变更事件
    /// </summary>
    /// <param name="propertyName">属性名称</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}