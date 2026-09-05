using Avalonia.Data.Converters;

namespace Cutback.App.ViewModels;

/// <summary>Static converters for XAML. Lives in the App project; view models never use these.</summary>
public static class Converters
{
    public static readonly IValueConverter PlayPauseLabel =
        new FuncValueConverter<bool, string>(playing => playing ? "Pause" : "Play");
}
