using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cutback.App.Playback;
using Cutback.App.Services;
using Cutback.App.ViewModels;
using Cutback.App.Views;

namespace Cutback.App;

public partial class App : Application
{
    private IVideoPlayer? _player;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new AppSettingsStore();
            settings.Load();

            string? startupError = null;
            try
            {
                LibVlcLocator.Initialize();
                _player = new VlcVideoPlayer();
            }
            catch (LibVlcNotFoundException ex)
            {
                startupError = ex.Message;
                _player = new NullVideoPlayer();
            }

            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(_player, new AvaloniaFileDialogService(window), new AvaloniaDialogService(window), settings)
            {
                ErrorMessage = startupError,
            };
            window.DataContext = viewModel;
            window.AttachPlayer(_player);

            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _player.Dispose();

            // "Open with" / command line: cutback <video or .cutback file>
            var startupFile = desktop.Args?.FirstOrDefault(File.Exists);
            if (startupFile is not null)
            {
                window.Opened += async (_, _) => await viewModel.OpenVideoAsync(startupFile);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
