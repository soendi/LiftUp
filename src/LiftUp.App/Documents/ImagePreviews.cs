using System.IO;
using System.Windows.Media.Imaging;

namespace LiftUp.App.Documents;

/// <summary>
/// Kleine Vorschaubilder für Bildlisten. SVG und TIFF kann WPF nicht darstellen;
/// sie bleiben ohne Vorschau, lassen sich aber weiterhin einfügen.
/// </summary>
public static class ImagePreviews
{
    public static BitmapImage? Load(string path, int decodeWidth = 48)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".svg" or ".tif" or ".tiff")
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Vorschaubild als Data-URI für die Liste im Dokumenteditor.</summary>
    public static string? DataUri(string path, int decodeWidth = 48)
    {
        var bitmap = Load(path, decodeWidth);
        if (bitmap is null)
        {
            return null;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);

            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
