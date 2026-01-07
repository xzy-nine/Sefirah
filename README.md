<p align="center">
  <img alt="Hero image" src="./.github/readme-images/Readme-Hero.png" />
</p>

**NotifyRelay** 旨在通过实现 Windows PC 与 Android 设备之间的~~无缝剪贴板~~通知共享来提升您的工作效率。它是现有解决方案的替代方案，专为希望以简单高效的方式保持设备同步的用户设计。

## 功能特性

- **媒体控制**：互相控制设备之间的媒体播放。
- **通知同步**：允许在桌面端显示来自 Android 设备的通知。
- **屏幕镜像**：通过 scrcpy 镜像和控制 Android 设备。
- ~~**文件共享**：轻松在设备之间共享文件。~~
- ~~**存储集成**：将您的 Android 存储集成到 Windows 文件资源管理器中。~~
- ~~**剪贴板共享**：在您的 Android 设备和 Windows PC 之间无缝共享剪贴板内容。~~
## 限制

### **通知同步**
- 由于 Android 的限制，从 Android 15 开始，敏感通知不再可见。
- 要解决此限制，您可以使用 ADB 授予必要的权限。运行以下命令：

  ```sh
  adb shell appops set com.xzyht.NotifyRelay RECEIVE_SENSITIVE_NOTIFICATIONS allow

## 安装

### Windows 应用
<p align="left">
  <!-- Store Badge -->
  <a style="text-decoration:none" href="https://example.com" target="_blank" rel="noopener noreferrer">
    <picture>
      <source media="(prefers-color-scheme: light)" srcset=".github/./readme-images/StoreBadge-dark.png" width="220" />
      <img src=".github/./readme-images/StoreBadge-light.png" width="200" />
    </picture>
  </a>
</p>

### Android 应用

## 使用方法

1. **下载并安装 [Android 应用](https://github.com/shrimqy/NotifyRelay-Android)**

2. **设置步骤**：
    - 在 Android 设备上，在引导页面允许必要的权限。（**注意**：尝试授予通知访问权限或辅助功能权限后，请从应用信息中允许受限设置，因为 Android 会阻止侧载应用请求敏感权限。）
    - 确保您的 Android 设备和 Windows PC 连接到同一网络。
    - 在您的 Windows PC 上启动应用，等待设备在两端显示。
    - 在 Android 设备上使用手动连接或自动连接发起连接。手动连接速度更快，而自动连接需要更多时间来确定适合您的 IP 地址。
    - 连接发起后，Windows 将收到一个弹出窗口，用于接受或拒绝连接。
    - 认证完成后，您将被导航到两端的主屏幕，请稍等片刻，以便 Windows 首次加载通知。

    **注意**：如果设备虽然能相互发现但无法连接，请确保检查您的防火墙设置，并确保这些端口已打开：23333。

3. **剪贴板共享**：
    - 当您在桌面复制内容时，它将自动与您的 Android 设备同步（前提是您已在设置中启用此功能）。如果您还启用了图像同步，图像也会被发送。**注意**：您必须启用“将收到的图像添加到剪贴板”选项，图像同步才能正常工作。
    - 要自动共享剪贴板，请在设置中启用相应的首选项（需要辅助功能权限）。**注意**：此方法可能并非在所有场景下都有效。
    - 要手动共享剪贴板，有两种主要方法：使用持久设备状态通知或共享表。
4. **文件传输**：
    - 在您的 Android 或 Windows 设备上使用共享表，选择应用程序在设备之间共享文件。
5. **Android 存储**：
    - 您需要 Android 11 或更高版本
    - 在 Android 应用的设置中启用存储访问权限，桌面应用将在文件资源管理器中创建指向 Android 存储的链接。
    - **注意**：此功能仍处于试验阶段，可能无法在所有 Windows 版本上工作，尤其是较旧版本的 Windows 10 和其他非官方精简版 Windows 11。
    - **警告**：请勿将远程存储位置设置为预先存在的文件夹，因为这会删除该文件夹的内容。
6. **使用 [Scrcpy](https://github.com/Genymobile/scrcpy) 进行屏幕镜像/音频共享**：
    - 您需要 [下载](https://github.com/Genymobile/scrcpy/releases)、解压并在应用设置中设置 scrcpy 位置。
    - 您可以在屏幕镜像部分指定 scrcpy 启动时的首选项。
    - 要启动屏幕镜像，最简单的方法是通过 USB 连接您的设备，然后使用铃声模式旁边的按钮开始（如果您想建立无线连接，请在参数文本框中添加 "--tcpip"，如果您不知道自己在做什么，则不需要指定更多参数）。
    - 如果默认 tcpip 端口打开，NotifyRelay 将尝试连接到您的设备以进行后续连接。
    - 如果您在连接 scrcpy 时有任何疑问或问题，请参考 [scrcpy 文档](https://github.com/Genymobile/scrcpy/blob/master/doc/connection.md) 和 [Scrcpy FAQ](https://github.com/Genymobile/scrcpy/blob/master/FAQ.md)。
    - 如果您喜欢他们的项目，请考虑支持作者 [rom1v](https://blog.rom1v.com/about/#support-my-open-source-work)
## 截图

<p align="center">
  <img alt="Files hero image" src="./.github/readme-images/Screenshot.png" />
</p>

## 贡献

如果您想报告错误、提供反馈或提出问题，请随时打开一个 issue。非常欢迎提交拉取请求！

## 致谢
本项目的ui层和部分功能使用的是 [Sefirah](https://github.com/shrimqy/Sefirah) 的代码.

