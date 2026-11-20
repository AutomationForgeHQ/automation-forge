using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.Views.Pages;

/// <summary>
/// The grid's scroll position is the page's to keep: it is taken when a project opens and given back when the grid
/// returns, after the grid has been laid out again (its tiles may have been rebuilt from disk meanwhile). A
/// different project opens at its top; the same project, rebuilt after a change, stays where it was.
/// </summary>
public partial class ProjectsPageView : UserControl
{
    private ProjectsPageViewModel? _vm;
    private Vector _gridOffset;
    private string? _detailPath;

    public ProjectsPageView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ProjectsPageViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null || e.PropertyName != nameof(ProjectsPageViewModel.Selected)) return;
        if (_vm.Selected is { } open)
        {
            // Only on the way in from the grid; a project rebuilt in place has no grid offset to take.
            if (_detailPath is null) _gridOffset = GridScroll.Offset;
            if (!string.Equals(_detailPath, open.Report.Path, StringComparison.OrdinalIgnoreCase)) DetailScroll.Offset = default;
            _detailPath = open.Report.Path;
        }
        else
        {
            _detailPath = null;
            var back = _gridOffset;
            Dispatcher.UIThread.Post(() => GridScroll.Offset = back, DispatcherPriority.Loaded);
        }
    }
}
