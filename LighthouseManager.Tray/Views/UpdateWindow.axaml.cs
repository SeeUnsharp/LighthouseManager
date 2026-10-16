using Avalonia.Controls;

namespace LighthouseManager.Tray.Views;

/// <summary>
///     Notice next to the tray when a new version is available; doesn't take focus and stays until "Update now" or
///     "Later".
/// </summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
        // The size is only final after layout
        SizeChanged += (_, _) =>
        {
            if (IsVisible) TrayPlacement.Place(this);
        };
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public void ShowNotice()
    {
        Show();
        TrayPlacement.Place(this);
    }
}
