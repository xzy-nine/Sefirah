using System.Threading.Channels;
using Sefirah.Platforms.Windows.Helpers;
using Sefirah.Platforms.Windows.Interop;
using Sefirah.Platforms.Windows.Interop.Extensions;
using Sefirah.Platforms.Windows.RemoteStorage.Abstractions;
using Sefirah.Platforms.Windows.RemoteStorage.RemoteAbstractions;
using Sefirah.Platforms.Windows.RemoteStorage.Worker;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using FileAttributes = System.IO.FileAttributes;

namespace Sefirah.Platforms.Windows.RemoteStorage.Worker.IO;
public class ClientWatcher : IDisposable
{
    private readonly ISyncProviderContextAccessor _contextAccessor;
    private readonly ChannelWriter<Func<Task>> _taskWriter;
    private readonly FileLocker _fileLocker;
    private readonly IRemoteReadWriteService _remoteService;
    private readonly PlaceholdersService _placeholdersService;
    private readonly ILogger _logger;
    private readonly FileSystemWatcher _watcher;

    private string _rootDirectory => _contextAccessor.Context.RootDirectory;

    public ClientWatcher(
        ISyncProviderContextAccessor contextAccessor,
        ChannelWriter<Func<Task>> taskWriter,
        FileLocker fileLocker,
        IRemoteReadWriteService remoteService,
        PlaceholdersService placeholdersService,
        ILogger logger
    )
    {
        _contextAccessor = contextAccessor;
        _taskWriter = taskWriter;
        _fileLocker = fileLocker;
        _remoteService = remoteService;
        _placeholdersService = placeholdersService;
        _logger = logger;
        _watcher = CreateWatcher();
    }

    private FileSystemWatcher CreateWatcher()
    {
        var watcher = new FileSystemWatcher(_rootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.Attributes
                | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };

        watcher.Changed += async (sender, e) => {
            try 
            {
                if (e.ChangeType != WatcherChangeTypes.Changed || 
                    !Path.Exists(e.FullPath) || 
                    FileHelper.IsSystemFile(e.FullPath))
                {
                    return;
                }

                var fileInfo = new FileInfo(e.FullPath);
                
                CF_PLACEHOLDER_STATE state;
                try 
                {
                    state = CloudFilter.GetPlaceholderState(e.FullPath);
                }
                catch (HFileException)
                {
                    _logger.LogWarning("无法获取占位符状态：{path}（连接可能已丢失）", e.FullPath);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "获取占位符状态时出错：{path}", e.FullPath);
                    return;
                }

                // More specific conditions for when to skip the change event
                if (state.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC) ||
                    state.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER) ||
                    fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    fileInfo.LastWriteTime == fileInfo.LastAccessTime ||
                    e.ChangeType == WatcherChangeTypes.Changed && 
                    (state == CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_NO_STATES || 
                     state == (CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER | CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC)))
                {
                    return;
                }

