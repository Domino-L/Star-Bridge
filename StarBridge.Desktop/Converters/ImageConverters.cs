using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace StarBridge.Desktop;

public sealed class ImagePathConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var bytes = TryReadImageBytes(path);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }

    private static byte[]? TryReadImageBytes(string value)
    {
        if (File.Exists(value))
        {
            return File.ReadAllBytes(value);
        }

        var payload = value.Trim();
        var commaIndex = payload.IndexOf(',');
        if (payload.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
        {
            payload = payload[(commaIndex + 1)..];
        }

        if (payload.Length < 64)
        {
            return null;
        }

        return System.Convert.FromBase64String(payload);
    }
}

public sealed class ImageDataConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string data || string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            var payload = data;
            var commaIndex = payload.IndexOf(',');
            if (payload.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
            {
                payload = payload[(commaIndex + 1)..];
            }

            var bytes = System.Convert.FromBase64String(payload);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
