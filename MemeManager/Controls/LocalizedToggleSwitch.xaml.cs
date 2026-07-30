using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemeManager.Controls;

// 本地化 ToggleSwitch：On/Off 文字通过内部 ToggleSwitch 的
// l:Uids.Uid="LocalizedToggleSwitch" 自动本地化（取 Resources.resw 的
// LocalizedToggleSwitch.OnContent / .OffContent）。对外暴露 IsOn 与 Toggled，
// 用法与原生 ToggleSwitch 一致。
public sealed partial class LocalizedToggleSwitch : UserControl
{
    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(
            nameof(IsOn),
            typeof(bool),
            typeof(LocalizedToggleSwitch),
            new PropertyMetadata(false));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public event RoutedEventHandler? Toggled;

    public LocalizedToggleSwitch()
    {
        InitializeComponent();
    }

    private void InnerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        Toggled?.Invoke(this, e);
    }
}
