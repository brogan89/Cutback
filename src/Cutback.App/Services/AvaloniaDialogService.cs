using Avalonia.Controls;
using Cutback.App.Views;

namespace Cutback.App.Services;

public sealed class AvaloniaDialogService : IDialogService
{
    private readonly Window _owner;

    public AvaloniaDialogService(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task ShowExportAsync(ViewModels.ExportViewModel viewModel)
    {
        var dialog = new ExportDialog { DataContext = viewModel };
        return dialog.ShowDialog(_owner);
    }

    public Task ShowPreferencesAsync(ViewModels.PreferencesViewModel viewModel)
    {
        var window = new PreferencesWindow { DataContext = viewModel };
        return window.ShowDialog(_owner);
    }

    public async Task<SaveChoice> ConfirmSaveChangesAsync(string projectName)
    {
        var dialog = new SaveChangesDialog(projectName);
        var result = await dialog.ShowDialog<SaveChoice?>(_owner);
        return result ?? SaveChoice.Cancel;
    }
}
