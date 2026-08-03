using Avalonia.Controls;
using Avalonia.Media;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Notification;
using MFAToolsPlus.Configuration;
using MFAToolsPlus.Extensions;
using MFAToolsPlus.Extensions.MaaFW;
using MFAToolsPlus.Extensions.MaaFW.Custom;
using MFAToolsPlus.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MFAToolsPlus.Helper;

/// <summary>
/// MaaFW 配置类
/// </summary>
public class MaaFWConfiguration
{
    public AdbDeviceCoreConfig AdbDevice { get; set; } = new();
    public DesktopWindowCoreConfig DesktopWindow { get; set; } = new();

    public PlayCoverCoreConfig PlayCover { get; set; } = new();
}

/// <summary>
/// 桌面窗口核心配置
/// </summary>
public class DesktopWindowCoreConfig
{
    public string Name { get; set; } = string.Empty;
    public nint HWnd { get; set; }
    public Win32InputMethod Mouse { get; set; } = Win32InputMethod.SendMessage;
    public Win32InputMethod KeyBoard { get; set; } = Win32InputMethod.SendMessage;
    public Win32ScreencapMethods ScreenCap { get; set; } = Win32ScreencapMethods.FramePool;
    public LinkOption Link { get; set; } = LinkOption.Start;
    public CheckStatusOption Check { get; set; } = CheckStatusOption.ThrowIfNotSucceeded;
}

/// <summary>
/// ADB 设备核心配置
/// </summary>
public class AdbDeviceCoreConfig
{
    public string Name { get; set; } = string.Empty;
    public string AdbPath { get; set; } = "adb";
    public string AdbSerial { get; set; } = "";
    public string Config { get; set; } = "{}";
    public AdbInputMethods Input { get; set; } = AdbInputMethods.Default;
    public AdbScreencapMethods ScreenCap { get; set; } = AdbScreencapMethods.Default;
    public AdbDeviceInfo? Info { get; set; } = null;
}

/// <summary>
/// PlayCover 设备核心配置
/// </summary>
public class PlayCoverCoreConfig
{
    public string Name { get; set; } = string.Empty;
    public string PlayCoverAddress { get; set; } = "";
    public string UUID { get; set; } = "maa.playcover";
}

public class MaaProcessor
{
    public static MaaProcessor Instance { get; } = new();
    public MaaTasker? MaaTasker { private set; get; }
    public static MaaToolkit Toolkit { get; set; } = new(true);
    public static MaaGlobal Global { get; set; } = new();
    public static MaaFWConfiguration Config { get; set; } = new();
    public void SetTasker(MaaTasker? maaTasker = null)
    {
        if (maaTasker == null)
        {
            Instances.ToolsViewModel.SetConnected(false);
            MaaTasker?.Dispose();
        }
        MaaTasker = maaTasker;
    }

    private static bool _isClosed = false;
    public static bool IsClosed => _isClosed;

    public void Close()
    {
        _isClosed = true;
        SetTasker();
    }

    // private int _screencapFailedCount;
    // private readonly Lock _screencapLogLock = new();
    // private const int ActionFailedLimit = 1;
    // private void ResetActionFailedCount()
    // {
    //     _screencapFailedCount = 0;
    // }

    // private bool HandleScreencapFailure()
    // {
    //     if (Instances.ToolsViewModel.IsConnected && ++_screencapFailedCount <= ActionFailedLimit)
    //     {
    //         return false;
    //     }
    //
    //     _screencapFailedCount = 0;
    //     Instances.ToolsViewModel.SetConnected(false);
    //
    //     SetTasker();
    //     return true;
    // }

    // public void HandleControllerCallBack(object? sender, MaaCallbackEventArgs args)
    // {
    //     var message = args.Message;
    //     if (message == MaaMsg.Controller.Action.Failed)
    //     {
    //         HandleScreencapFailure();
    //     }
    // }

