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

    public async Task<SaveChoice> ConfirmSaveChangesAsync(string projectName)
    {
        var dialog = new SaveChangesDialog(projectName);
        var result = await dialog.ShowDialog<SaveChoice?>(_owner);
        return result ?? SaveChoice.Cancel;
    }
}
