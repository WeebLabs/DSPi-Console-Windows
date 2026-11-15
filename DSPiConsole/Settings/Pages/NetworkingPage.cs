using CommunityToolkit.WinUI.Controls;
using DSPiConsole.Core.Link;
using DSPiConsole.Models;
using DSPiConsole.Services;
using DSPiConsole.Usb.Transport;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Settings › Networking (DSPi Link): sharing this PC's board as a hub (its
/// name, access, port and paired clients; NetworkingPage.Hub.cs), then
/// finding other hubs (computers and bridges sharing DSPi devices), the hubs
/// found or added, and adding one by address. Port of the macOS Console's
/// Networking settings.
/// </summary>
public sealed class NetworkingPage : ISettingsPage
{
    public string Id => "general.networking";
    public string Title => "Networking";
    public SettingsCategory Category => SettingsCategory.General;
    public string IconGlyph => ((char)0xE968).ToString(); // Network
    public int Order => 40;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker) => new NetworkingPanel();
}

internal sealed partial class NetworkingPanel : UserControl
{
    private static readonly string GlyphComputer = ((char)0xE7F4).ToString();
    private static readonly string GlyphBridge = ((char)0xE839).ToString();

    private readonly LinkNetwork _link = LinkNetwork.Shared;
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly TextBlock _empty = new() { Margin = new Thickness(2, 4, 2, 0), TextWrapping = TextWrapping.Wrap };

    public NetworkingPanel()
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _empty.Foreground = secondary;

        var auto = new ToggleSwitch { IsOn = AppSettings.Instance.LinkLookForHubs, OnContent = "On", OffContent = "Off" };
        auto.Toggled += (_, _) =>
        {
            AppSettings.Instance.LinkLookForHubs = auto.IsOn;
            AppSettings.Instance.Save();
            _link.ApplySettings();
            Render();
        };

        var add = new Button { Content = "Add by Address...", Margin = new Thickness(0, 8, 0, 0) };
        add.Click += async (_, _) => await AddByAddressAsync();

