using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Dialogs;

public sealed partial class ChannelSelectionDialog : ContentDialog
{
    private readonly List<CheckBox> _checkboxes = new();

    public List<int> SelectedChannelIds { get; } = new();

    public ChannelSelectionDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Configure for single-channel import (REW format) - applies to master channels.
    /// </summary>
    public void ConfigureForSingleChannel(int filterCount)
    {
        MessageText.Text = $"Found {filterCount} filter(s). Select which channel(s) to apply them to:";

        var masterChannels = new[] { Channel.MasterLeft, Channel.MasterRight };
        foreach (var channel in masterChannels)
        {
            var checkbox = new CheckBox
            {
                Content = channel.Name,
                Tag = (int)channel.Id,
                IsChecked = true
            };
            _checkboxes.Add(checkbox);
            ChannelCheckboxes.Children.Add(checkbox);
        }
    }

    /// <summary>
    /// Configure for multi-channel import (DSPi format) - shows channels found in file.
    /// </summary>
    public void ConfigureForMultiChannel(IEnumerable<int> availableChannelIds)
    {
        MessageText.Text = "This file contains filter settings for multiple channels. Select which channels to import:";

        foreach (var channelId in availableChannelIds)
        {
            var channel = Channel.All.FirstOrDefault(c => (int)c.Id == channelId);
            if (channel != null)
            {
                var checkbox = new CheckBox
                {
                    Content = channel.Name,
                    Tag = channelId,
                    IsChecked = true
                };
                _checkboxes.Add(checkbox);
                ChannelCheckboxes.Children.Add(checkbox);
            }
        }
    }

    public void CollectSelectedChannels()
    {
        SelectedChannelIds.Clear();
        foreach (var checkbox in _checkboxes)
        {
            if (checkbox.IsChecked == true && checkbox.Tag is int channelId)
            {
                SelectedChannelIds.Add(channelId);
            }
        }
    }
}
