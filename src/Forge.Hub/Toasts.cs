using Microsoft.Toolkit.Uwp.Notifications;

namespace Forge.Hub;

/// <summary>
/// Windows notifications, the ordinary kind — they land in the notification
/// centre and respect focus assist. A toast is a courtesy: if the platform
/// refuses one, the tray tooltip still carries the count and nothing else
/// changes.
/// </summary>
public static class Toasts
{
    public static bool Available => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public static void Show(string title, string body)
    {
        if (!Available) return;
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddText(title)
                .AddText(body)
                .Show();
        }
        catch (Exception)
        {
            // Notification platform unavailable (policy, a stripped-down Windows, a broken registration).
        }
    }

    /// <summary>Clicking a toast raises the hub, whether or not it was running.</summary>
    public static void OnActivated(Action handler)
    {
        if (!Available) return;
        try { ToastNotificationManagerCompat.OnActivated += _ => handler(); }
        catch (Exception) { }
    }

    /// <summary>Remove the registration the toolkit made. Called by the uninstaller.</summary>
    public static void Uninstall()
    {
        if (!Available) return;
        try { ToastNotificationManagerCompat.Uninstall(); }
        catch (Exception) { }
    }
}
