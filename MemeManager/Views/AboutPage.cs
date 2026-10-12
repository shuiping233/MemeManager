using MemeManager.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MemeManager.Views;

public static class AboutPage
{
    private static HyperlinkButton MakeLink(string textKey, string uri)
        => new()
        {
            Content = Localization.Get(textKey),
            NavigateUri = new Uri(uri),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4),
        };

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        if (xamlRoot == null) return;

        var logo = new Image
        {
            Width = 48,
            Height = 48,
            Margin = new Thickness(0, 0, 0, 12),
            Source = new BitmapImage(new Uri(AppConstants.IconPath))
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            },
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var desc = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Localization.Get("About_Description"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var license = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Localization.Get("About_License"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var author = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = string.Format(Localization.Get("About_Author"), "shuiping233"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var star = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Localization.Get("About_StarHint"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var link = MakeLink("About_SourceLink", "https://github.com/shuiping233/MemeManager");
        var depUi = MakeLink("About_Dep_MicrosoftUi", "https://github.com/microsoft/microsoft-ui-xaml");
        var depSdk = MakeLink("About_Dep_AppSdk", "https://learn.microsoft.com/windows/apps/windows-app-sdk/");
        var depLoc = MakeLink("About_Dep_Localizer", "https://github.com/AndrewKeepCoding/WinUI3Localizer");

        var panel = new StackPanel { Spacing = 0, MaxWidth = 350 };
        panel.Children.Add(logo);
        panel.Children.Add(desc);
        panel.Children.Add(license);
        panel.Children.Add(author);
        panel.Children.Add(star);
        panel.Children.Add(link);
        panel.Children.Add(depUi);
        panel.Children.Add(depSdk);
        panel.Children.Add(depLoc);

        var dialog = new AppContentDialog
        {
            Title = Localization.Get("Settings_About"),
            Content = panel,
            CloseButtonText = Localization.Get("Dialog_OK"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
            RequestedTheme = DialogHelper.DialogTheme,
        };

        await DialogHelper.SafeShowAsync(dialog);
    }
}
