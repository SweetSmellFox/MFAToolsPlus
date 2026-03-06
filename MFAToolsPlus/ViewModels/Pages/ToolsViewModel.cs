using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MFAToolsPlus.Helper.Converters;
using MFAToolsPlus.Configuration;
using MFAToolsPlus.Extensions;
using MFAToolsPlus.Extensions.MaaFW;
using MFAToolsPlus.Helper;
using MFAToolsPlus.Helper.Other;
using MFAToolsPlus.ViewModels.UsersControls;
using Newtonsoft.Json;
using SukiUI.Dialogs;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;

namespace MFAToolsPlus.ViewModels.Pages;

#pragma warning disable CS0618

public enum LiveViewToolMode
{
    None,
    Roi,
    ColorPick,
    Swipe,
    Ocr,
    Screenshot,
    Key,
    NeuralNetworkDetect
}

public enum KeyCodeMode
{
    Win32,
    Adb
}

public enum TestPanelMode
{
    None,
    Ocr,
    Screenshot,
    Color,
    Click,
    Swipe,
    Key,
    NeuralNetworkDetect
}

public enum LiveViewRoiSelectionType
{
    OriginRoi,
    OriginTarget,
    TargetRoi
}

public enum ClipboardCopyFormat
{
    ValuesOnly,
    FieldWithValues
}

