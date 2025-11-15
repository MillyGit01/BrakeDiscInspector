using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrakeDiscInspector_GUI_ROI.Imaging
{
    public static class Annotator
    {
        public static void SaveAnnotated(string inputPath, string outputPath, string labelText)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                throw new ArgumentException("Input path is required", nameof(inputPath));
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path is required", nameof(outputPath));
            }

            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException("Source image not found", inputPath);
            }

            var source = LoadBitmap(inputPath);
            if (source == null)
            {
                throw new InvalidOperationException($"Cannot decode source image: {inputPath}");
            }

            var annotated = DrawLabel(source, labelText ?? string.Empty);
            var outDir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outDir))
            {
                throw new InvalidOperationException("Cannot resolve output directory for annotated image.");
            }

            Directory.CreateDirectory(outDir);
            SavePng(annotated, outputPath);
        }

        private static BitmapSource? LoadBitmap(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.CanFreeze && !frame.IsFrozen)
            {
                frame.Freeze();
            }

            return frame;
        }

        private static BitmapSource DrawLabel(BitmapSource src, string text)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));

                var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                double fontSize = Math.Max(16, Math.Min(src.PixelWidth, src.PixelHeight) * 0.035);
                double pixelsPerDip = 1.0;

                var formatted = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.White,
                    pixelsPerDip);

                var margin = 10.0;
                var backgroundRect = new Rect(
                    margin,
                    margin,
                    formatted.Width + 2 * margin,
                    formatted.Height + 2 * margin);

                var background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
                if (background.CanFreeze && !background.IsFrozen)
                {
                    background.Freeze();
                }

                dc.DrawRectangle(background, null, backgroundRect);
                dc.DrawText(formatted, new Point(margin * 2, margin * 2));
            }

            var bitmap = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            if (bitmap.CanFreeze && !bitmap.IsFrozen)
            {
                bitmap.Freeze();
            }

            return bitmap;
        }

        private static void SavePng(BitmapSource src, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(fs);
        }
    }
}
