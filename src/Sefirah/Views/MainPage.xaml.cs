using System.Diagnostics;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Sefirah.Data.Models;
using Sefirah.ViewModels;
using Sefirah.ViewModels.Settings;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Sefirah.Views;
public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; }
    public DevicesViewModel DevicesViewModel { get; }

    public MainPage()
    {
        InitializeComponent();
        ViewModel = Ioc.Default.GetRequiredService<MainPageViewModel>();
        DevicesViewModel = Ioc.Default.GetRequiredService<DevicesViewModel>();
    }

    private readonly Dictionary<string, Type> Pages = new()
    {
        { "Settings", typeof(SettingsPage) },
        { "Messages", typeof(MessagesPage) },
        { "Apps", typeof(AppsPage) }
    };

    // Track the current animation to prevent conflicts
    private Storyboard? currentOverlayAnimation;

    // Handle mouse wheel events on the phone frame
    private void PhoneFrame_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Get the wheel delta - positive for scrolling up, negative for scrolling down
        var pointerPoint = e.GetCurrentPoint(PhoneFrameGrid);
        int wheelDelta = pointerPoint.Properties.MouseWheelDelta;        
        ViewModel.SwitchToNextDevice(wheelDelta);
        e.Handled = true;
    }

    private void NavigationView_SelectionChanged(NavigationView _, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem selectedItem && 
            selectedItem.Tag?.ToString() is string tag &&
            Pages.TryGetValue(tag, out Type? pageType))
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border &&
            border.FindName("PinIcon") is SymbolIcon pinIcon &&
            border.FindName("CloseButton") is Button closeButton &&
            border.FindName("MoreButton") is Button moreButton &&
            border.FindName("TimeStampTextBlock") is TextBlock timeStamp)
        {
            timeStamp.Visibility = Visibility.Collapsed;

            // Only make pinIcon visible if it's not already visible
            if (pinIcon.Tag is bool isPinned && isPinned)
            {
                pinIcon.Visibility = Visibility.Collapsed;
            }

            pinIcon.IsHitTestVisible = true;
            closeButton.Opacity = 1;
            closeButton.IsHitTestVisible = true;
            moreButton.Opacity = 1;
            moreButton.IsHitTestVisible = true;
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Check if MoreButton or its Flyout is open
        if (sender is Border border &&
            border.FindName("PinIcon") is SymbolIcon pinIcon &&
            border.FindName("CloseButton") is Button closeButton &&
            border.FindName("MoreButton") is Button moreButton &&
            border.FindName("TimeStampTextBlock") is TextBlock timeStamp)
        {
            // If the flyout is open, don't hide the buttons
            if (moreButton.Flyout is MenuFlyout flyout && flyout.IsOpen) return;

            timeStamp.Visibility = Visibility.Visible;

            if (pinIcon.Tag is bool isPinned && isPinned)
            {
                pinIcon.Visibility = Visibility.Visible;
            }

            pinIcon.IsHitTestVisible = false;
            closeButton.Opacity = 0;
            closeButton.IsHitTestVisible = false;
            moreButton.Opacity = 0;
            moreButton.IsHitTestVisible = false;
        }
    }

    private void MoreButtonFlyoutClosed(object sender, object e)
    {
        // The sender is the Flyout itself, so first get its parent button
        if (sender is MenuFlyout flyout && flyout.Target is Button moreButton && 
                VisualTreeHelper.GetParent(moreButton) is FrameworkElement parent &&
                parent.FindName("PinIcon") is SymbolIcon pinIcon &&
                parent.FindName("CloseButton") is Button closeButton &&
                parent.FindName("TimeStampTextBlock") is TextBlock timeStamp)
        {
            if (pinIcon.Tag is bool isPinned && isPinned)
            {
                pinIcon.Visibility = Visibility.Visible;
            }

            closeButton.Opacity = 0;
            closeButton.IsHitTestVisible = false;
            moreButton.Opacity = 0;
            moreButton.IsHitTestVisible = false;
            timeStamp.Visibility = Visibility.Visible;
        }
    }

    private async void OpenAppClick(object sender, RoutedEventArgs e)
    {   
        Debug.WriteLine($"[调试] OpenAppClick 被触发，sender 类型={sender?.GetType().Name}");

        Notification? notification = null;

        if (sender is MenuFlyoutItem menuItem)
        {
            Debug.WriteLine($"[调试] MenuFlyoutItem DataContext 类型={menuItem.DataContext?.GetType().Name}");
            if (menuItem.DataContext is Notification menuItemNotification)
            {
                notification = menuItemNotification;
                Debug.WriteLine($"[调试] MenuFlyoutItem 对应通知 Key={menuItemNotification.Key} AppPackage={menuItemNotification.AppPackage}");
            }
        }
        else if (sender is Button button)
        {
            Debug.WriteLine($"[调试] Button.CommandParameter 类型={button.CommandParameter?.GetType().Name} Tag 类型={button.Tag?.GetType().Name}");
            if (button.CommandParameter is Notification buttonNotification)
            {
                notification = buttonNotification;
                Debug.WriteLine($"[调试] Button.CommandParameter 对应通知 Key={buttonNotification.Key} AppPackage={buttonNotification.AppPackage}");
            }
            else if (button.Tag is Notification tagNotification)
            {
                // 仅在极少数情况：Tag 被直接设置为 Notification
                notification = tagNotification;
                Debug.WriteLine($"[调试] Button.Tag 为 Notification, Key={tagNotification.Key}");
            }
            else if (button.Tag is SourceDevice sd)
            {
                Debug.WriteLine($"[调试] Button.Tag 为 SourceDevice: DeviceId={sd.DeviceId} DeviceName={sd.DeviceName}");
            }
        }

        if (notification == null)
        {
            Debug.WriteLine("[警告] 未能在 OpenAppClick 中解析到 Notification 对象，调用链中断。检查 DeviceButtonsRepeater 是否已为按钮设置 CommandParameter 或 DataContext 继承是否生效。");
            return;
        }

        Debug.WriteLine($"[信息] 调用 ViewModel.OpenApp 通知 Key={notification.Key}");
        await ViewModel.OpenApp(notification);
    }

    private void UpdateNotificationFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.Tag is string appPackage)
        {
            ViewModel.UpdateNotificationFilter(appPackage);
        }
    }

    private void ToggleNotificationPinClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.DataContext is Notification notification)
        {
            ViewModel.ToggleNotificationPin(notification);
        }
    }
    
    private async void DeviceButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            // 优先使用 CommandParameter（如果设置的话）
            if (button.CommandParameter is Notification paramNotification)
            {
                if (button.Tag is SourceDevice sourceDeviceParam)
                {
                    await ViewModel.OpenApp(paramNotification, sourceDeviceParam.DeviceId);
                }
                else
                {
                    await ViewModel.OpenApp(paramNotification);
                }

                return;
            }

            // 向上遍历视觉树以查找第一个其 DataContext 为 Notification 的父元素（比仅查找 Border 更稳健）
            DependencyObject parent = button;
            Notification? notification = null;
            while (parent != null)
            {
                parent = VisualTreeHelper.GetParent(parent);
                if (parent is FrameworkElement fe && fe.DataContext is Notification n)
                {
                    notification = n;
                    break;
                }
            }

            if (notification is null) return;

            // 获取按钮的 Tag，它包含设备ID和设备名称
            if (button.Tag is SourceDevice sourceDevice)
            {
                var paired = DevicesViewModel?.PairedDevices?.FirstOrDefault(d => d.Id == sourceDevice.DeviceId);
                if (paired != null)
                {
                    await ViewModel.OpenApp(notification, sourceDevice.DeviceId);
                }
                else
                {
                    // 如果未找到对应的已配对设备，回退到当前活动设备以避免无响应
                    await ViewModel.OpenApp(notification);
                }
            }
        }
    }

    private void DeviceButtonsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is Button button)
        {
            Debug.WriteLine($"[调试] DeviceButtonsRepeater 元素准备：元素类型={args.Element?.GetType().Name}");

            // 如果已经有 CommandParameter，则无需覆盖
            if (button.CommandParameter != null)
            {
                Debug.WriteLine("[调试] Button 已有 CommandParameter，跳过设置。");
                return;
            }

            // 优先使用 sender.Tag（我们在 XAML 将 ItemsRepeater.Tag 绑定为外层 Notification）
            if (sender.Tag is Notification notifFromTag)
            {
                button.CommandParameter = notifFromTag;
                Debug.WriteLine($"[调试] 从 sender.Tag 设置 CommandParameter，Notification Key={notifFromTag.Key}");
                return;
            }

            // 否则回退到视觉树查找其父元素的 DataContext
            DependencyObject parent = button;
            while (parent != null)
            {
                parent = VisualTreeHelper.GetParent(parent);
                if (parent is FrameworkElement fe && fe.DataContext is Notification n)
                {
                    button.CommandParameter = n;
                    Debug.WriteLine($"[调试] 从视觉树找到父级 DataContext，设置 CommandParameter，Notification Key={n.Key}");
                    break;
                }
            }

            if (button.CommandParameter == null)
            {
                Debug.WriteLine("[警告] 未能为按钮设置 CommandParameter（未找到对应 Notification）。");
            }
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.Tag is Notification notification &&
            (button.Parent as FrameworkElement)?.FindName("ReplyTextBox") is TextBox replyTextBox)
        {
            string replyText = replyTextBox.Text;
            // Clear the textbox after getting the text
            replyTextBox.Text = string.Empty;

            ViewModel.HandleNotificationReply(notification, replyText);
        }
    }

    private void ReplyTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is TextBox textBox &&
            e.Key is VirtualKey.Enter &&
            textBox.Tag is Notification message)
        {
            ViewModel.HandleNotificationReply(message, textBox.Text);
            textBox.Text = string.Empty;
        }
    }

    private void PhoneFrame_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimateOverlay(PhoneFrameOverlay, true);
    }

    private void PhoneFrame_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        AnimateOverlay(PhoneFrameOverlay, false);
    }

    private void AnimateOverlay(UIElement overlay, bool show)
    {
        // Cancel any existing animation to prevent conflicts
        currentOverlayAnimation?.Stop();
        currentOverlayAnimation = null;

        if (show)
        {
            overlay.Visibility = Visibility.Visible;
            currentOverlayAnimation = FadeInStoryboard;
            FadeInStoryboard.Begin();
        }
        else
        {
            currentOverlayAnimation = FadeOutStoryboard;
            FadeOutStoryboard.Begin();
            
            // Hide overlay after animation completes
            FadeOutStoryboard.Completed += (s, args) => 
            {
                overlay.Visibility = Visibility.Collapsed;
                currentOverlayAnimation = null;
            };
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        // Check if the dropped data contains files
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            ViewModel.SendFiles(await e.DataView.GetStorageItemsAsync());
        }
    }

    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.PairedDevices.Count == 0) return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        if (ViewModel.PairedDevices.Count == 1)
        {
            e.DragUIOverride.Caption = $"Send to {ViewModel.Device?.Name}";
        }
    }
}