    public async Task TestConnecting()
    {
        await GetTaskerAsync();
        if (Instances.ToolsViewModel.CurrentController == MaaControllerTypes.Dbg)
        {
            Instances.ToolsViewModel.SetConnected(MaaTasker is { IsInitialized: true });
            return;
        }

        var task = MaaTasker?.Controller?.LinkStart();
        task?.Wait();
        Instances.ToolsViewModel.SetConnected(task?.Status == MaaJobStatus.Succeeded);
    }

    public async Task ReconnectAsync(CancellationToken token = default, bool showMessage = true)
    {
        await HandleDeviceConnectionAsync(token, showMessage);
    }

    async private Task HandleDeviceConnectionAsync(CancellationToken token, bool showMessage = true)
    {
        var controllerType = Instances.ToolsViewModel.CurrentController;
        var isAdb = controllerType == MaaControllerTypes.Adb;
        var isPlayCover = controllerType == MaaControllerTypes.PlayCover;
        var isDbg = controllerType == MaaControllerTypes.Dbg;
        var targetKey = controllerType switch
        {
            MaaControllerTypes.Adb => LangKeys.Emulator,
            MaaControllerTypes.Win32 => LangKeys.Window,
            MaaControllerTypes.PlayCover => "TabPlayCover",
            MaaControllerTypes.Dbg => "TabDbg",
            _ => LangKeys.Window
        };


        ToastHelper.Info(LangKeys.Tip.ToLocalization(), LangKeys.ConnectingTo.ToLocalizationFormatted(true, targetKey));

        if (!isPlayCover && !isDbg && Instances.ToolsViewModel.CurrentDevice == null && Instances.ConnectSettingsUserControlModel.AutoDetectOnConnectionFailed)
            Instances.ToolsViewModel.TryReadAdbDeviceFromConfig(false, true);

        var tuple = await TryConnectAsync(token);
        var connected = tuple.Item1;
        var shouldRetry = tuple.Item3;

        if (!connected && isAdb && !tuple.Item2 && shouldRetry)
        {
            connected = await HandleAdbConnectionAsync(token, showMessage);
        }

        if (!connected)
        {
            if (!tuple.Item2 && shouldRetry)
                HandleConnectionFailureAsync(controllerType, token);
            throw new Exception("Connection failed after all retries");
        }

        Instances.ToolsViewModel.SetConnected(true);
    }
    async private Task<bool> HandleAdbConnectionAsync(CancellationToken token, bool showMessage = true)
    {
        bool connected = false;
        var retrySteps = new List<Func<CancellationToken, Task<bool>>>
        {
            async t => await RetryConnectionAsync(t, showMessage, ReconnectByAdb, LangKeys.TryToReconnect),
            async t => await RetryConnectionAsync(t, showMessage, RestartAdb, LangKeys.RestartAdb, Instances.ConnectSettingsUserControlModel.AllowAdbRestart),
            async t => await RetryConnectionAsync(t, showMessage, HardRestartAdb, LangKeys.HardRestartAdb, Instances.ConnectSettingsUserControlModel.AllowAdbHardRestart)
        };

        foreach (var step in retrySteps)
        {
            if (token.IsCancellationRequested) break;
            connected = await step(token);
            if (connected) break;
        }

        return connected;
    }
    public async Task RestartAdb()
    {
        await ProcessHelper.RestartAdbAsync(Config.AdbDevice.AdbPath);
    }

    public async Task ReconnectByAdb()
    {
        await ProcessHelper.ReconnectByAdbAsync(Config.AdbDevice.AdbPath, Config.AdbDevice.AdbSerial);
    }

    public async Task HardRestartAdb()
    {
        ProcessHelper.HardRestartAdb(Config.AdbDevice.AdbPath);
    }
    async private Task<bool> RetryConnectionAsync(CancellationToken token, bool showMessage, Func<Task> action, string logKey, bool enable = true, Action? other = null)
    {
        if (!enable) return false;
        token.ThrowIfCancellationRequested();

        ToastHelper.Info(LangKeys.ConnectFailed.ToLocalization(), logKey.ToLocalization());
        await action();
        if (token.IsCancellationRequested)
        {
            return false;
        }
        other?.Invoke();
        var tuple = await TryConnectAsync(token);
        // 如果不应该重试（Agent启动失败或资源加载失败），直接返回 false
        if (!tuple.Item3)
        {
            return false;
        }
        return tuple.Item1;
    }

