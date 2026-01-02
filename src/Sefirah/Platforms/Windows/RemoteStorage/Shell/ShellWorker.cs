using Sefirah.Platforms.Windows.Async;
using Sefirah.Platforms.Windows.Helpers;

namespace Sefirah.Platforms.Windows.RemoteStorage.Shell;

public sealed class ShellWorker(
    ShellRegistrar shellRegistrar,
    ILogger logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Shell 工作器已启动");

            // Start up the task that registers and hosts the services for the shell
            using var disposableShellCookies = new Disposable<IReadOnlyList<uint>>(shellRegistrar.Register(), shellRegistrar.Revoke);

            await stoppingToken;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "执行 shell 工作项失败");
        }
    }
}
