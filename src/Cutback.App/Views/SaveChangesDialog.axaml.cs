using Avalonia.Controls;
using Cutback.App.Services;

namespace Cutback.App.Views;

public partial class SaveChangesDialog : Window
{
    public SaveChangesDialog()
    {
        InitializeComponent();
        SaveButton.Click += (_, _) => Close(SaveChoice.Save);
        DiscardButton.Click += (_, _) => Close(SaveChoice.Discard);
        CancelButton.Click += (_, _) => Close(SaveChoice.Cancel);
    }

    public SaveChangesDialog(string projectName)
        : this()
    {
        Message.Text = $"Save changes to “{projectName}” before closing?";
    }
}
