using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClimationChecker.App;

internal sealed class DonutOnlyWindow : Window
{
    private readonly Image _image;

    public DonutOnlyWindow()
    {
        Title = "DonutScope Donut Ring";
        Width = 560;
        Height = 560;
        MinWidth = 250;
        MinHeight = 250;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E141A"));
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app-icon.ico"));
        SourceInitialized += (_, _) => WindowTheme.ApplyDarkTitleBar(this);

        var border = new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#05090D")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#263442")),
            BorderThickness = new Thickness(1),
        };

        _image = new Image
        {
            Stretch = Stretch.Uniform,
        };

        border.Child = _image;
        Content = border;
    }

    public void SetImage(ImageSource? image)
    {
        _image.Source = image;
    }
}
