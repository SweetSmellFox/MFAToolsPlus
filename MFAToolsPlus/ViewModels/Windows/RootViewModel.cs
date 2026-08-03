using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Security.Principal;

namespace MFAToolsPlus.ViewModels;

public partial class RootViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isWindowVisible = true;
    
    [ObservableProperty] private string? _windowUpdateInfo = "";
    
    [ObservableProperty] private Action? _tempResourceUpdateAction;
    
    [ObservableProperty] private bool _isUpdating;
    
    [ObservableProperty] private bool _idle = true;

    [ObservableProperty] private bool _isAdmin;
    
    public static string Version => "v1.5.1";

    public RootViewModel()
    {
        IsAdmin = CheckIsAdmin();
    }

    private static bool CheckIsAdmin()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public void SetUpdating(bool isUpdating)
    {
        IsUpdating = isUpdating;
    }
    
    [RelayCommand]
    private void TryUpdate()
    {
        TempResourceUpdateAction?.Invoke();
    }
    
    [RelayCommand]
    public void ToggleVisible()
    {
        IsWindowVisible = !IsWindowVisible;
    }
}
