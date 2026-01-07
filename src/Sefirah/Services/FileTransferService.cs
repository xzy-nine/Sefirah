using System.Net;
using System.Net.Sockets;
using System.Text;
using NetCoreServer;
using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Sefirah.Data.Models;
using Sefirah.Dialogs;
using Sefirah.Extensions;
using Sefirah.Helpers;
using Sefirah.Services.Socket;
using Sefirah.Utils.Serialization;
using Uno.Logging;

namespace Sefirah.Services;

public class FileTransferService(
    ILogger logger,
    ISessionManager sessionManager,
    IUserSettingsService userSettingsService,
    IDeviceManager deviceManager,
    IPlatformNotificationHandler notificationHandler
    ) : IFileTransferService, ITcpClientProvider, ITcpServerProvider
{
    private string? storageLocation;
    private FileStream? currentFileStream;
    private FileMetadata? currentFileMetadata;
    private long bytesTransferred = 0;
    private Client? client;
    private Sefirah.Services.Socket.SslServer? server;
    private ServerInfo? serverInfo;
    private ServerSession? session;
    private uint notificationSequence = 1;
    private TransferContext? currentTransfer;
    private CancellationTokenSource? cancellationTokenSource;

    private const string COMPLETE_MESSAGE = "Complete";

    private TaskCompletionSource<ServerSession>? connectionSource;
    private TaskCompletionSource<bool>? transferCompletionSource;

    public event EventHandler<(PairedDevice device, StorageFile data)>? FileReceived;

    private readonly IEnumerable<int> PORT_RANGE = Enumerable.Range(5152, 18);

    public void CancelTransfer()
    {
        cancellationTokenSource?.Cancel();
    }

    #region Receive
    public async Task ReceiveBulkFiles(BulkFileTransfer bulkFile, PairedDevice device)
    {
        try
        {
            if (transferCompletionSource?.Task is not null)
            {
                await transferCompletionSource.Task;
            }

            storageLocation = userSettingsService.GeneralSettingsService.ReceivedFilesPath;
            var serverInfo = bulkFile.ServerInfo;

            currentTransfer = new TransferContext(
                device.Name,
                $"transfer_{DateTime.Now.Ticks}",
                bulkFile.Files
            );

            cancellationTokenSource = new CancellationTokenSource();

            client = new Client(serverInfo.IpAddress, serverInfo.Port, this);

            if (!client.ConnectAsync()) 
                throw new IOException("Failed to connect to file transfer server");

            // Adding a small delay for the android to open a read channel
            await Task.Delay(500);
            var passwordBytes = Encoding.UTF8.GetBytes(serverInfo.Password + "\n");
            client?.SendAsync(passwordBytes);

            notificationHandler.ShowFileTransferNotification(
                string.Format("FileTransferNotification.ReceivingBulk".GetLocalizedResource(), 1, currentTransfer.Files.Count, currentTransfer.Device),
                currentTransfer.Files[0].FileName,
                currentTransfer.TransferId,
                notificationSequence++);

            foreach (var fileMetadata in bulkFile.Files)
            {
                try
                {
                    // Check if the entire bulk transfer has been cancelled
                    cancellationTokenSource.Token.ThrowIfCancellationRequested();

                    logger.Info($"开始接收文件 {currentTransfer.CurrentFileIndex + 1}/{bulkFile.Files.Count}：{fileMetadata.FileName}");

                    if (transferCompletionSource?.Task is not null && !transferCompletionSource.Task.IsCompleted)
                    {
                        await transferCompletionSource.Task;
                    }
                    string fullPath = Path.Combine(storageLocation, fileMetadata.FileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                    transferCompletionSource = new();
                    currentFileMetadata = fileMetadata;
                    currentFileStream = new FileStream(fullPath, FileMode.Create);

                    // Wait for this file transfer to complete
                    await transferCompletionSource.Task;

                    currentTransfer.CurrentFileIndex++;
                    logger.Info($"已接收文件 {fileMetadata.FileName}");

                    // Clean up the file stream for the previous file
                    CleanupFileStream();
                }
                catch (Exception ex)
                {
                    CleanupFileStream();
                    var failedFilePath = Path.Combine(storageLocation, fileMetadata.FileName);
                    if (File.Exists(failedFilePath))
                    {
                        File.Delete(failedFilePath);
                    }

                    if (ex is OperationCanceledException)
                    {
                        logger.Info("批量文件传输被用户取消");
                        throw;
                    }
                    logger.Error($"接收文件 {fileMetadata.FileName} 时出错", ex);
                }
            }

            notificationHandler.ShowCompletedFileTransferNotification(
                string.Format("FileTransferNotification.CompletedBulk".GetLocalizedResource(), currentTransfer.Files.Count, currentTransfer.Device),
                currentTransfer.TransferId,
                folderPath: storageLocation);

            logger.Warn($"批量文件传输完成，但存在错误：{currentTransfer.CurrentFileIndex}/{currentTransfer.Files.Count} 个文件接收失败");
        }
        catch (Exception ex)
        {
            logger.Error("批量文件传输设置期间出错", ex);
        }
        finally
        {
            CleanupClient();
        }
    }

    private void CleanupFileStream()
    {
        currentFileStream?.Close();
        currentFileStream?.Dispose();
        currentFileStream = null;
        currentFileMetadata = null;
    }

    public async Task ReceiveFile(FileTransfer data, PairedDevice device)
    {
        storageLocation = userSettingsService.GeneralSettingsService.ReceivedFilesPath;
        string fullPath = Path.Combine(storageLocation, data.FileMetadata.FileName);
        try
        {
            // Wait for any existing transfer to complete
            if (transferCompletionSource?.Task is not null)
            {
                await transferCompletionSource.Task;
            }
            
            // Create transfer context for single file
            currentTransfer = new TransferContext(
                device.Name,
                $"transfer_{DateTime.Now.Ticks}",
                [data.FileMetadata]
            );

            transferCompletionSource = new TaskCompletionSource<bool>();
            cancellationTokenSource = new CancellationTokenSource();
            var serverInfo = data.ServerInfo;
            currentFileMetadata = data.FileMetadata;

            currentFileStream = new FileStream(fullPath, FileMode.Create);

            client = new Client(serverInfo.IpAddress, serverInfo.Port, this);
            if (!client.ConnectAsync())
                throw new IOException("Failed to connect to file transfer server");

            notificationHandler.ShowFileTransferNotification(
                string.Format("FileTransferNotification.Receiving".GetLocalizedResource(), currentTransfer.Device), 
                currentFileMetadata.FileName, 
                currentTransfer.TransferId,
                notificationSequence++, 
                0);

            // Adding a small delay for the android to open a read channel
            await Task.Delay(500);
            var passwordBytes = Encoding.UTF8.GetBytes(serverInfo.Password + "\n");
            client?.SendAsync(passwordBytes);

            await transferCompletionSource.Task;

            if (device.DeviceSettings.ClipboardFilesEnabled)
            {
                var file = await StorageFile.GetFileFromPathAsync(fullPath);
                FileReceived?.Invoke(this, (device, file));
            }

            notificationHandler.ShowCompletedFileTransferNotification(
                string.Format("FileTransferNotification.CompletedSingle".GetLocalizedResource(), currentFileMetadata.FileName, currentTransfer.Device), 
                currentTransfer.TransferId, 
                fullPath);
        }
        catch (Exception ex)
        {
            logger.Error("文件传输设置期间出错", ex);
            CleanupFileStream();
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        finally
        {
            CleanupClient();
        }
    }

    #region Server Events

    public void OnConnected()
    {
        logger.Info("已连接到文件传输服务器");
    }

    public void OnDisconnected()
    {
        logger.Info("已从文件传输服务器断开连接");
        if (currentFileMetadata != null &&
            currentFileStream != null &&
            currentTransfer != null &&
            currentTransfer.BytesTransferred < currentFileMetadata.FileSize)
        {
            transferCompletionSource?.TrySetException(new IOException("Connection to server lost"));
            CleanupClient();
        }
    }

    public void OnError(SocketError error)
    {
        logger.Error($"文件传输期间发生 Socket 错误：{error}");
        transferCompletionSource?.TrySetException(new IOException($"Socket 错误：{error}"));
        CleanupClient();
    }

    public void OnReceived(byte[] buffer, long offset, long size)
    {
        try
        {
            cancellationTokenSource?.Token.ThrowIfCancellationRequested();

            if (currentFileStream == null || currentFileMetadata == null || currentTransfer == null) return;

            currentFileStream.Write(buffer, (int)offset, (int)size);
            bytesTransferred += size;
            currentTransfer.BytesTransferred += size;

            var progress = (double)currentTransfer.BytesTransferred / currentTransfer.TotalBytes * 100;
            
            notificationHandler.ShowFileTransferNotification(
                string.Format("FileTransferNotification.Receiving".GetLocalizedResource(), currentTransfer.Device),
                currentFileMetadata.FileName,
                currentTransfer.TransferId,
                notificationSequence,
                progress);

            if (bytesTransferred >= currentFileMetadata.FileSize)
            {
                client?.Send(Encoding.UTF8.GetBytes(COMPLETE_MESSAGE + "\n"));
                bytesTransferred = 0;
                transferCompletionSource?.TrySetResult(true);
            }
        }
        catch (Exception ex)
        {
            transferCompletionSource?.TrySetException(ex);
        }
    }
    #endregion
    #endregion

    private void CleanupClient()
    {
        try
        {
            CleanupFileStream();

            try
            {
                client?.DisconnectAsync();
            }
            catch
            {
                // Ignore disconnect errors during cleanup
            }
            client?.Dispose();
            client = null;
            transferCompletionSource = null;
        }
        catch (Exception ex)
        {
            logger.Error("传输清理期间出错", ex);
        }
        finally
        {
            currentFileMetadata = null;
            currentTransfer = null;
            cancellationTokenSource?.Dispose();
            cancellationTokenSource = null;
        }
    }

    #region Send

    public async void SendFiles(IReadOnlyList<IStorageItem> storageItems)
    {
        try
        {
            var files = storageItems.OfType<StorageFile>().ToArray();
            var devices = deviceManager.PairedDevices.Where(d => d.ConnectionStatus).ToList();
            PairedDevice? selectedDevice = null;
            if (devices.Count == 0)
            {
                return;
            }
            else if (devices.Count == 1)
            {
                selectedDevice = devices[0];
            }
            else if (devices.Count > 1)
            {
                App.MainWindow.AppWindow.Show();
                App.MainWindow.Activate();
                selectedDevice = await DeviceSelector.ShowDeviceSelectionDialog(devices);
            }

            if (selectedDevice == null || selectedDevice.Session == null) return;

            await Task.Run(async () =>
            {
                if (files.Length > 1)
                {
                    await SendBulkFiles(files, selectedDevice);
                }
                else if (files.Length == 1)
                {
                    var metadata = await files[0].ToFileMetadata();
                    if (metadata == null) return;

                    await SendFile(files[0], metadata, selectedDevice);
                }
            });
        }
        catch (Exception ex)
        {
            logger.Error($"发送文件时出错：{ex.Message}", ex);
        }
    }

    public async Task SendFile(StorageFile file, FileMetadata metadata, PairedDevice device, FileTransferType transferType = FileTransferType.File)
    {
        try
        {
            if (!device.ConnectionStatus)
            {
                logger.LogWarning("设备未连接，无法发送文件");
                return;
            }
            // Wait for any existing transfer to complete
            if (transferCompletionSource?.Task.IsCompleted == false)
            {
                await transferCompletionSource.Task;
            }

            server?.Stop();
            session?.Disconnect();
            session = null;
            connectionSource = null;

            currentTransfer = new TransferContext(
                device.Name,
                $"transfer_{DateTime.Now.Ticks}",
                [metadata]
            );

            cancellationTokenSource = new CancellationTokenSource();

            var serverInfo = await InitializeServer();
            var transfer = new FileTransfer
            {
                TransferType = transferType,
                ServerInfo = serverInfo,
                FileMetadata = metadata
            };

            var json = SocketMessageSerializer.Serialize(transfer);
            logger.Debug($"发送元数据：{json}");
            sessionManager.SendMessage(device.Id, json);

            notificationHandler.ShowFileTransferNotification(
                string.Format("FileTransferNotification.Sending".GetLocalizedResource(), currentTransfer.Device),
                metadata.FileName,
                currentTransfer.TransferId,
                notificationSequence++,
                0);

            transferCompletionSource = new();
            await SendFileData(metadata, await file.OpenStreamForReadAsync());
            await transferCompletionSource.Task;

            notificationHandler.ShowCompletedFileTransferNotification(
                string.Format("FileTransferNotification.SentSingle".GetLocalizedResource(), metadata.FileName, currentTransfer.Device),
                currentTransfer.TransferId);
        }
        catch (OperationCanceledException)
        {
            logger.Info("文件传输被用户取消");
        }
        catch (Exception ex)
        {
            logger.Error("发送流数据时出错", ex);
        }
        finally
        {
            CleanupServer();
        }
    }

    public async Task SendBulkFiles(StorageFile[] files, PairedDevice device)
    {
        try
        {
            if (!device.ConnectionStatus)
            {
                logger.LogWarning("设备未连接，无法发送文件");
                return;
            }
            var fileMetadataList = await Task.WhenAll(files.Select(file => file.ToFileMetadata()));

            currentTransfer = new TransferContext(
                device.Name,
                $"transfer_{DateTime.Now.Ticks}",
                fileMetadataList.ToList()
            );

            cancellationTokenSource = new CancellationTokenSource();

            serverInfo = await InitializeServer();

            var transfer = new BulkFileTransfer
            {
                ServerInfo = serverInfo,
                Files = [.. fileMetadataList]
            };

            // Send metadata first
            sessionManager.SendMessage(device.Id, SocketMessageSerializer.Serialize(transfer));

            notificationHandler.ShowFileTransferNotification(
                string.Format("FileTransferNotification.SendingBulk".GetLocalizedResource(), 1, currentTransfer.Files.Count, currentTransfer.Device),
                currentTransfer.Files[0].FileName,
                currentTransfer.TransferId,
                notificationSequence++);

            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    cancellationTokenSource.Token.ThrowIfCancellationRequested();

                    logger.Debug($"正在发送文件：{fileMetadataList[i].FileName}");

                    transferCompletionSource = new TaskCompletionSource<bool>();

                    await SendFileData(fileMetadataList[i], await files[i].OpenStreamForReadAsync());

                    await transferCompletionSource.Task;
                    currentTransfer.CurrentFileIndex++;
                }
                catch (OperationCanceledException)
                {
                    logger.Info("批量文件传输被用户取消");
                    throw;
                }
            }

            notificationHandler.ShowCompletedFileTransferNotification(
                string.Format("FileTransferNotification.SentBulk".GetLocalizedResource(), currentTransfer.Files.Count, currentTransfer.Device),
                currentTransfer.TransferId);

            logger.Debug("所有文件已成功传输");
        }
        catch (OperationCanceledException)
        {
            logger.Info("批量文件传输被用户取消");
        }
        catch (Exception ex)
        {
            logger.Error("SendBulkFiles 内部出错", ex);
        }
        finally
        {
            CleanupServer();
        }
    }

    public async Task SendFileData(FileMetadata metadata, Stream stream)
    {
        try
        {
            if (session == null)
            {
                connectionSource = new();

                // Wait for Authentication from onReceived event to trigger
                session = await connectionSource.Task;
            }

            const int ChunkSize = 524288; // 512KB

            using (stream)
            {
                var buffer = new byte[ChunkSize];
                long totalBytesRead = 0;

                while (totalBytesRead < metadata.FileSize && session?.IsConnected == true)
                {
                    // Check if transfer has been canceled
                    cancellationTokenSource?.Token.ThrowIfCancellationRequested();

                    int bytesRead = await stream.ReadAsync(buffer);
                    if (bytesRead == 0) break;

                    session.Send(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    if (currentTransfer != null)
                    {
                        currentTransfer.BytesTransferred = totalBytesRead;
                        var progress = (double)totalBytesRead / metadata.FileSize * 100;

                        if (currentTransfer.Files.Count > 1)
                        {
                            notificationHandler.ShowFileTransferNotification(
                                string.Format("FileTransferNotification.SendingBulk".GetLocalizedResource(), currentTransfer.CurrentFileIndex + 1, currentTransfer.Files.Count, currentTransfer.Device),
                                metadata.FileName,
                                currentTransfer.TransferId,
                                notificationSequence,
                                progress);
                        }
                        else
                        {
                            notificationHandler.ShowFileTransferNotification(
                                string.Format("FileTransferNotification.Sending".GetLocalizedResource(), currentTransfer.Device),
                                metadata.FileName,
                                currentTransfer.TransferId,
                                notificationSequence,
                                progress);
                        }
                    }
                }
            }

            logger.Info($"已完成文件传输：{metadata.FileName}");
        }
        catch (Exception ex)
        {
            logger.Error("SendFileData 内部出错", ex);
            throw;
        }
    }

    public Task<ServerInfo> InitializeServer()
    {
        // Try each port in the range
        foreach (int port in PORT_RANGE)
        {
            try
            {
                server = new Server(IPAddress.Any, port, this, logger)
                {
                    OptionDualMode = true,
                    OptionReuseAddress = true,
                };
                server.Start();

                serverInfo = new ServerInfo
                {
                    Port = port,
                    Password = EcdhHelper.GenerateRandomPassword()
                };

                logger.Info($"文件传输服务器已在 {serverInfo.IpAddress}:{serverInfo.Port} 初始化");
                return Task.FromResult(serverInfo);
            }
            catch (Exception ex)
            {
                logger.Debug($"启动端口 {port} 的服务器失败：{ex.Message}");

                server?.Dispose();
                server = null;
            }
        }
        throw new IOException("启动文件传输服务器失败：范围内端口均不可用");
    }

    private void CleanupServer()
    {
        server?.Stop();
        server?.Dispose();
        server = null;
        serverInfo = null;
        connectionSource = null;
        session = null;
        currentTransfer = null;
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;
    }

    #region Server Events
    public void OnConnected(ServerSession session)
    {
        logger.Info($"客户端已连接到文件传输服务器：{session.Id}");
    }

    public void OnDisconnected(ServerSession session)
    {
        if (transferCompletionSource?.Task.IsCompleted == false)
        {
            transferCompletionSource?.TrySetException(new Exception("Client disconnected"));
        }
        logger.Info($"客户端已从文件传输服务器断开连接：{session.Id}");
        CleanupServer();
    }

    public void OnReceived(ServerSession session, byte[] buffer, long offset, long size)
    {
        string message = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
        if (connectionSource?.Task.IsCompleted == false && message == serverInfo?.Password)
        {
            connectionSource.SetResult(session);
        }
        if (message == COMPLETE_MESSAGE)
        {
            logger.Info($"传输完成");
            transferCompletionSource?.TrySetResult(true);
        }
    }

    #endregion

    #endregion
}