                await _taskWriter.WriteAsync(async () => {
                    var relativePath = PathMapper.GetRelativePath(e.FullPath, _rootDirectory);
                    using var locker = await _fileLocker.Lock(relativePath);

                    if (fileInfo.Attributes.HasAllSyncFlags(SyncAttributes.PINNED | (int)FileAttributes.Offline))
                    {
                        if (fileInfo.Attributes.HasFlag(FileAttributes.Directory))
                        {
                            try
                            {
                                _placeholdersService.CreateBulk(relativePath);
                                var childItems = Directory.EnumerateFiles(e.FullPath, "*", SearchOption.AllDirectories)
                                    .Where((x) => !FileHelper.IsSystemFile(x))
                                    .ToArray();
                                foreach (var childItem in childItems)
                                {
                                    try
                                    {
                                        CloudFilter.HydratePlaceholder(childItem);
                                    }
                                    catch (HFileException)
                                    {
                                        _logger.LogWarning("无法为 {path} 执行 hydrate 操作（连接可能已丢失）", e.FullPath);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Hydrate 文件失败：{filePath}", childItem);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "处理目录 {path} 时出错", e.FullPath);
                            }
                        }
                        else
                        {
                            try
                            {
                                CloudFilter.HydratePlaceholder(e.FullPath);
                            }
                            catch (HFileException)
                            {
                                _logger.LogWarning("无法为 {path 执行 hydrate 操作（连接可能已丢失）", e.FullPath);
                            }
                        }
                    }
                    else if (
                        fileInfo.Attributes.HasAnySyncFlag(SyncAttributes.UNPINNED)
                        && !fileInfo.Attributes.HasFlag(FileAttributes.Offline)
                        && !fileInfo.Attributes.HasFlag(FileAttributes.Directory)
                    )
                    {
                        try
                        {
                            CloudFilter.DehydratePlaceholder(e.FullPath, relativePath, fileInfo.Length);
                        }
                        catch (HFileException)
                        {
                            _logger.LogWarning("无法为 {path} 执行 dehydrate 操作（连接可能已丢失）", e.FullPath);
                        }
                    }

                    if (fileInfo.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        //var directoryInfo = new DirectoryInfo(e.FullPath);
                        //await _serverService.UpdateDirectory(directoryInfo, relativePath);
                    }
                    else
                    {
                        await _remoteService.UpdateFile(fileInfo, relativePath);
                    }
                    
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "文件系统监视器在 {path} 出现未处理的错误", e.FullPath);
            }
        };

        watcher.Created += async (sender, e) => {

            var state = CloudFilter.GetPlaceholderState(e.FullPath);
            if (state.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC))
            {
                return;
            }

            await _taskWriter.WriteAsync(async () => {
                var relativePath = PathMapper.GetRelativePath(e.FullPath, _rootDirectory);
                using var locker = await _fileLocker.Lock(relativePath);

                if (File.GetAttributes(e.FullPath).HasFlag(FileAttributes.Directory))
                {
                    var directoryInfo = new DirectoryInfo(e.FullPath);
                    await _remoteService.CreateDirectory(directoryInfo, relativePath);
                    var childItems = Directory.EnumerateFiles(e.FullPath, "*", SearchOption.AllDirectories)
                        .Where((x) => !FileHelper.IsSystemFile(x))
                        .ToArray();
                    foreach (var childItem in childItems)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(childItem);
                            await _remoteService.CreateFile(fileInfo, childItem);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "创建文件失败：{filePath}", childItem);
                        }
                    }
                }
                else
                {
                    try
                    {
                        var fileInfo = new FileInfo(e.FullPath);
                        await _remoteService.CreateFile(fileInfo, relativePath);

                        // Add explicit placeholder and sync state handling with delays
                        try
                        {
                            _logger.LogInformation("正在为新文件设置占位符状态：{path}", e.FullPath);
                            
                            if (!CloudFilter.IsPlaceholder(e.FullPath))
                            {
                                CloudFilter.ConvertToPlaceholder(e.FullPath);
                                _logger.LogInformation("已转换为占位符并更新：{path}", e.FullPath);
                                
                                // Give time for the placeholder conversion to settle
                                await Task.Delay(1000);
                            }

                            var stateAfterPlaceholder = CloudFilter.GetPlaceholderState(e.FullPath);
                            _logger.LogInformation("占位符转换后状态：{state}，文件：{path}", 
                                stateAfterPlaceholder, e.FullPath);

                            // Set sync state and wait for it to settle
                            CloudFilter.SetInSyncState(e.FullPath);
                            await Task.Delay(1000);  // Wait for state change to complete

                            var finalState = CloudFilter.GetPlaceholderState(e.FullPath);
                            _logger.LogInformation("同步完成后最终状态：{state}，文件：{path}", 
                                finalState, e.FullPath);

                            // One final check to ensure we don't trigger another upload
                            if (!finalState.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC))
                            {
                                _logger.LogWarning("同步状态未正确设置，正在重试：{path}", e.FullPath);
                                CloudFilter.SetInSyncState(e.FullPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "设置占位符/同步状态失败：{path}", e.FullPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "创建文件失败：{filePath}", e.FullPath);
                    }
                }
            });
        };

        watcher.Error += (sender, e) => {
            var ex = e.GetException();
            _logger.LogError(ex, "客户端文件监视器错误");
        };

        return watcher;
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }
}
