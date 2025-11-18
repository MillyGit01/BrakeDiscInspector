using System;
using OpenCvSharp;

namespace BrakeDiscInspector_GUI_ROI.Helpers
{
    internal static class RoiModelCvExtensions
    {
        public static Rect ToCvRect(this RoiModel roi, int imageWidth, int imageHeight)
        {
            if (roi == null)
            {
                return new Rect();
            }

            double left, top, right, bottom;
            if (roi.Shape == RoiShape.Rectangle)
            {
                left = roi.Left;
                top = roi.Top;
                right = left + roi.Width;
                bottom = top + roi.Height;
            }
            else
            {
                left = roi.CX - roi.R;
                top = roi.CY - roi.R;
                right = roi.CX + roi.R;
                bottom = roi.CY + roi.R;
            }

            int x = (int)Math.Floor(left);
            int y = (int)Math.Floor(top);
            int w = (int)Math.Ceiling(right - x);
            int h = (int)Math.Ceiling(bottom - y);

            int maxWidth = Math.Max(imageWidth - 1, 0);
            int maxHeight = Math.Max(imageHeight - 1, 0);

            x = Math.Clamp(x, 0, maxWidth);
            y = Math.Clamp(y, 0, maxHeight);
            w = Math.Clamp(w, 1, Math.Max(imageWidth - x, 1));
            h = Math.Clamp(h, 1, Math.Max(imageHeight - y, 1));

            return new Rect(x, y, w, h);
        }

        public static Mat ExtractSubMat(this RoiModel roi, Mat image)
        {
            if (roi == null || image == null || image.Empty())
            {
                return new Mat();
            }

            var rect = roi.ToCvRect(image.Width, image.Height);
            return rect.Width > 0 && rect.Height > 0 ? new Mat(image, rect) : new Mat();
        }
    }
}
