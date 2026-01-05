using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sefirah.Platforms.Windows.Abstractions;
using Sefirah.Platforms.Windows.Helpers;
using Sefirah.Platforms.Windows.RemoteStorage.Abstractions;
using Sefirah.Platforms.Windows.RemoteStorage.RemoteAbstractions;

namespace Sefirah.Platforms.Windows.RemoteStorage.Sftp;
public sealed class SftpWatcher(
    ISyncProviderContextAccessor syncContextAccessor,
    ISftpContextAccessor contextAccessor,
    SftpClient client,
    ILogger logger
) : IRemoteWatcher
{
    private readonly SyncProviderContext _syncContext = syncContextAccessor.Context;
    private readonly SftpContext _context = contextAccessor.Context;
    private readonly string[] _relativeDirectoryNames = [".", "..", "#Recycle"];
    private Dictionary<string, DateTime> _knownFiles = [];
    private bool _running = false;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public event RemoteCreateHandler? Created;
    public event RemoteChangeHandler? Changed;
    // 未在此类内部触发 `Renamed` 事件：重命名检测由上层 Worker（RemoteWatcher）负责。
    // 保留此事件以满足 `IRemoteWatcher` 接口契约并允许外部订阅。
    #pragma warning disable CS0067 // event is declared for interface implementation and used externally
    public event RemoteRenameHandler? Renamed;
    #pragma warning restore CS0067
    public event RemoteDeleteHandler? Deleted;

    public async void Start(CancellationToken stoppingToken = default)
    {
        ObjectDisposedException.ThrowIf(_cancellationTokenSource.IsCancellationRequested, this);
        if (_running)
        {
            throw new Exception("Already running");
        }
        _running = true;

        try
        {
            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _cancellationTokenSource.Token);
            while (!linkedTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (!client.IsConnected)
                    {
                        await TryReconnectAsync(linkedTokenSource.Token);
                        if (!client.IsConnected)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), linkedTokenSource.Token);
                            continue;
                        }
                    }

                    var foundFiles = IsHydrated(_context.Directory)
                                    ? FindFiles(_context.Directory)
                                    : [];

                    if (client.IsConnected)
                    {
                        var removedFiles = _knownFiles.Keys.Except(foundFiles.Keys).ToArray();
                        foreach (var removedFile in removedFiles)
                        {
                            Deleted?.Invoke(removedFile);
                        }

                        var addedFiles = foundFiles.Keys.Except(_knownFiles.Keys).ToArray();
                        foreach (var addedFile in addedFiles)
                        {
                            Created?.Invoke(addedFile);
                        }

                        var updatedFiles = foundFiles
                            .Where((pair) => _knownFiles.ContainsKey(pair.Key) && _knownFiles[pair.Key] < pair.Value)
                            .Select(pair => pair.Key)
                            .ToArray();
                        foreach (var updatedFile in updatedFiles)
                        {
                            Changed?.Invoke(updatedFile);
                        }

                        _knownFiles = foundFiles;
                    }

                    try
                    {
                        await Task.Delay(_context.WatchPeriodSeconds * 1000, linkedTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SshConnectionException ex)
                {
                    logger.LogError("SSH 连接错误", ex);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError("SFTP 监视器发生意外错误", ex);
                    break;
                }
            }
        }
        finally
        {
            _running = false;
        }
    }

    private Dictionary<string, DateTime> FindFiles(string directory)
    {
        if (!client?.IsConnected ?? true)
        {
            logger.LogWarning("SFTP 客户端在 FindFiles 中未连接");
            return _knownFiles;
        }

        try
        {
            var sftpFiles = client?.ListDirectory(directory);
            if (sftpFiles == null)
            {
                logger.LogWarning("ListDirectory 为 {directory} 返回空", directory);
                return _knownFiles; 
            }

            // Get all directories first, excluding system directories
            var directories = sftpFiles
                .Where(sftpFile => sftpFile.IsDirectory && 
                                  !_relativeDirectoryNames.Contains(sftpFile.Name) &&
                                  !FileHelper.IsSystemDirectory(PathMapper.GetRelativePath(sftpFile.FullName, _context.Directory)))
                .ToDictionary(
                    dir => dir.FullName,
                    _ => DateTime.MaxValue
                );

            // Get files from current directory
            var files = sftpFiles
                .Where(sftpFile => sftpFile.IsRegularFile)
                .ToDictionary(
                    file => file.FullName,
                    file => file.LastWriteTimeUtc
                );

            // Recursively get files from hydrated subdirectories
            var subFiles = directories.Keys
                .Where(IsHydrated) 
                .SelectMany(FindFiles)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value
                );

            return directories
                .Concat(files)
                .Concat(subFiles)
                .ToDictionary();
        }
        catch (SshConnectionException ex)
        {
            logger.LogError("FindFiles 中的 SSH 连接错误", ex);
            return _knownFiles;
        }
        catch (Exception ex)
        {
            logger.LogError("FindFiles 中发生意外错误", ex);
            return _knownFiles;
        }
    }

    private async Task TryReconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (client?.IsConnected ?? false)
            {
                try
                {
                    client.Disconnect();
                }
                catch (Exception ex) 
                { 
                    logger.LogError("断开 SFTP 客户端时出错", ex);
                }
            }

            if (client != null)
            {
                try
                {
                    client.Connect();
                    logger.LogInformation("已成功重新连接至 SFTP 服务器");
                }
                catch (Exception ex)
                {
                    logger.LogError("连接 SFTP 客户端失败", ex);
                    // Check cancellation before delay
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
            else
            {
                logger.LogError("重连尝试期间 SFTP 客户端为空");
            }
        }
        catch (Exception ex)
        {
            logger.LogError("重连期间发生意外错误", ex);
            throw;
        }
    }

    private bool IsHydrated(string serverPath)
    {
        serverPath = PathMapper.NormalizePath(serverPath);
        try
        {
            var relativePath = PathMapper.GetRelativePath(serverPath, _context.Directory);
            var clientPath = Path.Join(_syncContext.RootDirectory, relativePath);

            return Path.Exists(clientPath) &&
                   !File.GetAttributes(clientPath).HasAnySyncFlag(SyncAttributes.OFFLINE);
        }
        catch (Exception ex)
        {
            logger.LogError("检查 {path} 的 hydration 状态时出错", serverPath, ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                logger.LogDebug("正在处置 SFTP 监视器");
                _cancellationTokenSource.Cancel();
                
                // Safely disconnect the client
                try
                {
                    if (client?.IsConnected ?? false)
                    {
                        client.Disconnect();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("处置期间断开客户端时出错", ex);
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore if already disposed
            }
            finally
            {
                _cancellationTokenSource.Dispose();
            }
        }
    }
}
