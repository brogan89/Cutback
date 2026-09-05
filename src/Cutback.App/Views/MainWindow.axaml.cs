using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cutback.App.Playback;
using Cutback.App.Services;
using Cutback.App.ViewModels;

namespace Cutback.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>
    /// Wires the native video surface. VideoView only hands VLC the native handle when its
    /// MediaPlayer is assigned after the native control exists, so this waits for the window to
    /// open and the first layout pass before assigning.
    /// </summary>
    public void AttachPlayer(IVideoPlayer player)
    {
        if (player is not VlcVideoPlayer vlc)
        {
            return;
        }

        Opened += (_, _) => Dispatcher.UIThread.Post(() => VideoView.MediaPlayer = vlc.MediaPlayer, DispatcherPriority.Loaded);
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return AvaloniaFileDialogService.VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static string? DroppedVideoPath(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return null;
        }

        return files.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null && IsVideoFile(p));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedVideoPath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var path = DroppedVideoPath(e);
        if (path is not null && ViewModel is { } vm)
        {
            await vm.OpenVideoAsync(path);
        }
    }
}
