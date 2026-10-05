using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NameTool.Infrastructure;

/// <summary>
/// 把图标资源键（<see cref="FileIcon"/> 产出的字符串）解析成 Application 资源里的 DrawingImage。
/// 图标是矢量的，配 <c>Image</c> 的 <c>Stretch="Uniform"</c> 缩放不会糊。
/// </summary>
public sealed class IconLookupConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return null;
        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
