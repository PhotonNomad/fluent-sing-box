using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Shell;

// Shows an available update once per version, in the window banner and as a tray balloon.
internal sealed class UpdateNotificationPresenter(InfoBar banner, Func<TrayIcon?> trayIcon)
{
    private string? _notifiedUpdate;

    public void Refresh(AppUpdateService service)
    {
        if (!service.NotificationsAllowed)
        {
            banner.IsOpen = false;
            _notifiedUpdate = null;
            return;
        }
        if (service.State.Release is not { } release
            || service.State.Status is not ("readyToDownload" or "readyToInstall") || _notifiedUpdate == release.Version) return;
        _notifiedUpdate = release.Version;
        banner.Message = Localization.Get("Updates_State_readyToDownload") + " " + release.Version;
        banner.IsOpen = true;
        trayIcon()?.NotifyUpdate(banner.Message);
    }
}
