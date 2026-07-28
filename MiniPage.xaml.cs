using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MemeManager.Models;

namespace MemeManager;

public sealed partial class MiniPage : Page
{
    public MiniPage()
    {
        InitializeComponent();
    }

    private void BackToFullButton_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow.SwitchMode(AppMode.Full);
    }
}
