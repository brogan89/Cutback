using Avalonia.Controls;
using Cutback.App.ViewModels;

namespace Cutback.App.Views;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PreferencesViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
            }
        };
    }
}
