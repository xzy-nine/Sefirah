using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sefirah.Data.Models;
using Sefirah.Helpers;

namespace Sefirah.Services;

/// <summary>
/// 统一加密发送器
/// 
/// 封装加密、认证检查、TCP发送与报文头拼装：
/// 最终报文格式：`<HEADER>:<localUuid>:<localPublicKey>:<encryptedPayload>\n`
/// </summary>
public static class ProtocolSender
{
    private const string TAG = "ProtocolSender";
    private const int DEFAULT_TIMEOUT = 10000;
    private const int DEFAULT_CONNECT_TIMEOUT = 5000;
    
    /// <summary>
    /// 发送一条加密负载到指定设备。
    /// </summary>
    /// <param name="logger">日志记录器</param>
    /// <param name="device">目标设备</param>
    /// <param name="header">消息头，例如：DATA_JSON / DATA_ICON_REQUEST / DATA_ICON_RESPONSE 等</param>
    /// <param name="plaintext">明文内容</param>
    /// <param name="localDeviceId">本地设备ID</param>
    /// <param name="localPublicKey">本地公钥</param>
    /// <param name="timeoutMs">超时时间</param>
    public static async Task SendEncryptedAsync(
        ILogger logger,
        PairedDevice device,
        string header,
        string plaintext,
        string localDeviceId,
        string localPublicKey,
        int timeoutMs = DEFAULT_TIMEOUT
    )
    {
        try
        {
            // 检查设备是否已认证
            if (device.SharedSecret == null)
            {
                logger.LogWarning("设备未认证或未接受：{deviceName}", device.Name);
                return;
            }

            // 确保设备有IP地址
            if (device.IpAddresses == null || device.IpAddresses.Count == 0)
            {
                logger.LogWarning("设备 {deviceName} 没有可用的IP地址", device.Name);
                return;
            }

            // 使用第一个IP地址
            string ipAddress = device.IpAddresses[0];
            const int notifyRelayPort = 23333;

            logger.LogInformation("发送到设备：{deviceName} ({ipAddress})，deviceId={deviceId}", device.Name, ipAddress, device.Id);

            // 限制日志长度，特别是base64图片数据
            string loggableJson = plaintext.Length > 100 ? plaintext.Substring(0, 100) + "..." : plaintext;
            logger.LogInformation("请求内容：{loggableJson}", loggableJson);

            // 加密消息
            string encryptedPayload = NotifyCryptoHelper.Encrypt(plaintext, device.SharedSecret);
            
            // 构建最终消息
            string framedMessage = $"{header}:{localDeviceId}:{localPublicKey}:{encryptedPayload}\n";
            byte[] messageBytes = Encoding.UTF8.GetBytes(framedMessage);
            
            logger.LogDebug("消息字节长度：{length}", messageBytes.Length);

            // 创建TCP客户端并发送消息
            using var tcpClient = new TcpClient();
            tcpClient.ReceiveTimeout = (int)timeoutMs;
            tcpClient.SendTimeout = (int)timeoutMs;
            
            // 连接设备
            var connectTask = tcpClient.ConnectAsync(ipAddress, notifyRelayPort);
            var connectResult = await Task.WhenAny(connectTask, Task.Delay(DEFAULT_CONNECT_TIMEOUT));
            
            if (connectResult != connectTask || !connectTask.IsCompletedSuccessfully)
            {
                logger.LogWarning("连接设备超时：{ipAddress}:{port}，跳过发送", ipAddress, notifyRelayPort);
                return;
            }
            
            using var networkStream = tcpClient.GetStream();
            networkStream.ReadTimeout = (int)timeoutMs;
            networkStream.WriteTimeout = (int)timeoutMs;
            
            // 发送消息
            await networkStream.WriteAsync(messageBytes, 0, messageBytes.Length);
            // 确保数据完全发送
            await networkStream.FlushAsync();
            
            logger.LogInformation("成功发送请求：{header}，deviceId={deviceId}", header, device.Id);
        }
        catch (ObjectDisposedException ex)
        {
            logger.LogError(ex, "发送请求时 Socket 已释放：deviceId={deviceId}", device.Id);
        }
        catch (SocketException ex)
        {
            logger.LogError(ex, "发送请求时 Socket 错误：deviceId={deviceId}", device.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送请求时出错：deviceId={deviceId}", device.Id);
        }
    }

    /// <summary>
    /// 发送一条加密负载到指定设备，使用默认超时时间。
    /// </summary>
    public static Task SendEncryptedAsync(
        ILogger logger,
        PairedDevice device,
        string header,
        string plaintext,
        string localDeviceId,
        string localPublicKey
    )
    {
        return SendEncryptedAsync(logger, device, header, plaintext, localDeviceId, localPublicKey, DEFAULT_TIMEOUT);
    }
}