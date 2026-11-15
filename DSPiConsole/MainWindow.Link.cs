using DSPiConsole.Services;
using DSPiConsole.Usb.Transport;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole;

/// <summary>
/// DSPi Link in the main window: the pairing prompt a hub's PIN mode asks
/// for, and the status-row notice when another console holds the device.
/// </summary>
public sealed partial class MainWindow
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private DispatcherTimer? _lockNoticeTimer;
    private readonly SemaphoreSlim _pairingPromptGate = new(1, 1);

    private void InitializeLink()
    {
        LinkLockNotice.Posted += (_, _) => DispatcherQueue.TryEnqueue(ShowLockNotice);
        LinkNetwork.Shared.Start(PromptForHubPinAsync);
    }

    /// <summary>"Another console is applying changes", for 4 s after the last
    /// refusal (spec 7.7).</summary>
    private void ShowLockNotice()
    {
        LockNoticeText.Text = LinkLockNotice.Message;
        LockNoticeText.Visibility = Visibility.Visible;
        _lockNoticeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _lockNoticeTimer.Tick -= HideLockNotice;
        _lockNoticeTimer.Tick += HideLockNotice;
        _lockNoticeTimer.Stop();
        _lockNoticeTimer.Start();
    }

    private void HideLockNotice(object? sender, object e)
    {
        _lockNoticeTimer?.Stop();
        LockNoticeText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Asks for a hub's PIN, worded from its pin_source (spec 6.2). Called from
    /// a background thread; one prompt at a time, shown once no other dialog
    /// is open. Null when the user cancels.
    /// </summary>
    private async Task<string?> PromptForHubPinAsync(LinkPairingRequest request)
    {
        await _pairingPromptGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try { done.TrySetResult(await ShowPinDialogAsync(request)); }
                    catch (Exception) { done.TrySetResult(null); }
                }))
                return null;
            return await done.Task.ConfigureAwait(false);
        }
        finally
        {
            _pairingPromptGate.Release();
        }
    }

    private async Task<string?> ShowPinDialogAsync(LinkPairingRequest request)
    {
        // The hub is waiting: from the notification area the window comes
        // back, or the prompt would open unseen and the pairing lapse.
        if (BackgroundMode.Shared.IsMinimised) BackgroundMode.Shared.Restore();
        // Only one ContentDialog may be open; wait for another to close.
        if (!await WaitForNoDialogAsync(TimeSpan.FromMinutes(2))) return null;
        // Paired from Settings (its Networking page's Pair button): ask there,
        // not behind it in this window.
        var root = Content.XamlRoot;
        if (_settingsWindow is { } settings && settings.Content?.XamlRoot is { } settingsRoot
            && GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(settings))
        {
            var until = DateTime.UtcNow.AddMinutes(2);
            while (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(settingsRoot).Any(p => p.Child is ContentDialog))
            {
                if (DateTime.UtcNow >= until) return null;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            root = settingsRoot;
        }

        var fixedPin = request.PinSource == Core.Link.LinkPinSource.Fixed;
        var box = new TextBox
        {
            PlaceholderText = "6-digit PIN",
            InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.NumericPin) } },
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var body = new StackPanel { Spacing = 10 };
        if (request.RetryAfterWrongPin)
        {
            body.Children.Add(new TextBlock
            {
                Text = "That PIN wasn't accepted. Try again.",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }
        body.Children.Add(new TextBlock
        {
            Text = fixedPin
                ? $"Enter the PIN set on {request.HubName}."
                : $"Enter the PIN shown on {request.HubName}. To get one, allow a new client in its Networking settings.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = $"Pair with {request.HubName}",
            Content = body,
            PrimaryButtonText = "Pair",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = root,
        };
        box.TextChanged += (_, _) =>
        {
            // A pasted "123 456" or "PIN: 123456" keeps its digits; no MaxLength,
            // which would cut a paste before the filter sees it.
            var digits = new string(box.Text.Where(char.IsAsciiDigit).Take(6).ToArray());
            if (digits != box.Text)
            {
                box.Text = digits;
                box.SelectionStart = digits.Length;
            }
            dialog.IsPrimaryButtonEnabled = digits.Length == 6;
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && box.Text.Length == 6 ? box.Text : null;
    }
}
