using Avalonia.Controls;
using Cutback.App.ViewModels;

namespace Cutback.App.Views;

public partial class ExportDialog : Window
{
    public ExportDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ExportViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
            }
        };
        Closing += (_, e) =>
        {
            // The title-bar close button must not abandon a running ffmpeg. Cancel first.
            if (DataContext is ExportViewModel { CanClose: false })
            {
                e.Cancel = true;
            }
        };
    }
}
