using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DropLink;

/// <summary>
/// Turns a history row's kind into a line drawing. Three glyphs, one weight:
/// a thumbnail grid would mean decoding images on the UI thread to show a 32 px
/// box, and a wrong-looking thumbnail is worse than an honest icon.
/// </summary>
internal sealed class KindIconConverter : IValueConverter
{
    private static readonly Geometry Image = Geometry.Parse("M4,5 H20 V19 H4 Z M4,15.5 L9,11 L13.5,15 L17,12 L20,15");
    private static readonly Geometry Video = Geometry.Parse("M3.5,6.5 H14 V17.5 H3.5 Z M14,10.5 L20,7.5 V16.5 L14,13.5 Z");
    private static readonly Geometry File = Geometry.Parse("M6.5,3.5 H14 L19,8.5 V20.5 H6.5 Z M14,3.5 V8.5 H19");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch { "image" => Image, "video" => Video, _ => File };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
