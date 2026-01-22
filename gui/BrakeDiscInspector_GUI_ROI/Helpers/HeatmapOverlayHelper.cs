using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrakeDiscInspector_GUI_ROI.Helpers
{
    public static class HeatmapOverlayHelper
    {
        public static bool TryGetDisplayRect(Image imageControl, out Rect displayRect, out int pixelWidth, out int pixelHeight)
        {
            displayRect = Rect.Empty;
            pixelWidth = 0;
            pixelHeight = 0;

            if (imageControl?.Source is not BitmapSource bmp)
            {
                return false;
            }

            double cw = imageControl.ActualWidth;
            double ch = imageControl.ActualHeight;
            if (bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0 || cw <= 0 || ch <= 0)
            {
                return false;
            }

            double scale = Math.Min(cw / bmp.PixelWidth, ch / bmp.PixelHeight);
            double w = bmp.PixelWidth * scale;
            double h = bmp.PixelHeight * scale;
            double x = (cw - w) * 0.5;
            double y = (ch - h) * 0.5;
            displayRect = new Rect(x, y, w, h);
            pixelWidth = bmp.PixelWidth;
            pixelHeight = bmp.PixelHeight;
            return true;
        }

        public static Rect? UpdateHeatmapOverlay(
            Image overlay,
            FrameworkElement canvasHost,
            Rect displayRect,
            int imagePixelWidth,
            int imagePixelHeight,
            RoiModel roiImageSpace,
            double overlayOpacity,
            bool disableRotation,
            bool skipClip,
            string modeTag,
            Action<string>? log)
        {
            if (overlay == null || canvasHost == null || roiImageSpace == null)
            {
                return null;
            }

            if (displayRect.Width <= 0 || displayRect.Height <= 0 || imagePixelWidth <= 0 || imagePixelHeight <= 0)
            {
                log?.Invoke($"[heatmap:{modeTag}] skip placement (displayRect={displayRect}, imgPx={imagePixelWidth}x{imagePixelHeight}).");
                return null;
            }

            var roiCanvasRect = ConvertRoiImageToCanvasRect(roiImageSpace, displayRect, imagePixelWidth, imagePixelHeight);

            overlay.Width = Math.Max(1.0, roiCanvasRect.Width);
            overlay.Height = Math.Max(1.0, roiCanvasRect.Height);
            overlay.Opacity = overlayOpacity;

            var parent = VisualTreeHelper.GetParent(overlay) as FrameworkElement;
            bool parentIsCanvas = parent is Canvas;
            string parentType = parent?.GetType().Name ?? "<null>";
            Point hostOffset = new Point(0, 0);

            if (!parentIsCanvas && parent != null)
            {
                try
                {
                    hostOffset = canvasHost.TransformToVisual(parent).Transform(new Point(0, 0));
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[heatmap:{modeTag}] offset transform failed: {ex.Message}");
                }
            }

            if (parentIsCanvas)
            {
                overlay.Margin = new Thickness(0);
                Canvas.SetLeft(overlay, roiCanvasRect.Left);
                Canvas.SetTop(overlay, roiCanvasRect.Top);
            }
            else
            {
                double leftRounded = Math.Round(hostOffset.X + roiCanvasRect.Left, MidpointRounding.AwayFromZero);
                double topRounded = Math.Round(hostOffset.Y + roiCanvasRect.Top, MidpointRounding.AwayFromZero);
                overlay.Margin = new Thickness(leftRounded, topRounded, 0, 0);
            }

            double angle = double.IsNaN(roiImageSpace.AngleDeg) ? 0.0 : roiImageSpace.AngleDeg;
            if (disableRotation)
            {
                angle = 0.0;
            }

            var pivotLocal = RoiAdorner.GetRotationPivotLocalPoint(roiImageSpace, overlay.Width, overlay.Height);
            if (overlay.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = angle;
                rotate.CenterX = pivotLocal.X;
                rotate.CenterY = pivotLocal.Y;
            }
            else
            {
                overlay.RenderTransform = new RotateTransform(angle, pivotLocal.X, pivotLocal.Y);
            }

            overlay.Clip = BuildClipGeometry(roiImageSpace, overlay.Width, overlay.Height, skipClip);

            log?.Invoke(
                $"[heatmap:{modeTag}] roiId={roiImageSpace.Id ?? "<null>"} shape={roiImageSpace.Shape} angle={roiImageSpace.AngleDeg:0.##} " +
                $"disableRot={disableRotation} canvasRect=({roiCanvasRect.Left:0.##},{roiCanvasRect.Top:0.##},{roiCanvasRect.Width:0.##},{roiCanvasRect.Height:0.##}) " +
                $"parent={parentType} parentIsCanvas={parentIsCanvas} hostOffset=({hostOffset.X:0.##},{hostOffset.Y:0.##})");

            return roiCanvasRect;
        }

        public static Rect? GetRoiCanvasRect(Rect displayRect, int imagePixelWidth, int imagePixelHeight, RoiModel roiImageSpace)
        {
            if (roiImageSpace == null)
            {
                return null;
            }

            if (displayRect.Width <= 0 || displayRect.Height <= 0 || imagePixelWidth <= 0 || imagePixelHeight <= 0)
            {
                return null;
            }

            return ConvertRoiImageToCanvasRect(roiImageSpace, displayRect, imagePixelWidth, imagePixelHeight);
        }

        private static Rect ConvertRoiImageToCanvasRect(RoiModel roi, Rect displayRect, int imagePixelWidth, int imagePixelHeight)
        {
            double scaleX = displayRect.Width / imagePixelWidth;
            double scaleY = displayRect.Height / imagePixelHeight;
            double k = Math.Min(scaleX, scaleY);

            if (roi.Shape == RoiShape.Circle || roi.Shape == RoiShape.Annulus)
            {
                double cx = displayRect.X + roi.CX * scaleX;
                double cy = displayRect.Y + roi.CY * scaleY;
                double r = roi.R * k;
                return new Rect(cx - r, cy - r, r * 2.0, r * 2.0);
            }

            double w = roi.Width * scaleX;
            double h = roi.Height * scaleY;
            double centerX = displayRect.X + roi.X * scaleX;
            double centerY = displayRect.Y + roi.Y * scaleY;
            return new Rect(centerX - w / 2.0, centerY - h / 2.0, w, h);
        }

        private static Geometry? BuildClipGeometry(RoiModel roi, double overlayWidth, double overlayHeight, bool skipClip)
        {
            if (skipClip)
            {
                return null;
            }

            if (overlayWidth <= 0 || overlayHeight <= 0)
            {
                return null;
            }

            double outerR = Math.Max(overlayWidth, overlayHeight) * 0.5;
            var center = new Point(overlayWidth * 0.5, overlayHeight * 0.5);

            if (roi.R > 0 && roi.RInner > 0)
            {
                double innerR = outerR * (roi.RInner / roi.R);
                var outer = new EllipseGeometry(center, outerR, outerR);
                var inner = new EllipseGeometry(center, innerR, innerR);
                return new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
            }

            if (roi.R > 0)
            {
                return new EllipseGeometry(center, outerR, outerR);
            }

            return null;
        }
    }
}
