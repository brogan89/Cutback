using System.Windows.Input;

namespace Cutback.App.ViewModels;

/// <summary>One entry in the File > Open Recent menu.</summary>
public sealed record RecentFileItem(string Path, string Display, ICommand Open);
