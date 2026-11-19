namespace Forge.Hub.ViewModels.Pages;

/// <summary>Runners (4.13): what this machine can run. Its content and behaviour are 0.4.4's, on the new tokens.</summary>
public sealed class RunnersPageViewModel(MainViewModel hub) : PageViewModel(hub)
{
    public override string Title => "Runners";
}