public partial class ToolsViewModel : ViewModelBase
{
    private static readonly object ClipboardTempFilesLock = new();
    private static readonly HashSet<string> ClipboardTempFiles = [];
    private static readonly object ClipboardImagesLock = new();
    private static readonly List<IDisposable> ClipboardImages = [];
    private static readonly string ClipboardTempDir = Path.Combine(Path.GetTempPath(), "MFAToolsPlus_Clipboard");

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private ObservableCollection<object> _devices = [];
    [ObservableProperty] private object? _currentDevice;
    private bool _suppressAutoConnect;
    [ObservableProperty] private MaaControllerTypes _currentController =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.CurrentController, MaaControllerTypes.Adb, MaaControllerTypes.None, new UniversalEnumConverter<MaaControllerTypes>());

    /// <summary>
    /// 控制器选项列表
    /// </summary>
    [ObservableProperty] private ObservableCollection<MaaInterface.MaaResourceController> _controllerOptions = [];

    /// <summary>
    /// 当前选中的控制器
    /// </summary>
    [ObservableProperty] private MaaInterface.MaaResourceController? _selectedController;

    partial void OnSelectedControllerChanged(MaaInterface.MaaResourceController? value)
    {
        if (value != null && value.ControllerType != CurrentController)
        {
            CurrentController = value.ControllerType;
        }
    }

    partial void OnCurrentControllerChanged(MaaControllerTypes value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.CurrentController, value.ToString());

        if (value == MaaControllerTypes.PlayCover)
        {
            TryReadPlayCoverConfig();
        }
        Refresh();
    }

    partial void OnCurrentDeviceChanged(object? value)
    {
        ChangedDevice(value);

        if (!_suppressAutoConnect
            && value != null
            && Instances.ConnectSettingsUserControlModel.AutoConnectAfterRefresh)
        {
            _ = TaskManager.RunTaskAsync(() =>
            {
                try
                {
                    MaaProcessor.Instance.TestConnecting().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"Auto connect after device selection failed: {ex.Message}");
                }
            }, name: "选中设备后自动连接", catchException: true, shouldLog: true);
        }
    }

    private DateTime? _lastExecutionTime;

    public void ChangedDevice(object? value)
    {
        var igoreToast = false;
        if (value != null)
        {
            var now = DateTime.Now;
            if (_lastExecutionTime == null)
            {
                _lastExecutionTime = now;
            }
            else
            {
                if (now - _lastExecutionTime < TimeSpan.FromSeconds(2))
                    igoreToast = true;
                else
                    _lastExecutionTime = now;
            }
        }
        SetConnected(false);
        if (value is DesktopWindowInfo window)
        {
            if (!igoreToast) ToastHelper.Info(LangKeys.WindowSelectionMessage.ToLocalizationFormatted(false, ""), window.Name);
            MaaProcessor.Config.DesktopWindow.Name = window.Name;
            MaaProcessor.Config.DesktopWindow.HWnd = window.Handle;
            ConfigurationManager.Current.SetValue(ConfigurationKeys.DesktopWindowClassName, window.ClassName ?? string.Empty);
            ConfigurationManager.Current.SetValue(ConfigurationKeys.DesktopWindowName, window.Name ?? string.Empty);
            MaaProcessor.Instance.SetTasker();
        }
        else if (value is AdbDeviceInfo device)
        {
            if (!igoreToast) ToastHelper.Info(LangKeys.EmulatorSelectionMessage.ToLocalizationFormatted(false, ""), device.Name);
            MaaProcessor.Config.AdbDevice.Name = device.Name;
            MaaProcessor.Config.AdbDevice.AdbPath = device.AdbPath;
            MaaProcessor.Config.AdbDevice.AdbSerial = device.AdbSerial;
            MaaProcessor.Config.AdbDevice.Config = device.Config;
            MaaProcessor.Config.AdbDevice.Info = device;
            MaaProcessor.Instance.SetTasker();
            ConfigurationManager.Current.SetValue(ConfigurationKeys.AdbDevice, device);
        }
    }
    protected override void Initialize()
    {
        InitializeControllerOptions();
        AdbKeyOptions = new ObservableCollection<string>(AdbKeyOptionList);
        Win32KeyOptions = new ObservableCollection<string>(Win32KeyOptionList);
        if (IsKeyCodeAdb)
        {
            UpdateAdbKeyFromInput(AdbKeyInput);
        }
        else
        {
            UpdateWin32KeyFromInput(Win32KeyInput);
        }
    }
    /// <summary>
    /// 初始化控制器列表
    /// 从MaaInterface.Controller加载，如果为空则使用默认的Adb和Win32
    /// </summary>
    public void InitializeControllerOptions()
    {
        try
        {

            // 使用默认的Adb和Win32控制器
            var defaultControllers = CreateDefaultControllers();
            ControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(defaultControllers);


            // 根据当前控制器类型选择对应的控制器
            SelectedController = ControllerOptions.FirstOrDefault(c => c.ControllerType == CurrentController)
                ?? ControllerOptions.FirstOrDefault();
        }
        catch (Exception e)
        {
            LoggerHelper.Error(e);
            // 出错时使用默认控制器
            var defaultControllers = CreateDefaultControllers();
            ControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(defaultControllers);
            SelectedController = ControllerOptions.FirstOrDefault();
        }
    }
    /// <summary>
    /// 创建默认的Adb和Win32控制器
    /// </summary>
    private List<MaaInterface.MaaResourceController> CreateDefaultControllers()
    {
        var adbController = new MaaInterface.MaaResourceController
        {
            Name = "Adb",
            Type = MaaControllerTypes.Adb.ToJsonKey()
        };
        adbController.InitializeDisplayName();
        List<MaaInterface.MaaResourceController> controllers = [adbController];

        var dbgController = new MaaInterface.MaaResourceController
        {
            Name = "Dbg",
            Type = MaaControllerTypes.Dbg.ToJsonKey()
        };
        dbgController.InitializeDisplayName();
        controllers.Add(dbgController);

        if (OperatingSystem.IsWindows())
        {
            var win32Controller = new MaaInterface.MaaResourceController
            {
                Name = "Win32",
                Type = MaaControllerTypes.Win32.ToJsonKey()
            };
            win32Controller.InitializeDisplayName();
            controllers.Add(win32Controller);
        }
        if (OperatingSystem.IsMacOS())
        {
            var playCoverController = new MaaInterface.MaaResourceController
            {
                Name = "PlayCover",
                Type = MaaControllerTypes.PlayCover.ToJsonKey()
            };
            playCoverController.InitializeDisplayName();
            controllers.Add(playCoverController);
        }
        return controllers;
    }


    [ObservableProperty] private bool _isConnected;
    public void SetConnected(bool isConnected)
    {
        // 使用异步投递避免从非UI线程修改属性时导致死锁
        DispatcherHelper.PostOnMainThread(() => IsConnected = isConnected);
    }

    [RelayCommand]
    private void CustomAdb()
    {
        var deviceInfo = CurrentDevice as AdbDeviceInfo;

        Instances.DialogManager.CreateDialog().WithTitle("AdbEditor").WithViewModel(dialog => new AdbEditorDialogViewModel(deviceInfo, dialog)).Dismiss().ByClickingBackground().TryShow();
    }

    [RelayCommand]
    private void EditPlayCover()
    {
        Instances.DialogManager.CreateDialog().WithTitle("PlayCoverEditor")
            .WithViewModel(dialog => new PlayCoverEditorDialogViewModel(MaaProcessor.Config.PlayCover, dialog))
            .Dismiss().ByClickingBackground().TryShow();
    }

    private CancellationTokenSource? _refreshCancellationTokenSource;

    [RelayCommand]
    private async Task Reconnect()
    {
        if (CurrentController != MaaControllerTypes.PlayCover
            && CurrentController != MaaControllerTypes.Dbg
            && CurrentDevice == null)
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), "DeviceNotSelected".ToLocalization());
            LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
            return;
        }

        if (CurrentController == MaaControllerTypes.Adb
            && CurrentDevice is AdbDeviceInfo adbInfo
            && string.IsNullOrWhiteSpace(adbInfo.AdbSerial))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.AdbAddressEmpty.ToLocalization());
            LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
            return;
        }

        if (CurrentController == MaaControllerTypes.PlayCover
            && string.IsNullOrWhiteSpace(MaaProcessor.Config.PlayCover.PlayCoverAddress))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.PlayCoverAddressEmpty.ToLocalization());
            LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
            return;
        }

        try
        {
            using var tokenSource = new CancellationTokenSource();
            await MaaProcessor.Instance.ReconnectAsync(tokenSource.Token);
            await MaaProcessor.Instance.TestConnecting();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Reconnect failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        if (CurrentController == MaaControllerTypes.PlayCover
            || CurrentController == MaaControllerTypes.Dbg)
        {
            SetConnected(false);
            return;
        }

        _refreshCancellationTokenSource?.Cancel();
        _refreshCancellationTokenSource = new CancellationTokenSource();
        var controllerType = CurrentController;
        TaskManager.RunTask(() =>
            {
                AutoDetectDevice(_refreshCancellationTokenSource.Token);

                if (CurrentDevice != null
                    && Instances.ConnectSettingsUserControlModel.AutoConnectAfterRefresh)
                {
                    try
                    {
                        _refreshCancellationTokenSource.Token.ThrowIfCancellationRequested();
                        MaaProcessor.Instance.TestConnecting().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Warning($"Auto connect after refresh failed: {ex.Message}");
                    }
                }
            }, _refreshCancellationTokenSource.Token, name: "刷新", handleError: (e) => HandleDetectionError(e, controllerType),
            catchException: true, shouldLog: true);
    }
    public void AutoDetectDevice(CancellationToken token = default)
    {
        if (CurrentController == MaaControllerTypes.PlayCover
            || CurrentController == MaaControllerTypes.Dbg)
        {
            DispatcherHelper.RunOnMainThread(() =>
            {
                Devices = [];
                CurrentDevice = null;
            });
            SetConnected(false);
            return;
        }

        var controllerType = CurrentController;
        var isAdb = controllerType == MaaControllerTypes.Adb;

        ToastHelper.Info(GetDetectionMessage(controllerType));
        SetConnected(false);
        token.ThrowIfCancellationRequested();
        var (devices, index) = isAdb ? DetectAdbDevices() : DetectWin32Windows();
        token.ThrowIfCancellationRequested();
        UpdateDeviceList(devices, index);
        token.ThrowIfCancellationRequested();
        HandleControllerSettings(controllerType);
        token.ThrowIfCancellationRequested();
        UpdateConnectionStatus(devices.Count > 0, controllerType);
    }

    private string GetDetectionMessage(MaaControllerTypes controllerType) =>
        controllerType == MaaControllerTypes.Adb
            ? "EmulatorDetectionStarted".ToLocalization()
            : "WindowDetectionStarted".ToLocalization();

    private (ObservableCollection<object> devices, int index) DetectAdbDevices()
    {
        var devices = MaaProcessor.Toolkit.AdbDevice.Find();
        var index = CalculateAdbDeviceIndex(devices);
        return (new(devices), index);
    }

    private int CalculateAdbDeviceIndex(IList<AdbDeviceInfo> devices)
    {
        if (CurrentDevice is AdbDeviceInfo info)
        {
            LoggerHelper.Info($"Current device: {JsonConvert.SerializeObject(info)}");

            // 使用指纹匹配设备
            var matchedDevices = devices
                .Where(device => device.MatchesFingerprint(info))
                .ToList();

            LoggerHelper.Info($"Found {matchedDevices.Count} devices matching fingerprint");

            // 多匹配时排序：先比AdbSerial前缀（冒号前），再比设备名称
            if (matchedDevices.Any())
            {
                matchedDevices.Sort((a, b) =>
                {
                    var aPrefix = a.AdbSerial.Split(':', 2)[0];
                    var bPrefix = b.AdbSerial.Split(':', 2)[0];
                    int prefixCompare = string.Compare(aPrefix, bPrefix, StringComparison.Ordinal);
                    return prefixCompare != 0 ? prefixCompare : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                });
                return devices.IndexOf(matchedDevices.First());
            }
        }

        var config = ConfigurationManager.Current.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty);
        if (string.IsNullOrWhiteSpace(config)) return 0;

        var targetNumber = ExtractNumberFromEmulatorConfig(config);
        return devices.Select((d, i) =>
                TryGetIndexFromConfig(d.Config, out var index) && index == targetNumber ? i : -1)
            .FirstOrDefault(i => i >= 0);
    }


    public static int ExtractNumberFromEmulatorConfig(string emulatorConfig)
    {
        var match = Regex.Match(emulatorConfig, @"\d+");

        if (match.Success)
        {
            return int.Parse(match.Value);
        }

        return 0;
    }
    private (ObservableCollection<object> devices, int index) DetectWin32Windows()
    {
        Thread.Sleep(500);
        var windows = MaaProcessor.Toolkit.Desktop.Window.Find().Where(win => !string.IsNullOrWhiteSpace(win.Name)).ToList();
        var (index, filtered) = CalculateWindowIndex(windows);
        return (new(filtered), index);
    }

    private (int index, List<DesktopWindowInfo> afterFiltered) CalculateWindowIndex(List<DesktopWindowInfo> windows)
    {
        var controller = SelectedController
            ?? ControllerOptions.FirstOrDefault(c => c.Type?.Equals("win32", StringComparison.OrdinalIgnoreCase) == true);

        if (controller?.Win32 == null)
        {
            var idx = MatchPreviousWindow(windows);
            return (idx >= 0 ? idx : Math.Max(0, windows.FindIndex(win => !string.IsNullOrWhiteSpace(win.Name))), windows);
        }

        var filtered = windows.Where(win =>
            !string.IsNullOrWhiteSpace(win.Name)).ToList();

        filtered = ApplyRegexFilters(filtered, controller.Win32);
        var matchedIdx = MatchPreviousWindow(filtered);
        return (matchedIdx >= 0 ? matchedIdx : 0, filtered.ToList());
    }

    private int MatchPreviousWindow(List<DesktopWindowInfo> windows)
    {
        if (windows.Count == 0)
            return -1;

        if (CurrentDevice is DesktopWindowInfo previousWindow)
        {
            var exactMatch = windows.FindIndex(window =>
                string.Equals(window.ClassName, previousWindow.ClassName, StringComparison.Ordinal)
                && string.Equals(window.Name, previousWindow.Name, StringComparison.Ordinal));
            if (exactMatch >= 0)
                return exactMatch;

            var classMatch = windows.FindIndex(window =>
                string.Equals(window.ClassName, previousWindow.ClassName, StringComparison.Ordinal));
            if (classMatch >= 0)
                return classMatch;

            return -1;
        }

        var savedClassName = ConfigurationManager.Current.GetValue(ConfigurationKeys.DesktopWindowClassName, string.Empty);
        var savedWindowName = ConfigurationManager.Current.GetValue(ConfigurationKeys.DesktopWindowName, string.Empty);

        if (!string.IsNullOrEmpty(savedClassName))
        {
            var exactMatch = windows.FindIndex(window =>
                string.Equals(window.ClassName, savedClassName, StringComparison.Ordinal)
                && string.Equals(window.Name, savedWindowName, StringComparison.Ordinal));
            if (exactMatch >= 0)
                return exactMatch;

            var classMatch = windows.FindIndex(window =>
                string.Equals(window.ClassName, savedClassName, StringComparison.Ordinal));
            if (classMatch >= 0)
                return classMatch;
        }

        return -1;
    }


    private List<DesktopWindowInfo> ApplyRegexFilters(List<DesktopWindowInfo> windows, MaaInterface.MaaResourceControllerWin32 win32)
    {
        var filtered = windows;
        if (!string.IsNullOrWhiteSpace(win32.WindowRegex))
        {
            var regex = new Regex(win32.WindowRegex);
            filtered = filtered.Where(w => regex.IsMatch(w.Name)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(win32.ClassRegex))
        {
            var regex = new Regex(win32.ClassRegex);
            filtered = filtered.Where(w => regex.IsMatch(w.ClassName)).ToList();
        }
        return filtered;
    }

    private void UpdateDeviceList(ObservableCollection<object> devices, int index)
    {
        DispatcherHelper.RunOnMainThread(() =>
        {
            _suppressAutoConnect = true;
            try
            {
                Devices = devices;
                if (devices.Count > index)
                    CurrentDevice = devices[index];
                else
                    CurrentDevice = null;
            }
            finally
            {
                _suppressAutoConnect = false;
            }
        });
    }

    private bool TryGetIndexFromConfig(string configJson, out int index)
    {
        index = DeviceDisplayConverter.GetFirstEmulatorIndex(configJson);
        return index != -1;
    }

    public void TryReadAdbDeviceFromConfig(bool inTask = true, bool refresh = false)
    {
        if (CurrentController == MaaControllerTypes.PlayCover
            || CurrentController == MaaControllerTypes.Dbg)
        {
            SetConnected(false);
            return;
        }

        if (refresh
            || CurrentController != MaaControllerTypes.Adb
            || !ConfigurationManager.Current.GetValue(ConfigurationKeys.RememberAdb, true)
            || MaaProcessor.Config.AdbDevice.AdbPath != "adb"
            || !ConfigurationManager.Current.TryGetValue(ConfigurationKeys.AdbDevice, out AdbDeviceInfo savedDevice,
                new UniversalEnumConverter<AdbInputMethods>(), new UniversalEnumConverter<AdbScreencapMethods>()))
        {
            _refreshCancellationTokenSource?.Cancel();
            _refreshCancellationTokenSource = new CancellationTokenSource();
            if (inTask)
                TaskManager.RunTask(() => AutoDetectDevice(_refreshCancellationTokenSource.Token), name: "刷新设备");
            else
                AutoDetectDevice(_refreshCancellationTokenSource.Token);
            return;
        }
        // 检查是否启用指纹匹配功能
        var useFingerprintMatching = ConfigurationManager.Current.GetValue(ConfigurationKeys.UseFingerprintMatching, false);

        if (useFingerprintMatching)
        {
            // // 使用指纹匹配设备，而不是直接使用保存的设备信息
            // // 因为雷电模拟器等的AdbSerial每次启动都会变化
            // LoggerHelper.Info("Reading saved ADB device from configuration, using fingerprint matching.");
            // LoggerHelper.Info($"Saved device fingerprint: {savedDevice.GenerateDeviceFingerprint()}");
            //
            // // 搜索当前可用的设备
            // var currentDevices = MaaProcessor.Toolkit.AdbDevice.Find();
            //
            // // 尝试通过指纹匹配找到对应的设备（当任一方index为-1时不比较index）
            // AdbDeviceInfo? matchedDevice = null;
            // foreach (var device in currentDevices)
            // {
            //     if (device.MatchesFingerprint(savedDevice))
            //     {
            //         matchedDevice = device;
            //         LoggerHelper.Info($"Found matching device by fingerprint: {device.Name} ({device.AdbSerial})");
            //         break;
            //     }
            // }
            //
            // if (matchedDevice != null)
            // {
            //     // 使用新搜索到的设备信息（AdbSerial等可能已更新）
            //     DispatcherHelper.RunOnMainThread(() =>
            //     {
            //         Devices = new ObservableCollection<object>(currentDevices);
            //         CurrentDevice = matchedDevice;
            //     });
            //     ChangedDevice(matchedDevice);
            // }
            // else
            // {
            //     // 没有找到匹配的设备，执行自动检测
            //     LoggerHelper.Info("No matching device found by fingerprint, performing auto detection.");
            //     _refreshCancellationTokenSource?.Cancel();
            //     _refreshCancellationTokenSource = new CancellationTokenSource();
            //     if (inTask)
            //         TaskManager.RunTask(() => AutoDetectDevice(_refreshCancellationTokenSource.Token), name: "刷新设备");
            //     else
            //         AutoDetectDevice(_refreshCancellationTokenSource.Token);
            // }
        }
        else
        {
            // 不使用指纹匹配，直接使用保存的设备信息
            LoggerHelper.Info("Reading saved ADB device from configuration, fingerprint matching disabled.");
            DispatcherHelper.RunOnMainThread(() =>
            {
                Devices = [savedDevice];
                CurrentDevice = savedDevice;
            });
            ChangedDevice(savedDevice);
        }
    }

    private void HandleControllerSettings(MaaControllerTypes controllerType)
    {
        if (controllerType == MaaControllerTypes.PlayCover
            || controllerType == MaaControllerTypes.Dbg)
            return;

        MaaInterface.MaaResourceController? controller = null;

        if (controller == null) return;

        var isAdb = controllerType == MaaControllerTypes.Adb;
        HandleInputSettings(controller, isAdb);
        HandleScreenCapSettings(controller, isAdb);
    }

    private void HandleInputSettings(MaaInterface.MaaResourceController controller, bool isAdb)
    {
        if (isAdb)
        {
            var input = controller.Adb?.Input;
            if (input == null) return;
            Instances.ConnectSettingsUserControlModel.AdbControlInputType = input switch
            {
                1 => AdbInputMethods.AdbShell,
                2 => AdbInputMethods.MinitouchAndAdbKey,
                4 => AdbInputMethods.Maatouch,
                8 => AdbInputMethods.EmulatorExtras,
                _ => Instances.ConnectSettingsUserControlModel.AdbControlInputType
            };
        }
        else
        {
            var mouse = controller.Win32?.Mouse;
            if (mouse != null)
            {
                var parsed = ParseWin32InputMethod(mouse);
                if (parsed != null)
                    Instances.ConnectSettingsUserControlModel.Win32ControlMouseType = parsed.Value;
            }
            var keyboard = controller.Win32?.Keyboard;
            if (keyboard != null)
            {
                var parsed = ParseWin32InputMethod(keyboard);
                if (parsed != null)
                    Instances.ConnectSettingsUserControlModel.Win32ControlKeyboardType = parsed.Value;
            }
            var input = controller.Win32?.Input;
            if (keyboard == null && mouse == null && input != null)
            {
                var parsed = ParseWin32InputMethod(input);
                if (parsed != null)
                {
                    Instances.ConnectSettingsUserControlModel.Win32ControlKeyboardType = parsed.Value;
                    Instances.ConnectSettingsUserControlModel.Win32ControlMouseType = parsed.Value;
                }
            }
        }
    }

    /// <summary>
    /// 解析 Win32InputMethod，支持旧版 long 格式和新版 string 格式
    /// </summary>
    private static Win32InputMethod? ParseWin32InputMethod(object? value)
    {
        if (value == null) return null;

        // 新版 string 格式（枚举名）
        if (value is string strValue)
        {
            if (Enum.TryParse<Win32InputMethod>(strValue, ignoreCase: true, out var result))
                return result;
            return null;
        }

        // 旧版 long 格式
        var longValue = Convert.ToInt64(value);
        return longValue switch
        {
            1 => Win32InputMethod.Seize,
            2 => Win32InputMethod.SendMessage,
            4 => Win32InputMethod.PostMessage,
            8 => Win32InputMethod.LegacyEvent,
            16 => Win32InputMethod.PostThreadMessage,
            32 => Win32InputMethod.SendMessageWithCursorPos,
            64 => Win32InputMethod.PostMessageWithCursorPos,
            _ => null
        };
    }

    /// <summary>
    /// 解析 Win32ScreencapMethod，支持旧版 long 格式和新版 string 格式
    /// </summary>
    private static Win32ScreencapMethod? ParseWin32ScreencapMethod(object? value)
    {
        if (value == null) return null;

        // 新版 string 格式（枚举名）
        if (value is string strValue)
        {
            if (Enum.TryParse<Win32ScreencapMethod>(strValue, ignoreCase: true, out var result))
                return result;
            return null;
        }

        // 旧版 long 格式
        var longValue = Convert.ToInt64(value);
        return longValue switch
        {
            1 => Win32ScreencapMethod.GDI,
            2 => Win32ScreencapMethod.FramePool,
            4 => Win32ScreencapMethod.DXGI_DesktopDup,
            8 => Win32ScreencapMethod.DXGI_DesktopDup_Window,
            16 => Win32ScreencapMethod.PrintWindow,
            32 => Win32ScreencapMethod.ScreenDC,
            _ => null
        };
    }

    private void HandleScreenCapSettings(MaaInterface.MaaResourceController controller, bool isAdb)
    {
        if (isAdb)
        {
            var screenCap = controller.Adb?.ScreenCap;
            if (screenCap == null) return;
            Instances.ConnectSettingsUserControlModel.AdbControlScreenCapType = screenCap switch
            {
                1 => AdbScreencapMethods.EncodeToFileAndPull,
                2 => AdbScreencapMethods.Encode,
                4 => AdbScreencapMethods.RawWithGzip,
                8 => AdbScreencapMethods.RawByNetcat,
                16 => AdbScreencapMethods.MinicapDirect,
                32 => AdbScreencapMethods.MinicapStream,
                64 => AdbScreencapMethods.EmulatorExtras,
                _ => Instances.ConnectSettingsUserControlModel.AdbControlScreenCapType
            };
        }
        else
        {
            var screenCap = controller.Win32?.ScreenCap;
            if (screenCap == null) return;
            var parsed = ParseWin32ScreencapMethod(screenCap);
            if (parsed != null)
                Instances.ConnectSettingsUserControlModel.Win32ControlScreenCapType = parsed.Value;
        }
    }

    private void UpdateConnectionStatus(bool hasDevices, MaaControllerTypes controllerType)
    {
        if (!hasDevices)
        {
            var isAdb = controllerType == MaaControllerTypes.Adb;
            ToastHelper.Info((
                isAdb ? LangKeys.NoEmulatorFound : LangKeys.NoWindowFound).ToLocalization(), (
                isAdb ? LangKeys.NoEmulatorFoundDetail : "").ToLocalization());
        }
    }

    public void TryReadPlayCoverConfig()
    {
        if (ConfigurationManager.Current.TryGetValue(ConfigurationKeys.PlayCoverConfig, out PlayCoverCoreConfig savedConfig))
        {
            MaaProcessor.Config.PlayCover = savedConfig;
        }
    }

    private void HandleDetectionError(Exception ex, MaaControllerTypes controllerType)
    {
        var targetKey = controllerType switch
        {
            MaaControllerTypes.Adb => LangKeys.Emulator,
            MaaControllerTypes.Win32 => LangKeys.Window,
            MaaControllerTypes.PlayCover => LangKeys.TabPlayCover,
            MaaControllerTypes.Dbg => "TabDbg",
            _ => LangKeys.Window
        };
        ToastHelper.Warn(string.Format(
            LangKeys.TaskStackError.ToLocalization(),
            targetKey.ToLocalization(),
            ex.Message));

        LoggerHelper.Error(ex);
    }
    #region 实时画面

    [ObservableProperty] private bool _isLiveViewPaused;
    [ObservableProperty] private double _liveViewScale = 1;
    [ObservableProperty] private LiveViewToolMode _activeToolMode = LiveViewToolMode.None;
    [ObservableProperty] private Rect _selectionRect;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private Rect _secondarySelectionRect;
    [ObservableProperty] private bool _hasSecondarySelection;
    [ObservableProperty] private bool _isToolPanelVisible;
    [ObservableProperty] private bool _isTestPanelVisible;
    [ObservableProperty] private TestPanelMode _activeTestPanelMode = TestPanelMode.None;
    [ObservableProperty] private bool _isOcrTestPanelVisible;
    [ObservableProperty] private bool _isScreenshotTestPanelVisible;
    [ObservableProperty] private bool _isColorTestPanelVisible;
    [ObservableProperty] private bool _isClickTestPanelVisible;
    [ObservableProperty] private bool _isSwipeTestPanelVisible;
    [ObservableProperty] private bool _isKeyTestPanelVisible;
    [ObservableProperty] private bool _isNeuralNetworkDetectTestPanelVisible;
    [ObservableProperty] private bool _isDragMode = true;
    [ObservableProperty] private bool _isNeuralNetworkDetectMode;
    [ObservableProperty] private bool _isRoiMode;
    [ObservableProperty] private bool _isColorPickMode;
    [ObservableProperty] private bool _isSwipeMode;
    [ObservableProperty] private bool _isOcrMode;
    [ObservableProperty] private bool _isScreenshotMode;
    [ObservableProperty] private bool _isKeyMode;
    [ObservableProperty] private bool _isTargetMode;
    [ObservableProperty] private bool _isScreenshotBrushMode;
    [ObservableProperty] private int _screenshotBrushSize = 1;
    [ObservableProperty] private LiveViewRoiSelectionType _roiSelectionType = LiveViewRoiSelectionType.OriginRoi;
    [ObservableProperty] private ObservableCollection<string> _roiModeOptions = ["ROI", "Target"];
    [ObservableProperty] private int _roiModeIndex;

    private static readonly IBrush RoiSelectionStroke = new SolidColorBrush(Colors.DodgerBlue);
    private static readonly IBrush RoiSelectionFill = new SolidColorBrush(Color.FromArgb(40, 64, 128, 255));
    private static readonly IBrush TargetSelectionStrokeDefault = new SolidColorBrush(Colors.Orange);
    private static readonly IBrush TargetSelectionFillDefault = new SolidColorBrush(Color.FromArgb(40, 255, 165, 0));

    [ObservableProperty] private IBrush _selectionStroke = RoiSelectionStroke;
    [ObservableProperty] private IBrush _selectionFill = RoiSelectionFill;
    [ObservableProperty] private IBrush _secondarySelectionStroke = TargetSelectionStrokeDefault;
    [ObservableProperty] private IBrush _secondarySelectionFill = TargetSelectionFillDefault;
    [ObservableProperty] private IBrush _targetSelectionStroke = TargetSelectionStrokeDefault;
    [ObservableProperty] private IBrush _targetSelectionFill = TargetSelectionFillDefault;
    [ObservableProperty] private bool _hasSwipeArrow;
    [ObservableProperty] private Geometry? _swipeArrowGeometry;
    [ObservableProperty] private IBrush _swipeArrowStroke = TargetSelectionStrokeDefault;
    [ObservableProperty] private bool _isSelectingTarget;
    [ObservableProperty] private int _roiTargetIndex;
    [ObservableProperty] private ObservableCollection<string> _roiTargetOptions = ["ROI", "Target"];
    [ObservableProperty] private string _currentRoiSelectionLabel = "当前选择: ROI";

    [ObservableProperty] private string _roiX = "0";
    [ObservableProperty] private string _roiY = "0";
    [ObservableProperty] private string _roiW = "0";
    [ObservableProperty] private string _roiH = "0";

    [ObservableProperty] private string _colorPickX = "0";
    [ObservableProperty] private string _colorPickY = "0";
    [ObservableProperty] private string _colorPickW = "0";
    [ObservableProperty] private string _colorPickH = "0";
    [ObservableProperty] private string _colorPickExpandedX = "0";
    [ObservableProperty] private string _colorPickExpandedY = "0";
    [ObservableProperty] private string _colorPickExpandedW = "0";
    [ObservableProperty] private string _colorPickExpandedH = "0";

    [ObservableProperty] private string _screenshotX = "0";
    [ObservableProperty] private string _screenshotY = "0";
    [ObservableProperty] private string _screenshotW = "0";
    [ObservableProperty] private string _screenshotH = "0";
    [ObservableProperty] private string _screenshotExpandedX = "0";
    [ObservableProperty] private string _screenshotExpandedY = "0";
    [ObservableProperty] private string _screenshotExpandedW = "0";
    [ObservableProperty] private string _screenshotExpandedH = "0";
    [ObservableProperty] private string _screenshotRelativePath =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewScreenshotRelativePath, string.Empty);
    [ObservableProperty] private string _screenshotRelativeResult = string.Empty;

    [ObservableProperty] private string _ocrX = "0";
    [ObservableProperty] private string _ocrY = "0";
    [ObservableProperty] private string _ocrW = "0";
    [ObservableProperty] private string _ocrH = "0";
    [ObservableProperty] private string _ocrExpandedX = "0";
    [ObservableProperty] private string _ocrExpandedY = "0";
    [ObservableProperty] private string _ocrExpandedW = "0";
    [ObservableProperty] private string _ocrExpandedH = "0";
    [ObservableProperty] private string _ocrResult = string.Empty;

    [ObservableProperty] private string _ocrMatchRoiX = "0";
    [ObservableProperty] private string _ocrMatchRoiY = "0";
    [ObservableProperty] private string _ocrMatchRoiW = "0";
    [ObservableProperty] private string _ocrMatchRoiH = "0";
    [ObservableProperty] private string _ocrMatchTargetText = string.Empty;
    [ObservableProperty] private string _ocrMatchThreshold = "0.3";
    [ObservableProperty] private bool _ocrMatchOnlyRec = false;

    [ObservableProperty] private string _templateMatchRoiX = "0";
    [ObservableProperty] private string _templateMatchRoiY = "0";
    [ObservableProperty] private string _templateMatchRoiW = "0";
    [ObservableProperty] private string _templateMatchRoiH = "0";
    [ObservableProperty] private string _templateMatchThreshold = "0.7";
    [ObservableProperty] private bool _templateMatchGreenMask;
    [ObservableProperty] private int _templateMatchMethod = 5;
    [ObservableProperty] private Bitmap? _templateMatchImage;
    [ObservableProperty] private ObservableCollection<int> _templateMatchMethodOptions = [5, 3, 10001];

        [ObservableProperty] private string _clickTestTargetX = "0";
        [ObservableProperty] private string _clickTestTargetY = "0";
        [ObservableProperty] private string _clickTestTargetW = "0";
        [ObservableProperty] private string _clickTestTargetH = "0";
        [ObservableProperty] private string _clickTestOffsetX = "0";
        [ObservableProperty] private string _clickTestOffsetY = "0";
        [ObservableProperty] private string _clickTestOffsetW = "0";
        [ObservableProperty] private string _clickTestOffsetH = "0";
        [ObservableProperty] private string _clickTestContact = "0";

    [ObservableProperty] private string _swipeTestStartX = "0";
    [ObservableProperty] private string _swipeTestStartY = "0";
    [ObservableProperty] private string _swipeTestEndX = "0";
    [ObservableProperty] private string _swipeTestEndY = "0";
    [ObservableProperty] private string _swipeTestDuration = "200";
    [ObservableProperty] private ObservableCollection<Helper.Other.PairRow> _swipeTestEndRows = [];

    [ObservableProperty] private string _swipeStartX = "0";
    [ObservableProperty] private string _swipeStartY = "0";
    [ObservableProperty] private string _swipeEndX = "0";
    [ObservableProperty] private string _swipeEndY = "0";

    [ObservableProperty] private string _originTargetX = "0";
    [ObservableProperty] private string _originTargetY = "0";
    [ObservableProperty] private string _originTargetW = "0";
    [ObservableProperty] private string _originTargetH = "0";

    [ObservableProperty] private string _targetX = "0";
    [ObservableProperty] private string _targetY = "0";
    [ObservableProperty] private string _targetW = "0";
    [ObservableProperty] private string _targetH = "0";

    [ObservableProperty] private string _offsetX = "0";
    [ObservableProperty] private string _offsetY = "0";
    [ObservableProperty] private string _offsetW = "0";
    [ObservableProperty] private string _offsetH = "0";

    [ObservableProperty] private int _colorMode;
    [ObservableProperty] private int _colorMatchMethod = 4;
    [ObservableProperty] private string _colorMatchCount = "1";
    [ObservableProperty] private bool _colorMatchConnected;
    [ObservableProperty] private string _colorMatchRoiX = "0";
    [ObservableProperty] private string _colorMatchRoiY = "0";
    [ObservableProperty] private string _colorMatchRoiW = "0";
    [ObservableProperty] private string _colorMatchRoiH = "0";
    [ObservableProperty] private string _rgbUpperR = "0";
    [ObservableProperty] private string _rgbUpperG = "0";
    [ObservableProperty] private string _rgbUpperB = "0";
    [ObservableProperty] private string _rgbLowerR = "0";
    [ObservableProperty] private string _rgbLowerG = "0";
    [ObservableProperty] private string _rgbLowerB = "0";

    [ObservableProperty] private string _hsvUpperH = "0";
    [ObservableProperty] private string _hsvUpperS = "0";
    [ObservableProperty] private string _hsvUpperV = "0";
    [ObservableProperty] private string _hsvLowerH = "0";
    [ObservableProperty] private string _hsvLowerS = "0";
    [ObservableProperty] private string _hsvLowerV = "0";

    [ObservableProperty] private string _grayUpper = "0";
    [ObservableProperty] private string _grayLower = "0";

    [ObservableProperty] private string _neuralNetworkDetectX = "0";
    [ObservableProperty] private string _neuralNetworkDetectY = "0";
    [ObservableProperty] private string _neuralNetworkDetectW = "0";
    [ObservableProperty] private string _neuralNetworkDetectH = "0";
    [ObservableProperty] private string _neuralNetworkDetectExpandedX = "0";
    [ObservableProperty] private string _neuralNetworkDetectExpandedY = "0";
    [ObservableProperty] private string _neuralNetworkDetectExpandedW = "0";
    [ObservableProperty] private string _neuralNetworkDetectExpandedH = "0";
    [ObservableProperty] private string _neuralNetworkDetectMatchRoiX = "0";
    [ObservableProperty] private string _neuralNetworkDetectMatchRoiY = "0";
    [ObservableProperty] private string _neuralNetworkDetectMatchRoiW = "0";
    [ObservableProperty] private string _neuralNetworkDetectMatchRoiH = "0";
    
    [ObservableProperty] private string _neuralNetworkDetectIndex = "0";
    [ObservableProperty] private string _neuralNetworkDetectOrderBy = "Horizontal";
    [ObservableProperty] private string _neuralNetworkDetectModelPath = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _orderByOptions = ["Horizontal", "Vertical", "Score", "Area", "Random", "Expected"];
    [ObservableProperty] private ObservableCollection<Helper.Other.SingleRow> _classifyLabelRows = [];
    [ObservableProperty] private ObservableCollection<Helper.Other.SingleRow> _expectedRows = [];
    [ObservableProperty] private ObservableCollection<Helper.Other.SingleRow> _thresholdRows = [];

    public IAvaloniaReadOnlyList<LocalizationBlock> ColorMethods => new AvaloniaList<LocalizationBlock>()
    {
        new()
        {
            Name = "RGB",
            Other = 4
        },
        new()
        {
            Name = "HSV",
            Other = 40
        },
        new()
        {
            Name = "Gray",
            Other = 6
        }
    };
    [ObservableProperty] private ObservableCollection<Helper.Other.TripletRow> _rgbUpperRows = [];
    [ObservableProperty] private ObservableCollection<Helper.Other.TripletRow> _rgbLowerRows = [];
    [ObservableProperty] private ObservableCollection<Helper.Other.TripletRow> _hsvUpperRows = [];
    [ObservableProperty] private ObservableCollection<Helper.Other.TripletRow> _hsvLowerRows = [];
    [ObservableProperty] private ObservableCollection<Helper.Other.SingleRow> _grayUpperRows = [];
    [ObservableProperty] private ObservableCollection<Helper.Other.SingleRow> _grayLowerRows = [];

    private Rect _roiRect;
    private Rect _originTargetRect;
    private Rect _targetRect;
    private Rect _colorPickRect;
    private Rect _colorPickExpandedRect;
    private Rect _screenshotRect;
    private Rect _screenshotExpandedRect;
    private Rect _ocrRect;
    private Rect _ocrExpandedRect;
    private Rect _neuralNetworkDetectRect;
    private Rect _neuralNetworkDetectExpandedRect;
    private bool _suppressRoiSync;
    private bool _suppressNeuralNetworkDetectSync;
    private bool _suppressTargetSync;
    private bool _suppressToolModeSync;
    private bool _suppressRoiTargetSync;
    private bool _suppressColorPickSync;
    private bool _suppressScreenshotSync;
    private bool _suppressOcrSync;
    private bool _suppressTestPanelSync;

    [ObservableProperty] private bool _isColorPreviewActive;
    [ObservableProperty] private Color _colorPreviewFillColor = Colors.Black;
    private Bitmap? _colorPreviewImage;
    private WriteableBitmap? _screenshotBrushImage;

    [ObservableProperty] private bool _isKeyCodeAdb;
    [ObservableProperty] private bool _isKeyCaptureActive;
    [ObservableProperty] private string _keyCaptureKey = string.Empty;
    [ObservableProperty] private string _keyCaptureCode = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _adbKeyOptions = new(AdbKeyOptionList);
    [ObservableProperty] private string _adbKeyInput = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _win32KeyOptions = new(Win32KeyOptionList);
    [ObservableProperty] private string _win32KeyInput = string.Empty;
    private static readonly Dictionary<string, int> AdbKeyDefinitions = new(StringComparer.Ordinal)
    {
        {
            "KEYCODE_0", 7
        },
        {
            "KEYCODE_1", 8
        },
        {
            "KEYCODE_11", 227
        },
        {
            "KEYCODE_12", 228
        },
        {
            "KEYCODE_2", 9
        },
        {
            "KEYCODE_3", 10
        },
        {
            "KEYCODE_3D_MODE", 206
        },
        {
            "KEYCODE_4", 11
        },
        {
            "KEYCODE_5", 12
        },
        {
            "KEYCODE_6", 13
        },
        {
            "KEYCODE_7", 14
        },
        {
            "KEYCODE_8", 15
        },
        {
            "KEYCODE_9", 16
        },
        {
            "KEYCODE_A", 29
        },
        {
            "KEYCODE_ALL_APPS", 284
        },
        {
            "KEYCODE_ALT_LEFT", 57
        },
        {
            "KEYCODE_ALT_RIGHT", 58
        },
        {
            "KEYCODE_APOSTROPHE", 75
        },
        {
            "KEYCODE_APP_SWITCH", 187
        },
        {
            "KEYCODE_ASSIST", 219
        },
        {
            "KEYCODE_AT", 77
        },
        {
            "KEYCODE_AVR_INPUT", 182
        },
        {
            "KEYCODE_AVR_POWER", 181
        },
        {
            "KEYCODE_B", 30
        },
        {
            "KEYCODE_BACK", 4
        },
        {
            "KEYCODE_BACKSLASH", 73
        },
        {
            "KEYCODE_BOOKMARK", 174
        },
        {
            "KEYCODE_BREAK", 121
        },
        {
            "KEYCODE_BRIGHTNESS_DOWN", 220
        },
        {
            "KEYCODE_BRIGHTNESS_UP", 221
        },
        {
            "KEYCODE_BUTTON_1", 188
        },
        {
            "KEYCODE_BUTTON_10", 197
        },
        {
            "KEYCODE_BUTTON_11", 198
        },
        {
            "KEYCODE_BUTTON_12", 199
        },
        {
            "KEYCODE_BUTTON_13", 200
        },
        {
            "KEYCODE_BUTTON_14", 201
        },
        {
            "KEYCODE_BUTTON_15", 202
        },
        {
            "KEYCODE_BUTTON_16", 203
        },
        {
            "KEYCODE_BUTTON_2", 189
        },
        {
            "KEYCODE_BUTTON_3", 190
        },
        {
            "KEYCODE_BUTTON_4", 191
        },
        {
            "KEYCODE_BUTTON_5", 192
        },
        {
            "KEYCODE_BUTTON_6", 193
        },
        {
            "KEYCODE_BUTTON_7", 194
        },
        {
            "KEYCODE_BUTTON_8", 195
        },
        {
            "KEYCODE_BUTTON_9", 196
        },
        {
            "KEYCODE_BUTTON_A", 96
        },
        {
            "KEYCODE_BUTTON_B", 97
        },
        {
            "KEYCODE_BUTTON_C", 98
        },
        {
            "KEYCODE_BUTTON_L1", 102
        },
        {
            "KEYCODE_BUTTON_L2", 104
        },
        {
            "KEYCODE_BUTTON_MODE", 110
        },
        {
            "KEYCODE_BUTTON_R1", 103
        },
        {
            "KEYCODE_BUTTON_R2", 105
        },
        {
            "KEYCODE_BUTTON_SELECT", 109
        },
        {
            "KEYCODE_BUTTON_START", 108
        },
        {
            "KEYCODE_BUTTON_THUMBL", 106
        },
        {
            "KEYCODE_BUTTON_THUMBR", 107
        },
        {
            "KEYCODE_BUTTON_X", 99
        },
        {
            "KEYCODE_BUTTON_Y", 100
        },
        {
            "KEYCODE_BUTTON_Z", 101
        },
        {
            "KEYCODE_C", 31
        },
        {
            "KEYCODE_CALCULATOR", 210
        },
        {
            "KEYCODE_CALENDAR", 208
        },
        {
            "KEYCODE_CALL", 5
        },
        {
            "KEYCODE_CAMERA", 27
        },
        {
            "KEYCODE_CAPS_LOCK", 115
        },
        {
            "KEYCODE_CAPTIONS", 175
        },
        {
            "KEYCODE_CHANNEL_DOWN", 167
        },
        {
            "KEYCODE_CHANNEL_UP", 166
        },
        {
            "KEYCODE_CLEAR", 28
        },
        {
            "KEYCODE_CLOSE", 321
        },
        {
            "KEYCODE_COMMA", 55
        },
        {
            "KEYCODE_CONTACTS", 207
        },
        {
            "KEYCODE_COPY", 278
        },
        {
            "KEYCODE_CTRL_LEFT", 113
        },
        {
            "KEYCODE_CTRL_RIGHT", 114
        },
        {
            "KEYCODE_CUT", 277
        },
        {
            "KEYCODE_D", 32
        },
        {
            "KEYCODE_DEL", 67
        },
        {
            "KEYCODE_DEMO_APP_1", 301
        },
        {
            "KEYCODE_DEMO_APP_2", 302
        },
        {
            "KEYCODE_DEMO_APP_3", 303
        },
        {
            "KEYCODE_DEMO_APP_4", 304
        },
        {
            "KEYCODE_DICTATE", 319
        },
        {
            "KEYCODE_DO_NOT_DISTURB", 322
        },
        {
            "KEYCODE_DPAD_CENTER", 23
        },
        {
            "KEYCODE_DPAD_DOWN", 20
        },
        {
            "KEYCODE_DPAD_DOWN_LEFT", 269
        },
        {
            "KEYCODE_DPAD_DOWN_RIGHT", 271
        },
        {
            "KEYCODE_DPAD_LEFT", 21
        },
        {
            "KEYCODE_DPAD_RIGHT", 22
        },
        {
            "KEYCODE_DPAD_UP", 19
        },
        {
            "KEYCODE_DPAD_UP_LEFT", 268
        },
        {
            "KEYCODE_DPAD_UP_RIGHT", 270
        },
        {
            "KEYCODE_DVR", 173
        },
        {
            "KEYCODE_E", 33
        },
        {
            "KEYCODE_EISU", 212
        },
        {
            "KEYCODE_EMOJI_PICKER", 317
        },
        {
            "KEYCODE_ENDCALL", 6
        },
        {
            "KEYCODE_ENTER", 66
        },
        {
            "KEYCODE_ENVELOPE", 65
        },
        {
            "KEYCODE_EQUALS", 70
        },
        {
            "KEYCODE_ESCAPE", 111
        },
        {
            "KEYCODE_EXPLORER", 64
        },
        {
            "KEYCODE_F", 34
        },
        {
            "KEYCODE_F1", 131
        },
        {
            "KEYCODE_F10", 140
        },
        {
            "KEYCODE_F11", 141
        },
        {
            "KEYCODE_F12", 142
        },
        {
            "KEYCODE_F13", 326
        },
        {
            "KEYCODE_F14", 327
        },
        {
            "KEYCODE_F15", 328
        },
        {
            "KEYCODE_F16", 329
        },
        {
            "KEYCODE_F17", 330
        },
        {
            "KEYCODE_F18", 331
        },
        {
            "KEYCODE_F19", 332
        },
        {
            "KEYCODE_F2", 132
        },
        {
            "KEYCODE_F20", 333
        },
        {
            "KEYCODE_F21", 334
        },
        {
            "KEYCODE_F22", 335
        },
        {
            "KEYCODE_F23", 336
        },
        {
            "KEYCODE_F24", 337
        },
        {
            "KEYCODE_F3", 133
        },
        {
            "KEYCODE_F4", 134
        },
        {
            "KEYCODE_F5", 135
        },
        {
            "KEYCODE_F6", 136
        },
        {
            "KEYCODE_F7", 137
        },
        {
            "KEYCODE_F8", 138
        },
        {
            "KEYCODE_F9", 139
        },
        {
            "KEYCODE_FEATURED_APP_1", 297
        },
        {
            "KEYCODE_FEATURED_APP_2", 298
        },
        {
            "KEYCODE_FEATURED_APP_3", 299
        },
        {
            "KEYCODE_FEATURED_APP_4", 300
        },
        {
            "KEYCODE_FOCUS", 80
        },
        {
            "KEYCODE_FORWARD", 125
        },
        {
            "KEYCODE_FORWARD_DEL", 112
        },
        {
            "KEYCODE_FULLSCREEN", 325
        },
        {
            "KEYCODE_FUNCTION", 119
        },
        {
            "KEYCODE_G", 35
        },
        {
            "KEYCODE_GRAVE", 68
        },
        {
            "KEYCODE_GUIDE", 172
        },
        {
            "KEYCODE_H", 36
        },
        {
            "KEYCODE_HEADSETHOOK", 79
        },
        {
            "KEYCODE_HELP", 259
        },
        {
            "KEYCODE_HENKAN", 214
        },
        {
            "KEYCODE_HOME", 3
        },
        {
            "KEYCODE_I", 37
        },
        {
            "KEYCODE_INFO", 165
        },
        {
            "KEYCODE_INSERT", 124
        },
        {
            "KEYCODE_J", 38
        },
        {
            "KEYCODE_K", 39
        },
        {
            "KEYCODE_KANA", 218
        },
        {
            "KEYCODE_KATAKANA_HIRAGANA", 215
        },
        {
            "KEYCODE_KEYBOARD_BACKLIGHT_DOWN", 305
        },
        {
            "KEYCODE_KEYBOARD_BACKLIGHT_TOGGLE", 307
        },
        {
            "KEYCODE_KEYBOARD_BACKLIGHT_UP", 306
        },
        {
            "KEYCODE_L", 40
        },
        {
            "KEYCODE_LANGUAGE_SWITCH", 204
        },
        {
            "KEYCODE_LAST_CHANNEL", 229
        },
        {
            "KEYCODE_LEFT_BRACKET", 71
        },
        {
            "KEYCODE_LOCK", 324
        },
        {
            "KEYCODE_M", 41
        },
        {
            "KEYCODE_MACRO_1", 313
        },
        {
            "KEYCODE_MACRO_2", 314
        },
        {
            "KEYCODE_MACRO_3", 315
        },
        {
            "KEYCODE_MACRO_4", 316
        },
        {
            "KEYCODE_MANNER_MODE", 205
        },
        {
            "KEYCODE_MEDIA_AUDIO_TRACK", 222
        },
        {
            "KEYCODE_MEDIA_CLOSE", 128
        },
        {
            "KEYCODE_MEDIA_EJECT", 129
        },
        {
            "KEYCODE_MEDIA_FAST_FORWARD", 90
        },
        {
            "KEYCODE_MEDIA_NEXT", 87
        },
        {
            "KEYCODE_MEDIA_PAUSE", 127
        },
        {
            "KEYCODE_MEDIA_PLAY", 126
        },
        {
            "KEYCODE_MEDIA_PLAY_PAUSE", 85
        },
        {
            "KEYCODE_MEDIA_PREVIOUS", 88
        },
        {
            "KEYCODE_MEDIA_RECORD", 130
        },
        {
            "KEYCODE_MEDIA_REWIND", 89
        },
        {
            "KEYCODE_MEDIA_SKIP_BACKWARD", 273
        },
        {
            "KEYCODE_MEDIA_SKIP_FORWARD", 272
        },
        {
            "KEYCODE_MEDIA_STEP_BACKWARD", 275
        },
        {
            "KEYCODE_MEDIA_STEP_FORWARD", 274
        },
        {
            "KEYCODE_MEDIA_STOP", 86
        },
        {
            "KEYCODE_MEDIA_TOP_MENU", 226
        },
        {
            "KEYCODE_MENU", 82
        },
        {
            "KEYCODE_META_LEFT", 117
        },
        {
            "KEYCODE_META_RIGHT", 118
        },
        {
            "KEYCODE_MINUS", 69
        },
        {
            "KEYCODE_MOVE_END", 123
        },
        {
            "KEYCODE_MOVE_HOME", 122
        },
        {
            "KEYCODE_MUHENKAN", 213
        },
        {
            "KEYCODE_MUSIC", 209
        },
        {
            "KEYCODE_MUTE", 91
        },
        {
            "KEYCODE_N", 42
        },
        {
            "KEYCODE_NAVIGATE_IN", 262
        },
        {
            "KEYCODE_NAVIGATE_NEXT", 261
        },
        {
            "KEYCODE_NAVIGATE_OUT", 263
        },
        {
            "KEYCODE_NAVIGATE_PREVIOUS", 260
        },
        {
            "KEYCODE_NEW", 320
        },
        {
            "KEYCODE_NOTIFICATION", 83
        },
        {
            "KEYCODE_NUM", 78
        },
        {
            "KEYCODE_NUMPAD_0", 144
        },
        {
            "KEYCODE_NUMPAD_1", 145
        },
        {
            "KEYCODE_NUMPAD_2", 146
        },
        {
            "KEYCODE_NUMPAD_3", 147
        },
        {
            "KEYCODE_NUMPAD_4", 148
        },
        {
            "KEYCODE_NUMPAD_5", 149
        },
        {
            "KEYCODE_NUMPAD_6", 150
        },
        {
            "KEYCODE_NUMPAD_7", 151
        },
        {
            "KEYCODE_NUMPAD_8", 152
        },
        {
            "KEYCODE_NUMPAD_9", 153
        },
        {
            "KEYCODE_NUMPAD_ADD", 157
        },
        {
            "KEYCODE_NUMPAD_COMMA", 159
        },
        {
            "KEYCODE_NUMPAD_DIVIDE", 154
        },
        {
            "KEYCODE_NUMPAD_DOT", 158
        },
        {
            "KEYCODE_NUMPAD_ENTER", 160
        },
        {
            "KEYCODE_NUMPAD_EQUALS", 161
        },
        {
            "KEYCODE_NUMPAD_LEFT_PAREN", 162
        },
        {
            "KEYCODE_NUMPAD_MULTIPLY", 155
        },
        {
            "KEYCODE_NUMPAD_RIGHT_PAREN", 163
        },
        {
            "KEYCODE_NUMPAD_SUBTRACT", 156
        },
        {
            "KEYCODE_NUM_LOCK", 143
        },
        {
            "KEYCODE_O", 43
        },
        {
            "KEYCODE_P", 44
        },
        {
            "KEYCODE_PAGE_DOWN", 93
        },
        {
            "KEYCODE_PAGE_UP", 92
        },
        {
            "KEYCODE_PAIRING", 225
        },
        {
            "KEYCODE_PASTE", 279
        },
        {
            "KEYCODE_PERIOD", 56
        },
        {
            "KEYCODE_PICTSYMBOLS", 94
        },
        {
            "KEYCODE_PLUS", 81
        },
        {
            "KEYCODE_POUND", 18
        },
        {
            "KEYCODE_POWER", 26
        },
        {
            "KEYCODE_PRINT", 323
        },
        {
            "KEYCODE_PROFILE_SWITCH", 288
        },
        {
            "KEYCODE_PROG_BLUE", 186
        },
        {
            "KEYCODE_PROG_GREEN", 184
        },
        {
            "KEYCODE_PROG_RED", 183
        },
        {
            "KEYCODE_PROG_YELLOW", 185
        },
        {
            "KEYCODE_Q", 45
        },
        {
            "KEYCODE_R", 46
        },
        {
            "KEYCODE_RECENT_APPS", 312
        },
        {
            "KEYCODE_REFRESH", 285
        },
        {
            "KEYCODE_RIGHT_BRACKET", 72
        },
        {
            "KEYCODE_RO", 217
        },
        {
            "KEYCODE_S", 47
        },
        {
            "KEYCODE_SCREENSHOT", 318
        },
        {
            "KEYCODE_SCROLL_LOCK", 116
        },
        {
            "KEYCODE_SEARCH", 84
        },
        {
            "KEYCODE_SEMICOLON", 74
        },
        {
            "KEYCODE_SETTINGS", 176
        },
        {
            "KEYCODE_SHIFT_LEFT", 59
        },
        {
            "KEYCODE_SHIFT_RIGHT", 60
        },
        {
            "KEYCODE_SLASH", 76
        },
        {
            "KEYCODE_SLEEP", 223
        },
        {
            "KEYCODE_SOFT_LEFT", 1
        },
        {
            "KEYCODE_SOFT_RIGHT", 2
        },
        {
            "KEYCODE_SOFT_SLEEP", 276
        },
        {
            "KEYCODE_SPACE", 62
        },
        {
            "KEYCODE_STAR", 17
        },
        {
            "KEYCODE_STB_INPUT", 180
        },
        {
            "KEYCODE_STB_POWER", 179
        },
        {
            "KEYCODE_STEM_1", 265
        },
        {
            "KEYCODE_STEM_2", 266
        },
        {
            "KEYCODE_STEM_3", 267
        },
        {
            "KEYCODE_STEM_PRIMARY", 264
        },
        {
            "KEYCODE_STYLUS_BUTTON_PRIMARY", 308
        },
        {
            "KEYCODE_STYLUS_BUTTON_SECONDARY", 309
        },
        {
            "KEYCODE_STYLUS_BUTTON_TAIL", 311
        },
        {
            "KEYCODE_STYLUS_BUTTON_TERTIARY", 310
        },
        {
            "KEYCODE_SWITCH_CHARSET", 95
        },
        {
            "KEYCODE_SYM", 63
        },
        {
            "KEYCODE_SYSRQ", 120
        },
        {
            "KEYCODE_SYSTEM_NAVIGATION_DOWN", 281
        },
        {
            "KEYCODE_SYSTEM_NAVIGATION_LEFT", 282
        },
        {
            "KEYCODE_SYSTEM_NAVIGATION_RIGHT", 283
        },
        {
            "KEYCODE_SYSTEM_NAVIGATION_UP", 280
        },
        {
            "KEYCODE_T", 48
        },
        {
            "KEYCODE_TAB", 61
        },
        {
            "KEYCODE_THUMBS_DOWN", 287
        },
        {
            "KEYCODE_THUMBS_UP", 286
        },
        {
            "KEYCODE_TV", 170
        },
        {
            "KEYCODE_TV_ANTENNA_CABLE", 242
        },
        {
            "KEYCODE_TV_AUDIO_DESCRIPTION", 252
        },
        {
            "KEYCODE_TV_AUDIO_DESCRIPTION_MIX_DOWN", 254
        },
        {
            "KEYCODE_TV_AUDIO_DESCRIPTION_MIX_UP", 253
        },
        {
            "KEYCODE_TV_CONTENTS_MENU", 256
        },
        {
            "KEYCODE_TV_DATA_SERVICE", 230
        },
        {
            "KEYCODE_TV_INPUT", 178
        },
        {
            "KEYCODE_TV_INPUT_COMPONENT_1", 249
        },
        {
            "KEYCODE_TV_INPUT_COMPONENT_2", 250
        },
        {
            "KEYCODE_TV_INPUT_COMPOSITE_1", 247
        },
        {
            "KEYCODE_TV_INPUT_COMPOSITE_2", 248
        },
        {
            "KEYCODE_TV_INPUT_HDMI_1", 243
        },
        {
            "KEYCODE_TV_INPUT_HDMI_2", 244
        },
        {
            "KEYCODE_TV_INPUT_HDMI_3", 245
        },
        {
            "KEYCODE_TV_INPUT_HDMI_4", 246
        },
        {
            "KEYCODE_TV_INPUT_VGA_1", 251
        },
        {
            "KEYCODE_TV_MEDIA_CONTEXT_MENU", 257
        },
        {
            "KEYCODE_TV_NETWORK", 241
        },
        {
            "KEYCODE_TV_NUMBER_ENTRY", 234
        },
        {
            "KEYCODE_TV_POWER", 177
        },
        {
            "KEYCODE_TV_RADIO_SERVICE", 232
        },
        {
            "KEYCODE_TV_SATELLITE", 237
        },
        {
            "KEYCODE_TV_SATELLITE_BS", 238
        },
        {
            "KEYCODE_TV_SATELLITE_CS", 239
        },
        {
            "KEYCODE_TV_SATELLITE_SERVICE", 240
        },
        {
            "KEYCODE_TV_TELETEXT", 233
        },
        {
            "KEYCODE_TV_TERRESTRIAL_ANALOG", 235
        },
        {
            "KEYCODE_TV_TERRESTRIAL_DIGITAL", 236
        },
        {
            "KEYCODE_TV_TIMER_PROGRAMMING", 258
        },
        {
            "KEYCODE_TV_ZOOM_MODE", 255
        },
        {
            "KEYCODE_U", 49
        },
        {
            "KEYCODE_UNKNOWN", 0
        },
        {
            "KEYCODE_V", 50
        },
        {
            "KEYCODE_VIDEO_APP_1", 289
        },
        {
            "KEYCODE_VIDEO_APP_2", 290
        },
        {
            "KEYCODE_VIDEO_APP_3", 291
        },
        {
            "KEYCODE_VIDEO_APP_4", 292
        },
        {
            "KEYCODE_VIDEO_APP_5", 293
        },
        {
            "KEYCODE_VIDEO_APP_6", 294
        },
        {
            "KEYCODE_VIDEO_APP_7", 295
        },
        {
            "KEYCODE_VIDEO_APP_8", 296
        },
        {
            "KEYCODE_VOICE_ASSIST", 231
        },
        {
            "KEYCODE_VOLUME_DOWN", 25
        },
        {
            "KEYCODE_VOLUME_MUTE", 164
        },
        {
            "KEYCODE_VOLUME_UP", 24
        },
        {
            "KEYCODE_W", 51
        },
        {
            "KEYCODE_WAKEUP", 224
        },
        {
            "KEYCODE_WINDOW", 171
        },
        {
            "KEYCODE_X", 52
        },
        {
            "KEYCODE_Y", 53
        },
        {
            "KEYCODE_YEN", 216
        },
        {
            "KEYCODE_Z", 54
        },
        {
            "KEYCODE_ZENKAKU_HANKAKU", 211
        },
        {
            "KEYCODE_ZOOM_IN", 168
        },
        {
            "KEYCODE_ZOOM_OUT", 169
        },
    };

    private static readonly string[] AdbKeyOptionList =
    [
        "KEYCODE_0",
        "KEYCODE_1",
        "KEYCODE_11",
        "KEYCODE_12",
        "KEYCODE_2",
        "KEYCODE_3",
        "KEYCODE_3D_MODE",
        "KEYCODE_4",
        "KEYCODE_5",
        "KEYCODE_6",
        "KEYCODE_7",
        "KEYCODE_8",
        "KEYCODE_9",
        "KEYCODE_A",
        "KEYCODE_ALL_APPS",
        "KEYCODE_ALT_LEFT",
        "KEYCODE_ALT_RIGHT",
        "KEYCODE_APOSTROPHE",
        "KEYCODE_APP_SWITCH",
        "KEYCODE_ASSIST",
        "KEYCODE_AT",
        "KEYCODE_AVR_INPUT",
        "KEYCODE_AVR_POWER",
        "KEYCODE_B",
        "KEYCODE_BACK",
        "KEYCODE_BACKSLASH",
        "KEYCODE_BOOKMARK",
        "KEYCODE_BREAK",
        "KEYCODE_BRIGHTNESS_DOWN",
        "KEYCODE_BRIGHTNESS_UP",
        "KEYCODE_BUTTON_1",
        "KEYCODE_BUTTON_10",
        "KEYCODE_BUTTON_11",
        "KEYCODE_BUTTON_12",
        "KEYCODE_BUTTON_13",
        "KEYCODE_BUTTON_14",
        "KEYCODE_BUTTON_15",
        "KEYCODE_BUTTON_16",
        "KEYCODE_BUTTON_2",
        "KEYCODE_BUTTON_3",
        "KEYCODE_BUTTON_4",
        "KEYCODE_BUTTON_5",
        "KEYCODE_BUTTON_6",
        "KEYCODE_BUTTON_7",
        "KEYCODE_BUTTON_8",
        "KEYCODE_BUTTON_9",
        "KEYCODE_BUTTON_A",
        "KEYCODE_BUTTON_B",
        "KEYCODE_BUTTON_C",
        "KEYCODE_BUTTON_L1",
        "KEYCODE_BUTTON_L2",
        "KEYCODE_BUTTON_MODE",
        "KEYCODE_BUTTON_R1",
        "KEYCODE_BUTTON_R2",
        "KEYCODE_BUTTON_SELECT",
        "KEYCODE_BUTTON_START",
        "KEYCODE_BUTTON_THUMBL",
        "KEYCODE_BUTTON_THUMBR",
        "KEYCODE_BUTTON_X",
        "KEYCODE_BUTTON_Y",
        "KEYCODE_BUTTON_Z",
        "KEYCODE_C",
        "KEYCODE_CALCULATOR",
        "KEYCODE_CALENDAR",
        "KEYCODE_CALL",
        "KEYCODE_CAMERA",
        "KEYCODE_CAPS_LOCK",
        "KEYCODE_CAPTIONS",
        "KEYCODE_CHANNEL_DOWN",
        "KEYCODE_CHANNEL_UP",
        "KEYCODE_CLEAR",
        "KEYCODE_CLOSE",
        "KEYCODE_COMMA",
        "KEYCODE_CONTACTS",
        "KEYCODE_COPY",
        "KEYCODE_CTRL_LEFT",
        "KEYCODE_CTRL_RIGHT",
        "KEYCODE_CUT",
        "KEYCODE_D",
        "KEYCODE_DEL",
        "KEYCODE_DEMO_APP_1",
        "KEYCODE_DEMO_APP_2",
        "KEYCODE_DEMO_APP_3",
        "KEYCODE_DEMO_APP_4",
        "KEYCODE_DICTATE",
        "KEYCODE_DO_NOT_DISTURB",
        "KEYCODE_DPAD_CENTER",
        "KEYCODE_DPAD_DOWN",
        "KEYCODE_DPAD_DOWN_LEFT",
        "KEYCODE_DPAD_DOWN_RIGHT",
        "KEYCODE_DPAD_LEFT",
        "KEYCODE_DPAD_RIGHT",
        "KEYCODE_DPAD_UP",
        "KEYCODE_DPAD_UP_LEFT",
        "KEYCODE_DPAD_UP_RIGHT",
        "KEYCODE_DVR",
        "KEYCODE_E",
        "KEYCODE_EISU",
        "KEYCODE_EMOJI_PICKER",
        "KEYCODE_ENDCALL",
        "KEYCODE_ENTER",
        "KEYCODE_ENVELOPE",
        "KEYCODE_EQUALS",
        "KEYCODE_ESCAPE",
        "KEYCODE_EXPLORER",
        "KEYCODE_F",
        "KEYCODE_F1",
        "KEYCODE_F10",
        "KEYCODE_F11",
        "KEYCODE_F12",
        "KEYCODE_F13",
        "KEYCODE_F14",
        "KEYCODE_F15",
        "KEYCODE_F16",
        "KEYCODE_F17",
        "KEYCODE_F18",
        "KEYCODE_F19",
        "KEYCODE_F2",
        "KEYCODE_F20",
        "KEYCODE_F21",
        "KEYCODE_F22",
        "KEYCODE_F23",
        "KEYCODE_F24",
        "KEYCODE_F3",
        "KEYCODE_F4",
        "KEYCODE_F5",
        "KEYCODE_F6",
        "KEYCODE_F7",
        "KEYCODE_F8",
        "KEYCODE_F9",
        "KEYCODE_FEATURED_APP_1",
        "KEYCODE_FEATURED_APP_2",
        "KEYCODE_FEATURED_APP_3",
        "KEYCODE_FEATURED_APP_4",
        "KEYCODE_FOCUS",
        "KEYCODE_FORWARD",
        "KEYCODE_FORWARD_DEL",
        "KEYCODE_FULLSCREEN",
        "KEYCODE_FUNCTION",
        "KEYCODE_G",
        "KEYCODE_GRAVE",
        "KEYCODE_GUIDE",
        "KEYCODE_H",
        "KEYCODE_HEADSETHOOK",
        "KEYCODE_HELP",
        "KEYCODE_HENKAN",
        "KEYCODE_HOME",
        "KEYCODE_I",
        "KEYCODE_INFO",
        "KEYCODE_INSERT",
        "KEYCODE_J",
        "KEYCODE_K",
        "KEYCODE_KANA",
        "KEYCODE_KATAKANA_HIRAGANA",
        "KEYCODE_KEYBOARD_BACKLIGHT_DOWN",
        "KEYCODE_KEYBOARD_BACKLIGHT_TOGGLE",
        "KEYCODE_KEYBOARD_BACKLIGHT_UP",
        "KEYCODE_L",
        "KEYCODE_LANGUAGE_SWITCH",
        "KEYCODE_LAST_CHANNEL",
        "KEYCODE_LEFT_BRACKET",
        "KEYCODE_LOCK",
        "KEYCODE_M",
        "KEYCODE_MACRO_1",
        "KEYCODE_MACRO_2",
        "KEYCODE_MACRO_3",
        "KEYCODE_MACRO_4",
        "KEYCODE_MANNER_MODE",
        "KEYCODE_MEDIA_AUDIO_TRACK",
        "KEYCODE_MEDIA_CLOSE",
        "KEYCODE_MEDIA_EJECT",
        "KEYCODE_MEDIA_FAST_FORWARD",
        "KEYCODE_MEDIA_NEXT",
        "KEYCODE_MEDIA_PAUSE",
        "KEYCODE_MEDIA_PLAY",
        "KEYCODE_MEDIA_PLAY_PAUSE",
        "KEYCODE_MEDIA_PREVIOUS",
        "KEYCODE_MEDIA_RECORD",
        "KEYCODE_MEDIA_REWIND",
        "KEYCODE_MEDIA_SKIP_BACKWARD",
        "KEYCODE_MEDIA_SKIP_FORWARD",
        "KEYCODE_MEDIA_STEP_BACKWARD",
        "KEYCODE_MEDIA_STEP_FORWARD",
        "KEYCODE_MEDIA_STOP",
        "KEYCODE_MEDIA_TOP_MENU",
        "KEYCODE_MENU",
        "KEYCODE_META_LEFT",
        "KEYCODE_META_RIGHT",
        "KEYCODE_MINUS",
        "KEYCODE_MOVE_END",
        "KEYCODE_MOVE_HOME",
        "KEYCODE_MUHENKAN",
        "KEYCODE_MUSIC",
        "KEYCODE_MUTE",
        "KEYCODE_N",
        "KEYCODE_NAVIGATE_IN",
        "KEYCODE_NAVIGATE_NEXT",
        "KEYCODE_NAVIGATE_OUT",
        "KEYCODE_NAVIGATE_PREVIOUS",
        "KEYCODE_NEW",
        "KEYCODE_NOTIFICATION",
        "KEYCODE_NUM",
        "KEYCODE_NUMPAD_0",
        "KEYCODE_NUMPAD_1",
        "KEYCODE_NUMPAD_2",
        "KEYCODE_NUMPAD_3",
        "KEYCODE_NUMPAD_4",
        "KEYCODE_NUMPAD_5",
        "KEYCODE_NUMPAD_6",
        "KEYCODE_NUMPAD_7",
        "KEYCODE_NUMPAD_8",
        "KEYCODE_NUMPAD_9",
        "KEYCODE_NUMPAD_ADD",
        "KEYCODE_NUMPAD_COMMA",
        "KEYCODE_NUMPAD_DIVIDE",
        "KEYCODE_NUMPAD_DOT",
        "KEYCODE_NUMPAD_ENTER",
        "KEYCODE_NUMPAD_EQUALS",
        "KEYCODE_NUMPAD_LEFT_PAREN",
        "KEYCODE_NUMPAD_MULTIPLY",
        "KEYCODE_NUMPAD_RIGHT_PAREN",
        "KEYCODE_NUMPAD_SUBTRACT",
        "KEYCODE_NUM_LOCK",
        "KEYCODE_O",
        "KEYCODE_P",
        "KEYCODE_PAGE_DOWN",
        "KEYCODE_PAGE_UP",
        "KEYCODE_PAIRING",
        "KEYCODE_PASTE",
        "KEYCODE_PERIOD",
        "KEYCODE_PICTSYMBOLS",
        "KEYCODE_PLUS",
        "KEYCODE_POUND",
        "KEYCODE_POWER",
        "KEYCODE_PRINT",
        "KEYCODE_PROFILE_SWITCH",
        "KEYCODE_PROG_BLUE",
        "KEYCODE_PROG_GREEN",
        "KEYCODE_PROG_RED",
        "KEYCODE_PROG_YELLOW",
        "KEYCODE_Q",
        "KEYCODE_R",
        "KEYCODE_RECENT_APPS",
        "KEYCODE_REFRESH",
        "KEYCODE_RIGHT_BRACKET",
        "KEYCODE_RO",
        "KEYCODE_S",
        "KEYCODE_SCREENSHOT",
        "KEYCODE_SCROLL_LOCK",
        "KEYCODE_SEARCH",
        "KEYCODE_SEMICOLON",
        "KEYCODE_SETTINGS",
        "KEYCODE_SHIFT_LEFT",
        "KEYCODE_SHIFT_RIGHT",
        "KEYCODE_SLASH",
        "KEYCODE_SLEEP",
        "KEYCODE_SOFT_LEFT",
        "KEYCODE_SOFT_RIGHT",
        "KEYCODE_SOFT_SLEEP",
        "KEYCODE_SPACE",
        "KEYCODE_STAR",
        "KEYCODE_STB_INPUT",
        "KEYCODE_STB_POWER",
        "KEYCODE_STEM_1",
        "KEYCODE_STEM_2",
        "KEYCODE_STEM_3",
        "KEYCODE_STEM_PRIMARY",
        "KEYCODE_STYLUS_BUTTON_PRIMARY",
        "KEYCODE_STYLUS_BUTTON_SECONDARY",
        "KEYCODE_STYLUS_BUTTON_TAIL",
        "KEYCODE_STYLUS_BUTTON_TERTIARY",
        "KEYCODE_SWITCH_CHARSET",
        "KEYCODE_SYM",
        "KEYCODE_SYSRQ",
        "KEYCODE_SYSTEM_NAVIGATION_DOWN",
        "KEYCODE_SYSTEM_NAVIGATION_LEFT",
        "KEYCODE_SYSTEM_NAVIGATION_RIGHT",
        "KEYCODE_SYSTEM_NAVIGATION_UP",
        "KEYCODE_T",
        "KEYCODE_TAB",
        "KEYCODE_THUMBS_DOWN",
        "KEYCODE_THUMBS_UP",
        "KEYCODE_TV",
        "KEYCODE_TV_ANTENNA_CABLE",
        "KEYCODE_TV_AUDIO_DESCRIPTION",
        "KEYCODE_TV_AUDIO_DESCRIPTION_MIX_DOWN",
        "KEYCODE_TV_AUDIO_DESCRIPTION_MIX_UP",
        "KEYCODE_TV_CONTENTS_MENU",
        "KEYCODE_TV_DATA_SERVICE",
        "KEYCODE_TV_INPUT",
        "KEYCODE_TV_INPUT_COMPONENT_1",
        "KEYCODE_TV_INPUT_COMPONENT_2",
        "KEYCODE_TV_INPUT_COMPOSITE_1",
        "KEYCODE_TV_INPUT_COMPOSITE_2",
        "KEYCODE_TV_INPUT_HDMI_1",
        "KEYCODE_TV_INPUT_HDMI_2",
        "KEYCODE_TV_INPUT_HDMI_3",
        "KEYCODE_TV_INPUT_HDMI_4",
        "KEYCODE_TV_INPUT_VGA_1",
        "KEYCODE_TV_MEDIA_CONTEXT_MENU",
        "KEYCODE_TV_NETWORK",
        "KEYCODE_TV_NUMBER_ENTRY",
        "KEYCODE_TV_POWER",
        "KEYCODE_TV_RADIO_SERVICE",
        "KEYCODE_TV_SATELLITE",
        "KEYCODE_TV_SATELLITE_BS",
        "KEYCODE_TV_SATELLITE_CS",
        "KEYCODE_TV_SATELLITE_SERVICE",
        "KEYCODE_TV_TELETEXT",
        "KEYCODE_TV_TERRESTRIAL_ANALOG",
        "KEYCODE_TV_TERRESTRIAL_DIGITAL",
        "KEYCODE_TV_TIMER_PROGRAMMING",
        "KEYCODE_TV_ZOOM_MODE",
        "KEYCODE_U",
        "KEYCODE_UNKNOWN",
        "KEYCODE_V",
        "KEYCODE_VIDEO_APP_1",
        "KEYCODE_VIDEO_APP_2",
        "KEYCODE_VIDEO_APP_3",
        "KEYCODE_VIDEO_APP_4",
        "KEYCODE_VIDEO_APP_5",
        "KEYCODE_VIDEO_APP_6",
        "KEYCODE_VIDEO_APP_7",
        "KEYCODE_VIDEO_APP_8",
        "KEYCODE_VOICE_ASSIST",
        "KEYCODE_VOLUME_DOWN",
        "KEYCODE_VOLUME_MUTE",
        "KEYCODE_VOLUME_UP",
        "KEYCODE_W",
        "KEYCODE_WAKEUP",
        "KEYCODE_WINDOW",
        "KEYCODE_X",
        "KEYCODE_Y",
        "KEYCODE_YEN",
        "KEYCODE_Z",
        "KEYCODE_ZENKAKU_HANKAKU",
        "KEYCODE_ZOOM_IN",
        "KEYCODE_ZOOM_OUT"
    ];

    [ObservableProperty] private bool _hasBrushPreview;
    [ObservableProperty] private Point _brushPreviewPoint;
    [ObservableProperty] private double _brushPreviewSize = 1;
    [ObservableProperty] private string _brushPreviewRectText = string.Empty;
    [ObservableProperty] private IBrush _brushPreviewStroke = new SolidColorBrush(Colors.LimeGreen);
    [ObservableProperty] private IBrush _brushPreviewFill = new SolidColorBrush(Color.FromArgb(30, 0, 255, 0));

    private Bitmap? _pausedLiveViewImage;
    private Size? _lastLiveViewSize;

    public Bitmap? LiveViewDisplayImage
    {
        get
        {
            if (ActiveToolMode == LiveViewToolMode.Screenshot
                && IsLiveViewPaused
                && _screenshotBrushImage != null)
            {
                return _screenshotBrushImage;
            }


            if (IsColorPreviewActive && _colorPreviewImage != null)
            {
                return _colorPreviewImage;
            }

            return IsLiveViewPaused ? _pausedLiveViewImage : LiveViewImage;
        }
    }

    partial void OnIsLiveViewPausedChanged(bool value)
    {
        if (value)
        {
            _pausedLiveViewImage = LiveViewImage;
            if (IsColorPreviewActive)
            {
                ApplyColorFilter();
            }

            if (IsScreenshotBrushMode)
            {
                ResetScreenshotBrushPreview();
            }
        }
        else
        {
            _pausedLiveViewImage = null;
            DisableColorPreview();
            ResetScreenshotBrushPreview();
        }

        UpdateBrushPreviewAvailability();
        OnPropertyChanged(nameof(LiveViewDisplayImage));

    }

    partial void OnLiveViewScaleChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.1, 15.0);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            LiveViewScale = clamped;
        }
    }

    partial void OnRoiXChanged(string value) => UpdateRoiRectFromText();
    partial void OnRoiYChanged(string value) => UpdateRoiRectFromText();
    partial void OnRoiWChanged(string value) => UpdateRoiRectFromText();
    partial void OnRoiHChanged(string value) => UpdateRoiRectFromText();

    partial void OnOriginTargetXChanged(string value) => UpdateOriginTargetRectFromText();
    partial void OnOriginTargetYChanged(string value) => UpdateOriginTargetRectFromText();
    partial void OnOriginTargetWChanged(string value) => UpdateOriginTargetRectFromText();
    partial void OnOriginTargetHChanged(string value) => UpdateOriginTargetRectFromText();

    partial void OnTargetXChanged(string value) => UpdateTargetRectFromText();
    partial void OnTargetYChanged(string value) => UpdateTargetRectFromText();
    partial void OnTargetWChanged(string value) => UpdateTargetRectFromText();
    partial void OnTargetHChanged(string value) => UpdateTargetRectFromText();

    partial void OnColorPickXChanged(string value) => UpdateColorPickRectFromText();
    partial void OnColorPickYChanged(string value) => UpdateColorPickRectFromText();
    partial void OnColorPickWChanged(string value) => UpdateColorPickRectFromText();
    partial void OnColorPickHChanged(string value) => UpdateColorPickRectFromText();

    partial void OnScreenshotXChanged(string value) => UpdateScreenshotRectFromText();
    partial void OnScreenshotYChanged(string value) => UpdateScreenshotRectFromText();
    partial void OnScreenshotWChanged(string value) => UpdateScreenshotRectFromText();
    partial void OnScreenshotHChanged(string value) => UpdateScreenshotRectFromText();

    partial void OnOcrXChanged(string value) => UpdateOcrRectFromText();
    partial void OnOcrYChanged(string value) => UpdateOcrRectFromText();
    partial void OnOcrWChanged(string value) => UpdateOcrRectFromText();
    partial void OnOcrHChanged(string value) => UpdateOcrRectFromText();

    partial void OnNeuralNetworkDetectXChanged(string value) => UpdateNeuralNetworkDetectRectFromText();
    partial void OnNeuralNetworkDetectYChanged(string value) => UpdateNeuralNetworkDetectRectFromText();
    partial void OnNeuralNetworkDetectWChanged(string value) => UpdateNeuralNetworkDetectRectFromText();
    partial void OnNeuralNetworkDetectHChanged(string value) => UpdateNeuralNetworkDetectRectFromText();

    partial void OnSwipeStartXChanged(string value) => UpdateSwipeFromText();
    partial void OnSwipeStartYChanged(string value) => UpdateSwipeFromText();
    partial void OnSwipeEndXChanged(string value) => UpdateSwipeFromText();
    partial void OnSwipeEndYChanged(string value) => UpdateSwipeFromText();
    partial void OnSwipeTestEndXChanged(string value) => SyncSwipeTestEndRows(false);
    partial void OnSwipeTestEndYChanged(string value) => SyncSwipeTestEndRows(false);

    partial void OnActiveToolModeChanged(LiveViewToolMode value)
    {
        if (value != LiveViewToolMode.ColorPick)
        {
            DisableColorPreview();
        }

        if (value != LiveViewToolMode.Screenshot)
        {
            IsScreenshotBrushMode = false;
            ResetScreenshotBrushPreview();
        }

        if (value != LiveViewToolMode.Ocr
            && value != LiveViewToolMode.Screenshot
            && value != LiveViewToolMode.Roi
            && value != LiveViewToolMode.Swipe
            && value != LiveViewToolMode.Key
            && value != LiveViewToolMode.ColorPick)
        {
            _suppressTestPanelSync = true;
            IsOcrTestPanelVisible = false;
            IsScreenshotTestPanelVisible = false;
            IsColorTestPanelVisible = false;
            IsClickTestPanelVisible = false;
            IsSwipeTestPanelVisible = false;
            IsKeyTestPanelVisible = false;
            _suppressTestPanelSync = false;
            IsTestPanelVisible = false;
            ActiveTestPanelMode = TestPanelMode.None;
        }

        if (value != LiveViewToolMode.Key)
        {
            IsKeyCaptureActive = false;
        }

        IsToolPanelVisible = value != LiveViewToolMode.None;
        if (value != LiveViewToolMode.None)
        {
            IsDragMode = false;
            if (!IsLiveViewPaused)
            {
                IsLiveViewPaused = true;
            }
        }

        SyncToolModeFlags();

        if (value != LiveViewToolMode.None)
        {
            ApplySelectionPreview();
        }
    }

    partial void OnIsDragModeChanged(bool value)
    {
        if (value)
        {
            DisableColorPreview();
            ActiveToolMode = LiveViewToolMode.None;
            HasSelection = false;
            HasSecondarySelection = false;
        }
    }

    partial void OnIsKeyModeChanged(bool value)
    {
        if (_suppressToolModeSync) return;
        if (value)
        {
            ActiveToolMode = LiveViewToolMode.Key;
        }
        else if (ActiveToolMode == LiveViewToolMode.Key)
        {
            ActiveToolMode = LiveViewToolMode.None;
        }
    }
    partial void OnIsTargetModeChanged(bool value)
    {
        if (!_suppressRoiTargetSync)
        {
            _suppressRoiTargetSync = true;
            RoiModeIndex = value ? 1 : 0;
            _suppressRoiTargetSync = false;
        }

        RoiSelectionType = value
            ? LiveViewRoiSelectionType.OriginTarget
            : LiveViewRoiSelectionType.OriginRoi;

        ApplySelectionPreview();
        UpdateOffsets();
    }


    partial void OnIsSelectingTargetChanged(bool value)
    {
        if (!_suppressRoiTargetSync)
        {
            _suppressRoiTargetSync = true;
            RoiTargetIndex = value ? 1 : 0;
            _suppressRoiTargetSync = false;
        }

        CurrentRoiSelectionLabel = value ? "当前选择: Target" : "当前选择: ROI";
        ApplySelectionPreview();
    }

    partial void OnRoiTargetIndexChanged(int value)
    {
        if (_suppressRoiTargetSync) return;
        _suppressRoiTargetSync = true;
        IsSelectingTarget = value == 1;
        _suppressRoiTargetSync = false;
    }

    partial void OnRoiModeIndexChanged(int value)
    {
        if (_suppressRoiTargetSync) return;
        _suppressRoiTargetSync = true;
        IsTargetMode = value == 1;
        _suppressRoiTargetSync = false;
    }

    partial void OnIsRoiModeChanged(bool value)
    {
        if (_suppressToolModeSync) return;
        if (value)
        {
            ActiveToolMode = LiveViewToolMode.Roi;
        }
        else if (ActiveToolMode == LiveViewToolMode.Roi)
        {
            ActiveToolMode = LiveViewToolMode.None;
        }
    }

    partial void OnIsColorPickModeChanged(bool value)
    {
        if (_suppressToolModeSync) return;
        if (value)
        {
            ActiveToolMode = LiveViewToolMode.ColorPick;
        }
        else if (ActiveToolMode == LiveViewToolMode.ColorPick)
        {
            ActiveToolMode = LiveViewToolMode.None;
        }
    }

    partial void OnIsSwipeModeChanged(bool value)
    {
        if (_suppressToolModeSync) return;
        if (value)
        {
            ActiveToolMode = LiveViewToolMode.Swipe;
        }
        else if (ActiveToolMode == LiveViewToolMode.Swipe)
        {
            ActiveToolMode = LiveViewToolMode.None;
        }
    }

    partial void OnIsOcrModeChanged(bool value)
    {
        if (_suppressToolModeSync) return;
        if (value)
        {
            ActiveToolMode = LiveViewToolMode.Ocr;
        }
        else if (ActiveToolMode == LiveViewToolMode.Ocr)
        {
            ActiveToolMode = LiveViewToolMode.None;
        }
    }

    partial void OnIsNeuralNetworkDetectModeChanged(bool value)
    {
        if (_suppressToolModeSync) return;
        if (value)
        {
            ActiveToolMode = LiveViewToolMode.NeuralNetworkDetect;
        }
        else if (ActiveToolMode == LiveViewToolMode.NeuralNetworkDetect)
        {
            ActiveToolMode = LiveViewToolMode.None;
        }
    }

    partial void OnIsScreenshotModeChanged(bool value)
    {
        if (_suppressToolModeSync) return;
        if (value)
        {
            ActiveToolMode = LiveViewToolMode.Screenshot;
        }
        else if (ActiveToolMode == LiveViewToolMode.Screenshot)
        {
            ActiveToolMode = LiveViewToolMode.None;
        }
    }

    partial void OnIsTestPanelVisibleChanged(bool value)
    {
        if (value)
        {
            return;
        }

        _suppressTestPanelSync = true;
        IsOcrTestPanelVisible = false;
        IsScreenshotTestPanelVisible = false;
        IsColorTestPanelVisible = false;
        IsClickTestPanelVisible = false;
        IsSwipeTestPanelVisible = false;
        IsKeyTestPanelVisible = false;
        IsNeuralNetworkDetectTestPanelVisible = false;
        _suppressTestPanelSync = false;
        ActiveTestPanelMode = TestPanelMode.None;
    }

    partial void OnIsOcrTestPanelVisibleChanged(bool value)
    {
        if (_suppressTestPanelSync)
        {
            return;
        }

        if (value)
        {
            _suppressTestPanelSync = true;
            IsScreenshotTestPanelVisible = false;
            IsClickTestPanelVisible = false;
            IsSwipeTestPanelVisible = false;
            IsKeyTestPanelVisible = false;
            IsNeuralNetworkDetectTestPanelVisible = false;
            _suppressTestPanelSync = false;
            IsTestPanelVisible = true;
            ActiveTestPanelMode = TestPanelMode.Ocr;
            SyncOcrMatchDefaults(true);
        }
        else if (!IsScreenshotTestPanelVisible && !IsColorTestPanelVisible && !IsClickTestPanelVisible && !IsSwipeTestPanelVisible && !IsKeyTestPanelVisible && !IsNeuralNetworkDetectTestPanelVisible)
        {
            IsTestPanelVisible = false;
            ActiveTestPanelMode = TestPanelMode.None;
        }
    }

    partial void OnIsScreenshotTestPanelVisibleChanged(bool value)
    {
        if (_suppressTestPanelSync)
        {
            return;
        }

        if (value)
        {
            _suppressTestPanelSync = true;
            IsOcrTestPanelVisible = false;
            IsClickTestPanelVisible = false;
            IsSwipeTestPanelVisible = false;
            IsKeyTestPanelVisible = false;
            _suppressTestPanelSync = false;
            IsTestPanelVisible = true;
            ActiveTestPanelMode = TestPanelMode.Screenshot;
            SyncTemplateMatchDefaults(true);
        }
        else if (!IsOcrTestPanelVisible && !IsColorTestPanelVisible && !IsClickTestPanelVisible && !IsSwipeTestPanelVisible && !IsKeyTestPanelVisible)
        {
            IsTestPanelVisible = false;
            ActiveTestPanelMode = TestPanelMode.None;
        }
    }

    partial void OnIsColorTestPanelVisibleChanged(bool value)
    {
        if (_suppressTestPanelSync)
        {
            return;
        }

        if (value)
        {
            _suppressTestPanelSync = true;
            IsOcrTestPanelVisible = false;
            IsScreenshotTestPanelVisible = false;
            IsClickTestPanelVisible = false;
            IsSwipeTestPanelVisible = false;
            IsKeyTestPanelVisible = false;
            IsNeuralNetworkDetectTestPanelVisible = false;
            _suppressTestPanelSync = false;
            IsTestPanelVisible = true;
            ActiveTestPanelMode = TestPanelMode.Color;
            EnsureColorMatchRowsInitialized();
            SyncColorMatchDefaults(true);
        }
        else if (!IsOcrTestPanelVisible && !IsScreenshotTestPanelVisible && !IsClickTestPanelVisible && !IsSwipeTestPanelVisible && !IsKeyTestPanelVisible && !IsNeuralNetworkDetectTestPanelVisible)
        {
            IsTestPanelVisible = false;
            ActiveTestPanelMode = TestPanelMode.None;
        }
    }

    partial void OnIsClickTestPanelVisibleChanged(bool value)
    {
        if (_suppressTestPanelSync)
        {
            return;
        }

        if (value)
        {
            _suppressTestPanelSync = true;
            IsOcrTestPanelVisible = false;
            IsScreenshotTestPanelVisible = false;
            IsSwipeTestPanelVisible = false;
            IsKeyTestPanelVisible = false;
            IsNeuralNetworkDetectTestPanelVisible = false;
            _suppressTestPanelSync = false;
            IsTestPanelVisible = true;
            ActiveTestPanelMode = TestPanelMode.Click;
            SyncClickTestDefaults(true);
        }
        else if (!IsOcrTestPanelVisible && !IsScreenshotTestPanelVisible && !IsColorTestPanelVisible && !IsSwipeTestPanelVisible && !IsKeyTestPanelVisible && !IsNeuralNetworkDetectTestPanelVisible)
        {
            IsTestPanelVisible = false;
            ActiveTestPanelMode = TestPanelMode.None;
        }
    }

    partial void OnIsSwipeTestPanelVisibleChanged(bool value)
    {
        if (_suppressTestPanelSync)
        {
            return;
        }

        if (value)
        {
            _suppressTestPanelSync = true;
            IsOcrTestPanelVisible = false;
            IsScreenshotTestPanelVisible = false;
            IsClickTestPanelVisible = false;
            IsKeyTestPanelVisible = false;
            IsNeuralNetworkDetectTestPanelVisible = false;
            _suppressTestPanelSync = false;
            IsTestPanelVisible = true;
            ActiveTestPanelMode = TestPanelMode.Swipe;
            SyncSwipeTestDefaults(true);
        }
        else if (!IsOcrTestPanelVisible && !IsScreenshotTestPanelVisible && !IsColorTestPanelVisible && !IsClickTestPanelVisible && !IsKeyTestPanelVisible && !IsNeuralNetworkDetectTestPanelVisible)
        {
            IsTestPanelVisible = false;
            ActiveTestPanelMode = TestPanelMode.None;
        }
    }

    partial void OnIsKeyTestPanelVisibleChanged(bool value)
    {
        if (_suppressTestPanelSync)
        {
            return;
        }

        if (value)
        {
            _suppressTestPanelSync = true;
            IsOcrTestPanelVisible = false;
            IsScreenshotTestPanelVisible = false;
            IsClickTestPanelVisible = false;
            IsSwipeTestPanelVisible = false;
            IsNeuralNetworkDetectTestPanelVisible = false;
            _suppressTestPanelSync = false;
            IsTestPanelVisible = true;
            ActiveTestPanelMode = TestPanelMode.Key;
        }
        else if (!IsOcrTestPanelVisible && !IsScreenshotTestPanelVisible && !IsColorTestPanelVisible && !IsClickTestPanelVisible && !IsSwipeTestPanelVisible && !IsNeuralNetworkDetectTestPanelVisible)
        {
            IsTestPanelVisible = false;
            ActiveTestPanelMode = TestPanelMode.None;
        }
    }

    partial void OnIsNeuralNetworkDetectTestPanelVisibleChanged(bool value)
    {
        if (_suppressTestPanelSync)
        {
            return;
        }

        if (value)
        {
            _suppressTestPanelSync = true;
            IsOcrTestPanelVisible = false;
            IsScreenshotTestPanelVisible = false;
            IsClickTestPanelVisible = false;
            IsSwipeTestPanelVisible = false;
            IsKeyTestPanelVisible = false;
            _suppressTestPanelSync = false;
            IsTestPanelVisible = true;
            ActiveTestPanelMode = TestPanelMode.NeuralNetworkDetect;
            SyncNeuralNetworkDetectDefaults(true);
        }
        else if (!IsOcrTestPanelVisible && !IsScreenshotTestPanelVisible && !IsColorTestPanelVisible && !IsClickTestPanelVisible && !IsSwipeTestPanelVisible && !IsKeyTestPanelVisible)
        {
            IsTestPanelVisible = false;
            ActiveTestPanelMode = TestPanelMode.None;
        }
    }

    partial void OnIsScreenshotBrushModeChanged(bool value)
    {
        UpdateBrushPreviewAvailability();
        ApplySelectionPreview();
    }


    partial void OnScreenshotRelativePathChanged(string value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.LiveViewScreenshotRelativePath, value);
    }

    partial void OnScreenshotBrushSizeChanged(int value)
    {
        BrushPreviewSize = Math.Max(1, value);
        UpdateBrushPreviewRectText();
    }

    [RelayCommand]
    private void ToggleLiveViewPause()
    {
        IsLiveViewPaused = !IsLiveViewPaused;
    }

    // [RelayCommand]
    // private void ZoomIn()
    // {
    //     if (!IsDragMode) return;
    //     LiveViewScale = Math.Clamp(LiveViewScale + 0.1, 0.1, 1.0);
    // }
    //
    // [RelayCommand]
    // private void ZoomOut()
    // {
    //     if (!IsDragMode) return;
    //     LiveViewScale = Math.Clamp(LiveViewScale - 0.1, 0.1, 1.0);
    // }
    //
    // [RelayCommand]
    // private void ResetZoom()
    // {
    //     if (!IsDragMode) return;
    //     LiveViewScale = 1.0;
    // }

    [RelayCommand]
    private void ActivateTool(LiveViewToolMode mode)
    {
        if (!IsLiveViewPaused)
        {
            IsLiveViewPaused = true;
        }

        ActiveToolMode = mode;
        ApplySelectionPreview();
    }
    [RelayCommand]
    private void SelectRoi()
    {
        if (!IsLiveViewPaused)
        {
            IsLiveViewPaused = true;
        }

        IsTargetMode = false;
        ActiveToolMode = LiveViewToolMode.Roi;
        RoiSelectionType = LiveViewRoiSelectionType.OriginRoi;
        ApplySelectionPreview();
    }

    [RelayCommand]
    private void SelectOriginTarget()
    {
        if (!IsLiveViewPaused)
        {
            IsLiveViewPaused = true;
        }

        IsTargetMode = true;
        ActiveToolMode = LiveViewToolMode.Roi;
        RoiSelectionType = LiveViewRoiSelectionType.OriginTarget;
        ApplySelectionPreview();
    }

    [RelayCommand]
    private void SelectTarget()
    {
        if (!IsLiveViewPaused)
        {
            IsLiveViewPaused = true;
        }

        ActiveToolMode = LiveViewToolMode.Roi;
        RoiSelectionType = LiveViewRoiSelectionType.TargetRoi;
        ApplySelectionPreview();
    }

    [RelayCommand]
    private void ClearRoi()
    {
        _suppressRoiSync = true;
        _roiRect = default;
        RoiX = "0";
        RoiY = "0";
        RoiW = "0";
        RoiH = "0";
        _suppressRoiSync = false;
        UpdateOffsets();
        RefreshSelectionRects();
    }

    [RelayCommand]
    private void ClearOriginTarget()
    {
        _suppressTargetSync = true;
        _originTargetRect = default;
        OriginTargetX = "0";
        OriginTargetY = "0";
        OriginTargetW = "0";
        OriginTargetH = "0";
        _suppressTargetSync = false;
        UpdateOffsets();
        RefreshSelectionRects();
    }

    [RelayCommand]
    private void ClearTarget()
    {
        _suppressTargetSync = true;
        _targetRect = default;
        TargetX = "0";
        TargetY = "0";
        TargetW = "0";
        TargetH = "0";
        _suppressTargetSync = false;
        UpdateOffsets();
        RefreshSelectionRects();
    }

    [RelayCommand]
    private void CopyRoiToTarget()
    {
        if (_roiRect.Width <= 0 || _roiRect.Height <= 0) return;
        SetTargetRect(_roiRect);
        UpdateOffsets();
        RefreshSelectionRects();
    }

    [RelayCommand]
    private void CopyOriginTargetToTarget()
    {
        if (_originTargetRect.Width <= 0 || _originTargetRect.Height <= 0) return;
        SetTargetRect(_originTargetRect);
        UpdateOffsets();
        RefreshSelectionRects();
    }

    [RelayCommand]
    private void CopyTargetToOrigin()
    {
        if (_targetRect.Width <= 0 || _targetRect.Height <= 0) return;
        if (IsTargetMode)
            SetOriginTargetRect(_targetRect);
        else
            SetRoiRect(_targetRect);
        UpdateOffsets();
        RefreshSelectionRects();
    }

    [RelayCommand]
    private void CopyRoiToClipboard()
    {
        CopyTextToClipboard(BuildRectClipboardText("roi", RoiX, RoiY, RoiW, RoiH), "复制ROI到剪贴板");
    }

    [RelayCommand]
    private async Task PasteRoiFromClipboard()
    {
        if (await TryGetClipboardRectAsync(SetRoiRect))
        {
            UpdateOffsets();
            RefreshSelectionRects();
        }
    }

    [RelayCommand]
    private void CopyOriginTargetToClipboard()
    {
        CopyTextToClipboard(BuildRectClipboardText("target", OriginTargetX, OriginTargetY, OriginTargetW, OriginTargetH), "复制OriginTarget到剪贴板");
    }

    [RelayCommand]
    private async Task PasteOriginTargetFromClipboard()
    {
        if (await TryGetClipboardRectAsync(SetOriginTargetRect))
        {
            UpdateOffsets();
            RefreshSelectionRects();
        }
    }

    [RelayCommand]
    private void CopyTargetToClipboard()
    {
        CopyTextToClipboard(BuildRectClipboardText("target", TargetX, TargetY, TargetW, TargetH), "复制Target到剪贴板");
    }

    [RelayCommand]
    private async Task PasteTargetFromClipboard()
    {
        if (await TryGetClipboardRectAsync(SetTargetRect))
        {
            UpdateOffsets();
            RefreshSelectionRects();
        }
    }

    [RelayCommand]
    private void CopyOffsetToClipboard()
    {
        var label = IsTargetMode ? "target_offset" : "roi_offset";
        CopyTextToClipboard(BuildRectClipboardText(label, OffsetX, OffsetY, OffsetW, OffsetH), "复制Offset到剪贴板");
    }

    private string BuildRectClipboardText(string label, string xText, string yText, string wText, string hText)
    {
        if (double.TryParse(xText, out var x)
            && double.TryParse(yText, out var y)
            && double.TryParse(wText, out var w)
            && double.TryParse(hText, out var h))
        {
            return FormatClipboardValue(label,
                $"[{(int)Math.Round(x)}, {(int)Math.Round(y)}, {(int)Math.Round(w)}, {(int)Math.Round(h)}]");
        }

        return FormatClipboardValue(label, "[0, 0, 0, 0]");
    }

    private string FormatClipboardValue(string label, string value)
    {
        var modeValue = ConfigurationManager.Current.GetValue(ConfigurationKeys.ClipboardCopyFormat, ClipboardCopyFormat.ValuesOnly.ToString());
        if (!Enum.TryParse<ClipboardCopyFormat>(modeValue, true, out var mode))
        {
            mode = ClipboardCopyFormat.ValuesOnly;
        }

        return mode == ClipboardCopyFormat.FieldWithValues
            ? $"\"{label}\": {value}"
            : value;
    }

    private int GetExpandX()
    {
        return ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewExpandX, 25);
    }

    private int GetExpandY()
    {
        return ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewExpandY, 25);
    }

    private int GetExpandW(int expandX)
    {
        return ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewExpandW, expandX * 2);
    }

    private int GetExpandH(int expandY)
    {
        return ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewExpandH, expandY * 2);
    }

    private void CopyTextToClipboard(string text, string taskName)
    {
        TaskManager.RunTaskAsync(() =>
        {
            var clipboard = Instances.RootView?.Clipboard;
            if (clipboard == null) return;

            DispatcherHelper.PostOnMainThread(async () => await clipboard.SetTextAsync(text));
        }, name: taskName);
    }

    private async Task<bool> TryGetClipboardRectAsync(Action<Rect> applyAction)
    {
        var clipboard = Instances.RootView?.Clipboard;
        if (clipboard == null)
        {
            return false;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var matches = Regex.Matches(text, @"-?\d+");
        if (matches.Count < 4)
        {
            return false;
        }

        if (!double.TryParse(matches[0].Value, out var x)
            || !double.TryParse(matches[1].Value, out var y)
            || !double.TryParse(matches[2].Value, out var w)
            || !double.TryParse(matches[3].Value, out var h))
        {
            return false;
        }

        if (x == 0 && y == 0 && w == 0 && h == 0)
        {
            applyAction(new Rect(0, 0, 0, 0));
            return true;
        }

        if (w <= 0 || h <= 0)
        {
            return false;
        }

        var rect = new Rect(Math.Max(0, x), Math.Max(0, y), Math.Max(1, w), Math.Max(1, h));
        applyAction(rect);
        return true;
    }

    private async Task<bool> TryGetClipboardPointAsync(Action<Point> applyAction)
    {
        var clipboard = Instances.RootView?.Clipboard;
        if (clipboard == null)
        {
            return false;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var matches = Regex.Matches(text, @"-?\d+");
        if (matches.Count < 2)
        {
            return false;
        }

        if (!double.TryParse(matches[0].Value, out var x)
            || !double.TryParse(matches[1].Value, out var y))
        {
            return false;
        }

        var point = new Point(Math.Max(0, x), Math.Max(0, y));
        applyAction(point);
        return true;
    }

    private async Task<bool> TryGetClipboardNumberAsync(Action<double> applyAction)
    {
        var clipboard = Instances.RootView?.Clipboard;
        if (clipboard == null)
        {
            return false;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var matches = Regex.Matches(text, @"-?\d+");
        if (matches.Count < 1)
        {
            return false;
        }

        if (!double.TryParse(matches[0].Value, out var value))
        {
            return false;
        }

        applyAction(value);
        return true;
    }

    private async Task<bool> TryGetClipboardTripletAsync(Action<int, int, int> applyAction)
    {
        var clipboard = Instances.RootView?.Clipboard;
        if (clipboard == null)
        {
            return false;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var matches = Regex.Matches(text, @"-?\d+");
        if (matches.Count < 3)
        {
            return false;
        }

        if (!int.TryParse(matches[0].Value, out var first)
            || !int.TryParse(matches[1].Value, out var second)
            || !int.TryParse(matches[2].Value, out var third))
        {
            return false;
        }

        applyAction(first, second, third);
        return true;
    }

    [RelayCommand]
    private void SetDragMode()
    {
        IsDragMode = true;
    }

    [RelayCommand]
    private void SetScreenshotMode(string? mode)
    {
        IsScreenshotBrushMode = string.Equals(mode, "Brush", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ResetScreenshotBrush()
    {
        ResetScreenshotBrushPreview();
    }

    private void SyncToolModeFlags()
    {
        _suppressToolModeSync = true;
        IsRoiMode = ActiveToolMode == LiveViewToolMode.Roi;
        IsColorPickMode = ActiveToolMode == LiveViewToolMode.ColorPick;
        IsSwipeMode = ActiveToolMode == LiveViewToolMode.Swipe;
        IsOcrMode = ActiveToolMode == LiveViewToolMode.Ocr;
        IsScreenshotMode = ActiveToolMode == LiveViewToolMode.Screenshot;
        IsKeyMode = ActiveToolMode == LiveViewToolMode.Key;
        IsNeuralNetworkDetectMode = ActiveToolMode == LiveViewToolMode.NeuralNetworkDetect;
        _suppressToolModeSync = false;
    }


    [RelayCommand]
    private void SetColorMode(string? mode)
    {
        if (int.TryParse(mode, out var value))
        {
            ColorMode = value;
        }
    }

    public void UpdateSelection(Rect rect, bool hasSelection)
    {
        if (ActiveToolMode == LiveViewToolMode.Roi)
        {
            UpdateSelectionWithSecondary(rect, hasSelection, RoiSelectionType);
            return;
        }

        SelectionRect = rect;
        HasSelection = hasSelection;
        SecondarySelectionRect = default;
        HasSecondarySelection = false;
    }

    public void ApplySelection(Rect rect)
    {
        switch (RoiSelectionType)
        {
            case LiveViewRoiSelectionType.OriginRoi:
                SetRoiRect(rect);
                break;
            case LiveViewRoiSelectionType.OriginTarget:
                SetOriginTargetRect(rect);
                break;
            case LiveViewRoiSelectionType.TargetRoi:
                SetTargetRect(rect);
                break;
        }

        UpdateSelection(rect, rect.Width > 0 && rect.Height > 0);
        UpdateOffsets();
    }

    public void ApplySelectionForTool(Rect rect)
    {
        switch (ActiveToolMode)
        {
            case LiveViewToolMode.ColorPick:
                SetColorPickRect(rect);
                break;
            case LiveViewToolMode.Screenshot:
                SetScreenshotRect(rect);
                break;
            case LiveViewToolMode.Ocr:
                SetOcrRect(rect);
                break;
            case LiveViewToolMode.NeuralNetworkDetect:
                SetNeuralNetworkDetectRect(rect);
                break;
        }

        ApplySelectionPreview();
    }

    public void SetSwipeStart(Point point)
    {
        SwipeStartX = ((int)Math.Round(point.X)).ToString();
        SwipeStartY = ((int)Math.Round(point.Y)).ToString();
        UpdateSwipeArrow();
    }

    public void SetSwipeEnd(Point point)
    {
        SwipeEndX = ((int)Math.Round(point.X)).ToString();
        SwipeEndY = ((int)Math.Round(point.Y)).ToString();
        UpdateSwipeArrow();
    }

    public void UpdateColorRangeFromSelection(Rect rect)
    {
        if (LiveViewDisplayImage is not Bitmap bitmap)
        {
            return;
        }

        SetColorPickRect(rect);

        var x = (int)Math.Clamp(rect.X, 0, bitmap.Size.Width - 1);
        var y = (int)Math.Clamp(rect.Y, 0, bitmap.Size.Height - 1);
        var w = (int)Math.Clamp(rect.Width, 1, bitmap.Size.Width - x);
        var h = (int)Math.Clamp(rect.Height, 1, bitmap.Size.Height - y);

        if (w <= 0 || h <= 0)
        {
            return;
        }

        var width = (int)bitmap.Size.Width;
        var height = (int)bitmap.Size.Height;
        var stride = width * 4;
        var buffer = new byte[height * stride];
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), (IntPtr)ptr, buffer.Length, stride);
            }
        }

        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;
        int minH = 180, minS = 255, minV = 255;
        int maxH = 0, maxS = 0, maxV = 0;
        int minGray = 255, maxGray = 0;

        for (var yy = y; yy < y + h; yy++)
        {
            var rowStart = yy * stride;
            for (var xx = x; xx < x + w; xx++)
            {
                var index = rowStart + xx * 4;
                var b = buffer[index];
                var g = buffer[index + 1];
                var r = buffer[index + 2];

                minR = Math.Min(minR, r);
                minG = Math.Min(minG, g);
                minB = Math.Min(minB, b);
                maxR = Math.Max(maxR, r);
                maxG = Math.Max(maxG, g);
                maxB = Math.Max(maxB, b);

                var gray = (r + g + b) / 3;
                minGray = Math.Min(minGray, gray);
                maxGray = Math.Max(maxGray, gray);

                RgbToHsv(r, g, b, out var hVal, out var sVal, out var vVal);
                minH = Math.Min(minH, hVal);
                minS = Math.Min(minS, sVal);
                minV = Math.Min(minV, vVal);
                maxH = Math.Max(maxH, hVal);
                maxS = Math.Max(maxS, sVal);
                maxV = Math.Max(maxV, vVal);
            }
        }

        RgbUpperR = maxR.ToString();
        RgbUpperG = maxG.ToString();
        RgbUpperB = maxB.ToString();
        RgbLowerR = minR.ToString();
        RgbLowerG = minG.ToString();
        RgbLowerB = minB.ToString();

        HsvUpperH = maxH.ToString();
        HsvUpperS = maxS.ToString();
        HsvUpperV = maxV.ToString();
        HsvLowerH = minH.ToString();
        HsvLowerS = minS.ToString();
        HsvLowerV = minV.ToString();

        GrayUpper = maxGray.ToString();
        GrayLower = minGray.ToString();

        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    [RelayCommand]
    private void ToggleColorPreview()
    {
        IsColorPreviewActive = !IsColorPreviewActive;
        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
        else
        {
            _colorPreviewImage = null;
            OnPropertyChanged(nameof(LiveViewDisplayImage));
        }
    }

    partial void OnColorPreviewFillColorChanged(Color value)
    {
        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    private void DisableColorPreview()
    {
        if (!IsColorPreviewActive)
        {
            return;
        }

        IsColorPreviewActive = false;
        _colorPreviewImage = null;
        OnPropertyChanged(nameof(LiveViewDisplayImage));
    }

    [RelayCommand]
    private void CopyColorPickRect()
    {
        CopyTextToClipboard(BuildRectClipboardText("roi", ColorPickX, ColorPickY, ColorPickW, ColorPickH), "复制取色ROI到剪贴板");
    }

    [RelayCommand]
    private async Task PasteColorPickRect()
    {
        if (await TryGetClipboardRectAsync(SetColorPickRect))
        {
            ApplySelectionPreview();
        }
    }

    [RelayCommand]
    private void CopyColorPickExpandedRect()
    {
        CopyTextToClipboard(BuildRectClipboardText("roi", ColorPickExpandedX, ColorPickExpandedY, ColorPickExpandedW, ColorPickExpandedH), "复制取色扩展ROI到剪贴板");
    }

    [RelayCommand]
    private void ApplyColorPickRoiToMatch()
    {
        ApplyRectToColorMatch(_colorPickRect);
    }

    [RelayCommand]
    private void ApplyColorPickExpandedRoiToMatch()
    {
        ApplyRectToColorMatch(_colorPickExpandedRect);
    }

    [RelayCommand]
    private void ApplyRoiToMatch()
    {
        ApplyRectToClickTestTarget(_roiRect);
    }

    [RelayCommand]
    private void ApplyOriginTargetToMatch()
    {
        ApplyRectToClickTestTarget(_originTargetRect);
    }

    [RelayCommand]
    private void ApplyTargetToMatch()
    {
        ApplyRectToClickTestTarget(_targetRect);
    }

    [RelayCommand]
    private void ApplyScreenshotRoiToMatch()
    {
        ApplyRectToTemplateMatch(_screenshotRect);
    }

    [RelayCommand]
    private void ApplyScreenshotExpandedRoiToMatch()
    {
        ApplyRectToTemplateMatch(_screenshotExpandedRect);
    }

    [RelayCommand]
    private void ApplyOcrRoiToMatch()
    {
        ApplyRectToOcrMatch(_ocrRect);
    }

    [RelayCommand]
    private void ApplyOcrExpandedRoiToMatch()
    {
        ApplyRectToOcrMatch(_ocrExpandedRect);
    }

    [RelayCommand]
    private void CopyRgbUpper()
    {
        CopyTextToClipboard(BuildColorTripletClipboardText("upper", RgbUpperR, RgbUpperG, RgbUpperB), "复制RGB Upper到剪贴板");
    }

    [RelayCommand]
    private async Task PasteRgbUpper()
    {
        if (await TryGetClipboardTripletAsync((first, second, third) =>
            {
                RgbUpperR = first.ToString();
                RgbUpperG = second.ToString();
                RgbUpperB = third.ToString();
            }))
        {
            if (IsColorPreviewActive)
            {
                ApplyColorFilter();
            }
        }
    }

    [RelayCommand]
    private void ClearRgbUpper()
    {
        RgbUpperR = "0";
        RgbUpperG = "0";
        RgbUpperB = "0";
        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    [RelayCommand]
    private void CopyRgbLower()
    {
        CopyTextToClipboard(BuildColorTripletClipboardText("lower", RgbLowerR, RgbLowerG, RgbLowerB), "复制RGB Lower到剪贴板");
    }

    [RelayCommand]
    private async Task PasteRgbLower()
    {
        if (await TryGetClipboardTripletAsync((first, second, third) =>
            {
                RgbLowerR = first.ToString();
                RgbLowerG = second.ToString();
                RgbLowerB = third.ToString();
            }))
        {
            if (IsColorPreviewActive)
            {
                ApplyColorFilter();
            }
        }
    }

    [RelayCommand]
    private void ClearRgbLower()
    {
        RgbLowerR = "0";
        RgbLowerG = "0";
        RgbLowerB = "0";
        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    [RelayCommand]
    private void CopyHsvUpper()
    {
        CopyTextToClipboard(BuildColorTripletClipboardText("upper", HsvUpperH, HsvUpperS, HsvUpperV), "复制HSV Upper到剪贴板");
    }

    [RelayCommand]
    private async Task PasteHsvUpper()
    {
        if (await TryGetClipboardTripletAsync((first, second, third) =>
            {
                HsvUpperH = first.ToString();
                HsvUpperS = second.ToString();
                HsvUpperV = third.ToString();
            }))
        {
            if (IsColorPreviewActive)
            {
                ApplyColorFilter();
            }
        }
    }

    [RelayCommand]
    private void ClearHsvUpper()
    {
        HsvUpperH = "0";
        HsvUpperS = "0";
        HsvUpperV = "0";
        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    [RelayCommand]
    private void CopyHsvLower()
    {
        CopyTextToClipboard(BuildColorTripletClipboardText("lower", HsvLowerH, HsvLowerS, HsvLowerV), "复制HSV Lower到剪贴板");
    }

    [RelayCommand]
    private async Task PasteHsvLower()
    {
        if (await TryGetClipboardTripletAsync((first, second, third) =>
            {
                HsvLowerH = first.ToString();
                HsvLowerS = second.ToString();
                HsvLowerV = third.ToString();
            }))
        {
            if (IsColorPreviewActive)
            {
                ApplyColorFilter();
            }
        }
    }

    [RelayCommand]
    private void ClearHsvLower()
    {
        HsvLowerH = "0";
        HsvLowerS = "0";
        HsvLowerV = "0";
        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    [RelayCommand]
    private void CopyGrayUpper()
    {
        CopyTextToClipboard(BuildGrayClipboardText("upper", GrayUpper), "复制Gray Upper到剪贴板");
    }

    [RelayCommand]
    private async Task PasteGrayUpper()
    {
        if (await TryGetClipboardNumberAsync(value =>
            {
                GrayUpper = ((int)Math.Round(value)).ToString();
            }))
        {
            if (IsColorPreviewActive)
            {
                ApplyColorFilter();
            }
        }
    }

    [RelayCommand]
    private void ClearGrayUpper()
    {
        GrayUpper = "0";
        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    [RelayCommand]
    private void CopyGrayLower()
    {
        CopyTextToClipboard(BuildGrayClipboardText("lower", GrayLower), "复制Gray Lower到剪贴板");
    }

    [RelayCommand]
    private async Task PasteGrayLower()
    {
        if (await TryGetClipboardNumberAsync(value =>
            {
                GrayLower = ((int)Math.Round(value)).ToString();
            }))
        {
            if (IsColorPreviewActive)
            {
                ApplyColorFilter();
            }
        }
    }

    [RelayCommand]
    private void ClearGrayLower()
    {
        GrayLower = "0";
        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    [RelayCommand]
    private void ExportColorMatchPair()
    {
        switch (ColorMode)
        {
            case 1:
                AddOrUpdateTripletPair(
                    HsvUpperRows,
                    HsvLowerRows,
                    ParseIntOrZero(HsvUpperH),
                    ParseIntOrZero(HsvUpperS),
                    ParseIntOrZero(HsvUpperV),
                    ParseIntOrZero(HsvLowerH),
                    ParseIntOrZero(HsvLowerS),
                    ParseIntOrZero(HsvLowerV));
                break;
            case 2:
                AddOrUpdateSinglePair(
                    GrayUpperRows,
                    GrayLowerRows,
                    ParseIntOrZero(GrayUpper),
                    ParseIntOrZero(GrayLower));
                break;
            default:
                AddOrUpdateTripletPair(
                    RgbUpperRows,
                    RgbLowerRows,
                    ParseIntOrZero(RgbUpperR),
                    ParseIntOrZero(RgbUpperG),
                    ParseIntOrZero(RgbUpperB),
                    ParseIntOrZero(RgbLowerR),
                    ParseIntOrZero(RgbLowerG),
                    ParseIntOrZero(RgbLowerB));
                break;
        }
    }

    [RelayCommand]
    private void CopyColorMatchPairs()
    {
        var method = GetColorMatchMethodValue();
        var mode = ResolveColorMatchMode(method);
        var text = BuildColorMatchPairsClipboardText(mode);
        CopyTextToClipboard(text, LangKeys.CopyToClipboard.ToLocalization());
    }

    [RelayCommand]
    private void AddRgbUpperRow() => AddTripletPair(RgbUpperRows, RgbLowerRows);

    [RelayCommand]
    private void RemoveRgbUpperRow(Helper.Other.TripletRow? row) => RemoveTripletPair(RgbUpperRows, RgbLowerRows, row, true);

    [RelayCommand]
    private void ClearRgbUpperRow(Helper.Other.TripletRow? row) => ClearTripletRow(row);

    [RelayCommand]
    private async Task PasteRgbUpperRow(Helper.Other.TripletRow? row) => await PasteTripletRow(row);

    [RelayCommand]
    private void AddRgbLowerRow() => AddTripletPair(RgbUpperRows, RgbLowerRows);

    [RelayCommand]
    private void RemoveRgbLowerRow(Helper.Other.TripletRow? row) => RemoveTripletPair(RgbUpperRows, RgbLowerRows, row, false);

    [RelayCommand]
    private void ClearRgbLowerRow(Helper.Other.TripletRow? row) => ClearTripletRow(row);

    [RelayCommand]
    private async Task PasteRgbLowerRow(Helper.Other.TripletRow? row) => await PasteTripletRow(row);

    [RelayCommand]
    private void AddHsvUpperRow() => AddTripletPair(HsvUpperRows, HsvLowerRows);

    [RelayCommand]
    private void RemoveHsvUpperRow(Helper.Other.TripletRow? row) => RemoveTripletPair(HsvUpperRows, HsvLowerRows, row, true);

    [RelayCommand]
    private void ClearHsvUpperRow(Helper.Other.TripletRow? row) => ClearTripletRow(row);

    [RelayCommand]
    private async Task PasteHsvUpperRow(Helper.Other.TripletRow? row) => await PasteTripletRow(row);

    [RelayCommand]
    private void AddHsvLowerRow() => AddTripletPair(HsvUpperRows, HsvLowerRows);

    [RelayCommand]
    private void RemoveHsvLowerRow(Helper.Other.TripletRow? row) => RemoveTripletPair(HsvUpperRows, HsvLowerRows, row, false);

    [RelayCommand]
    private void ClearHsvLowerRow(Helper.Other.TripletRow? row) => ClearTripletRow(row);

    [RelayCommand]
    private async Task PasteHsvLowerRow(Helper.Other.TripletRow? row) => await PasteTripletRow(row);

    [RelayCommand]
    private void AddGrayUpperRow() => AddSinglePair(GrayUpperRows, GrayLowerRows);

    [RelayCommand]
    private void RemoveGrayUpperRow(Helper.Other.SingleRow? row) => RemoveSinglePair(GrayUpperRows, GrayLowerRows, row, true);

    [RelayCommand]
    private void ClearGrayUpperRow(Helper.Other.SingleRow? row) => ClearSingleRow(row);

    [RelayCommand]
    private async Task PasteGrayUpperRow(Helper.Other.SingleRow? row) => await PasteSingleRow(row);

    [RelayCommand]
    private void AddGrayLowerRow() => AddSinglePair(GrayUpperRows, GrayLowerRows);

    [RelayCommand]
    private void RemoveGrayLowerRow(Helper.Other.SingleRow? row) => RemoveSinglePair(GrayUpperRows, GrayLowerRows, row, false);

    [RelayCommand]
    private void ClearGrayLowerRow(Helper.Other.SingleRow? row) => ClearSingleRow(row);

    [RelayCommand]
    private async Task PasteGrayLowerRow(Helper.Other.SingleRow? row) => await PasteSingleRow(row);

    private void AddTripletPair(
        ObservableCollection<Helper.Other.TripletRow> upperRows,
        ObservableCollection<Helper.Other.TripletRow> lowerRows)
    {
        EnsureTripletPairCounts(upperRows, lowerRows);
        upperRows.Add(new Helper.Other.TripletRow());
        lowerRows.Add(new Helper.Other.TripletRow());
        UpdateColorPreviewIfActive();
    }

    private void AddSinglePair(
        ObservableCollection<Helper.Other.SingleRow> upperRows,
        ObservableCollection<Helper.Other.SingleRow> lowerRows)
    {
        EnsureSinglePairCounts(upperRows, lowerRows);
        upperRows.Add(new Helper.Other.SingleRow());
        lowerRows.Add(new Helper.Other.SingleRow());
        UpdateColorPreviewIfActive();
    }

    private static int ParseIntOrZero(string text)
    {
        return int.TryParse(text, out var value) ? value : 0;
    }

    private void AddOrUpdateTripletPair(
        ObservableCollection<Helper.Other.TripletRow> upperRows,
        ObservableCollection<Helper.Other.TripletRow> lowerRows,
        int upperFirst,
        int upperSecond,
        int upperThird,
        int lowerFirst,
        int lowerSecond,
        int lowerThird)
    {
        EnsureTripletPairCounts(upperRows, lowerRows);
        if (upperRows.Count == 0 && lowerRows.Count == 0)
        {
            upperRows.Add(new Helper.Other.TripletRow(upperFirst.ToString(), upperSecond.ToString(), upperThird.ToString()));
            lowerRows.Add(new Helper.Other.TripletRow(lowerFirst.ToString(), lowerSecond.ToString(), lowerThird.ToString()));
            UpdateColorPreviewIfActive();
            return;
        }

        EnsureTripletPairCounts(upperRows, lowerRows);
        var index = Math.Min(upperRows.Count, lowerRows.Count) - 1;
        if (index < 0)
        {
            upperRows.Add(new Helper.Other.TripletRow(upperFirst.ToString(), upperSecond.ToString(), upperThird.ToString()));
            lowerRows.Add(new Helper.Other.TripletRow(lowerFirst.ToString(), lowerSecond.ToString(), lowerThird.ToString()));
            UpdateColorPreviewIfActive();
            return;
        }

        var upperRow = upperRows[index];
        var lowerRow = lowerRows[index];
        if (IsTripletRowZero(upperRow) && IsTripletRowZero(lowerRow))
        {
            upperRow.First = upperFirst.ToString();
            upperRow.Second = upperSecond.ToString();
            upperRow.Third = upperThird.ToString();
            lowerRow.First = lowerFirst.ToString();
            lowerRow.Second = lowerSecond.ToString();
            lowerRow.Third = lowerThird.ToString();
            UpdateColorPreviewIfActive();
            return;
        }

        upperRows.Add(new Helper.Other.TripletRow(upperFirst.ToString(), upperSecond.ToString(), upperThird.ToString()));
        lowerRows.Add(new Helper.Other.TripletRow(lowerFirst.ToString(), lowerSecond.ToString(), lowerThird.ToString()));
        UpdateColorPreviewIfActive();
    }

    private void AddOrUpdateSinglePair(
        ObservableCollection<Helper.Other.SingleRow> upperRows,
        ObservableCollection<Helper.Other.SingleRow> lowerRows,
        int upperValue,
        int lowerValue)
    {
        EnsureSinglePairCounts(upperRows, lowerRows);
        if (upperRows.Count == 0 && lowerRows.Count == 0)
        {
            upperRows.Add(new Helper.Other.SingleRow(upperValue.ToString()));
            lowerRows.Add(new Helper.Other.SingleRow(lowerValue.ToString()));
            UpdateColorPreviewIfActive();
            return;
        }

        EnsureSinglePairCounts(upperRows, lowerRows);
        var index = Math.Min(upperRows.Count, lowerRows.Count) - 1;
        if (index < 0)
        {
            upperRows.Add(new Helper.Other.SingleRow(upperValue.ToString()));
            lowerRows.Add(new Helper.Other.SingleRow(lowerValue.ToString()));
            UpdateColorPreviewIfActive();
            return;
        }

        var upperRow = upperRows[index];
        var lowerRow = lowerRows[index];
        if (IsSingleRowZero(upperRow) && IsSingleRowZero(lowerRow))
        {
            upperRow.Value = upperValue.ToString();
            lowerRow.Value = lowerValue.ToString();
            UpdateColorPreviewIfActive();
            return;
        }

        upperRows.Add(new Helper.Other.SingleRow(upperValue.ToString()));
        lowerRows.Add(new Helper.Other.SingleRow(lowerValue.ToString()));
        UpdateColorPreviewIfActive();
    }

    private static bool IsTripletRowZero(Helper.Other.TripletRow row)
    {
        return int.TryParse(row.First, out var firstValue)
            && int.TryParse(row.Second, out var secondValue)
            && int.TryParse(row.Third, out var thirdValue)
            && firstValue == 0
            && secondValue == 0
            && thirdValue == 0;
    }

    private static bool IsSingleRowZero(Helper.Other.SingleRow row)
    {
        return int.TryParse(row.Value, out var value)
            && value == 0;
    }

    private void RemoveTripletPair(
        ObservableCollection<Helper.Other.TripletRow> upperRows,
        ObservableCollection<Helper.Other.TripletRow> lowerRows,
        Helper.Other.TripletRow? row,
        bool fromUpper,
        bool deleteDouble = true)
    {
        if (row == null)
        {
            return;
        }

        var list = fromUpper ? upperRows : lowerRows;
        var o_list = !fromUpper ? upperRows : lowerRows;
        var index = list.IndexOf(row);
        if (index < 0)
        {
            return;
        }
        var o_row = o_list[index];
        if (list.Count <= 1)
        {
            ClearTripletRow(row);
            if (deleteDouble)
                ClearTripletRow(o_row);
            return;
        }

        list.RemoveAt(index);
        if (deleteDouble)
            o_list.RemoveAt(index);
        UpdateColorPreviewIfActive();
    }

    private void RemoveSinglePair(
        ObservableCollection<Helper.Other.SingleRow> upperRows,
        ObservableCollection<Helper.Other.SingleRow> lowerRows,
        Helper.Other.SingleRow? row,
        bool fromUpper,
        bool deleteDouble = true)
    {
        if (row == null)
        {
            return;
        }

        var list = fromUpper ? upperRows : lowerRows;
        var o_list = !fromUpper ? upperRows : lowerRows;
        var index = list.IndexOf(row);
        if (index < 0)
        {
            return;
        }
        var o_row = o_list[index];
        if (list.Count <= 1)
        {
            ClearSingleRow(row);
            if (deleteDouble)
                ClearSingleRow(o_row);
            return;
        }

        list.RemoveAt(index);
        if (deleteDouble)
            o_list.RemoveAt(index);
        UpdateColorPreviewIfActive();
    }

    private static void EnsureTripletPairCounts(
        ObservableCollection<Helper.Other.TripletRow> upperRows,
        ObservableCollection<Helper.Other.TripletRow> lowerRows)
    {
        if (upperRows.Count == lowerRows.Count)
        {
            return;
        }

        if (upperRows.Count < lowerRows.Count)
        {
            var missing = lowerRows.Count - upperRows.Count;
            for (var i = 0; i < missing; i++)
            {
                upperRows.Add(new Helper.Other.TripletRow());
            }

            return;
        }

        var gap = upperRows.Count - lowerRows.Count;
        for (var i = 0; i < gap; i++)
        {
            lowerRows.Add(new Helper.Other.TripletRow());
        }
    }

    private static void EnsureSinglePairCounts(
        ObservableCollection<Helper.Other.SingleRow> upperRows,
        ObservableCollection<Helper.Other.SingleRow> lowerRows)
    {
        if (upperRows.Count == lowerRows.Count)
        {
            return;
        }

        if (upperRows.Count < lowerRows.Count)
        {
            var missing = lowerRows.Count - upperRows.Count;
            for (var i = 0; i < missing; i++)
            {
                upperRows.Add(new Helper.Other.SingleRow());
            }

            return;
        }

        var gap = upperRows.Count - lowerRows.Count;
        for (var i = 0; i < gap; i++)
        {
            lowerRows.Add(new Helper.Other.SingleRow());
        }
    }

    private void ClearTripletPairAt(
        ObservableCollection<Helper.Other.TripletRow> upperRows,
        ObservableCollection<Helper.Other.TripletRow> lowerRows,
        int index)
    {
        if (index < 0)
        {
            return;
        }

        if (index < upperRows.Count)
        {
            var upperRow = upperRows[index];
            upperRow.First = "0";
            upperRow.Second = "0";
            upperRow.Third = "0";
        }

        if (index < lowerRows.Count)
        {
            var lowerRow = lowerRows[index];
            lowerRow.First = "0";
            lowerRow.Second = "0";
            lowerRow.Third = "0";
        }

        EnsureTripletPairCounts(upperRows, lowerRows);
        UpdateColorPreviewIfActive();
    }

    private void ClearSinglePairAt(
        ObservableCollection<Helper.Other.SingleRow> upperRows,
        ObservableCollection<Helper.Other.SingleRow> lowerRows,
        int index)
    {
        if (index < 0)
        {
            return;
        }

        if (index < upperRows.Count)
        {
            upperRows[index].Value = "0";
        }

        if (index < lowerRows.Count)
        {
            lowerRows[index].Value = "0";
        }

        EnsureSinglePairCounts(upperRows, lowerRows);
        UpdateColorPreviewIfActive();
    }

    private void ClearTripletRow(Helper.Other.TripletRow? row)
    {
        if (row == null)
        {
            return;
        }

        row.First = "0";
        row.Second = "0";
        row.Third = "0";
        UpdateColorPreviewIfActive();
    }

    private void ClearSingleRow(Helper.Other.SingleRow? row)
    {
        if (row == null)
        {
            return;
        }

        row.Value = "0";
        UpdateColorPreviewIfActive();
    }

    private async Task PasteTripletRow(Helper.Other.TripletRow? row)
    {
        if (row == null)
        {
            return;
        }

        if (await TryGetClipboardTripletAsync((first, second, third) =>
            {
                row.First = first.ToString();
                row.Second = second.ToString();
                row.Third = third.ToString();
            }))
        {
            UpdateColorPreviewIfActive();
        }
    }

    private async Task PasteSingleRow(Helper.Other.SingleRow? row)
    {
        if (row == null)
        {
            return;
        }

        if (await TryGetClipboardNumberAsync(value =>
            {
                row.Value = ((int)Math.Round(value)).ToString();
            }))
        {
            UpdateColorPreviewIfActive();
        }
    }

    private void UpdateColorPreviewIfActive()
    {
        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    [RelayCommand]
    private void ClearColorPick()
    {
        _suppressColorPickSync = true;
        _colorPickRect = default;
        ColorPickX = "0";
        ColorPickY = "0";
        ColorPickW = "0";
        ColorPickH = "0";
        _suppressColorPickSync = false;
        UpdateColorPickExpandedRect();
        ApplySelectionPreview();
    }

    [RelayCommand]
    private void CopyScreenshotRect()
    {
        CopyTextToClipboard(BuildRectClipboardText("roi", ScreenshotX, ScreenshotY, ScreenshotW, ScreenshotH), "复制截图ROI到剪贴板");
    }

    [RelayCommand]
    private async Task PasteScreenshotRect()
    {
        if (await TryGetClipboardRectAsync(SetScreenshotRect))
        {
            ApplySelectionPreview();
        }
    }

    [RelayCommand]
    private void CopyScreenshotExpandedRect()
    {
        CopyTextToClipboard(BuildRectClipboardText("roi", ScreenshotExpandedX, ScreenshotExpandedY, ScreenshotExpandedW, ScreenshotExpandedH), "复制截图扩展ROI到剪贴板");
    }

    [RelayCommand]
    private void ClearScreenshot()
    {
        _suppressScreenshotSync = true;
        _screenshotRect = default;
        ScreenshotX = "0";
        ScreenshotY = "0";
        ScreenshotW = "0";
        ScreenshotH = "0";
        _suppressScreenshotSync = false;
        UpdateScreenshotExpandedRect();
        ApplySelectionPreview();
    }

    [RelayCommand]
    private void CopyOcrRect()
    {
        CopyTextToClipboard(BuildRectClipboardText("roi", OcrX, OcrY, OcrW, OcrH), "复制OCR ROI到剪贴板");
    }

    [RelayCommand]
    private async Task PasteOcrRect()
    {
        if (await TryGetClipboardRectAsync(SetOcrRect))
        {
            ApplySelectionPreview();
        }
    }

    [RelayCommand]
    private void CopyOcrExpandedRect()
    {
        CopyTextToClipboard(BuildRectClipboardText("roi", OcrExpandedX, OcrExpandedY, OcrExpandedW, OcrExpandedH), "复制OCR扩展ROI到剪贴板");
    }

    [RelayCommand]
    private void ClearOcr()
    {
        _suppressOcrSync = true;
        _ocrRect = default;
        OcrX = "0";
        OcrY = "0";
        OcrW = "0";
        OcrH = "0";
        _suppressOcrSync = false;
        UpdateOcrExpandedRect();
        ApplySelectionPreview();
    }

    [RelayCommand]
    private void CopyNeuralNetworkDetectRect()
    {
        CopyTextToClipboard(BuildRectClipboardText("roi", NeuralNetworkDetectX, NeuralNetworkDetectY, NeuralNetworkDetectW, NeuralNetworkDetectH), "复制神经网络检测ROI到剪贴板");
    }

    [RelayCommand]
    private async Task PasteNeuralNetworkDetectRect()
    {
        if (await TryGetClipboardRectAsync(SetNeuralNetworkDetectRect))
        {
            ApplySelectionPreview();
        }
    }

    [RelayCommand]
    private void CopyNeuralNetworkDetectExpandedRect()
    {
        CopyTextToClipboard(BuildRectClipboardText("roi", NeuralNetworkDetectExpandedX, NeuralNetworkDetectExpandedY, NeuralNetworkDetectExpandedW, NeuralNetworkDetectExpandedH), "复制神经网络检测扩展ROI到剪贴板");
    }

    [RelayCommand]
    private void ClearNeuralNetworkDetect()
    {
        _suppressNeuralNetworkDetectSync = true;
        _neuralNetworkDetectRect = default;
        NeuralNetworkDetectX = "0";
        NeuralNetworkDetectY = "0";
        NeuralNetworkDetectW = "0";
        NeuralNetworkDetectH = "0";
        _suppressNeuralNetworkDetectSync = false;
        UpdateNeuralNetworkDetectExpandedRect();
        ApplySelectionPreview();
    }

    [RelayCommand]
    private void ApplyNeuralNetworkDetectRoiToMatch()
    {
        ApplyRectToNeuralNetworkDetectMatch(_neuralNetworkDetectRect);
    }

    [RelayCommand]
    private void ApplyNeuralNetworkDetectExpandedRoiToMatch()
    {
        ApplyRectToNeuralNetworkDetectMatch(_neuralNetworkDetectExpandedRect);
    }

    private void ApplyRectToNeuralNetworkDetectMatch(Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        NeuralNetworkDetectMatchRoiX = ((int)Math.Round(rect.X)).ToString();
        NeuralNetworkDetectMatchRoiY = ((int)Math.Round(rect.Y)).ToString();
        NeuralNetworkDetectMatchRoiW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        NeuralNetworkDetectMatchRoiH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
    }

    private void SetNeuralNetworkDetectRect(Rect rect)
    {
        _suppressNeuralNetworkDetectSync = true;
        _neuralNetworkDetectRect = rect;
        NeuralNetworkDetectX = ((int)Math.Round(rect.X)).ToString();
        NeuralNetworkDetectY = ((int)Math.Round(rect.Y)).ToString();
        NeuralNetworkDetectW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        NeuralNetworkDetectH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
        _suppressNeuralNetworkDetectSync = false;
        UpdateNeuralNetworkDetectExpandedRect();
    }

    private void UpdateNeuralNetworkDetectRectFromText()
    {
        if (_suppressNeuralNetworkDetectSync)
        {
            return;
        }

        if (!TryParseRect(NeuralNetworkDetectX, NeuralNetworkDetectY, NeuralNetworkDetectW, NeuralNetworkDetectH, out var rect))
        {
            return;
        }

        _neuralNetworkDetectRect = rect;
        UpdateNeuralNetworkDetectExpandedRect();
        ApplySelectionPreview();
    }


    [RelayCommand]
    private void CopyOcrResult()
    {
        CopyTextToClipboard(OcrResult ?? string.Empty, "复制OCR结果到剪贴板");
    }

    [RelayCommand]
    private async Task PasteOcrMatchRect()
    {
        if (await TryGetClipboardRectAsync(SetOcrMatchRect))
        {
            return;
        }
    }

    [RelayCommand]
    private void ClearOcrMatchRect()
    {
        OcrMatchRoiX = "0";
        OcrMatchRoiY = "0";
        OcrMatchRoiW = "0";
        OcrMatchRoiH = "0";
    }

    [RelayCommand]
    private async Task PasteTemplateMatchRect()
    {
        if (await TryGetClipboardRectAsync(SetTemplateMatchRect))
        {
            return;
        }
    }

    [RelayCommand]
    private void ClearTemplateMatchRect()
    {
        TemplateMatchRoiX = "0";
        TemplateMatchRoiY = "0";
        TemplateMatchRoiW = "0";
        TemplateMatchRoiH = "0";
    }

    [RelayCommand]
    private async Task PasteColorMatchRect()
    {
        if (await TryGetClipboardRectAsync(SetColorMatchRect))
        {
            return;
        }
    }

    [RelayCommand]
    private void ClearColorMatchRect()
    {
        ColorMatchRoiX = "0";
        ColorMatchRoiY = "0";
        ColorMatchRoiW = "0";
        ColorMatchRoiH = "0";
    }

    [RelayCommand]
    private async Task PasteClickTestTarget()
    {
        if (await TryGetClipboardRectAsync(SetClickTestTargetRect))
        {
            return;
        }
    }

    [RelayCommand]
    private void ClearClickTestTarget()
    {
        ClickTestTargetX = "0";
        ClickTestTargetY = "0";
        ClickTestTargetW = "0";
        ClickTestTargetH = "0";
    }

    [RelayCommand]
    private async Task PasteClickTestOffset()
    {
        if (await TryGetClipboardRectAsync(SetClickTestOffsetRect))
        {
            return;
        }
    }

    [RelayCommand]
    private void ClearClickTestOffset()
    {
        ClickTestOffsetX = "0";
        ClickTestOffsetY = "0";
        ClickTestOffsetW = "0";
        ClickTestOffsetH = "0";
    }

    [RelayCommand]
    private async Task PasteSwipeTestStart()
    {
        if (await TryGetClipboardPointAsync(SetSwipeTestStart))
        {
            return;
        }
    }

    [RelayCommand]
    private void ClearSwipeTestStart()
    {
        SwipeTestStartX = "0";
        SwipeTestStartY = "0";
    }

    [RelayCommand]
    private async Task PasteSwipeTestEnd()
    {
        if (await TryGetClipboardPointAsync(SetSwipeTestEnd))
        {
            return;
        }
    }

    [RelayCommand]
    private void ClearSwipeTestEnd()
    {
        SwipeTestEndX = "0";
        SwipeTestEndY = "0";
        if (SwipeTestEndRows.Count > 0)
        {
            SwipeTestEndRows[0].First = "0";
            SwipeTestEndRows[0].Second = "0";
        }
    }

    [RelayCommand]
    private async Task PasteSwipeTestDuration()
    {
        if (await TryGetClipboardNumberAsync(SetSwipeTestDuration))
        {
            return;
        }
    }

    [RelayCommand]
    private void ClearSwipeTestDuration()
    {
        SwipeTestDuration = "0";
    }

    [RelayCommand]
    private void AddSwipeTestEndRow()
    {
        EnsureSwipeTestEndRowsInitialized();
        SwipeTestEndRows.Add(new Helper.Other.PairRow());
    }

    [RelayCommand]
    private void RemoveSwipeTestEndRow(Helper.Other.PairRow? row)
    {
        if (row == null)
        {
            return;
        }

        if (SwipeTestEndRows.Count <= 1)
        {
            ClearSwipeTestEndRow(row);
            return;
        }

        var index = SwipeTestEndRows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        SwipeTestEndRows.RemoveAt(index);
        if (index == 0 && SwipeTestEndRows.Count > 0)
        {
            SwipeTestEndX = SwipeTestEndRows[0].First;
            SwipeTestEndY = SwipeTestEndRows[0].Second;
        }
    }

    [RelayCommand]
    private void ClearSwipeTestEndRow(Helper.Other.PairRow? row)
    {
        if (row == null)
        {
            return;
        }

        row.First = "0";
        row.Second = "0";
        if (SwipeTestEndRows.IndexOf(row) == 0)
        {
            SwipeTestEndX = row.First;
            SwipeTestEndY = row.Second;
        }
    }

    [RelayCommand]
    private async Task PasteSwipeTestEndRow(Helper.Other.PairRow? row)
    {
        if (row == null)
        {
            return;
        }

        if (await TryGetClipboardPointAsync(point =>
            {
                row.First = ((int)Math.Round(point.X)).ToString();
                row.Second = ((int)Math.Round(point.Y)).ToString();
            }))
        {
            if (SwipeTestEndRows.IndexOf(row) == 0)
            {
                SwipeTestEndX = row.First;
                SwipeTestEndY = row.Second;
            }
        }
    }

    [RelayCommand]
    private async Task ImportImage()
    {
        var topLevel = TopLevel.GetTopLevel(Instances.RootView);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LangKeys.LoadImageTitle.ToLocalization(),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"]
                }
            ]
        });

        var file = files?.FirstOrDefault();
        if (file == null)
        {
            return;
        }

        if (!IsLiveViewPaused)
        {
            IsLiveViewPaused = true;
        }

        await using var stream = await file.OpenReadAsync();
        var bitmap = await LoadImportedBitmapAsync(stream);
        LiveViewImage = bitmap;
        _pausedLiveViewImage = bitmap;
        OnPropertyChanged(nameof(LiveViewDisplayImage));

        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    [RelayCommand]
    private async Task RunOcr()
    {
        IsRunning = true;
        var sourceBitmap = LiveViewImage ?? LiveViewDisplayImage;
        if (sourceBitmap is not Bitmap bitmap)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewNoScreenshot.ToLocalization());
            IsRunning = false;
            return;
        }

        if (!TryParseRect(OcrX, OcrY, OcrW, OcrH, out var rect))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewSelectOcrRegion.ToLocalization());
            IsRunning = false;
            return;
        }

        var tasker = await MaaProcessor.Instance.GetTaskerAsync();
        if (tasker == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewRecognizerUnavailable.ToLocalization());
            IsRunning = false;
            return;
        }

        TaskManager.RunTask(() =>
        {
            var result = RecognitionHelper.ReadTextFromMaaTasker(
                tasker,
                bitmap,
                (int)Math.Round(rect.X),
                (int)Math.Round(rect.Y),
                Math.Max(1, (int)Math.Round(rect.Width)),
                Math.Max(1, (int)Math.Round(rect.Height)));
            DispatcherHelper.PostOnMainThread(() => OcrResult = result);
            IsRunning = false;
        }, "OCR");
    }

    [RelayCommand]
    private async Task RunOcrHitTest()
    {
        IsOcrTestPanelVisible = true;
        SyncOcrMatchDefaults();

        if (!TryParseRect(OcrMatchRoiX, OcrMatchRoiY, OcrMatchRoiW, OcrMatchRoiH, out var rect))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewSelectOcrRegion.ToLocalization());
            return;
        }

        if (string.IsNullOrWhiteSpace(OcrMatchTargetText))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.EditTaskDialog_RecognitionText_Tooltip.ToLocalization());
            return;
        }

        var tasker = await MaaProcessor.Instance.GetTaskerAsync();
        if (tasker == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewRecognizerUnavailable.ToLocalization());
            return;
        }

        if (!double.TryParse(OcrMatchThreshold, out var threshold))
        {
            threshold = 0.3;
        }

        RecognitionHelper.RunOcrMatch(
            tasker,
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            Math.Max(0, (int)Math.Round(rect.Width)),
            Math.Max(0, (int)Math.Round(rect.Height)),
            OcrMatchTargetText,
            threshold,
            OcrMatchOnlyRec);
    }

    [RelayCommand]
    private async Task RunTemplateMatchTest()
    {
        IsScreenshotTestPanelVisible = true;
        SyncTemplateMatchDefaults();

        if (TemplateMatchImage == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.SelectExistingImage.ToLocalization());
            return;
        }

        if (!TryParseRect(TemplateMatchRoiX, TemplateMatchRoiY, TemplateMatchRoiW, TemplateMatchRoiH, out var rect))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewSelectScreenshotRegion.ToLocalization());
            return;
        }

        var tasker = await MaaProcessor.Instance.GetTaskerAsync();
        if (tasker == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewRecognizerUnavailable.ToLocalization());
            return;
        }

        if (!double.TryParse(TemplateMatchThreshold, out var threshold))
        {
            threshold = 0.7;
        }

        RecognitionHelper.RunTemplateMatch(
            tasker,
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            Math.Max(0, (int)Math.Round(rect.Width)),
            Math.Max(0, (int)Math.Round(rect.Height)),
            TemplateMatchImage,
            TemplateMatchGreenMask,
            threshold,
            TemplateMatchMethod);
    }

    [RelayCommand]
    private async Task RunColorMatchTest()
    {
        IsColorTestPanelVisible = true;
        SyncColorMatchDefaults();

        if (!TryParseRect(ColorMatchRoiX, ColorMatchRoiY, ColorMatchRoiW, ColorMatchRoiH, out var rect))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewSelectColorRegion.ToLocalization());
            return;
        }

        var tasker = await MaaProcessor.Instance.GetTaskerAsync();
        if (tasker == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewRecognizerUnavailable.ToLocalization());
            return;
        }

        var method = GetColorMatchMethodValue();

        if (!int.TryParse(ColorMatchCount, out var count) || count <= 0)
        {
            count = 1;
        }

        var mode = ResolveColorMatchMode(method);
        if (!TryBuildColorMatchLists(mode, out var upper, out var lower))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.EditTaskDialog_ColorExtraction_Tooltip.ToLocalization());
            return;
        }

        RecognitionHelper.RunColorMatch(
            tasker,
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            Math.Max(1, (int)Math.Round(rect.Width)),
            Math.Max(1, (int)Math.Round(rect.Height)),
            method,
            upper,
            lower,
            count,
            ColorMatchConnected);
    }

    [RelayCommand]
    private async Task RunClickHitTest()
    {
        IsClickTestPanelVisible = true;
        SyncClickTestDefaults();

        if (!TryParseRect(ClickTestTargetX, ClickTestTargetY, ClickTestTargetW, ClickTestTargetH, out var targetRect))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.EditTaskDialog_SelectionRegion_Tooltip.ToLocalization());
            return;
        }

        if (!TryParseOffset(ClickTestOffsetX, ClickTestOffsetY, ClickTestOffsetW, ClickTestOffsetH,
                out var offsetX, out var offsetY, out var offsetW, out var offsetH))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.EditTaskDialog_SelectionRegion_Tooltip.ToLocalization());
            return;
        }

        var tasker = await MaaProcessor.Instance.GetTaskerAsync();
        if (tasker == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewRecognizerUnavailable.ToLocalization());
            return;
        }

                if (!int.TryParse(ClickTestContact, out var contact))
                {
                    contact = 0;
                }
        
                if (IsLiveViewPaused)
                    IsLiveViewPaused = false;
                RecognitionHelper.RunClickTest(
                    tasker,
                    (int)Math.Round(targetRect.X),
                    (int)Math.Round(targetRect.Y),
                    Math.Max(0, (int)Math.Round(targetRect.Width)),
                    Math.Max(0, (int)Math.Round(targetRect.Height)),
                    (int)Math.Round(offsetX),
                    (int)Math.Round(offsetY),
                    (int)Math.Round(offsetW),
                    (int)Math.Round(offsetH),
                    contact);
            }

    [RelayCommand]
    private async Task RunSwipeHitTest()
    {
        IsSwipeTestPanelVisible = true;
        SyncSwipeTestDefaults();

        if (!TryParsePoint(SwipeTestStartX, SwipeTestStartY, out var start)
            || !TryBuildSwipeTestEndList(out var ends))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewSelectScreenshotRegion.ToLocalization());
            return;
        }

        var tasker = await MaaProcessor.Instance.GetTaskerAsync();
        if (tasker == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewRecognizerUnavailable.ToLocalization());
            return;
        }

        if (!int.TryParse(SwipeTestDuration, out var duration) || duration <= 0)
        {
            duration = 200;
        }

        if (IsLiveViewPaused)
            IsLiveViewPaused = false;
        RecognitionHelper.RunSwipeTest(
            tasker,
            [
                (int)Math.Round(start.X),
                (int)Math.Round(start.Y)
            ],
            ends,
            duration);
    }

    [RelayCommand]
    private async Task RunKeyHitTest()
    {
        IsKeyTestPanelVisible = true;
        var list = new List<int>();
        var keys = KeyCaptureCode.Split(",");
        foreach (var key in keys)
        {
            if (TryParseKeyCode(key, out var keyCode))
            {
                list.Add(keyCode);
            }
        }
        if (!keys.Any())
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewSelectKey.ToLocalization());
            return;
        }

        var tasker = await MaaProcessor.Instance.GetTaskerAsync();
        if (tasker == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewRecognizerUnavailable.ToLocalization());
            return;
        }

        if (IsLiveViewPaused)
            IsLiveViewPaused = false;

        RecognitionHelper.RunKeyClickTest(tasker, list);
    }

    [RelayCommand]
    private async Task RunNeuralNetworkDetectTest()
    {
        IsNeuralNetworkDetectTestPanelVisible = true;
        SyncNeuralNetworkDetectDefaults();

        if (string.IsNullOrWhiteSpace(NeuralNetworkDetectModelPath))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.SelectModel.ToLocalization());
            return;
        }

        if (!TryParseRect(NeuralNetworkDetectMatchRoiX, NeuralNetworkDetectMatchRoiY, NeuralNetworkDetectMatchRoiW, NeuralNetworkDetectMatchRoiH, out var rect))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewSelectScreenshotRegion.ToLocalization());
            return;
        }


        var tasker = await MaaProcessor.Instance.GetTaskerAsync();
        if (tasker == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewRecognizerUnavailable.ToLocalization());
            return;
        }

        var labels = ClassifyLabelRows.Select(r => r.Value).ToList();
        var expecteds = ExpectedRows.Select(r => ParseIntOrZero(r.Value)).ToList();
        var thresholds = ThresholdRows.Select(r => double.TryParse(r.Value, out var v) ? v : 0.7).ToList();

        object thresholdArg = thresholds.Count == 1 ? thresholds[0] : thresholds;
        if (thresholds.Count == 0) thresholdArg = 0.3; // Default
        if (!int.TryParse(NeuralNetworkDetectIndex, out var idx))
        {
            idx = 0;
        }

        RecognitionHelper.RunNeuralNetworkDetect(
            tasker,
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            Math.Max(0, (int)Math.Round(rect.Width)),
            Math.Max(0, (int)Math.Round(rect.Height)),
            NeuralNetworkDetectModelPath,
            labels,
            expecteds,
            thresholdArg,
            idx,
            NeuralNetworkDetectOrderBy
        );
    }

    private void SyncNeuralNetworkDetectDefaults(bool force = false)
    {
        if (force)
        {
            if (TryParseRect(NeuralNetworkDetectExpandedX, NeuralNetworkDetectExpandedY, NeuralNetworkDetectExpandedW, NeuralNetworkDetectExpandedH, out var expandedRect)
                && expandedRect.Width > 0
                && expandedRect.Height > 0)
            {
                NeuralNetworkDetectMatchRoiX = NeuralNetworkDetectExpandedX;
                NeuralNetworkDetectMatchRoiY = NeuralNetworkDetectExpandedY;
                NeuralNetworkDetectMatchRoiW = NeuralNetworkDetectExpandedW;
                NeuralNetworkDetectMatchRoiH = NeuralNetworkDetectExpandedH;
            }
        }
        else if (!TryParseRect(NeuralNetworkDetectMatchRoiX, NeuralNetworkDetectMatchRoiY, NeuralNetworkDetectMatchRoiW, NeuralNetworkDetectMatchRoiH, out var matchRect)
                 || matchRect.Width <= 0
                 || matchRect.Height <= 0)
        {
            NeuralNetworkDetectMatchRoiX = NeuralNetworkDetectExpandedX;
            NeuralNetworkDetectMatchRoiY = NeuralNetworkDetectExpandedY;
            NeuralNetworkDetectMatchRoiW = NeuralNetworkDetectExpandedW;
            NeuralNetworkDetectMatchRoiH = NeuralNetworkDetectExpandedH;
        }

        EnsureSingleRowsInitialized(ClassifyLabelRows, string.Empty);
        EnsureSingleRowsInitialized(ExpectedRows, string.Empty);
        EnsureSingleRowsInitialized(ThresholdRows, "0.3");
    }


    [RelayCommand]
    private async Task SelectModelPath()
    {
        var topLevel = TopLevel.GetTopLevel(Instances.RootView);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LangKeys.SelectExecutableFile.ToLocalization(), // Reuse or add new key
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("ONNX Model")
                {
                    Patterns = ["*.onnx"]
                }
            ]
        });

        var file = files?.FirstOrDefault();
        if (file == null) return;

        // Make path relative if possible or use full path?
        // Usually models are in resource directory.
        // For now use full path or filename if in resource.
        // Assuming path relative to MAA or resource.
        // Let's just put the name if it is in the standard location or full path.
        // Actually MAA expects path relative to resource or absolute.
        var path = file.Path.LocalPath;
        // Try get relative path from execution dir?
        // Simplest is to just set it.
        NeuralNetworkDetectModelPath = path;
    }

    [RelayCommand]
    private void AddClassifyLabelRow() => AddSingleRow(ClassifyLabelRows, string.Empty);

    [RelayCommand]
    private void RemoveClassifyLabelRow(Helper.Other.SingleRow? row) => RemoveSingleRow(ClassifyLabelRows, row);

    [RelayCommand]
    private void ClearClassifyLabelRow(Helper.Other.SingleRow? row) => ClearSingleRow(row);

    [RelayCommand]
    private async Task PasteClassifyLabelRow(Helper.Other.SingleRow? row)
    {
        if (row == null) return;
        var clipboard = Instances.RootView?.Clipboard;
        if (clipboard == null) return;
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text)) row.Value = text;
    }

    [RelayCommand]
    private void AddExpectedRow() => AddSingleRow(ExpectedRows);

    [RelayCommand]
    private void RemoveExpectedRow(Helper.Other.SingleRow? row) => RemoveSingleRow(ExpectedRows, row);

    [RelayCommand]
    private void ClearExpectedRow(Helper.Other.SingleRow? row) => ClearSingleRow(row);

    [RelayCommand]
    private async Task PasteExpectedRow(Helper.Other.SingleRow? row) => await PasteSingleRow(row);

    [RelayCommand]
    private void AddThresholdRow() => AddSingleRow(ThresholdRows, "0.3");

    [RelayCommand]
    private void RemoveThresholdRow(Helper.Other.SingleRow? row) => RemoveSingleRow(ThresholdRows, row);

    [RelayCommand]
    private void ClearThresholdRow(Helper.Other.SingleRow? row) => ClearSingleRow(row);

    [RelayCommand]
    private async Task PasteThresholdRow(Helper.Other.SingleRow? row) => await PasteSingleRow(row);

    private void AddSingleRow(ObservableCollection<Helper.Other.SingleRow> rows, string defaultValue = "0")
    {
        rows.Add(new Helper.Other.SingleRow(defaultValue));
    }

    private void RemoveSingleRow(ObservableCollection<Helper.Other.SingleRow> rows, Helper.Other.SingleRow? row)
    {
        if (row == null) return;
        if (rows.Count <= 1)
        {
            ClearSingleRow(row);
            return;
        }
        rows.Remove(row);
    }

    private static bool TryParseKeyCode(string? value, out int code)
    {
        code = 0;
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out code);
        }

        if (int.TryParse(trimmed, out code))
        {
            return true;
        }

        return int.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out code);
    }

    [RelayCommand]
    private async Task SelectTemplateImage()
    {
        var topLevel = TopLevel.GetTopLevel(Instances.RootView);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LangKeys.SelectExistingImage.ToLocalization(),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"]
                }
            ]
        });

        var file = files?.FirstOrDefault();
        if (file == null)
        {
            return;
        }

        await using var stream = await file.OpenReadAsync();
        TemplateMatchImage = new Bitmap(stream);
    }


    [RelayCommand]
    private async Task SaveScreenshot()
    {
        using var cropped = TryGetSelectedScreenshot();
        if (cropped == null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(Instances.RootView);
        if (topLevel == null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LangKeys.SaveScreenshot.ToLocalization(),
            SuggestedFileName = "screenshot.png",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG")
                {
                    Patterns = ["*.png"]
                },
                new FilePickerFileType("JPG")
                {
                    Patterns = ["*.jpg", "*.jpeg"]
                },
                new FilePickerFileType("BMP")
                {
                    Patterns = ["*.bmp"]
                }
            ]
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        await using (var stream = File.Create(path))
        {
            cropped.Save(stream);
        }

        ScreenshotRelativeResult = string.IsNullOrWhiteSpace(ScreenshotRelativePath)
            ? path
            : Path.Combine(ScreenshotRelativePath, Path.GetFileName(path));

        ToastHelper.Info(LangKeys.Tip.ToLocalization(),
            string.Format(LangKeys.LiveViewScreenshotSaved.ToLocalization(), path));
    }

    [RelayCommand]
    private async Task CopyScreenshotToClipboard()
    {
        var image = TryGetFullScreenshot();
        if (image == null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(Instances.RootView);
        var clipboard = topLevel?.Clipboard;
        if (clipboard == null)
        {
            image.Dispose();
            return;
        }

        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(DataFormat.Bitmap, image));

        string? tempPath = null;
        try
        {
            tempPath = CreateClipboardTempFilePath();
            image.Save(tempPath);

            IStorageFile? storageFile = null;
            TopLevel? t =  Application.Current?.ApplicationLifetime switch
            {
                IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
                ISingleViewApplicationLifetime single when single.MainView is Control control => TopLevel.GetTopLevel(control),
                _ => null
            };
            var storageProvider = t?.StorageProvider;
            if (storageProvider != null)
                storageFile = await storageProvider.TryGetFileFromPathAsync(tempPath);
            if (storageFile != null)
                dataTransfer.Add(DataTransferItem.CreateFile(storageFile));
        }
        catch
        {
        }

        await clipboard.SetDataAsync(dataTransfer);
        RecordClipboardImage(image);

        if (!string.IsNullOrWhiteSpace(tempPath))
        {
            RecordClipboardTempFile(tempPath);
        }

        ToastHelper.Info(LangKeys.Tip.ToLocalization(), LangKeys.CopiedToClipboard.ToLocalization());
    }

    [RelayCommand]
    private async Task QuickSaveScreenshotToFolder()
    {
        using var image = TryGetFullScreenshot();
        if (image == null)
        {
            return;
        }

        var folderPath = ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewQuickScreenshotFolderPath, string.Empty)?.Trim();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), "请先设置快速截图保存文件夹");
            return;
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            var fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            var path = Path.Combine(folderPath, fileName);

            await using (var stream = File.Create(path))
            {
                image.Save(stream);
            }

            ScreenshotRelativeResult = path;
            ToastHelper.Info(LangKeys.Tip.ToLocalization(),
                string.Format(LangKeys.LiveViewScreenshotSaved.ToLocalization(), path));
        }
        catch (Exception ex)
        {
            ToastHelper.Error(LangKeys.Tip.ToLocalization(), $"快速截图保存失败：{ex.Message}");
        }
    }

    private Bitmap? TryGetSelectedScreenshot()
    {
        if (LiveViewDisplayImage is not Bitmap bitmap)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewNoScreenshot.ToLocalization());
            return null;
        }

        if (_screenshotRect.Width <= 0 || _screenshotRect.Height <= 0)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewSelectScreenshotRegion.ToLocalization());
            return null;
        }

        var cropped = CropBitmap(bitmap, _screenshotRect);
        if (cropped == null)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewInvalidScreenshotRegion.ToLocalization());
            return null;
        }

        return cropped;
    }

    private Bitmap? TryGetFullScreenshot()
    {
        if (LiveViewDisplayImage is not Bitmap bitmap)
        {
            ToastHelper.Warn(LangKeys.Tip.ToLocalization(), LangKeys.LiveViewNoScreenshot.ToLocalization());
            return null;
        }

        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var buffer = new byte[height * stride];

        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), (IntPtr)ptr, buffer.Length, stride);
            }
        }

        var target = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = target.Lock())
        {
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    Buffer.MemoryCopy(ptr, (void*)fb.Address, fb.RowBytes * height, buffer.Length);
                }
            }
        }

        return target;
    }

    private static string CreateClipboardTempFilePath()
    {
        Directory.CreateDirectory(ClipboardTempDir);
        return Path.Combine(ClipboardTempDir, $"screenshot_{Guid.NewGuid():N}.png");
    }

    private static void RecordClipboardTempFile(string tempPath)
    {
        lock (ClipboardTempFilesLock)
        {
            ClipboardTempFiles.Add(tempPath);
        }
    }

    private static async Task<Bitmap> LoadImportedBitmapAsync(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        var imageBytes = memoryStream.ToArray();

        if (!Instances.ToolSettingsUserControlModel.ResizeImportedImageOnImport)
        {
            return new Bitmap(new MemoryStream(imageBytes));
        }

        using var buffer = new MaaImageBuffer();
        if (!TrySetEncodedData(buffer, imageBytes))
        {
            return new Bitmap(new MemoryStream(imageBytes));
        }

        var shortestSide = Math.Max(1, Instances.ToolSettingsUserControlModel.ResizeShortestSide);
        var targetWidth = buffer.Width <= buffer.Height ? shortestSide : 0;
        var targetHeight = buffer.Width <= buffer.Height ? 0 : shortestSide;

        if (buffer.Width > 0 && buffer.Height > 0)
        {
            buffer.TryResize(targetWidth, targetHeight);
        }

        return buffer.ToBitmap() ?? new Bitmap(new MemoryStream(imageBytes));
    }

    private static void RecordClipboardImage(IDisposable image)
    {
        lock (ClipboardImagesLock)
        {
            ClipboardImages.Add(image);
        }
    }

    public static void CleanupClipboardTempFiles()
    {
        IDisposable[] images;
        lock (ClipboardImagesLock)
        {
            images = ClipboardImages.ToArray();
            ClipboardImages.Clear();
        }

        foreach (var image in images)
        {
            try
            {
                image.Dispose();
            }
            catch
            {
            }
        }

        string[] files;
        lock (ClipboardTempFilesLock)
        {
            files = ClipboardTempFiles.ToArray();
            ClipboardTempFiles.Clear();
        }

        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
            }
        }

        try
        {
            if (Directory.Exists(ClipboardTempDir)
                && !Directory.EnumerateFileSystemEntries(ClipboardTempDir).Any())
            {
                Directory.Delete(ClipboardTempDir, false);
            }
        }
        catch
        {
        }
    }

    [RelayCommand]
    private void CopySwipeStart()
    {
        CopyTextToClipboard(BuildPointClipboardText("begin", SwipeStartX, SwipeStartY), "复制Swipe起点到剪贴板");
    }

    [RelayCommand]
    private async Task PasteSwipeStart()
    {
        if (await TryGetClipboardPointAsync(SetSwipeStart))
        {
            ApplySelectionPreview();
        }
    }

    [RelayCommand]
    private void CopySwipeEnd()
    {
        CopyTextToClipboard(BuildPointClipboardText("end", SwipeEndX, SwipeEndY), "复制Swipe终点到剪贴板");
    }

    [RelayCommand]
    private async Task PasteSwipeEnd()
    {
        if (await TryGetClipboardPointAsync(SetSwipeEnd))
        {
            ApplySelectionPreview();
        }
    }

    [RelayCommand]
    private void CopySwipeMatchPairs()
    {
        if (!TryParsePoint(SwipeStartX, SwipeStartY, out var start))
        {
            start = new Point(0, 0);
        }

        if (!TryBuildSwipeTestEndList(out var ends))
        {
            if (TryParsePoint(SwipeEndX, SwipeEndY, out var end))
            {
                ends = [[(int)Math.Round(end.X), (int)Math.Round(end.Y)]];
            }
            else
            {
                ends = [[0, 0]];
            }
        }

        var startStr = $"[{(int)Math.Round(start.X)}, {(int)Math.Round(start.Y)}, 0, 0]";
        
        string endStr;
        if (ends.Count == 1)
        {
            endStr = $"[{ends[0][0]}, {ends[0][1]}, 0, 0]";
        }
        else
        {
            var endItems = ends.Select(e => $"[{e[0]}, {e[1]}, 0, 0]");
            endStr = $"[{string.Join(", ", endItems)}]";
        }

        var text = $"\"begin\": {startStr},\n\"end\": {endStr}";
        CopyTextToClipboard(text, LangKeys.CopyToClipboard.ToLocalization());
    }

    [RelayCommand]
    private void ClearSwipe()
    {
        SwipeStartX = "0";
        SwipeStartY = "0";
        SwipeEndX = "0";
        SwipeEndY = "0";
        UpdateSwipeArrow();
    }

    private string BuildPointClipboardText(string label, string xText, string yText)
    {
        if (double.TryParse(xText, out var x)
            && double.TryParse(yText, out var y))
        {
            return FormatClipboardValue(label, $"[{(int)Math.Round(x)}, {(int)Math.Round(y)}]");
        }

        return FormatClipboardValue(label, "[0, 0]");
    }

    private string BuildColorTripletClipboardText(string label, string first, string second, string third)
    {
        if (int.TryParse(first, out var a)
            && int.TryParse(second, out var b)
            && int.TryParse(third, out var c))
        {
            return FormatClipboardValue(label, $"[{a}, {b}, {c}]");
        }

        return FormatClipboardValue(label, "[0, 0, 0]");
    }

    private string BuildGrayClipboardText(string label, string value)
    {
        if (int.TryParse(value, out var gray))
        {
            return FormatClipboardValue(label, $"[{gray}]");
        }

        return FormatClipboardValue(label, "[0]");
    }

    private string BuildColorMatchPairsClipboardText(int mode)
    {
        if (!TryBuildColorMatchListsForCopy(mode, out var upper, out var lower))
        {
            var fallback = mode == 2 ? CreateZeroSingle() : CreateZeroTriplet();
            upper = [fallback];
            lower = [fallback];
        }

        var upperText = FormatColorListForCopy(upper);
        var lowerText = FormatColorListForCopy(lower);
        return $"\"upper\": {upperText},\n\"lower\": {lowerText}";
    }

    private static string FormatColorListForCopy(List<List<int>> values)
    {
        if (values.Count == 1)
        {
            return FormatColorRow(values[0]);
        }

        var rows = values.Select(FormatColorRow);
        return $"[{string.Join(", ", rows)}]";
    }

    private static string FormatColorRow(IReadOnlyList<int> values)
    {
        return $"[{string.Join(", ", values)}]";
    }

    private void UpdateRoiRectFromText()
    {
        if (_suppressRoiSync)
        {
            return;
        }

        if (!TryParseRect(RoiX, RoiY, RoiW, RoiH, out var rect))
        {
            return;
        }

        _roiRect = rect;
        UpdateOffsets();
        ApplySelectionPreview();
    }

    private void UpdateColorPickRectFromText()
    {
        if (_suppressColorPickSync)
        {
            return;
        }

        if (!TryParseRect(ColorPickX, ColorPickY, ColorPickW, ColorPickH, out var rect))
        {
            return;
        }

        _colorPickRect = rect;
        UpdateColorPickExpandedRect();
        ApplySelectionPreview();
    }

    private void UpdateScreenshotRectFromText()
    {
        if (_suppressScreenshotSync)
        {
            return;
        }

        if (!TryParseRect(ScreenshotX, ScreenshotY, ScreenshotW, ScreenshotH, out var rect))
        {
            return;
        }

        _screenshotRect = rect;
        UpdateScreenshotExpandedRect();
        ApplySelectionPreview();
    }

    private void UpdateOcrRectFromText()
    {
        if (_suppressOcrSync)
        {
            return;
        }

        if (!TryParseRect(OcrX, OcrY, OcrW, OcrH, out var rect))
        {
            return;
        }

        _ocrRect = rect;
        UpdateOcrExpandedRect();
        ApplySelectionPreview();
    }

    private void UpdateSwipeFromText()
    {
        UpdateSwipeArrow();
    }

    private void UpdateOriginTargetRectFromText()
    {
        if (_suppressTargetSync)
        {
            return;
        }

        if (!TryParseRect(OriginTargetX, OriginTargetY, OriginTargetW, OriginTargetH, out var rect))
        {
            return;
        }

        _originTargetRect = rect;
        UpdateOffsets();
        ApplySelectionPreview();
    }

    private void UpdateTargetRectFromText()
    {
        if (_suppressTargetSync)
        {
            return;
        }

        if (!TryParseRect(TargetX, TargetY, TargetW, TargetH, out var rect))
        {
            return;
        }

        _targetRect = rect;
        UpdateOffsets();
        ApplySelectionPreview();
    }

    private void SetRoiRect(Rect rect)
    {
        _suppressRoiSync = true;
        _roiRect = rect;
        RoiX = ((int)Math.Round(rect.X)).ToString();
        RoiY = ((int)Math.Round(rect.Y)).ToString();
        RoiW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        RoiH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
        _suppressRoiSync = false;
        RefreshSelectionRects();
    }

    private void SetColorPickRect(Rect rect)
    {
        _suppressColorPickSync = true;
        _colorPickRect = rect;
        ColorPickX = ((int)Math.Round(rect.X)).ToString();
        ColorPickY = ((int)Math.Round(rect.Y)).ToString();
        ColorPickW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        ColorPickH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
        _suppressColorPickSync = false;
        UpdateColorPickExpandedRect();
    }

    private void SetScreenshotRect(Rect rect)
    {
        _suppressScreenshotSync = true;
        _screenshotRect = rect;
        ScreenshotX = ((int)Math.Round(rect.X)).ToString();
        ScreenshotY = ((int)Math.Round(rect.Y)).ToString();
        ScreenshotW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        ScreenshotH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
        _suppressScreenshotSync = false;
        UpdateScreenshotExpandedRect();
    }

    private void SetOcrRect(Rect rect)
    {
        _suppressOcrSync = true;
        _ocrRect = rect;
        OcrX = ((int)Math.Round(rect.X)).ToString();
        OcrY = ((int)Math.Round(rect.Y)).ToString();
        OcrW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        OcrH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
        _suppressOcrSync = false;
        UpdateOcrExpandedRect();
    }


    private void UpdateColorPickExpandedRect()
    {
        _colorPickExpandedRect = BuildExpandedRect(_colorPickRect);
        ColorPickExpandedX = ((int)Math.Round(_colorPickExpandedRect.X)).ToString();
        ColorPickExpandedY = ((int)Math.Round(_colorPickExpandedRect.Y)).ToString();
        ColorPickExpandedW = Math.Max(1, (int)Math.Round(_colorPickExpandedRect.Width)).ToString();
        ColorPickExpandedH = Math.Max(1, (int)Math.Round(_colorPickExpandedRect.Height)).ToString();
    }

    private void UpdateScreenshotExpandedRect()
    {
        _screenshotExpandedRect = BuildExpandedRect(_screenshotRect);
        ScreenshotExpandedX = ((int)Math.Round(_screenshotExpandedRect.X)).ToString();
        ScreenshotExpandedY = ((int)Math.Round(_screenshotExpandedRect.Y)).ToString();
        ScreenshotExpandedW = Math.Max(1, (int)Math.Round(_screenshotExpandedRect.Width)).ToString();
        ScreenshotExpandedH = Math.Max(1, (int)Math.Round(_screenshotExpandedRect.Height)).ToString();
    }

    private void UpdateOcrExpandedRect()
    {
        _ocrExpandedRect = BuildExpandedRect(_ocrRect);
        OcrExpandedX = ((int)Math.Round(_ocrExpandedRect.X)).ToString();
        OcrExpandedY = ((int)Math.Round(_ocrExpandedRect.Y)).ToString();
        OcrExpandedW = Math.Max(1, (int)Math.Round(_ocrExpandedRect.Width)).ToString();
        OcrExpandedH = Math.Max(1, (int)Math.Round(_ocrExpandedRect.Height)).ToString();
    }

    private void UpdateNeuralNetworkDetectExpandedRect()
    {
        _neuralNetworkDetectExpandedRect = BuildExpandedRect(_neuralNetworkDetectRect);
        NeuralNetworkDetectExpandedX = ((int)Math.Round(_neuralNetworkDetectExpandedRect.X)).ToString();
        NeuralNetworkDetectExpandedY = ((int)Math.Round(_neuralNetworkDetectExpandedRect.Y)).ToString();
        NeuralNetworkDetectExpandedW = Math.Max(1, (int)Math.Round(_neuralNetworkDetectExpandedRect.Width)).ToString();
        NeuralNetworkDetectExpandedH = Math.Max(1, (int)Math.Round(_neuralNetworkDetectExpandedRect.Height)).ToString();
    }

    private void SetOcrMatchRect(Rect rect)
    {
        OcrMatchRoiX = ((int)Math.Round(rect.X)).ToString();
        OcrMatchRoiY = ((int)Math.Round(rect.Y)).ToString();
        OcrMatchRoiW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        OcrMatchRoiH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
    }

    private void SetTemplateMatchRect(Rect rect)
    {
        TemplateMatchRoiX = ((int)Math.Round(rect.X)).ToString();
        TemplateMatchRoiY = ((int)Math.Round(rect.Y)).ToString();
        TemplateMatchRoiW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        TemplateMatchRoiH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
    }

    private void SetColorMatchRect(Rect rect)
    {
        ColorMatchRoiX = ((int)Math.Round(rect.X)).ToString();
        ColorMatchRoiY = ((int)Math.Round(rect.Y)).ToString();
        ColorMatchRoiW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        ColorMatchRoiH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
    }

    private void ApplyRectToColorMatch(Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        SetColorMatchRect(rect);
    }

    private void ApplyRectToTemplateMatch(Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        SetTemplateMatchRect(rect);
    }

    private void ApplyRectToOcrMatch(Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        SetOcrMatchRect(rect);
    }

    private void ApplyRectToClickTestTarget(Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        SetClickTestTargetRect(rect);
    }

    private void SetClickTestTargetRect(Rect rect)
    {
        ClickTestTargetX = ((int)Math.Round(rect.X)).ToString();
        ClickTestTargetY = ((int)Math.Round(rect.Y)).ToString();
        ClickTestTargetW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        ClickTestTargetH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
    }

    private void SetClickTestOffsetRect(Rect rect)
    {
        ClickTestOffsetX = ((int)Math.Round(rect.X)).ToString();
        ClickTestOffsetY = ((int)Math.Round(rect.Y)).ToString();
        ClickTestOffsetW = ((int)Math.Round(rect.Width)).ToString();
        ClickTestOffsetH = ((int)Math.Round(rect.Height)).ToString();
    }

    private void SetSwipeTestStart(Point point)
    {
        SwipeTestStartX = ((int)Math.Round(point.X)).ToString();
        SwipeTestStartY = ((int)Math.Round(point.Y)).ToString();
    }

    private void SetSwipeTestEnd(Point point)
    {
        SwipeTestEndX = ((int)Math.Round(point.X)).ToString();
        SwipeTestEndY = ((int)Math.Round(point.Y)).ToString();
        EnsureSwipeTestEndRowsInitialized();
        if (SwipeTestEndRows.Count > 0)
        {
            SwipeTestEndRows[0].First = SwipeTestEndX;
            SwipeTestEndRows[0].Second = SwipeTestEndY;
        }
    }

    private void SetSwipeTestDuration(double duration)
    {
        SwipeTestDuration = ((int)Math.Round(duration)).ToString();
    }

    private void SyncOcrMatchDefaults(bool force = false)
    {
        if (force)
        {
            if (TryParseRect(OcrExpandedX, OcrExpandedY, OcrExpandedW, OcrExpandedH, out var expandedRect)
                && expandedRect.Width > 0
                && expandedRect.Height > 0)
            {
                OcrMatchRoiX = OcrExpandedX;
                OcrMatchRoiY = OcrExpandedY;
                OcrMatchRoiW = OcrExpandedW;
                OcrMatchRoiH = OcrExpandedH;
            }
        }
        else if (!TryParseRect(OcrMatchRoiX, OcrMatchRoiY, OcrMatchRoiW, OcrMatchRoiH, out var matchRect)
                 || matchRect.Width <= 0
                 || matchRect.Height <= 0)
        {
            OcrMatchRoiX = OcrExpandedX;
            OcrMatchRoiY = OcrExpandedY;
            OcrMatchRoiW = OcrExpandedW;
            OcrMatchRoiH = OcrExpandedH;
        }

        if (!string.IsNullOrWhiteSpace(OcrResult))
        {
            OcrMatchTargetText = OcrResult;
        }
    }

    private void SyncTemplateMatchDefaults(bool force = false)
    {
        if (force)
        {
            if (TryParseRect(ScreenshotX, ScreenshotY, ScreenshotW, ScreenshotH, out var screenshotRect)
                && screenshotRect.Width > 0
                && screenshotRect.Height > 0)
            {
                TemplateMatchRoiX = ScreenshotX;
                TemplateMatchRoiY = ScreenshotY;
                TemplateMatchRoiW = ScreenshotW;
                TemplateMatchRoiH = ScreenshotH;
            }
        }
        else if (!TryParseRect(TemplateMatchRoiX, TemplateMatchRoiY, TemplateMatchRoiW, TemplateMatchRoiH, out var matchRect)
                 || matchRect.Width <= 0
                 || matchRect.Height <= 0)
        {
            TemplateMatchRoiX = ScreenshotX;
            TemplateMatchRoiY = ScreenshotY;
            TemplateMatchRoiW = ScreenshotW;
            TemplateMatchRoiH = ScreenshotH;
        }

        if (TemplateMatchMethod == 0)
        {
            TemplateMatchMethod = 5;
        }
    }

    private void SyncColorMatchDefaults(bool force = false)
    {
        if (force || (ColorMatchMethod != 4 && ColorMatchMethod != 40 && ColorMatchMethod != 6))
        {
            ColorMatchMethod = GetDefaultColorMatchMethodValue();
        }

        if (force || !int.TryParse(ColorMatchCount, out var count) || count <= 0)
        {
            ColorMatchCount = "1";
        }

        SyncColorMatchRoiDefaults(force);
    }

    private void SyncColorMatchRoiDefaults(bool force = false)
    {
        if (force)
        {
            if (TryParseRect(ColorPickExpandedX, ColorPickExpandedY, ColorPickExpandedW, ColorPickExpandedH, out var expandedRect)
                && expandedRect.Width > 0
                && expandedRect.Height > 0)
            {
                ColorMatchRoiX = ColorPickExpandedX;
                ColorMatchRoiY = ColorPickExpandedY;
                ColorMatchRoiW = ColorPickExpandedW;
                ColorMatchRoiH = ColorPickExpandedH;
            }
        }
        else if (!TryParseRect(ColorMatchRoiX, ColorMatchRoiY, ColorMatchRoiW, ColorMatchRoiH, out var matchRect)
                 || matchRect.Width < 0
                 || matchRect.Height < 0)
        {
            ColorMatchRoiX = ColorPickExpandedX;
            ColorMatchRoiY = ColorPickExpandedY;
            ColorMatchRoiW = ColorPickExpandedW;
            ColorMatchRoiH = ColorPickExpandedH;
        }
    }

    private void EnsureColorMatchRowsInitialized()
    {
        EnsureTripletRowsInitialized(RgbUpperRows, RgbUpperR, RgbUpperG, RgbUpperB);
        EnsureTripletRowsInitialized(RgbLowerRows, RgbLowerR, RgbLowerG, RgbLowerB);
        EnsureTripletRowsInitialized(HsvUpperRows, HsvUpperH, HsvUpperS, HsvUpperV);
        EnsureTripletRowsInitialized(HsvLowerRows, HsvLowerH, HsvLowerS, HsvLowerV);
        EnsureSingleRowsInitialized(GrayUpperRows, GrayUpper);
        EnsureSingleRowsInitialized(GrayLowerRows, GrayLower);
    }

    private static void EnsureTripletRowsInitialized(
        ObservableCollection<Helper.Other.TripletRow> rows,
        string first,
        string second,
        string third)
    {
        if (rows.Count == 0)
        {
            rows.Add(new Helper.Other.TripletRow(first, second, third));
        }
    }

    private static void EnsureSingleRowsInitialized(ObservableCollection<Helper.Other.SingleRow> rows, string value)
    {
        if (rows.Count == 0)
        {
            rows.Add(new Helper.Other.SingleRow(value));
        }
    }

    private int GetDefaultColorMatchMethodValue()
    {
        return ColorMode switch
        {
            1 => 40,
            2 => 6,
            _ => 4
        };
    }

    private int GetColorMatchMethodValue()
    {
        return ColorMatchMethod == 4 || ColorMatchMethod == 40 || ColorMatchMethod == 6
            ? ColorMatchMethod
            : GetDefaultColorMatchMethodValue();
    }

    private int ResolveColorMatchMode(int method)
    {
        return method switch
        {
            40 => 1,
            6 => 2,
            4 => 0,
            _ => ColorMode
        };
    }

    private void SyncClickTestDefaults(bool force = false)
    {
        var baseRect = IsTargetMode ? _originTargetRect : _roiRect;
        if (force)
        {
            if (baseRect.Width > 0 && baseRect.Height > 0)
            {
                SetClickTestTargetRect(baseRect);
            }
        }
        else if (!TryParseRect(ClickTestTargetX, ClickTestTargetY, ClickTestTargetW, ClickTestTargetH, out var targetRect)
                 || targetRect.Width < 0
                 || targetRect.Height < 0)
        {
            if (baseRect.Width > 0 && baseRect.Height > 0)
            {
                SetClickTestTargetRect(baseRect);
            }
        }

        if (force)
        {
            if (TryParseOffset(OffsetX, OffsetY, OffsetW, OffsetH, out _, out _, out _, out _))
            {
                ClickTestOffsetX = OffsetX;
                ClickTestOffsetY = OffsetY;
                ClickTestOffsetW = OffsetW;
                ClickTestOffsetH = OffsetH;
            }
        }
        else if (!TryParseOffset(ClickTestOffsetX, ClickTestOffsetY, ClickTestOffsetW, ClickTestOffsetH,
                     out _, out _, out _, out _))
        {
            ClickTestOffsetX = OffsetX;
            ClickTestOffsetY = OffsetY;
            ClickTestOffsetW = OffsetW;
            ClickTestOffsetH = OffsetH;
        }
    }

    private void EnsureSwipeTestEndRowsInitialized()
    {
        if (SwipeTestEndRows.Count == 0)
        {
            SwipeTestEndRows.Add(new Helper.Other.PairRow(SwipeTestEndX, SwipeTestEndY));
        }
    }

    private void SyncSwipeTestEndRows(bool force)
    {
        EnsureSwipeTestEndRowsInitialized();
        if (SwipeTestEndRows.Count == 0)
        {
            return;
        }

        var firstRow = SwipeTestEndRows[0];
        if (force)
        {
            if (TryParsePoint(SwipeEndX, SwipeEndY, out _))
            {
                firstRow.First = SwipeEndX;
                firstRow.Second = SwipeEndY;
            }
        }
        else if (!TryParsePoint(firstRow.First, firstRow.Second, out _))
        {
            if (TryParsePoint(SwipeEndX, SwipeEndY, out _))
            {
                firstRow.First = SwipeEndX;
                firstRow.Second = SwipeEndY;
            }
        }

        SwipeTestEndX = firstRow.First;
        SwipeTestEndY = firstRow.Second;
    }

    private bool TryBuildSwipeTestEndList(out List<List<int>> ends)
    {
        ends = [];
        if (SwipeTestEndRows.Count == 0)
        {
            if (!TryParsePoint(SwipeTestEndX, SwipeTestEndY, out var fallback))
            {
                return false;
            }

            ends.Add(
            [
                (int)Math.Round(fallback.X),
                (int)Math.Round(fallback.Y)
            ]);
            return true;
        }

        foreach (var row in SwipeTestEndRows)
        {
            if (!TryParsePoint(row.First, row.Second, out var point))
            {
                return false;
            }

            ends.Add(
            [
                (int)Math.Round(point.X),
                (int)Math.Round(point.Y)
            ]);
        }

        return ends.Count > 0;
    }

    private void SyncSwipeTestDefaults(bool force = false)
    {
        if (force)
        {
            if (TryParsePoint(SwipeStartX, SwipeStartY, out _))
            {
                SwipeTestStartX = SwipeStartX;
                SwipeTestStartY = SwipeStartY;
            }
        }
        else
        {
            if (!TryParsePoint(SwipeTestStartX, SwipeTestStartY, out _))
            {
                if (TryParsePoint(SwipeStartX, SwipeStartY, out _))
                {
                    SwipeTestStartX = SwipeStartX;
                    SwipeTestStartY = SwipeStartY;
                }
            }
        }

        SyncSwipeTestEndRows(force);

        if (!int.TryParse(SwipeTestDuration, out var duration) || duration <= 0)
        {
            SwipeTestDuration = "200";
        }
    }

    private Rect BuildExpandedRect(Rect rect)
    {
        if (LiveViewDisplayImage is not Bitmap bitmap)
        {
            return rect;
        }

        var width = bitmap.Size.Width;
        var height = bitmap.Size.Height;

        var expandX = GetExpandX();
        var expandY = GetExpandY();
        var expandW = GetExpandW(expandX);
        var expandH = GetExpandH(expandY);

        var x = Math.Max(0, rect.X - expandX);
        var y = Math.Max(0, rect.Y - expandY);

        var expandedWidth = rect.Width + expandW;
        var expandedHeight = rect.Height + expandH;

        var right = Math.Min(width, x + expandedWidth);
        var bottom = Math.Min(height, y + expandedHeight);

        expandedWidth = Math.Max(0, right - x);
        expandedHeight = Math.Max(0, bottom - y);

        return expandedWidth > 0 && expandedHeight > 0
            ? new Rect(x, y, expandedWidth, expandedHeight)
            : default;
    }

    public void RefreshExpandedRects()
    {
        UpdateColorPickExpandedRect();
        UpdateScreenshotExpandedRect();
        UpdateOcrExpandedRect();
        UpdateNeuralNetworkDetectExpandedRect();
        ApplySelectionPreview();
    }

    private void SetOriginTargetRect(Rect rect)
    {
        _suppressTargetSync = true;
        _originTargetRect = rect;
        OriginTargetX = ((int)Math.Round(rect.X)).ToString();
        OriginTargetY = ((int)Math.Round(rect.Y)).ToString();
        OriginTargetW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        OriginTargetH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
        _suppressTargetSync = false;
        RefreshSelectionRects();
    }

    private void SetTargetRect(Rect rect)
    {
        _suppressTargetSync = true;
        _targetRect = rect;
        TargetX = ((int)Math.Round(rect.X)).ToString();
        TargetY = ((int)Math.Round(rect.Y)).ToString();
        TargetW = Math.Max(1, (int)Math.Round(rect.Width)).ToString();
        TargetH = Math.Max(1, (int)Math.Round(rect.Height)).ToString();
        _suppressTargetSync = false;
        RefreshSelectionRects();
    }

    private void UpdateOffsets()
    {
        var baseRect = IsTargetMode ? _originTargetRect : _roiRect;
        var offset = new Rect(
            _targetRect.X - baseRect.X,
            _targetRect.Y - baseRect.Y,
            _targetRect.Width - baseRect.Width,
            _targetRect.Height - baseRect.Height);

        OffsetX = ((int)Math.Round(offset.X)).ToString();
        OffsetY = ((int)Math.Round(offset.Y)).ToString();
        OffsetW = ((int)Math.Round(offset.Width)).ToString();
        OffsetH = ((int)Math.Round(offset.Height)).ToString();
    }

    private void ApplySelectionPreview()
    {
        SelectionStroke = RoiSelectionStroke;
        SelectionFill = RoiSelectionFill;
        TargetSelectionStroke = TargetSelectionStrokeDefault;
        TargetSelectionFill = TargetSelectionFillDefault;

        switch (ActiveToolMode)
        {
            case LiveViewToolMode.Roi:
                HasSwipeArrow = false;
                RefreshSelectionRects();
                break;
            case LiveViewToolMode.ColorPick:
                HasSwipeArrow = false;
                UpdateSelectionPreviewWithExpanded(_colorPickRect, _colorPickExpandedRect);
                break;
            case LiveViewToolMode.Screenshot:
                HasSwipeArrow = false;
                if (IsScreenshotBrushMode)
                {
                    ClearSelectionPreview();
                }
                else
                {
                    UpdateSelectionPreviewWithExpanded(_screenshotRect, _screenshotExpandedRect);
                }
                break;
            case LiveViewToolMode.Ocr:
                HasSwipeArrow = false;
                UpdateSelectionPreviewWithExpanded(_ocrRect, _ocrExpandedRect);
                break;
            case LiveViewToolMode.Swipe:
                ClearSelectionPreview();
                UpdateSwipeArrow();
                break;
            case LiveViewToolMode.Key:
                HasSwipeArrow = false;
                ClearSelectionPreview();
                break;
            case LiveViewToolMode.NeuralNetworkDetect:
                HasSwipeArrow = false;
                UpdateSelectionPreviewWithExpanded(_neuralNetworkDetectRect, _neuralNetworkDetectExpandedRect);
                break;
        }
    }

    private void ApplyColorFilter()
    {
        var bitmap = GetLiveViewBaseImage();
        if (bitmap is null)
        {
            return;
        }

        if (!TryBuildColorMatchRanges(ColorMode, out var ranges))
        {
            return;
        }

        var width = (int)bitmap.Size.Width;
        var height = (int)bitmap.Size.Height;
        var stride = width * 4;
        var buffer = new byte[height * stride];
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), (IntPtr)ptr, buffer.Length, stride);
            }
        }

        var fillColor = ColorPreviewFillColor;
        var fillA = fillColor.A;
        var fillR = (byte)(fillColor.R * fillA / 255);
        var fillG = (byte)(fillColor.G * fillA / 255);
        var fillB = (byte)(fillColor.B * fillA / 255);

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * stride;
            for (var x = 0; x < width; x++)
            {
                var index = rowStart + x * 4;
                var b = buffer[index];
                var g = buffer[index + 1];
                var r = buffer[index + 2];

                if (!IsColorInRange(ranges, r, g, b))
                {
                    buffer[index] = fillB;
                    buffer[index + 1] = fillG;
                    buffer[index + 2] = fillR;
                    buffer[index + 3] = fillA;
                }
            }
        }

        _colorPreviewImage?.Dispose();
        var writeable = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = writeable.Lock())
        {
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    Buffer.MemoryCopy(ptr, (void*)fb.Address, fb.RowBytes * height, buffer.Length);
                }
            }
        }

        _colorPreviewImage = writeable;
        OnPropertyChanged(nameof(LiveViewDisplayImage));
    }

    private static WriteableBitmap? CropBitmap(Bitmap source, Rect rect)
    {
        var width = (int)source.Size.Width;
        var height = (int)source.Size.Height;

        var x = (int)Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = (int)Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var w = (int)Math.Clamp(rect.Width, 1, Math.Max(0, width - x));
        var h = (int)Math.Clamp(rect.Height, 1, Math.Max(0, height - y));

        if (w <= 0 || h <= 0)
        {
            return null;
        }

        var stride = w * 4;
        var buffer = new byte[h * stride];

        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                source.CopyPixels(new PixelRect(x, y, w, h), (IntPtr)ptr, buffer.Length, stride);
            }
        }

        var cropped = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = cropped.Lock())
        {
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    Buffer.MemoryCopy(ptr, (void*)fb.Address, fb.RowBytes * h, buffer.Length);
                }
            }
        }

        return cropped;
    }

    private Bitmap? GetLiveViewBaseImage()
    {
        return IsLiveViewPaused ? _pausedLiveViewImage ?? LiveViewImage : LiveViewImage;
    }

    public void UpdatePointerPreview(Point point)
    {
        var bitmap = GetLiveViewBaseImage();
        if (bitmap == null)
        {
            HasBrushPreview = false;
            BrushPreviewRectText = string.Empty;
            return;
        }

        var maxX = Math.Max(0, bitmap.Size.Width - 1);
        var maxY = Math.Max(0, bitmap.Size.Height - 1);
        var snapped = new Point(
            Math.Clamp(Math.Round(point.X), 0, maxX),
            Math.Clamp(Math.Round(point.Y), 0, maxY));

        BrushPreviewSize = IsScreenshotBrushMode ? Math.Max(1, ScreenshotBrushSize) : 1;
        BrushPreviewPoint = snapped;
        HasBrushPreview = true;
        UpdateBrushPreviewRectText();

        var color = ReadPixelColor(bitmap, snapped);
        var inverted = Color.FromArgb(220, (byte)(255 - color.R), (byte)(255 - color.G), (byte)(255 - color.B));
        BrushPreviewStroke = new SolidColorBrush(inverted);

        var fillAlpha = BrushPreviewSize <= 1 ? (byte)220 : (byte)40;
        BrushPreviewFill = new SolidColorBrush(Color.FromArgb(fillAlpha, inverted.R, inverted.G, inverted.B));
    }

    private static Color ReadPixelColor(Bitmap bitmap, Point point)
    {
        var x = (int)Math.Clamp(point.X, 0, Math.Max(0, bitmap.Size.Width - 1));
        var y = (int)Math.Clamp(point.Y, 0, Math.Max(0, bitmap.Size.Height - 1));
        var buffer = new byte[4];
        var rect = new PixelRect(x, y, 1, 1);
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                bitmap.CopyPixels(rect, (IntPtr)ptr, buffer.Length, 4);
            }
        }

        var a = buffer[3];
        if (a == 0)
        {
            return Colors.Transparent;
        }

        if (a < 255)
        {
            var r = (byte)Math.Clamp(buffer[2] * 255.0 / a, 0, 255);
            var g = (byte)Math.Clamp(buffer[1] * 255.0 / a, 0, 255);
            var b = (byte)Math.Clamp(buffer[0] * 255.0 / a, 0, 255);
            return Color.FromArgb(a, r, g, b);
        }

        return Color.FromArgb(a, buffer[2], buffer[1], buffer[0]);
    }

    private void ResetScreenshotBrushPreview()
    {
        _screenshotBrushImage?.Dispose();
        _screenshotBrushImage = null;
        OnPropertyChanged(nameof(LiveViewDisplayImage));
    }

    private void UpdateBrushPreviewRectText()
    {
        if (!HasBrushPreview)
        {
            BrushPreviewRectText = string.Empty;
            return;
        }

        var sizeValue = Math.Max(1, (int)Math.Round(BrushPreviewSize));
        var half = (sizeValue - 1) / 2.0;
        var x = (int)Math.Floor(BrushPreviewPoint.X - half);
        var y = (int)Math.Floor(BrushPreviewPoint.Y - half);
        BrushPreviewRectText = $"[{x}, {y}, {sizeValue}, {sizeValue}]";
    }

    private void UpdateBrushPreviewAvailability()
    {
        HasBrushPreview = GetLiveViewBaseImage() != null;
        if (!HasBrushPreview)
        {
            BrushPreviewPoint = default;
        }

        UpdateBrushPreviewRectText();
    }

    public void UpdateScreenshotBrushPreview(Point point)
    {
        UpdatePointerPreview(point);
    }

    private void EnsureScreenshotBrushImage()
    {
        var source = GetLiveViewBaseImage();
        if (source is null)
        {
            return;
        }

        var width = (int)source.Size.Width;
        var height = (int)source.Size.Height;

        if (_screenshotBrushImage != null
            && _screenshotBrushImage.PixelSize.Width == width
            && _screenshotBrushImage.PixelSize.Height == height)
        {
            return;
        }

        _screenshotBrushImage?.Dispose();
        _screenshotBrushImage = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        var stride = width * 4;
        var buffer = new byte[height * stride];
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                source.CopyPixels(new PixelRect(0, 0, width, height), (IntPtr)ptr, buffer.Length, stride);
            }
        }

        using (var fb = _screenshotBrushImage.Lock())
        {
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    Buffer.MemoryCopy(ptr, (void*)fb.Address, fb.RowBytes * height, buffer.Length);
                }
            }
        }
    }

    public void StartScreenshotBrush(Point point)
    {
        if (!IsScreenshotBrushMode)
        {
            return;
        }

        EnsureScreenshotBrushImage();
        DrawBrushLine(point, point);
    }

    public void UpdateScreenshotBrush(Point point, Point previousPoint)
    {
        if (!IsScreenshotBrushMode)
        {
            return;
        }

        EnsureScreenshotBrushImage();
        DrawBrushLine(previousPoint, point);
    }

    private void DrawBrushLine(Point start, Point end)
    {
        if (_screenshotBrushImage == null)
        {
            return;
        }

        var width = _screenshotBrushImage.PixelSize.Width;
        var height = _screenshotBrushImage.PixelSize.Height;

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var steps = (int)Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (steps <= 0)
        {
            steps = 1;
        }

        using var fb = _screenshotBrushImage.Lock();
        unsafe
        {
            var dstPtr = (byte*)fb.Address;
            var stride = fb.RowBytes;
            var radius = Math.Max(1, ScreenshotBrushSize);
            var half = radius / 2;

            for (var i = 0; i <= steps; i++)
            {
                var x = start.X + dx * i / steps;
                var y = start.Y + dy * i / steps;
                var cx = (int)Math.Round(x);
                var cy = (int)Math.Round(y);

                for (var yy = cy - half; yy <= cy + half; yy++)
                {
                    if (yy < 0 || yy >= height) continue;
                    var rowStart = yy * stride;
                    for (var xx = cx - half; xx <= cx + half; xx++)
                    {
                        if (xx < 0 || xx >= width) continue;
                        var index = rowStart + xx * 4;
                        dstPtr[index] = 0;
                        dstPtr[index + 1] = 255;
                        dstPtr[index + 2] = 0;
                        dstPtr[index + 3] = 255;
                    }
                }
            }
        }

        OnPropertyChanged(nameof(LiveViewDisplayImage));
    }

    private static byte[] BitmapToBytes(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    private static bool TrySetEncodedData(MaaImageBuffer buffer, byte[] data)
    {
        var method = buffer.GetType().GetMethod("TrySetEncodedData", new[]
        {
            typeof(byte[])
        });
        if (method == null)
        {
            return false;
        }

        return method.Invoke(buffer, new object[]
            {
                data
            }) is bool result
            && result;
    }

    private static string BuildOcrTaskJson(Rect rect)
    {
        var payload = new
        {
            recognition = "OCR",
            roi = new[]
            {
                (int)Math.Round(rect.X),
                (int)Math.Round(rect.Y),
                Math.Max(1, (int)Math.Round(rect.Width)),
                Math.Max(1, (int)Math.Round(rect.Height))
            }
        };

        return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        });
    }

    private sealed class OcrRecognitionQuery
    {
        [JsonProperty("best")]
        public OcrRecognitionResult? Best { get; set; }
    }

    private sealed class OcrRecognitionResult
    {
        [JsonProperty("text")]
        public string? Text { get; set; }
    }

    private readonly struct ColorRange(
        int lowerR,
        int lowerG,
        int lowerB,
        int upperR,
        int upperG,
        int upperB,
        int lowerH,
        int lowerS,
        int lowerV,
        int upperH,
        int upperS,
        int upperV,
        int lowerGray,
        int upperGray,
        int mode)
    {
        public int LowerR { get; } = lowerR;
        public int LowerG { get; } = lowerG;
        public int LowerB { get; } = lowerB;
        public int UpperR { get; } = upperR;
        public int UpperG { get; } = upperG;
        public int UpperB { get; } = upperB;
        public int LowerH { get; } = lowerH;
        public int LowerS { get; } = lowerS;
        public int LowerV { get; } = lowerV;
        public int UpperH { get; } = upperH;
        public int UpperS { get; } = upperS;
        public int UpperV { get; } = upperV;
        public int LowerGray { get; } = lowerGray;
        public int UpperGray { get; } = upperGray;
        public int Mode { get; } = mode;
    }

    private bool TryBuildColorMatchRanges(int mode, out List<ColorRange> ranges)
    {
        ranges = [];
        if (!TryBuildColorMatchLists(mode, out var upper, out var lower))
        {
            return false;
        }

        for (var index = 0; index < upper.Count; index++)
        {
            var upperRow = upper[index];
            var lowerRow = lower[index];
            switch (mode)
            {
                case 1:
                    ranges.Add(new ColorRange(
                        0, 0, 0,
                        0, 0, 0,
                        lowerRow[0], lowerRow[1], lowerRow[2],
                        upperRow[0], upperRow[1], upperRow[2],
                        0, 0, mode));
                    break;
                case 2:
                    ranges.Add(new ColorRange(
                        0, 0, 0,
                        0, 0, 0,
                        0, 0, 0,
                        0, 0, 0,
                        lowerRow[0], upperRow[0], mode));
                    break;
                default:
                    ranges.Add(new ColorRange(
                        lowerRow[0], lowerRow[1], lowerRow[2],
                        upperRow[0], upperRow[1], upperRow[2],
                        0, 0, 0,
                        0, 0, 0,
                        0, 0, mode));
                    break;
            }
        }

        return ranges.Count > 0;
    }

    private bool TryBuildColorMatchLists(int mode, out List<List<int>> upper, out List<List<int>> lower)
    {
        EnsureColorMatchRowsInitialized();
        return mode switch
        {
            1 => TryBuildTripletLists(HsvUpperRows, HsvLowerRows, out upper, out lower),
            2 => TryBuildSingleLists(GrayUpperRows, GrayLowerRows, out upper, out lower),
            _ => TryBuildTripletLists(RgbUpperRows, RgbLowerRows, out upper, out lower)
        };
    }

    private bool TryBuildColorMatchListsForCopy(int mode, out List<List<int>> upper, out List<List<int>> lower)
    {
        EnsureColorMatchRowsInitialized();
        return mode switch
        {
            1 => TryBuildTripletListsForCopy(HsvUpperRows, HsvLowerRows, out upper, out lower),
            2 => TryBuildSingleListsForCopy(GrayUpperRows, GrayLowerRows, out upper, out lower),
            _ => TryBuildTripletListsForCopy(RgbUpperRows, RgbLowerRows, out upper, out lower)
        };
    }

    private static bool TryBuildTripletListsForCopy(
        ObservableCollection<Helper.Other.TripletRow> upperRows,
        ObservableCollection<Helper.Other.TripletRow> lowerRows,
        out List<List<int>> upper,
        out List<List<int>> lower)
    {
        upper = [];
        lower = [];
        var maxCount = Math.Max(upperRows.Count, lowerRows.Count);
        if (maxCount <= 0)
        {
            maxCount = 1;
        }

        for (var index = 0; index < maxCount; index++)
        {
            if (index < upperRows.Count)
            {
                var row = upperRows[index];
                upper.Add([ParseIntOrZero(row.First), ParseIntOrZero(row.Second), ParseIntOrZero(row.Third)]);
            }
            else
            {
                upper.Add(CreateZeroTriplet());
            }

            if (index < lowerRows.Count)
            {
                var row = lowerRows[index];
                lower.Add([ParseIntOrZero(row.First), ParseIntOrZero(row.Second), ParseIntOrZero(row.Third)]);
            }
            else
            {
                lower.Add(CreateZeroTriplet());
            }
        }

        return true;
    }

    private static bool TryBuildSingleListsForCopy(
        ObservableCollection<Helper.Other.SingleRow> upperRows,
        ObservableCollection<Helper.Other.SingleRow> lowerRows,
        out List<List<int>> upper,
        out List<List<int>> lower)
    {
        upper = [];
        lower = [];
        var maxCount = Math.Max(upperRows.Count, lowerRows.Count);
        if (maxCount <= 0)
        {
            maxCount = 1;
        }

        for (var index = 0; index < maxCount; index++)
        {
            if (index < upperRows.Count)
            {
                var row = upperRows[index];
                upper.Add([ParseIntOrZero(row.Value)]);
            }
            else
            {
                upper.Add(CreateZeroSingle());
            }

            if (index < lowerRows.Count)
            {
                var row = lowerRows[index];
                lower.Add([ParseIntOrZero(row.Value)]);
            }
            else
            {
                lower.Add(CreateZeroSingle());
            }
        }

        return true;
    }

    private static bool TryBuildTripletLists(
        ObservableCollection<Helper.Other.TripletRow> upperRows,
        ObservableCollection<Helper.Other.TripletRow> lowerRows,
        out List<List<int>> upper,
        out List<List<int>> lower)
    {
        upper = [];
        lower = [];
        var maxCount = Math.Max(upperRows.Count, lowerRows.Count);
        if (maxCount <= 0)
        {
            maxCount = 1;
        }

        for (var index = 0; index < maxCount; index++)
        {
            if (index < upperRows.Count)
            {
                if (!TryParseTripletRow(upperRows[index], out var upperValues))
                {
                    return false;
                }

                upper.Add(upperValues);
            }
            else
            {
                upper.Add(CreateZeroTriplet());
            }

            if (index < lowerRows.Count)
            {
                if (!TryParseTripletRow(lowerRows[index], out var lowerValues))
                {
                    return false;
                }

                lower.Add(lowerValues);
            }
            else
            {
                lower.Add(CreateZeroTriplet());
            }
        }

        return true;
    }

    private static bool TryBuildSingleLists(
        ObservableCollection<Helper.Other.SingleRow> upperRows,
        ObservableCollection<Helper.Other.SingleRow> lowerRows,
        out List<List<int>> upper,
        out List<List<int>> lower)
    {
        upper = [];
        lower = [];
        var maxCount = Math.Max(upperRows.Count, lowerRows.Count);
        if (maxCount <= 0)
        {
            maxCount = 1;
        }

        for (var index = 0; index < maxCount; index++)
        {
            if (index < upperRows.Count)
            {
                if (!TryParseSingleRow(upperRows[index], out var upperValues))
                {
                    return false;
                }

                upper.Add(upperValues);
            }
            else
            {
                upper.Add(CreateZeroSingle());
            }

            if (index < lowerRows.Count)
            {
                if (!TryParseSingleRow(lowerRows[index], out var lowerValues))
                {
                    return false;
                }

                lower.Add(lowerValues);
            }
            else
            {
                lower.Add(CreateZeroSingle());
            }
        }

        return true;
    }

    private static bool TryParseTripletRow(Helper.Other.TripletRow row, out List<int> values)
    {
        values = [];
        if (!int.TryParse(row.First, out var firstValue)) return false;
        if (!int.TryParse(row.Second, out var secondValue)) return false;
        if (!int.TryParse(row.Third, out var thirdValue)) return false;
        values = [firstValue, secondValue, thirdValue];
        return true;
    }

    private static bool TryParseSingleRow(Helper.Other.SingleRow row, out List<int> values)
    {
        values = [];
        if (!int.TryParse(row.Value, out var value)) return false;
        values = [value];
        return true;
    }

    private static List<int> CreateZeroTriplet() => [0, 0, 0];

    private static List<int> CreateZeroSingle() => [0];

    private static bool IsColorInRange(IReadOnlyList<ColorRange> ranges, byte r, byte g, byte b)
    {
        foreach (var range in ranges)
        {
            if (IsColorInRange(range, r, g, b))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsColorInRange(ColorRange range, byte r, byte g, byte b)
    {
        return range.Mode switch
        {
            0 => r >= range.LowerR
                && r <= range.UpperR
                && g >= range.LowerG
                && g <= range.UpperG
                && b >= range.LowerB
                && b <= range.UpperB,
            1 => IsHsvInRange(range, r, g, b),
            _ => IsGrayInRange(range, r, g, b)
        };
    }

    private static bool IsHsvInRange(ColorRange range, byte r, byte g, byte b)
    {
        RgbToHsv(r, g, b, out var h, out var s, out var v);
        return h >= range.LowerH
            && h <= range.UpperH
            && s >= range.LowerS
            && s <= range.UpperS
            && v >= range.LowerV
            && v <= range.UpperV;
    }

    private static bool IsGrayInRange(ColorRange range, byte r, byte g, byte b)
    {
        var gray = (r + g + b) / 3;
        return gray >= range.LowerGray && gray <= range.UpperGray;
    }

    private void RefreshSelectionRects()
    {
        var primary = IsTargetMode ? _originTargetRect : _roiRect;
        SelectionRect = primary;
        HasSelection = primary.Width > 0 && primary.Height > 0;

        SecondarySelectionRect = _targetRect;
        HasSecondarySelection = _targetRect.Width > 0 && _targetRect.Height > 0;
    }

    private void UpdateSwipeArrow()
    {
        if (!double.TryParse(SwipeStartX, out var startX)
            || !double.TryParse(SwipeStartY, out var startY)
            || !double.TryParse(SwipeEndX, out var endX)
            || !double.TryParse(SwipeEndY, out var endY))
        {
            HasSwipeArrow = false;
            SwipeArrowGeometry = null;
            return;
        }

        var start = new Point(startX, startY);
        var end = new Point(endX, endY);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
        {
            HasSwipeArrow = false;
            SwipeArrowGeometry = null;
            return;
        }

        var angle = Math.Atan2(dy, dx);
        const double headLength = 12;
        const double headAngle = Math.PI / 6;

        var left = new Point(
            end.X - headLength * Math.Cos(angle - headAngle),
            end.Y - headLength * Math.Sin(angle - headAngle));
        var right = new Point(
            end.X - headLength * Math.Cos(angle + headAngle),
            end.Y - headLength * Math.Sin(angle + headAngle));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.LineTo(end);
            ctx.BeginFigure(end, false);
            ctx.LineTo(left);
            ctx.BeginFigure(end, false);
            ctx.LineTo(right);
        }

        SwipeArrowGeometry = geometry;
        HasSwipeArrow = true;
    }

    private void UpdateSelectionPreviewWithExpanded(Rect origin, Rect expanded)
    {
        SelectionRect = origin;
        HasSelection = origin.Width > 0 && origin.Height > 0;
        SecondarySelectionRect = expanded;
        HasSecondarySelection = expanded.Width > 0 && expanded.Height > 0;
    }

    private void ClearSelectionPreview()
    {
        SelectionRect = default;
        SecondarySelectionRect = default;
        HasSelection = false;
        HasSecondarySelection = false;
    }

    private void UpdateSelectionWithSecondary(Rect rect, bool hasSelection, LiveViewRoiSelectionType selectionType)
    {
        switch (selectionType)
        {
            case LiveViewRoiSelectionType.OriginRoi:
                SelectionRect = rect;
                HasSelection = hasSelection;
                SecondarySelectionRect = _targetRect;
                HasSecondarySelection = _targetRect.Width > 0 && _targetRect.Height > 0;
                break;
            case LiveViewRoiSelectionType.OriginTarget:
                SelectionRect = rect;
                HasSelection = hasSelection;
                SecondarySelectionRect = _targetRect;
                HasSecondarySelection = _targetRect.Width > 0 && _targetRect.Height > 0;
                break;
            case LiveViewRoiSelectionType.TargetRoi:
                SecondarySelectionRect = rect;
                HasSecondarySelection = hasSelection;
                var primary = IsTargetMode ? _originTargetRect : _roiRect;
                SelectionRect = primary;
                HasSelection = primary.Width > 0 && primary.Height > 0;
                break;
        }
    }

    private static bool TryParseRect(string xText, string yText, string wText, string hText, out Rect rect)
    {
        rect = default;
        if (!double.TryParse(xText, out var x)) return false;
        if (!double.TryParse(yText, out var y)) return false;
        if (!double.TryParse(wText, out var w)) return false;
        if (!double.TryParse(hText, out var h)) return false;

        if (x == 0 && y == 0 && w == 0 && h == 0)
        {
            rect = new Rect(0, 0, 0, 0);
            return true;
        }

        if (w <= 0 || h <= 0) return false;

        rect = new Rect(Math.Max(0, x), Math.Max(0, y), w, h);
        return true;
    }

    private static bool TryParseOffset(string xText,
        string yText,
        string wText,
        string hText,
        out double x,
        out double y,
        out double w,
        out double h)
    {
        x = y = w = h = 0;
        if (!double.TryParse(xText, out x)) return false;
        if (!double.TryParse(yText, out y)) return false;
        if (!double.TryParse(wText, out w)) return false;
        if (!double.TryParse(hText, out h)) return false;
        return true;
    }

    private static bool TryParsePoint(string xText, string yText, out Point point)
    {
        point = default;
        if (!double.TryParse(xText, out var x)) return false;
        if (!double.TryParse(yText, out var y)) return false;
        point = new Point(x, y);
        return true;
    }

    private static void RgbToHsv(byte r, byte g, byte b, out int h, out int s, out int v)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;

        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        double hue;
        if (delta == 0)
        {
            hue = 0;
        }
        else if (max == rf)
        {
            hue = 60 * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            hue = 60 * (((bf - rf) / delta) + 2);
        }
        else
        {
            hue = 60 * (((rf - gf) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        var sat = max == 0 ? 0 : delta / max;
        h = (int)Math.Round(hue / 2);
        s = (int)Math.Round(sat * 255);
        v = (int)Math.Round(max * 255);
    }
    private static readonly Dictionary<Key, string> Win32KeyNameMap = new()
    {
        {
            Key.Back, "VK_BACK"
        },
        {
            Key.Tab, "VK_TAB"
        },
        {
            Key.Enter, "VK_RETURN"
        },
        {
            Key.Escape, "VK_ESCAPE"
        },
        {
            Key.Space, "VK_SPACE"
        },
        {
            Key.PageUp, "VK_PRIOR"
        },
        {
            Key.PageDown, "VK_NEXT"
        },
        {
            Key.End, "VK_END"
        },
        {
            Key.Home, "VK_HOME"
        },
        {
            Key.Left, "VK_LEFT"
        },
        {
            Key.Up, "VK_UP"
        },
        {
            Key.Right, "VK_RIGHT"
        },
        {
            Key.Down, "VK_DOWN"
        },
        {
            Key.Insert, "VK_INSERT"
        },
        {
            Key.Delete, "VK_DELETE"
        },
        {
            Key.D0, "0"
        },
        {
            Key.D1, "1"
        },
        {
            Key.D2, "2"
        },
        {
            Key.D3, "3"
        },
        {
            Key.D4, "4"
        },
        {
            Key.D5, "5"
        },
        {
            Key.D6, "6"
        },
        {
            Key.D7, "7"
        },
        {
            Key.D8, "8"
        },
        {
            Key.D9, "9"
        },
        {
            Key.A, "A"
        },
        {
            Key.B, "B"
        },
        {
            Key.C, "C"
        },
        {
            Key.D, "D"
        },
        {
            Key.E, "E"
        },
        {
            Key.F, "F"
        },
        {
            Key.G, "G"
        },
        {
            Key.H, "H"
        },
        {
            Key.I, "I"
        },
        {
            Key.J, "J"
        },
        {
            Key.K, "K"
        },
        {
            Key.L, "L"
        },
        {
            Key.M, "M"
        },
        {
            Key.N, "N"
        },
        {
            Key.O, "O"
        },
        {
            Key.P, "P"
        },
        {
            Key.Q, "Q"
        },
        {
            Key.R, "R"
        },
        {
            Key.S, "S"
        },
        {
            Key.T, "T"
        },
        {
            Key.U, "U"
        },
        {
            Key.V, "V"
        },
        {
            Key.W, "W"
        },
        {
            Key.X, "X"
        },
        {
            Key.Y, "Y"
        },
        {
            Key.Z, "Z"
        },
        {
            Key.LWin, "VK_LWIN"
        },
        {
            Key.RWin, "VK_RWIN"
        },
        {
            Key.NumPad0, "VK_NUMPAD0"
        },
        {
            Key.NumPad1, "VK_NUMPAD1"
        },
        {
            Key.NumPad2, "VK_NUMPAD2"
        },
        {
            Key.NumPad3, "VK_NUMPAD3"
        },
        {
            Key.NumPad4, "VK_NUMPAD4"
        },
        {
            Key.NumPad5, "VK_NUMPAD5"
        },
        {
            Key.NumPad6, "VK_NUMPAD6"
        },
        {
            Key.NumPad7, "VK_NUMPAD7"
        },
        {
            Key.NumPad8, "VK_NUMPAD8"
        },
        {
            Key.NumPad9, "VK_NUMPAD9"
        },
        {
            Key.Multiply, "VK_MULTIPLY"
        },
        {
            Key.Add, "VK_ADD"
        },
        {
            Key.Subtract, "VK_SUBTRACT"
        },
        {
            Key.Decimal, "VK_DECIMAL"
        },
        {
            Key.Divide, "VK_DIVIDE"
        },
        {
            Key.F1, "VK_F1"
        },
        {
            Key.F2, "VK_F2"
        },
        {
            Key.F3, "VK_F3"
        },
        {
            Key.F4, "VK_F4"
        },
        {
            Key.F5, "VK_F5"
        },
        {
            Key.F6, "VK_F6"
        },
        {
            Key.F7, "VK_F7"
        },
        {
            Key.F8, "VK_F8"
        },
        {
            Key.F9, "VK_F9"
        },
        {
            Key.F10, "VK_F10"
        },
        {
            Key.F11, "VK_F11"
        },
        {
            Key.F12, "VK_F12"
        },
        {
            Key.F13, "VK_F13"
        },
        {
            Key.F14, "VK_F14"
        },
        {
            Key.F15, "VK_F15"
        },
        {
            Key.F16, "VK_F16"
        },
        {
            Key.F17, "VK_F17"
        },
        {
            Key.F18, "VK_F18"
        },
        {
            Key.F19, "VK_F19"
        },
        {
            Key.F20, "VK_F20"
        },
        {
            Key.F21, "VK_F21"
        },
        {
            Key.F22, "VK_F22"
        },
        {
            Key.F23, "VK_F23"
        },
        {
            Key.F24, "VK_F24"
        },
        {
            Key.NumLock, "VK_NUMLOCK"
        },
        {
            Key.Scroll, "VK_SCROLL"
        },
        {
            Key.LeftShift, "VK_LSHIFT"
        },
        {
            Key.RightShift, "VK_RSHIFT"
        },
        {
            Key.LeftCtrl, "VK_LCONTROL"
        },
        {
            Key.RightCtrl, "VK_RCONTROL"
        },
        {
            Key.LeftAlt, "VK_LMENU"
        },
        {
            Key.RightAlt, "VK_RMENU"
        },
        {
            Key.OemSemicolon, "VK_OEM_1"
        },
        {
            Key.OemPlus, "VK_OEM_PLUS"
        },
        {
            Key.OemComma, "VK_OEM_COMMA"
        },
        {
            Key.OemMinus, "VK_OEM_MINUS"
        },
        {
            Key.OemPeriod, "VK_OEM_PERIOD"
        },
        {
            Key.OemQuestion, "VK_OEM_2"
        },
        {
            Key.OemTilde, "VK_OEM_3"
        },
        {
            Key.OemOpenBrackets, "VK_OEM_4"
        },
        {
            Key.OemPipe, "VK_OEM_5"
        },
        {
            Key.OemCloseBrackets, "VK_OEM_6"
        },
        {
            Key.OemQuotes, "VK_OEM_7"
        },
    };
    private static readonly Dictionary<string, int> Win32KeyDefinitions = new(StringComparer.Ordinal)
    {
        {
            "VK_BACK", 0x08
        },
        {
            "VK_TAB", 0x09
        },
        {
            "VK_RETURN", 0x0D
        },
        {
            "VK_ESCAPE", 0x1B
        },
        {
            "VK_SPACE", 0x20
        },
        {
            "VK_PRIOR", 0x21
        },
        {
            "VK_NEXT", 0x22
        },
        {
            "VK_END", 0x23
        },
        {
            "VK_HOME", 0x24
        },
        {
            "VK_LEFT", 0x25
        },
        {
            "VK_UP", 0x26
        },
        {
            "VK_RIGHT", 0x27
        },
        {
            "VK_DOWN", 0x28
        },
        {
            "VK_INSERT", 0x2D
        },
        {
            "VK_DELETE", 0x2E
        },
        {
            "0", 0x30
        },
        {
            "1", 0x31
        },
        {
            "2", 0x32
        },
        {
            "3", 0x33
        },
        {
            "4", 0x34
        },
        {
            "5", 0x35
        },
        {
            "6", 0x36
        },
        {
            "7", 0x37
        },
        {
            "8", 0x38
        },
        {
            "9", 0x39
        },
        {
            "A", 0x41
        },
        {
            "B", 0x42
        },
        {
            "C", 0x43
        },
        {
            "D", 0x44
        },
        {
            "E", 0x45
        },
        {
            "F", 0x46
        },
        {
            "G", 0x47
        },
        {
            "H", 0x48
        },
        {
            "I", 0x49
        },
        {
            "J", 0x4A
        },
        {
            "K", 0x4B
        },
        {
            "L", 0x4C
        },
        {
            "M", 0x4D
        },
        {
            "N", 0x4E
        },
        {
            "O", 0x4F
        },
        {
            "P", 0x50
        },
        {
            "Q", 0x51
        },
        {
            "R", 0x52
        },
        {
            "S", 0x53
        },
        {
            "T", 0x54
        },
        {
            "U", 0x55
        },
        {
            "V", 0x56
        },
        {
            "W", 0x57
        },
        {
            "X", 0x58
        },
        {
            "Y", 0x59
        },
        {
            "Z", 0x5A
        },
        {
            "VK_LWIN", 0x5B
        },
        {
            "VK_RWIN", 0x5C
        },
        {
            "VK_NUMPAD0", 0x60
        },
        {
            "VK_NUMPAD1", 0x61
        },
        {
            "VK_NUMPAD2", 0x62
        },
        {
            "VK_NUMPAD3", 0x63
        },
        {
            "VK_NUMPAD4", 0x64
        },
        {
            "VK_NUMPAD5", 0x65
        },
        {
            "VK_NUMPAD6", 0x66
        },
        {
            "VK_NUMPAD7", 0x67
        },
        {
            "VK_NUMPAD8", 0x68
        },
        {
            "VK_NUMPAD9", 0x69
        },
        {
            "VK_MULTIPLY", 0x6A
        },
        {
            "VK_ADD", 0x6B
        },
        {
            "VK_SUBTRACT", 0x6D
        },
        {
            "VK_DECIMAL", 0x6E
        },
        {
            "VK_DIVIDE", 0x6F
        },
        {
            "VK_F1", 0x70
        },
        {
            "VK_F2", 0x71
        },
        {
            "VK_F3", 0x72
        },
        {
            "VK_F4", 0x73
        },
        {
            "VK_F5", 0x74
        },
        {
            "VK_F6", 0x75
        },
        {
            "VK_F7", 0x76
        },
        {
            "VK_F8", 0x77
        },
        {
            "VK_F9", 0x78
        },
        {
            "VK_F10", 0x79
        },
        {
            "VK_F11", 0x7A
        },
        {
            "VK_F12", 0x7B
        },
        {
            "VK_F13", 0x7C
        },
        {
            "VK_F14", 0x7D
        },
        {
            "VK_F15", 0x7E
        },
        {
            "VK_F16", 0x7F
        },
        {
            "VK_F17", 0x80
        },
        {
            "VK_F18", 0x81
        },
        {
            "VK_F19", 0x82
        },
        {
            "VK_F20", 0x83
        },
        {
            "VK_F21", 0x84
        },
        {
            "VK_F22", 0x85
        },
        {
            "VK_F23", 0x86
        },
        {
            "VK_F24", 0x87
        },
        {
            "VK_NUMLOCK", 0x90
        },
        {
            "VK_SCROLL", 0x91
        },
        {
            "VK_LSHIFT", 0xA0
        },
        {
            "VK_RSHIFT", 0xA1
        },
        {
            "VK_LCONTROL", 0xA2
        },
        {
            "VK_RCONTROL", 0xA3
        },
        {
            "VK_LMENU", 0xA4
        },
        {
            "VK_RMENU", 0xA5
        },
        {
            "VK_OEM_1", 0xBA
        },
        {
            "VK_OEM_PLUS", 0xBB
        },
        {
            "VK_OEM_COMMA", 0xBC
        },
        {
            "VK_OEM_MINUS", 0xBD
        },
        {
            "VK_OEM_PERIOD", 0xBE
        },
        {
            "VK_OEM_2", 0xBF
        },
        {
            "VK_OEM_3", 0xC0
        },
        {
            "VK_OEM_4", 0xDB
        },
        {
            "VK_OEM_5", 0xDC
        },
        {
            "VK_OEM_6", 0xDD
        },
        {
            "VK_OEM_7", 0xDE
        },
    };
    private static readonly string[] Win32KeyOptionList =
    [
        "VK_BACK",
        "VK_TAB",
        "VK_RETURN",
        "VK_ESCAPE",
        "VK_SPACE",
        "VK_PRIOR",
        "VK_NEXT",
        "VK_END",
        "VK_HOME",
        "VK_LEFT",
        "VK_UP",
        "VK_RIGHT",
        "VK_DOWN",
        "VK_INSERT",
        "VK_DELETE",
        "0",
        "1",
        "2",
        "3",
        "4",
        "5",
        "6",
        "7",
        "8",
        "9",
        "A",
        "B",
        "C",
        "D",
        "E",
        "F",
        "G",
        "H",
        "I",
        "J",
        "K",
        "L",
        "M",
        "N",
        "O",
        "P",
        "Q",
        "R",
        "S",
        "T",
        "U",
        "V",
        "W",
        "X",
        "Y",
        "Z",
        "VK_LWIN",
        "VK_RWIN",
        "VK_NUMPAD0",
        "VK_NUMPAD1",
        "VK_NUMPAD2",
        "VK_NUMPAD3",
        "VK_NUMPAD4",
        "VK_NUMPAD5",
        "VK_NUMPAD6",
        "VK_NUMPAD7",
        "VK_NUMPAD8",
        "VK_NUMPAD9",
        "VK_MULTIPLY",
        "VK_ADD",
        "VK_SUBTRACT",
        "VK_DECIMAL",
        "VK_DIVIDE",
        "VK_F1",
        "VK_F2",
        "VK_F3",
        "VK_F4",
        "VK_F5",
        "VK_F6",
        "VK_F7",
        "VK_F8",
        "VK_F9",
        "VK_F10",
        "VK_F11",
        "VK_F12",
        "VK_F13",
        "VK_F14",
        "VK_F15",
        "VK_F16",
        "VK_F17",
        "VK_F18",
        "VK_F19",
        "VK_F20",
        "VK_F21",
        "VK_F22",
        "VK_F23",
        "VK_F24",
        "VK_NUMLOCK",
        "VK_SCROLL",
        "VK_LSHIFT",
        "VK_RSHIFT",
        "VK_LCONTROL",
        "VK_RCONTROL",
        "VK_LMENU",
        "VK_RMENU",
        "VK_OEM_1",
        "VK_OEM_PLUS",
        "VK_OEM_COMMA",
        "VK_OEM_MINUS",
        "VK_OEM_PERIOD",
        "VK_OEM_2",
        "VK_OEM_3",
        "VK_OEM_4",
        "VK_OEM_5",
        "VK_OEM_6",
        "VK_OEM_7"
    ];
    private static readonly Dictionary<Key, int> Win32KeyMap = new()
    {
        {
            Key.Back, 0x08
        },
        {
            Key.Tab, 0x09
        },
        {
            Key.Enter, 0x0D
        },
        {
            Key.Escape, 0x1B
        },
        {
            Key.Space, 0x20
        },
        {
            Key.PageUp, 0x21
        },
        {
            Key.PageDown, 0x22
        },
        {
            Key.End, 0x23
        },
        {
            Key.Home, 0x24
        },
        {
            Key.Left, 0x25
        },
        {
            Key.Up, 0x26
        },
        {
            Key.Right, 0x27
        },
        {
            Key.Down, 0x28
        },
        {
            Key.Insert, 0x2D
        },
        {
            Key.Delete, 0x2E
        },
        {
            Key.D0, 0x30
        },
        {
            Key.D1, 0x31
        },
        {
            Key.D2, 0x32
        },
        {
            Key.D3, 0x33
        },
        {
            Key.D4, 0x34
        },
        {
            Key.D5, 0x35
        },
        {
            Key.D6, 0x36
        },
        {
            Key.D7, 0x37
        },
        {
            Key.D8, 0x38
        },
        {
            Key.D9, 0x39
        },
        {
            Key.A, 0x41
        },
        {
            Key.B, 0x42
        },
        {
            Key.C, 0x43
        },
        {
            Key.D, 0x44
        },
        {
            Key.E, 0x45
        },
        {
            Key.F, 0x46
        },
        {
            Key.G, 0x47
        },
        {
            Key.H, 0x48
        },
        {
            Key.I, 0x49
        },
        {
            Key.J, 0x4A
        },
        {
            Key.K, 0x4B
        },
        {
            Key.L, 0x4C
        },
        {
            Key.M, 0x4D
        },
        {
            Key.N, 0x4E
        },
        {
            Key.O, 0x4F
        },
        {
            Key.P, 0x50
        },
        {
            Key.Q, 0x51
        },
        {
            Key.R, 0x52
        },
        {
            Key.S, 0x53
        },
        {
            Key.T, 0x54
        },
        {
            Key.U, 0x55
        },
        {
            Key.V, 0x56
        },
        {
            Key.W, 0x57
        },
        {
            Key.X, 0x58
        },
        {
            Key.Y, 0x59
        },
        {
            Key.Z, 0x5A
        },
        {
            Key.LWin, 0x5B
        },
        {
            Key.RWin, 0x5C
        },
        {
            Key.NumPad0, 0x60
        },
        {
            Key.NumPad1, 0x61
        },
        {
            Key.NumPad2, 0x62
        },
        {
            Key.NumPad3, 0x63
        },
        {
            Key.NumPad4, 0x64
        },
        {
            Key.NumPad5, 0x65
        },
        {
            Key.NumPad6, 0x66
        },
        {
            Key.NumPad7, 0x67
        },
        {
            Key.NumPad8, 0x68
        },
        {
            Key.NumPad9, 0x69
        },
        {
            Key.Multiply, 0x6A
        },
        {
            Key.Add, 0x6B
        },
        {
            Key.Subtract, 0x6D
        },
        {
            Key.Decimal, 0x6E
        },
        {
            Key.Divide, 0x6F
        },
        {
            Key.F1, 0x70
        },
        {
            Key.F2, 0x71
        },
        {
            Key.F3, 0x72
        },
        {
            Key.F4, 0x73
        },
        {
            Key.F5, 0x74
        },
        {
            Key.F6, 0x75
        },
        {
            Key.F7, 0x76
        },
        {
            Key.F8, 0x77
        },
        {
            Key.F9, 0x78
        },
        {
            Key.F10, 0x79
        },
        {
            Key.F11, 0x7A
        },
        {
            Key.F12, 0x7B
        },
        {
            Key.F13, 0x7C
        },
        {
            Key.F14, 0x7D
        },
        {
            Key.F15, 0x7E
        },
        {
            Key.F16, 0x7F
        },
        {
            Key.F17, 0x80
        },
        {
            Key.F18, 0x81
        },
        {
            Key.F19, 0x82
        },
        {
            Key.F20, 0x83
        },
        {
            Key.F21, 0x84
        },
        {
            Key.F22, 0x85
        },
        {
            Key.F23, 0x86
        },
        {
            Key.F24, 0x87
        },
        {
            Key.NumLock, 0x90
        },
        {
            Key.Scroll, 0x91
        },
        {
            Key.LeftShift, 0xA0
        },
        {
            Key.RightShift, 0xA1
        },
        {
            Key.LeftCtrl, 0xA2
        },
        {
            Key.RightCtrl, 0xA3
        },
        {
            Key.LeftAlt, 0xA4
        },
        {
            Key.RightAlt, 0xA5
        },
        {
            Key.OemSemicolon, 0xBA
        },
        {
            Key.OemPlus, 0xBB
        },
        {
            Key.OemComma, 0xBC
        },
        {
            Key.OemMinus, 0xBD
        },
        {
            Key.OemPeriod, 0xBE
        },
        {
            Key.OemQuestion, 0xBF
        },
        {
            Key.OemTilde, 0xC0
        },
        {
            Key.OemOpenBrackets, 0xDB
        },
        {
            Key.OemPipe, 0xDC
        },
        {
            Key.OemCloseBrackets, 0xDD
        },
        {
            Key.OemQuotes, 0xDE
        },
    };
    private static readonly Dictionary<Key, int> AdbKeyMap = new()
    {
        {
            Key.Back, 67
        },
        {
            Key.Tab, 61
        },
        {
            Key.Enter, 66
        },
        {
            Key.Escape, 111
        },
        {
            Key.Space, 62
        },
        {
            Key.PageUp, 92
        },
        {
            Key.PageDown, 93
        },
        {
            Key.End, 123
        },
        {
            Key.Home, 3
        },
        {
            Key.Left, 21
        },
        {
            Key.Up, 19
        },
        {
            Key.Right, 22
        },
        {
            Key.Down, 20
        },
        {
            Key.Insert, 124
        },
        {
            Key.Delete, 112
        },
        {
            Key.D0, 7
        },
        {
            Key.D1, 8
        },
        {
            Key.D2, 9
        },
        {
            Key.D3, 10
        },
        {
            Key.D4, 11
        },
        {
            Key.D5, 12
        },
        {
            Key.D6, 13
        },
        {
            Key.D7, 14
        },
        {
            Key.D8, 15
        },
        {
            Key.D9, 16
        },
        {
            Key.A, 29
        },
        {
            Key.B, 30
        },
        {
            Key.C, 31
        },
        {
            Key.D, 32
        },
        {
            Key.E, 33
        },
        {
            Key.F, 34
        },
        {
            Key.G, 35
        },
        {
            Key.H, 36
        },
        {
            Key.I, 37
        },
        {
            Key.J, 38
        },
        {
            Key.K, 39
        },
        {
            Key.L, 40
        },
        {
            Key.M, 41
        },
        {
            Key.N, 42
        },
        {
            Key.O, 43
        },
        {
            Key.P, 44
        },
        {
            Key.Q, 45
        },
        {
            Key.R, 46
        },
        {
            Key.S, 47
        },
        {
            Key.T, 48
        },
        {
            Key.U, 49
        },
        {
            Key.V, 50
        },
        {
            Key.W, 51
        },
        {
            Key.X, 52
        },
        {
            Key.Y, 53
        },
        {
            Key.Z, 54
        },
        {
            Key.NumPad0, 144
        },
        {
            Key.NumPad1, 145
        },
        {
            Key.NumPad2, 146
        },
        {
            Key.NumPad3, 147
        },
        {
            Key.NumPad4, 148
        },
        {
            Key.NumPad5, 149
        },
        {
            Key.NumPad6, 150
        },
        {
            Key.NumPad7, 151
        },
        {
            Key.NumPad8, 152
        },
        {
            Key.NumPad9, 153
        },
        {
            Key.Multiply, 155
        },
        {
            Key.Add, 157
        },
        {
            Key.Subtract, 156
        },
        {
            Key.Decimal, 158
        },
        {
            Key.Divide, 154
        },
        {
            Key.F1, 131
        },
        {
            Key.F2, 132
        },
        {
            Key.F3, 133
        },
        {
            Key.F4, 134
        },
        {
            Key.F5, 135
        },
        {
            Key.F6, 136
        },
        {
            Key.F7, 137
        },
        {
            Key.F8, 138
        },
        {
            Key.F9, 139
        },
        {
            Key.F10, 140
        },
        {
            Key.F11, 141
        },
        {
            Key.F12, 142
        },
    };

    [RelayCommand]
    private void StartKeyCapture()
    {
        if (IsKeyCodeAdb)
        {
            return;
        }

        IsKeyCaptureActive = true;
        KeyCaptureKey = LangKeys.HotKeyPressing.ToLocalization();
        KeyCaptureCode = string.Empty;
    }

    [RelayCommand]
    private void SetKeyCodeMode(string? mode)
    {
        IsKeyCodeAdb = string.Equals(mode, "Adb", StringComparison.OrdinalIgnoreCase);
    }
    partial void OnIsKeyCodeAdbChanged(bool value)
    {
        IsKeyCaptureActive = false;
        if (value)
        {
            UpdateAdbKeyFromInput(AdbKeyInput);
        }
        else
        {
            UpdateWin32KeyFromInput(Win32KeyInput);
        }
    }
    partial void OnAdbKeyInputChanged(string value)
    {
        if (IsKeyCodeAdb)
        {
            UpdateAdbKeyFromInput(value);
        }
    }
    partial void OnWin32KeyInputChanged(string value)
    {
        if (!IsKeyCodeAdb)
        {
            UpdateWin32KeyFromInput(value);
        }
    }

    private void UpdateAdbKeyFromInput(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            KeyCaptureKey = string.Empty;
            KeyCaptureCode = string.Empty;
            return;
        }

        KeyCaptureKey = trimmed;
        KeyCaptureCode = AdbKeyDefinitions.TryGetValue(trimmed, out var code)
            ? code.ToString()
            : "-";
    }

    private void UpdateWin32KeyFromInput(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            KeyCaptureKey = string.Empty;
            KeyCaptureCode = string.Empty;
            return;
        }

        KeyCaptureKey = trimmed;
        KeyCaptureCode = Win32KeyDefinitions.TryGetValue(trimmed, out var code)
            ? code.ToString()
            : "-";
    }
//     return candidates.FirstOrDefault(File.Exists);
// }

    public void CaptureKey(Key key)
    {
        if (IsKeyCodeAdb)
        {
            return;
        }

        var hasValue = TryMapKeyToCode(key, out var code);
        var keyName = TryGetWin32KeyName(key, out var name) ? name : key.ToString();
        Win32KeyInput = keyName;
        KeyCaptureKey = keyName;
        KeyCaptureCode = hasValue ? code.ToString() : "-";
        IsKeyCaptureActive = false;
    }

    private bool TryMapKeyToCode(Key key, out int code)
    {
        if (IsKeyCodeAdb)
        {
            code = default;
            return false;
        }

        return Win32KeyMap.TryGetValue(key, out code);
    }

    private static bool TryGetWin32KeyName(Key key, out string name)
    {
        return Win32KeyNameMap.TryGetValue(key, out name);
    }
    /// <summary>
    /// Live View 刷新率变化事件，参数为计算后的间隔（秒）
    /// </summary>
    public event Action<double>? LiveViewRefreshRateChanged;

    /// <summary>
    /// Live View 是否启用
    /// </summary>
    [ObservableProperty] private bool _enableLiveView =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableLiveView, true);

    /// <summary>
    /// Live View 刷新率（FPS），范围 1-60，默认 10
    /// </summary>
    [ObservableProperty] private double _liveViewRefreshRate =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.LiveViewRefreshRate, 30.0);

    [ObservableProperty] private Bitmap? _liveViewImage;
    private WriteableBitmap? _liveViewWriteableBitmap;


    [ObservableProperty] private double _liveViewFps;
    private DateTime _liveViewFpsWindowStart = DateTime.UtcNow;
    private int _liveViewFrameCount;
    private int _liveViewImageCount;
    private int _liveViewImageNewestCount;
    private static int _liveViewSemaphoreCurrentCount = 2;
    private const int LiveViewSemaphoreMaxCount = 5;
    private static int _liveViewSemaphoreFailCount = 0;
    private static readonly SemaphoreSlim _liveViewSemaphore = new(_liveViewSemaphoreCurrentCount, LiveViewSemaphoreMaxCount);
    private readonly WriteableBitmap?[] _liveViewImageCache = new WriteableBitmap?[LiveViewSemaphoreMaxCount];
    public double GetLiveViewRefreshInterval() => 1.0 / LiveViewRefreshRate;
    /// <summary>
    /// Live View 是否可见（已连接且有图像）
    /// </summary>
    public bool IsLiveViewVisible => EnableLiveView && IsConnected && LiveViewImage != null;
    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLiveViewVisible));
        if (!value)
        {
            return;
        }

        if (ActiveToolMode == LiveViewToolMode.None)
        {
            if (IsLiveViewPaused)
            {
                IsLiveViewPaused = false;
            }
            return;
        }
        if (IsLiveViewPaused)
        {
            TaskManager.RunTask(async () =>
            {
                if (IsLiveViewPaused)
                {
                    IsLiveViewPaused = false;
                    await Task.Delay(500);
                    IsLiveViewPaused = true;
                }
            }, name: "刷新实时画面");
        }

    }
    partial void OnLiveViewImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(IsLiveViewVisible));
        if (!IsLiveViewPaused)
        {
            OnPropertyChanged(nameof(LiveViewDisplayImage));
        }

        if (value is { } bitmap)
        {
            var newSize = bitmap.Size;
            if (_lastLiveViewSize is null || _lastLiveViewSize.Value != newSize)
            {
                _lastLiveViewSize = newSize;
                ClearSelectionPreview();
                HasSwipeArrow = false;
                SwipeArrowGeometry = null;
            }
        }
        else
        {
            _lastLiveViewSize = null;
            ClearSelectionPreview();
            HasSwipeArrow = false;
            SwipeArrowGeometry = null;
        }

        if (IsColorPreviewActive)
        {
            ApplyColorFilter();
        }
    }

    /// <summary>
    /// 更新 Live View 图像（仿 WPF：直接写入缓冲）
    /// </summary>
    public async Task UpdateLiveViewImageAsync(MaaImageBuffer? buffer)
    {
        if (!await _liveViewSemaphore.WaitAsync(0))
        {
            if (++_liveViewSemaphoreFailCount < 3)
            {
                buffer?.Dispose();
                return;
            }

            _liveViewSemaphoreFailCount = 0;

            if (_liveViewSemaphoreCurrentCount < LiveViewSemaphoreMaxCount)
            {
                _liveViewSemaphoreCurrentCount++;
                _liveViewSemaphore.Release();
                LoggerHelper.Info($"LiveView Semaphore Full, increase semaphore count to {_liveViewSemaphoreCurrentCount}");
            }

            buffer?.Dispose();
            return;
        }

        try
        {
            var count = Interlocked.Increment(ref _liveViewImageCount);
            var index = count % _liveViewImageCache.Length;

            if (buffer == null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LiveViewImage = null;
                    _liveViewWriteableBitmap?.Dispose();
                    _liveViewWriteableBitmap = null;
                    Array.Fill(_liveViewImageCache, null);
                    _liveViewImageNewestCount = 0;
                    _liveViewImageCount = 0;
                });
                return;
            }

            if (!buffer.TryGetRawData(out var rawData, out var width, out var height, out _))
            {
                return;
            }

            if (rawData == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return;
            }

            if (buffer.Channels is not (3 or 4))
            {
                return;
            }

            if (count <= _liveViewImageNewestCount)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _liveViewImageCache[index] = WriteBgrToBitmap(rawData, width, height, buffer.Channels, _liveViewImageCache[index]);
                LiveViewImage = _liveViewImageCache[index];
            });

            Interlocked.Exchange(ref _liveViewImageNewestCount, count);
            _liveViewSemaphoreFailCount = 0;

            var now = DateTime.UtcNow;
            Interlocked.Increment(ref _liveViewFrameCount);
            var totalSeconds = (now - _liveViewFpsWindowStart).TotalSeconds;
            if (totalSeconds >= 1)
            {
                var frameCount = Interlocked.Exchange(ref _liveViewFrameCount, 0);
                _liveViewFpsWindowStart = now;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LiveViewFps = frameCount / totalSeconds;
                });
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"LiveView update failed: {ex.Message}");
        }
        finally
        {
            buffer?.Dispose();
            _liveViewSemaphore.Release();
        }
    }

    private static WriteableBitmap WriteBgrToBitmap(IntPtr bgrData, int width, int height, int channels, WriteableBitmap? targetBitmap)
    {
        const int dstBytesPerPixel = 4;

        if (width <= 0 || height <= 0)
        {
            return targetBitmap
                ?? new WriteableBitmap(
                    new PixelSize(1, 1),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);
        }

        if (targetBitmap == null
            || targetBitmap.PixelSize.Width != width
            || targetBitmap.PixelSize.Height != height)
        {
            targetBitmap?.Dispose();
            targetBitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
        }

        using var framebuffer = targetBitmap.Lock();
        unsafe
        {
            var dstStride = framebuffer.RowBytes;
            if (dstStride <= 0)
            {
                return targetBitmap;
            }

            var dstPtr = (byte*)framebuffer.Address;

            if (channels == 4)
            {
                var srcStride = width * dstBytesPerPixel;
                var rowCopy = Math.Min(srcStride, dstStride);
                var srcPtr = (byte*)bgrData;
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(srcPtr + y * srcStride, dstPtr + y * dstStride, dstStride, rowCopy);
                }

                return targetBitmap;
            }

            if (channels == 3)
            {
                var srcStride = width * 3;
                var rowBuffer = ArrayPool<byte>.Shared.Rent(width * dstBytesPerPixel);
                try
                {
                    var srcPtr = (byte*)bgrData;
                    var rowCopy = Math.Min(width * dstBytesPerPixel, dstStride);
                    for (var y = 0; y < height; y++)
                    {
                        var srcRow = srcPtr + y * srcStride;
                        for (var x = 0; x < width; x++)
                        {
                            var srcIndex = x * 3;
                            var dstIndex = x * dstBytesPerPixel;
                            rowBuffer[dstIndex] = srcRow[srcIndex];
                            rowBuffer[dstIndex + 1] = srcRow[srcIndex + 1];
                            rowBuffer[dstIndex + 2] = srcRow[srcIndex + 2];
                            rowBuffer[dstIndex + 3] = 255;
                        }

                        fixed (byte* rowPtr = rowBuffer)
                        {
                            Buffer.MemoryCopy(rowPtr, dstPtr + y * dstStride, dstStride, rowCopy);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rowBuffer);
                }

                return targetBitmap;
            }
        }

        return targetBitmap;
    }

    #endregion
}
