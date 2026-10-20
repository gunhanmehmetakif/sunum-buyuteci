using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SunumBuyuteci.Tray;

/// <summary>Small read-only list of all hotkeys.</summary>
public sealed class ShortcutsWindow : Window
{
    private static readonly (string Keys, string Action)[] Rows =
    {
        ("Ctrl + Alt + ↑   /   +   /   Tekerlek yukarı", "Yakınlaştır"),
        ("Ctrl + Alt + ↓   /   −   /   Tekerlek aşağı", "Uzaklaştır"),
        ("Ctrl + Alt + 0", "Tam ekran (1x)"),
        ("Ctrl + Alt + K", "Çerçeveyi kilitle / çöz"),
        ("Ctrl + Alt + Q", "Çıkış"),
    };

    public ShortcutsWindow()
    {
        Title = "Kısayollar";
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico"));
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFA));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        panel.Children.Add(new TextBlock
        {
            Text = "Kısayollar", FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), Margin = new Thickness(0, 0, 0, 14),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < Rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock
            {
                Text = Rows[i].Keys, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), Margin = new Thickness(0, 4, 28, 4),
            };
            var action = new TextBlock
            {
                Text = Rows[i].Action, FontSize = 15, Margin = new Thickness(0, 4, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock
        {
            Text = "Sol Alt tuşu kullanılır; AltGr ile çalışmaz.", FontSize = 13, Margin = new Thickness(0, 14, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
        });
        Content = panel;
    }
}
