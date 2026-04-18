using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;
using pdfSignr.ViewModels;

namespace pdfSignr.Views.Converters;

/// <summary>
/// Maps a <see cref="ToolMode"/> to a placement <see cref="Cursor"/>. Kept on the view side
/// so the ViewModel does not have to reference Avalonia UI types.
/// </summary>
public class ToolToCursorConverter : IValueConverter
{
    public static readonly ToolToCursorConverter Instance = new();
    private static readonly Cursor DragMoveCursor = new(StandardCursorType.DragMove);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ToolMode mode && mode is ToolMode.Text or ToolMode.Signature ? DragMoveCursor : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
