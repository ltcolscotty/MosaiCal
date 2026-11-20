using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using Windows.UI;

namespace MosaiCal.Controls;

/// <summary>
/// Reusable color selector: hue/saturation wheel, brightness slider, and exact RGB / hex entry.
/// Drop it into any XAML layout, or use <see cref="ColorSelectorDialog"/> for a pop-up picker.
/// </summary>
public sealed partial class ColorSelector : UserControl
{
    public static readonly Color DefaultColor = Color.FromArgb(255, 58, 58, 58);

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(ColorSelector),
        new PropertyMetadata(DefaultColor, OnColorPropertyChanged));

    public static readonly DependencyProperty HexColorProperty = DependencyProperty.Register(
        nameof(HexColor), typeof(string), typeof(ColorSelector),
        new PropertyMetadata(ToHex(DefaultColor), OnHexColorPropertyChanged));

    public static readonly DependencyProperty IsAlphaEnabledProperty = DependencyProperty.Register(
        nameof(IsAlphaEnabled), typeof(bool), typeof(ColorSelector),
        new PropertyMetadata(false, OnIsAlphaEnabledChanged));

    private bool _isSyncing;

    /// <summary>Raised whenever the selected color changes, from user input or code.</summary>
    public event EventHandler<Color>? ColorChanged;

    public ColorSelector()
    {
        InitializeComponent();
        Picker.Color = Color;
    }

    /// <summary>The selected color.</summary>
    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>
    /// The selected color as "#RRGGBB" (or "#AARRGGBB" when <see cref="IsAlphaEnabled"/> is on),
    /// the same format stored in source configs. Invalid strings are ignored.
    /// </summary>
    public string HexColor
    {
        get => (string)GetValue(HexColorProperty);
        set => SetValue(HexColorProperty, value);
    }

    /// <summary>Shows an opacity slider/input and includes alpha in <see cref="HexColor"/>.</summary>
    public bool IsAlphaEnabled
    {
        get => (bool)GetValue(IsAlphaEnabledProperty);
        set => SetValue(IsAlphaEnabledProperty, value);
    }

    public static string ToHex(Color color, bool includeAlpha = false) => includeAlpha
        ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB" (leading '#' optional).</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = DefaultColor;
        string hex = (text ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) ||
            !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            return false;
        }

        byte alpha = hex.Length == 8 ? (byte)(value >> 24) : (byte)255;
        color = Color.FromArgb(alpha, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    private static void OnColorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ColorSelector)d).ApplyColor((Color)e.NewValue);
    }

    private static void OnHexColorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var selector = (ColorSelector)d;
        if (selector._isSyncing)
        {
            return;
        }

        if (TryParseHex(e.NewValue as string, out Color color))
        {
            selector.Color = color;
        }
        else
        {
            // Snap an invalid hex back to the current color so the two properties never disagree.
            selector.ApplyColor(selector.Color);
        }
    }

    private static void OnIsAlphaEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var selector = (ColorSelector)d;
        bool enabled = (bool)e.NewValue;
        selector.Picker.IsAlphaEnabled = enabled;
        selector.Picker.IsAlphaSliderVisible = enabled;
        selector.Picker.IsAlphaTextInputVisible = enabled;

        if (!enabled && selector.Color.A != 255)
        {
            Color c = selector.Color;
            selector.Color = Color.FromArgb(255, c.R, c.G, c.B);
        }
        else
        {
            selector.ApplyColor(selector.Color);
        }
    }

    // Pushes a color into the inner picker and the hex property, then notifies listeners.
    private void ApplyColor(Color color)
    {
        if (_isSyncing)
        {
            return;
        }

        _isSyncing = true;
        try
        {
            if (Picker.Color != color)
            {
                Picker.Color = color;
            }

            HexColor = ToHex(color, IsAlphaEnabled);
        }
        finally
        {
            _isSyncing = false;
        }

        ColorChanged?.Invoke(this, color);
    }

    private void PickerColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_isSyncing && args.NewColor != Color)
        {
            Color = args.NewColor;
        }
    }
}