        var root = new StackPanel { Spacing = 4 };
        BuildHubSection(root);
        root.Children.Add(Section("Other hubs", top: 20));
        root.Children.Add(new SettingsCard
        {
            Header = "Find hubs automatically",
            Description = "Look for computers and DSPi bridges on this network that share DSPi devices.",
            Content = auto,
        });
        root.Children.Add(_list);
        root.Children.Add(_empty);
        root.Children.Add(add);
        root.Children.Add(new TextBlock
        {
            Text = "Devices on other hubs appear in the device list at the bottom of the main window. A hub that asks for a PIN is paired the first time you choose one of its devices, or with its Pair button here.",
            Foreground = secondary,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 12, 2, 0),
        });
        Content = root;

        EventHandler<IReadOnlyList<LinkHubRecord>> onHubs = (_, _) => DispatcherQueue.TryEnqueue(Render);
        // A pairing finishing lists the hub's devices: drop its Pair button.
        EventHandler onDevices = (_, _) => DispatcherQueue.TryEnqueue(Render);
        Loaded += (_, _) =>
        {
            _link.Browser.HubsChanged -= onHubs;
            _link.Browser.HubsChanged += onHubs;
            _link.Transport.AvailableDevicesChanged -= onDevices;
            _link.Transport.AvailableDevicesChanged += onDevices;
            Render();
        };
        Unloaded += (_, _) =>
        {
            _link.Browser.HubsChanged -= onHubs;
            _link.Transport.AvailableDevicesChanged -= onDevices;
        };
    }

    private void Render()
    {
        _list.Children.Clear();
        var hubs = _link.Browser.Hubs;
        var manual = AppSettings.Instance.LinkManualHubs ??= new();
        foreach (var hub in hubs) _list.Children.Add(HubCard(hub));

        // Typed addresses that haven't answered yet are still listed, so they
        // can be removed. One that answered is the hub's own card (listed as
        // discovered, if discovery found it too).
        foreach (var address in manual)
        {
            if (!LinkHubAddress.TryParse(address, out _, out _)) continue;
            if (_link.Browser.AnsweredId(address) is { } id && hubs.Any(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
            _list.Children.Add(UnreachableCard(address));
        }

        _empty.Visibility = _list.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = AppSettings.Instance.LinkLookForHubs ? "Looking for hubs..." : "No hubs added.";
    }

    private SettingsCard HubCard(LinkHubRecord hub)
    {
        // A connected hub's session knows better than its last announcement.
        int count = Math.Max(hub.DeviceCount, _link.Transport.AvailableDevices.Count(d => string.Equals(d.HubId, hub.Id, StringComparison.OrdinalIgnoreCase)));
        var devices = count == 1 ? "1 device" : $"{count} devices";
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = new TextBlock { Text = hub.Auth == LinkAuthMode.Pin ? "Private" : "Open", FontSize = 11 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(badge, hub.Auth == LinkAuthMode.Pin
            ? "This hub asks for a PIN the first time."
            : "Anyone on this network can use this hub's devices.");

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(badge);
        // Not paired yet: pair here rather than through one of its devices,
        // which the device list can only show once the hub has named it.
        if (hub.Auth == LinkAuthMode.Pin && _link.Tokens.Token(hub.Id) == null)
        {
            var pair = new Button { Content = "Pair..." };
            ToolTipService.SetToolTip(pair, "Enter this hub's PIN, so its devices appear in the device list.");
            pair.Click += (_, _) => _link.Transport.OpenHub(hub);
            right.Children.Add(pair);
        }
        // Typed in (whether or not discovery found it too): removable.
        var typed = (AppSettings.Instance.LinkManualHubs ?? new()).Where(a =>
            string.Equals(_link.Browser.AnsweredId(a), hub.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (typed.Count > 0) right.Children.Add(RemoveButton(typed));

        return new SettingsCard
        {
            Header = hub.Name,
            Description = $"{hub.DisplayAddress}, {devices}",
            HeaderIcon = new FontIcon { Glyph = hub.Kind == LinkHubKind.Bridge ? GlyphBridge : GlyphComputer },
            Content = right,
        };
    }

    private SettingsCard UnreachableCard(string address) => new()
    {
        Header = address,
        Description = "Not answering. Console keeps trying.",
        HeaderIcon = new FontIcon { Glyph = GlyphComputer },
        Content = RemoveButton(new[] { address }),
    };

    private Button RemoveButton(IReadOnlyList<string> addresses)
    {
        var remove = new Button { Content = "Remove" };
        remove.Click += (_, _) =>
        {
            (AppSettings.Instance.LinkManualHubs ??= new()).RemoveAll(a => addresses.Contains(a, StringComparer.OrdinalIgnoreCase));
            AppSettings.Instance.Save();
            _link.ApplySettings();
            Render();
        };
        return remove;
    }

    /// <summary>Adds a hub by address (spec 3.3): it must parse, and must not
    /// be this PC's own hub. One that doesn't answer yet is still added, and
    /// tried again now and then.</summary>
    private async Task AddByAddressAsync()
    {
        var box = new TextBox { PlaceholderText = "studio.local, 192.168.1.20:11915 or [fe80::1%12]", Width = 320 };
        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "The address of a computer or bridge sharing DSPi devices.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(box);
        body.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "Add a Hub",
            Content = body,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot,
        };
        box.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = LinkHubAddress.TryParse(box.Text, out _, out _);
            error.Visibility = Visibility.Collapsed;
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var text = box.Text.Trim();
                LinkHubAddress.TryParse(text, out var host, out var port);
                // The same hub by another spelling ("studio.local" and
                // "studio.local:11915") is the same entry.
                if ((AppSettings.Instance.LinkManualHubs ?? new()).Any(a => LinkHubAddress.TryParse(a, out var h, out var p)
                        && p == port && string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
                {
                    Fail("That address is already in the list.");
                    return;
                }
                dialog.IsPrimaryButtonEnabled = false;
                var probed = await _link.Browser.ProbeAsync(text);
                dialog.IsPrimaryButtonEnabled = true;
                if (probed != null && _link.Browser.IsOwnHub(probed))
                {
                    Fail("That is this PC's own hub.");
                    return;
                }
            }
            finally { deferral.Complete(); }

            void Fail(string message)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        (AppSettings.Instance.LinkManualHubs ??= new()).Add(box.Text.Trim());
        AppSettings.Instance.Save();
        _link.ApplySettings();
        Render();
    }

    private static TextBlock Section(string title, double top) => new()
    {
        Text = title,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(1, top, 0, 4),
    };
}
