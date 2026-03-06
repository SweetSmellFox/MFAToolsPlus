using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using MFAToolsPlus.Configuration;
using MFAToolsPlus.Helper;
using MFAToolsPlus.Helper.Other;
using MFAToolsPlus.ViewModels.Pages;
using System;

namespace MFAToolsPlus.ViewModels.UsersControls.Settings;

public partial class ToolSettingsUserControlModel : ViewModelBase
{
    [ObservableProperty] private int _expandX = ReadExpandX();
    [ObservableProperty] private int _expandY = ReadExpandY();
    [ObservableProperty] private int _expandW = ReadExpandW();
    [ObservableProperty] private int _expandH = ReadExpandH();

    [ObservableProperty] private AvaloniaList<LocalizationBlock<ClipboardCopyFormat>> _clipboardCopyFormatOptions =
    [
        new("ClipboardCopyFormatValuesOnly")
        {
            Other = ClipboardCopyFormat.ValuesOnly
        },
        new("ClipboardCopyFormatFieldWithValues")
        {
            Other = ClipboardCopyFormat.FieldWithValues
        }
    ];

    [ObservableProperty] private ClipboardCopyFormat _clipboardCopyFormat = ReadClipboardCopyFormat();

    partial void OnExpandXChanged(int value) => UpdateExpand(ConfigurationKeys.LiveViewExpandX, value);

    partial void OnExpandYChanged(int value) => UpdateExpand(ConfigurationKeys.LiveViewExpandY, value);

    partial void OnExpandWChanged(int value) => UpdateExpand(ConfigurationKeys.LiveViewExpandW, value);

    partial void OnExpandHChanged(int value) => UpdateExpand(ConfigurationKeys.LiveViewExpandH, value);

    partial void OnClipboardCopyFormatChanged(ClipboardCopyFormat value) =>
        HandlePropertyChanged(ConfigurationKeys.ClipboardCopyFormat, value.ToString());

    partial void OnResizeShortestSideChanged(int value) =>
        HandlePropertyChanged(ConfigurationKeys.ResizeShortestSide, value);

    partial void OnResizeImportedImageOnImportChanged(bool value) =>
        HandlePropertyChanged(ConfigurationKeys.ResizeImportedImageOnImport, value);

    private void UpdateExpand(string key, int value)
    {
        HandlePropertyChanged(key, value);
        if (Instances.IsResolved<ToolsViewModel>())
        {
            Instances.ToolsViewModel.RefreshExpandedRects();
        }
    }

    private static int ReadExpandX() =>
        ConfigurationManager.Current.GetValue(
            ConfigurationKeys.LiveViewExpandX,
            25);

    private static int ReadExpandY() =>
        ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewExpandY,
            25);

    private static int ReadExpandW()
    {
        var expandX = ReadExpandX();
        return (int)Math.Round(ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewExpandW, expandX * 2.0));
    }

    private static int ReadExpandH()
    {
        var expandY = ReadExpandY();
        return (int)Math.Round(ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewExpandH, expandY * 2.0));
    }

    private static ClipboardCopyFormat ReadClipboardCopyFormat()
    {
        var modeValue = ConfigurationManager.Current.GetValue(
            ConfigurationKeys.ClipboardCopyFormat,
            ClipboardCopyFormat.ValuesOnly.ToString());

        return Enum.TryParse(modeValue, true, out ClipboardCopyFormat mode)
            ? mode
            : ClipboardCopyFormat.ValuesOnly;
    }

    [ObservableProperty] private int _resizeShortestSide = ConfigurationManager.Current.GetValue(ConfigurationKeys.ResizeShortestSide, 720);

    [ObservableProperty] private bool _resizeImportedImageOnImport =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.ResizeImportedImageOnImport, false);
}
