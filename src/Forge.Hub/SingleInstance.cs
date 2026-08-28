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
    private const string ShowEvent = @"Local\AutomationForgeHub.Show";
    private const string QuitEvent = @"Local\AutomationForgeHub.Quit";

    public static void SignalShow() => Signal(ShowEvent);

    /// <summary>Ask the running hub to quit — the installer does, before it replaces the files.</summary>
    public static void SignalQuit() => Signal(QuitEvent);

    /// <summary>True once no instance holds the mutex, or false after the timeout.</summary>
    public static bool WaitForExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var m = Mutex.OpenExisting(MutexName);
            }
            catch (WaitHandleCannotBeOpenedException) { return true; }
            Thread.Sleep(200);
        }
        return false;
    }

    public static void ListenForShow(Action onShow) => Listen(ShowEvent, onShow, "single-instance");
    public static void ListenForQuit(Action onQuit) => Listen(QuitEvent, onQuit, "quit-request");

    private static void Signal(string name)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(name);
            e.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Nobody is listening — no instance, or one between the mutex and the listener.
        }
    }

    private static void Listen(string name, Action action, string threadName)
    {
        var e = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        var thread = new Thread(() =>
        {
            while (true)
            {
                e.WaitOne();
                action();
            }
        }) { IsBackground = true, Name = threadName };
        thread.Start();
    }
}
