using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GameBuddy.Converters;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            System.Collections.ICollection c => c.Count == 0,
            _ => false
        };

        return empty ? (Invert ? Visibility.Visible : Visibility.Collapsed)
                     : (Invert ? Visibility.Collapsed : Visibility.Visible);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed ? Invert : !Invert;
}

/// <summary>TimeSpan → "3 小时 24 分钟" 这类中文可读文本。</summary>
public sealed class TimeSpanToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TimeSpan ts) return "—";
        if (ts.TotalMinutes < 1) return "未游玩";

        var hours = (int)ts.TotalHours;
        var minutes = ts.Minutes;
        return hours > 0 ? $"{hours} 小时 {minutes} 分钟" : $"{minutes} 分钟";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public sealed class BytesToTextConverter : IValueConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes || bytes <= 0) return "—";
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {Units[unit]}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>把 0/1/2 之类的结果码换成"成功/未找到/失败"。</summary>
public sealed class MetadataStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            0 => "已就绪",
            1 => "未找到匹配",
            2 => "抓取失败",
            _ => ""
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
