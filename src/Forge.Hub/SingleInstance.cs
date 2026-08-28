namespace Forge.Hub;

/// <summary>
/// One hub per session. The first instance owns a mutex and listens on a named
/// event; any later launch — a second click on the shortcut, a toast, the
/// installer bringing the hub back — pokes the event and exits, and the running
/// hub raises its window.
/// </summary>
public static class SingleInstance
{
    public const string MutexName = @"Local\AutomationForgeHub";
    private const string EventName = @"Local\AutomationForgeHub.Show";

    public static void SignalShow()
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(EventName);
            e.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The owner is between the mutex and the listener; nothing to raise yet.
        }
    }

    public static void ListenForShow(Action onShow)
    {
        var e = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                e.WaitOne();
                onShow();
            }
        }) { IsBackground = true, Name = "single-instance" };
        thread.Start();
    }
}
