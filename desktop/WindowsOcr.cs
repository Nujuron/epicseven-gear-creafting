using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

internal sealed class WindowsOcr
{
    private readonly OcrEngine engine;

    public string LanguageTag { get { return engine.RecognizerLanguage.LanguageTag; } }

    public WindowsOcr()
    {
        engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ??
            OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
        {
            foreach (var language in OcrEngine.AvailableRecognizerLanguages)
            {
                engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine != null)
                    break;
            }
        }
        if (engine == null)
            throw new InvalidOperationException("No Windows text recognizer is installed. Add English (United States) Basic typing in Windows Language settings.");
    }

    public async Task<string> Read(Bitmap source, Rectangle area, CancellationToken token, bool whiteNumbers = false,
        int? nearX = null, bool yellowNumbers = false)
    {
        token.ThrowIfCancellationRequested();
        bool isolateNumbers = whiteNumbers || yellowNumbers;
        int scale = isolateNumbers ? 3 : 2;
        int padding = isolateNumbers ? 18 : 0;
        using (var scaled = new Bitmap(area.Width * scale + padding * 2, area.Height * scale + padding * 2, PixelFormat.Format32bppArgb))
        using (var bytes = new MemoryStream())
        using (var stream = new InMemoryRandomAccessStream())
        {
            using (var crop = source.Clone(area, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(scaled))
            {
                if (isolateNumbers)
                {
                    graphics.Clear(Color.White);
                    for (int y = 0; y < crop.Height; y++)
                    for (int x = 0; x < crop.Width; x++)
                    {
                        var p = crop.GetPixel(x, y);
                        int min = Math.Min(p.R, Math.Min(p.G, p.B));
                        int max = Math.Max(p.R, Math.Max(p.G, p.B));
                        bool ink = yellowNumbers
                            ? p.R >= 178 && p.G >= 141 && p.R - p.G <= 82 && p.G - p.B >= 37
                            : min >= 168 && max - min <= 38;
                        crop.SetPixel(x, y, ink ? Color.Black : Color.White);
                    }
                }
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(crop, new Rectangle(padding, padding, area.Width * scale, area.Height * scale),
                    new Rectangle(Point.Empty, crop.Size), GraphicsUnit.Pixel);
            }
            scaled.Save(bytes, ImageFormat.Bmp);
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes.ToArray());
                await writer.StoreAsync().AsTask(token).ConfigureAwait(false);
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
            using (var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore).AsTask(token).ConfigureAwait(false))
            {
                var result = await engine.RecognizeAsync(bitmap).AsTask(token).ConfigureAwait(false);
                if (nearX.HasValue)
                {
                    return result.Lines.SelectMany(line => line.Words)
                        .Where(word => Math.Abs(area.X + (word.BoundingRect.X - padding) / scale - nearX.Value) <= 12)
                        .Select(word => word.Text).FirstOrDefault() ?? "";
                }
                return result.Text;
            }
        }
    }
}
