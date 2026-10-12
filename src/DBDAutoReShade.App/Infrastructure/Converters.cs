using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DBDAutoReShade.App.Infrastructure;

/// <summary>A bool as Visibility (Collapsed when false; <see cref="Invert"/> swaps it).</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    /// <summary>Visible when the value is false.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool on = value is bool b && b;
        return on != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v ? (v == Visibility.Visible) != Invert : Binding.DoNothing;
}

/// <summary>
/// True (or Visible, for a Visibility target) when the bound value's name equals the parameter, e.g. an enum value
/// against "Presets"; several names may be given separated by '|'. ConvertBack (two-way, e.g. a RadioButton) returns
/// the parameter parsed as the target enum when checked.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    /// <summary>Matches when the value is NOT one of the names.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? name = value?.ToString();
        string[] names = (parameter as string ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool match = name is not null && names.Contains(name, StringComparer.Ordinal);
        match ^= Invert;
        if (targetType == typeof(Visibility))
        {
            return match ? Visibility.Visible : Visibility.Collapsed;
        }

        return match;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name && targetType.IsEnum && Enum.TryParse(targetType, name, out object? parsed))
        {
            return parsed!;
        }

        return Binding.DoNothing;
    }
}

/// <summary>Converts one enum value to another enum type by name (e.g. a row mark to a status dot kind).</summary>
public sealed class EnumNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value is not null && type.IsEnum && Enum.TryParse(type, value.ToString(), out object? parsed))
        {
            return parsed!;
        }

        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Whether the bound value is set (not null, not a blank string), as a bool or (for a Visibility target) Visible /
/// Collapsed; <see cref="Invert"/> asks whether it is NOT set.
/// </summary>
public sealed class HasValueConverter : IValueConverter
{
    /// <summary>True (visible) when the value is NOT set.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true,
        };
        has ^= Invert;
        return targetType == typeof(Visibility) ? (has ? Visibility.Visible : Visibility.Collapsed) : has;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
