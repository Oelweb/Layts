using System.Windows;
using Layts.Core;
using Layts.ViewModels;

namespace Layts;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        SourceInitialized += (_, _) => WindowTheme.ApplyDarkTitleBar(this);
        StateChanged += (_, _) => UpdateState();
        FitToScreen();
    }

    /// <summary>Auf kleineren Bildschirmen nicht hinter der Taskleiste verschwinden.</summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width - 40);
        Height = Math.Min(Height, area.Height - 40);
    }

    private void UpdateState()
    {
        bool max = WindowState == WindowState.Maximized;
        // Maximierte Fenster ragen sonst über den Bildschirmrand hinaus.
        Root.Margin = max ? new Thickness(7) : new Thickness(0);
        MaxIcon.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
        MaxButton.ToolTip = Loc.Get(max ? "Restore" : "Maximize");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