    private void HandleConnectionFailureAsync(MaaControllerTypes controllerType, CancellationToken token)
    {
        // 如果 token 已取消，不需要再调用 Stop，因为已经在其他地方处理了
        if (token.IsCancellationRequested)
        {
            LoggerHelper.Info("HandleConnectionFailureAsync: token is already canceled, skipping Stop call");
            return;
        }
        ToastHelper.Error(LangKeys.ConnectFailed);
        Instances.ToolsViewModel.SetConnected(false);
        var targetKey = controllerType switch
        {
            MaaControllerTypes.Adb => LangKeys.Emulator,
            MaaControllerTypes.Win32 => LangKeys.Window,
            MaaControllerTypes.PlayCover => "TabPlayCover",
            MaaControllerTypes.Dbg => "TabDbg",
            _ => LangKeys.Window
        };
        ToastHelper.Warn(LangKeys.Warning_CannotConnect.ToLocalizationFormatted(true, targetKey));
    }


    async private Task<(bool, bool, bool)> TryConnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tuple = await GetTaskerAndBoolAsync(token);
        return (tuple.Item1 is { IsInitialized: true }, tuple.Item2, tuple.Item3);
    }

    public async Task<(MaaTasker?, bool, bool)> GetTaskerAndBoolAsync(CancellationToken token = default)
    {
        var tuple = MaaTasker != null ? (MaaTasker, false, false) : await InitializeMaaTasker(token);
        MaaTasker ??= tuple.Item1;
        return (MaaTasker, tuple.Item2, tuple.Item3);
    }

    public async Task<MaaTasker?> GetTaskerAsync(CancellationToken token = default)
    {
        MaaTasker ??= (await InitializeMaaTasker(token)).Item1;
        return MaaTasker;
    }
    async private Task<(MaaTasker?, bool, bool)> InitializeMaaTasker(CancellationToken token) // 添加 async 和 token
    {
        var InvalidResource = false;
        var ShouldRetry = true;

        LoggerHelper.Info(LangKeys.LoadingResources.ToLocalization());

        if (Design.IsDesignMode)
        {
            return (null, false, false);
        }
        MaaResource? maaResource = null;
        try
        {
            maaResource = await TaskManager.RunTaskAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                return new MaaResource(Path.Combine(AppContext.BaseDirectory, "resource", "base"));
            }, token: token, name: "资源检测", catchException: true, shouldLog: false, handleError: exception =>
            {
                HandleInitializationError(exception, LangKeys.LoadResourcesFailed.ToLocalization(), LangKeys.LoadResourcesFailedDetail.ToLocalization());
                ToastHelper.Error(LangKeys.LoadResourcesFailed.ToLocalization());
                InvalidResource = true;
                throw exception;
            });

            Instances.PerformanceUserControlModel.ChangeGpuOption(maaResource, Instances.PerformanceUserControlModel.GpuOption);

            LoggerHelper.Info(
                $"GPU acceleration: {(Instances.PerformanceUserControlModel.GpuOption.IsDirectML ? Instances.PerformanceUserControlModel.GpuOption.Adapter.AdapterName : Instances.PerformanceUserControlModel.GpuOption.Device.ToString())}{(Instances.PerformanceUserControlModel.GpuOption.IsDirectML ? $",Adapter Id: {Instances.PerformanceUserControlModel.GpuOption.Adapter.AdapterId}" : "")}");

        }
        catch (OperationCanceledException)
        {
            ShouldRetry = false;
            LoggerHelper.Warning("Resource loading was canceled");
            return (null, InvalidResource, ShouldRetry);
        }
        catch (MaaJobStatusException)
        {
            ShouldRetry = false;
            return (null, InvalidResource, ShouldRetry);
        }
        catch (Exception e)
        {
            ShouldRetry = false;
            LoggerHelper.Error("Initialization resource error", e);
            return (null, InvalidResource, ShouldRetry);
        }

        // 初始化控制器部分同理
        MaaController? controller = null;
        try
        {
            controller = await TaskManager.RunTaskAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                return InitializeController(Instances.ToolsViewModel.CurrentController);
            }, token: token, name: "控制器检测", catchException: true, shouldLog: false, handleError: exception => HandleInitializationError(exception,
                LangKeys.ConnectingEmulatorOrWindow.ToLocalization()
                    .FormatWith(Instances.ToolsViewModel.CurrentController == MaaControllerTypes.Adb
                        ? LangKeys.Emulator.ToLocalization()
                        : LangKeys.Window.ToLocalization()), true,
                LangKeys.InitControllerFailed.ToLocalization()));

            controller.SetOption_ScreenshotTargetShortSide(Instances.ToolSettingsUserControlModel.ResizeShortestSide);
        }
        catch (OperationCanceledException)
        {
            LoggerHelper.Warning("Controller initialization was canceled");
            return (null, InvalidResource, ShouldRetry);
        }
        catch (MaaException)
        {
            return (null, InvalidResource, ShouldRetry); // 控制器异常可以重试
        }
        catch (Exception e)
        {
            LoggerHelper.Error("Initialization controller error", e);
            return (null, InvalidResource, ShouldRetry); // 控制器错误可以重试
        }

        try
        {
            token.ThrowIfCancellationRequested();


            var tasker = new MaaTasker
            {
                Controller = controller,
                Resource = maaResource,
                Toolkit = MaaProcessor.Toolkit,
                Global = MaaProcessor.Global,
                DisposeOptions = DisposeOptions.All,
            };
            
            try
            {
                var tempMFADir = Path.Combine(AppContext.BaseDirectory, "temp_mfa");
                if (Directory.Exists(tempMFADir))
                    Directory.Delete(tempMFADir, true);

                var tempMaaDir = Path.Combine(AppContext.BaseDirectory, "temp_maafw");
                if (Directory.Exists(tempMaaDir))
                    Directory.Delete(tempMaaDir, true);

                var tempResDir = Path.Combine(AppContext.BaseDirectory, "temp_res");
                if (Directory.Exists(tempResDir))
                    Directory.Delete(tempResDir, true);
            }
            catch (Exception e)
            {
                LoggerHelper.Error(e);
            }
            
            tasker.Global.SetOption_SaveOnError(false);
            tasker.Global.SetOption_DebugMode(true);
            tasker.Resource.Register(new MFAOCRRecognition());

            var linkStatus = tasker.Controller?.LinkStart().Wait();
            if (linkStatus != MaaJobStatus.Succeeded && Instances.ToolsViewModel.CurrentController != MaaControllerTypes.Dbg)
            {
                tasker.Dispose();
                return (null, InvalidResource, ShouldRetry);
            }
            Instances.ToolsViewModel.SetConnected(true);
            return (tasker, InvalidResource, ShouldRetry);
        }
        catch (OperationCanceledException)
        {
            LoggerHelper.Warning("Tasker initialization was canceled");
            return (null, InvalidResource, ShouldRetry);
        }
        catch (MaaException)
        {
            return (null, InvalidResource, ShouldRetry);
        }
        catch (Exception e)
        {
            LoggerHelper.Error("Initialization tasker error", e);
            return (null, InvalidResource, ShouldRetry);
        }
    }
    private void HandleInitializationError(Exception e,
        string message,
        bool hasWarning = false,
        string waringMessage = "")
    {
        ToastHelper.Error(message);
        if (hasWarning)
            LoggerHelper.Warning(waringMessage);
        LoggerHelper.Error(e.ToString());
    }

    private void HandleInitializationError(Exception e,
        string title,
        string message,
        bool hasWarning = false,
        string waringMessage = "")
    {
        ToastHelper.Error(title, message);
        if (hasWarning)
            LoggerHelper.Warning(waringMessage);
        LoggerHelper.Error(e.ToString());
    }

    private MaaController? InitializeController(MaaControllerTypes controllerType)
    {
        ConnectToMAA();

        switch (controllerType)
        {
            case MaaControllerTypes.Adb:
                LoggerHelper.Info($"Name: {Config.AdbDevice.Name}");
                LoggerHelper.Info($"AdbPath: {Config.AdbDevice.AdbPath}");
                LoggerHelper.Info($"AdbSerial: {Config.AdbDevice.AdbSerial}");
                LoggerHelper.Info($"ScreenCap: {Config.AdbDevice.ScreenCap}");
                LoggerHelper.Info($"Input: {Config.AdbDevice.Input}");
                LoggerHelper.Info($"Config: {Config.AdbDevice.Config}");

                return new MaaAdbController(
                    Config.AdbDevice.AdbPath,
                    Config.AdbDevice.AdbSerial,
                    Config.AdbDevice.ScreenCap, Config.AdbDevice.Input,
                    !string.IsNullOrWhiteSpace(Config.AdbDevice.Config) ? Config.AdbDevice.Config : "{}",
                    Path.Combine(AppContext.BaseDirectory, "libs", "MaaAgentBinary")
                );

            case MaaControllerTypes.PlayCover:
                LoggerHelper.Info($"PlayCover Address: {Config.PlayCover.PlayCoverAddress}");
                LoggerHelper.Info($"PlayCover BundleId: {Config.PlayCover.UUID}");

                return new MaaPlayCoverController(Config.PlayCover.PlayCoverAddress, Config.PlayCover.UUID);

            case MaaControllerTypes.Win32:

                LoggerHelper.Info($"Name: {Config.DesktopWindow.Name}");
                LoggerHelper.Info($"HWnd: {Config.DesktopWindow.HWnd}");
                LoggerHelper.Info($"ScreenCap: {Config.DesktopWindow.ScreenCap}");
                LoggerHelper.Info($"MouseInput: {Config.DesktopWindow.Mouse}");
                LoggerHelper.Info($"KeyboardInput: {Config.DesktopWindow.KeyBoard}");
                LoggerHelper.Info($"Link: {Config.DesktopWindow.Link}");
                LoggerHelper.Info($"Check: {Config.DesktopWindow.Check}");

                return new MaaWin32Controller(
                    Config.DesktopWindow.HWnd,
                    Config.DesktopWindow.ScreenCap, Config.DesktopWindow.Mouse, Config.DesktopWindow.KeyBoard,
                    Config.DesktopWindow.Link,
                    Config.DesktopWindow.Check);
            case MaaControllerTypes.Dbg:
            default:
                return MaaController.Null;
        }
    }
    public void ConnectToMAA()
    {
        LoggerHelper.Info("Loading MAA Controller Configuration...");
        ConfigureMaaProcessorForADB();
        ConfigureMaaProcessorForWin32();
        ConfigureMaaProcessorForPlayCover();
    }

    private void ConfigureMaaProcessorForADB()
    {
        if (Instances.ToolsViewModel.CurrentController == MaaControllerTypes.Adb)
        {
            var adbInputType = ConfigureAdbInputTypes();
            var adbScreenCapType = ConfigureAdbScreenCapTypes();

            Config.AdbDevice.Input = adbInputType;
            Config.AdbDevice.ScreenCap = adbScreenCapType;
            LoggerHelper.Info(
                $"{LangKeys.AdbInputMode.ToLocalization()}{adbInputType},{LangKeys.AdbCaptureMode.ToLocalization()}{adbScreenCapType}");
        }
    }

    public static string ScreenshotType()
    {
        if (Instances.ToolsViewModel.CurrentController == MaaControllerTypes.Adb)
            return ConfigureAdbScreenCapTypes().ToString();
        return ConfigureWin32ScreenCapTypes().ToString();
    }


    private static AdbInputMethods ConfigureAdbInputTypes()
    {
        return Instances.ConnectSettingsUserControlModel.AdbControlInputType switch
        {
            AdbInputMethods.None => Config.AdbDevice.Info?.InputMethods ?? AdbInputMethods.Default,
            _ => Instances.ConnectSettingsUserControlModel.AdbControlInputType
        };
    }

    private static AdbScreencapMethods ConfigureAdbScreenCapTypes()
    {
        return Instances.ConnectSettingsUserControlModel.AdbControlScreenCapType switch
        {
            AdbScreencapMethods.None => Config.AdbDevice.Info?.ScreencapMethods ?? AdbScreencapMethods.Default,
            _ => Instances.ConnectSettingsUserControlModel.AdbControlScreenCapType
        };
    }

    private void ConfigureMaaProcessorForWin32()
    {
        if (Instances.ToolsViewModel.CurrentController == MaaControllerTypes.Win32)
        {
            var win32MouseInputType = ConfigureWin32MouseInputTypes();
            var win32KeyboardInputType = ConfigureWin32KeyboardInputTypes();
            var winScreenCapType = ConfigureWin32ScreenCapTypes();

            Config.DesktopWindow.Mouse = win32MouseInputType;
            Config.DesktopWindow.KeyBoard = win32KeyboardInputType;
            Config.DesktopWindow.ScreenCap = winScreenCapType;

            LoggerHelper.Info(
                $"{LangKeys.MouseInput.ToLocalization()}:{win32MouseInputType},{LangKeys.KeyboardInput.ToLocalization()}:{win32KeyboardInputType},{LangKeys.AdbCaptureMode.ToLocalization()}{winScreenCapType}");
        }
    }

    private void ConfigureMaaProcessorForPlayCover()
    {
        if (Instances.ToolsViewModel.CurrentController != MaaControllerTypes.PlayCover)
            return;

        // var controller = Interface?.Controller?.FirstOrDefault(c =>
        //     c.Type?.Equals("playcover", StringComparison.OrdinalIgnoreCase) == true);
        //
        // if (!string.IsNullOrWhiteSpace(controller?.PlayCover?.Uuid))
        // {
        //     Config.PlayCover.UUID = controller.PlayCover.Uuid;
        // }
    }

    private static Win32ScreencapMethods ConfigureWin32ScreenCapTypes()
    {
        return Instances.ConnectSettingsUserControlModel.Win32ControlScreenCapType;
    }

    private static Win32InputMethod ConfigureWin32MouseInputTypes()
    {
        return Instances.ConnectSettingsUserControlModel.Win32ControlMouseType;
    }

    private static Win32InputMethod ConfigureWin32KeyboardInputTypes()
    {
        return Instances.ConnectSettingsUserControlModel.Win32ControlKeyboardType;
    }

    public MaaJobStatus PostScreencap()
    {
        var controller = GetScreenshotController();

        if (controller == null || !controller.IsConnected)
            return MaaJobStatus.Invalid;

        try
        {
            return controller.Screencap().Wait();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"PostScreencap failed: {ex.Message}");
            return MaaJobStatus.Invalid;
        }
    }

    public MaaImageBuffer? GetLiveViewBuffer()
    {
        var controller = GetScreenshotController();
        return GetImage(controller, false);
    }

    /// <summary>
    /// 获取截图的MaaImageBuffer。调用者必须负责释放返回的 buffer。
    /// </summary>
    /// <param name="maaController">控制器实例</param>
    /// <param name="screencap">是否主动截图</param>
    /// <returns>包含截图的 MaaImageBuffer，如果失败则返回 null</returns>
    public MaaImageBuffer? GetImage(IMaaController? maaController, bool screencap = true)
    {
        if (maaController == null)
            return null;

        var buffer = new MaaImageBuffer();
        try
        {
            if (screencap)
            {
                var status = maaController.Screencap().Wait();
                if (status != MaaJobStatus.Succeeded)
                {
                    buffer.Dispose();
                    return null;
                }
            }
            if (!maaController.GetCachedImage(buffer))
            {
                buffer.Dispose();
                return null;
            }

            return buffer;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"GetImage failed: {ex.Message}");
            buffer.Dispose();
            return null;
        }
    }

    private IMaaController? GetScreenshotController()
    {
        if (!_isClosed)
            TryConnectAsync(CancellationToken.None);

        return GetTaskerAsync().Result?.Controller;
    }
}
