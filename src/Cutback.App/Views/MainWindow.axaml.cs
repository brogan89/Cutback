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
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closing += OnClosing;
        ShowShortcutHints();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>
    /// Menu shortcut labels. InputGesture is display-only (the bindings live in Window.KeyBindings)
    /// and the modifier name differs per OS, so it is set here rather than in XAML.
    /// </summary>
    private void ShowShortcutHints()
    {
        var mod = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        NewProjectMenuItem.InputGesture = KeyGesture.Parse($"{mod}+N");
        OpenVideoMenuItem.InputGesture = KeyGesture.Parse($"{mod}+O");
        OpenProjectMenuItem.InputGesture = KeyGesture.Parse($"{mod}+Shift+O");
        SaveMenuItem.InputGesture = KeyGesture.Parse($"{mod}+S");
        SaveAsMenuItem.InputGesture = KeyGesture.Parse($"{mod}+Shift+S");
        ExportMenuItem.InputGesture = KeyGesture.Parse($"{mod}+E");
        // Preferences (Cmd+, / Ctrl+,) gets no label: Avalonia renders the key as "OemComma".
        UndoMenuItem.InputGesture = KeyGesture.Parse($"{mod}+Z");
        RedoMenuItem.InputGesture = KeyGesture.Parse($"{mod}+Shift+Z");
    }

    /// <summary>Unsaved changes get a Save / Don't Save / Cancel prompt before the window closes.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || ViewModel is not { IsDirty: true } vm)
        {
            return;
        }

        e.Cancel = true;
        if (await vm.ConfirmDiscardChangesAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

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

    private static bool IsOpenable(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return AvaloniaFileDialogService.VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
            || string.Equals(ext, "cutback", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DroppedVideoPath(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return null;
        }

        return files.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null && IsOpenable(p));
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
        if (path is not null && ViewModel is { } vm && await vm.ConfirmDiscardChangesAsync())
        {
            await vm.OpenVideoAsync(path);
        }
    }
}
