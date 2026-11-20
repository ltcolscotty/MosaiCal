using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.UI;

namespace MosaiCal.Controls;

/// <summary>
/// Shows a <see cref="ColorSelector"/> in a modal dialog.
/// <code>
/// string? hex = await ColorSelectorDialog.PickHexAsync(Content.XamlRoot, card.BackgroundColor);
/// if (hex != null) { /* save it */ }
/// </code>
/// </summary>
public static class ColorSelectorDialog
{
    /// <summary>Returns the chosen color, or null if the user cancelled.</summary>
    public static async Task<Color?> PickAsync(
        XamlRoot xamlRoot,
        Color initialColor,
        string title = "Choose a color",
        bool isAlphaEnabled = false)
    {
        var selector = new ColorSelector
        {
            IsAlphaEnabled = isAlphaEnabled,
            Color = initialColor,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new ScrollViewer { Content = selector },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? selector.Color : null;
    }

    /// <summary>
    /// Hex-string convenience overload matching the "#RRGGBB" format used in source configs.
    /// An unparsable <paramref name="initialHex"/> starts from <see cref="ColorSelector.DefaultColor"/>.
    /// Returns null if the user cancelled.
    /// </summary>
    public static async Task<string?> PickHexAsync(
        XamlRoot xamlRoot,
        string? initialHex,
        string title = "Choose a color",
        bool isAlphaEnabled = false)
    {
        ColorSelector.TryParseHex(initialHex, out Color initial);
        Color? picked = await PickAsync(xamlRoot, initial, title, isAlphaEnabled);
        return picked is Color color ? ColorSelector.ToHex(color, isAlphaEnabled) : null;
    }
}
