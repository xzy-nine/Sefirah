using Sefirah.Data.Contracts;
using Sefirah.Data.Models;
using Sefirah.Data.Models.Actions;
using Sefirah.Utils.Serialization;

namespace Sefirah.Services;

public abstract class BaseActionService(
    IGeneralSettingsService generalSettingsService,
    IUserSettingsService userSettingsService,
    ISessionManager sessionManager,
    ILogger logger) : IActionService
{
    public virtual Task InitializeAsync()
    {
        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        if (ApplicationData.Current.LocalSettings.Values["DefaultActionsLoaded"] == null)
        {
            ApplicationData.Current.LocalSettings.Values["DefaultActionsLoaded"] = true;
            var defaultActions = DefaultActionsProvider.GetDefaultActions();
            userSettingsService.GeneralSettingsService.Actions = [.. defaultActions];
        }

        return Task.CompletedTask;
    }

    private void OnConnectionStatusChanged(object? sender, (PairedDevice Device, bool IsConnected) args)
    {
        // 仅处理连接状态变化，不再自动发送所有动作
        // 应用列表和图标同步通过专用方法处理
    }

    public virtual void HandleActionMessage(ActionMessage action)
    {
        logger.LogInformation("正在执行动作：{name}", action.ActionName);
        var actionToExecute = generalSettingsService.Actions.FirstOrDefault(a => a.Id == action.ActionId);

        if (actionToExecute is not null && actionToExecute is ProcessAction processAction)
        {
            processAction.ExecuteAsync();
        }
    }
} 
