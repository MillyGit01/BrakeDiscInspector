using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrakeDiscInspector_GUI_ROI.Models;
using BrakeDiscInspector_GUI_ROI.Workflow;

namespace BrakeDiscInspector_GUI_ROI.Workflow.DesignTime
{
    internal sealed class DesignWorkflowViewModel
    {
        private const int ThumbnailWidth = 120;
        private const int ThumbnailHeight = 90;

        public DesignWorkflowViewModel()
        {
            BrowseDatasetCommand = new NoOpCommand();
            OpenDatasetFolderCommand = new NoOpCommand();
            TrainSelectedRoiCommand = new NoOpCommand();
            CalibrateSelectedRoiCommand = new NoOpCommand();
            EvaluateSelectedRoiCommand = new NoOpCommand();
            RemoveSelectedCommand = new NoOpCommand();
            InferEnabledRoisCommand = new NoOpCommand();

            InspectionRois = new ObservableCollection<InspectionRoiConfig>
            {
                CreateRoi(1, Colors.SeaGreen, Colors.MediumSeaGreen),
                CreateRoi(2, Colors.SteelBlue, Colors.DodgerBlue),
                CreateRoi(3, Colors.IndianRed, Colors.OrangeRed),
                CreateRoi(4, Colors.Goldenrod, Colors.Gold)
            };

            SelectedInspectionRoi = InspectionRois.First();
            MmPerPx = 0.185;
            HeatmapOpacity = 0.65;
            LocalThreshold = 0.85;
            Regions = new ObservableCollection<Region>
            {
                new Region { x = 12, y = 18, w = 32, h = 24, area_px = 768, area_mm2 = 4.2 },
                new Region { x = 58, y = 44, w = 26, h = 18, area_px = 468, area_mm2 = 2.6 }
            };
        }

        public ObservableCollection<InspectionRoiConfig> InspectionRois { get; }

        public InspectionRoiConfig SelectedInspectionRoi { get; set; }

        public double MmPerPx { get; set; }

        public double HeatmapOpacity { get; set; }

        public double LocalThreshold { get; set; }

        public ObservableCollection<Region> Regions { get; }

        public ICommand BrowseDatasetCommand { get; }

        public ICommand OpenDatasetFolderCommand { get; }

        public ICommand TrainSelectedRoiCommand { get; }

        public ICommand CalibrateSelectedRoiCommand { get; }

        public ICommand EvaluateSelectedRoiCommand { get; }

        public ICommand RemoveSelectedCommand { get; }

        public ICommand InferEnabledRoisCommand { get; }

        private static InspectionRoiConfig CreateRoi(int index, Color okBase, Color ngBase)
        {
            var roi = new InspectionRoiConfig(index)
            {
                DatasetStatus = "Dataset listo",
                DatasetOkCount = 24,
                DatasetKoCount = 6,
                ThresholdDefault = 0.5,
                CalibratedThreshold = 0.72,
                HasFitOk = true
            };

            roi.OkPreview = CreatePreviewCollection("ok", okBase);
            roi.NgPreview = CreatePreviewCollection("ng", ngBase);

            return roi;
        }

        private static ObservableCollection<DatasetPreviewItem> CreatePreviewCollection(string label, Color baseColor)
        {
            var items = new ObservableCollection<DatasetPreviewItem>();
            for (var i = 0; i < 5; i++)
            {
                var accent = Color.FromRgb(
                    (byte)Math.Clamp(baseColor.R + i * 12, 0, 255),
                    (byte)Math.Clamp(baseColor.G + i * 8, 0, 255),
                    (byte)Math.Clamp(baseColor.B + i * 6, 0, 255));
                items.Add(new DatasetPreviewItem
                {
                    Path = $"{label}_sample_{i + 1}.png",
                    Thumbnail = CreateThumbnail(baseColor, accent)
                });
            }

            return items;
        }

        private static BitmapSource CreateThumbnail(Color start, Color end)
        {
            var writeable = new WriteableBitmap(ThumbnailWidth, ThumbnailHeight, 96, 96, PixelFormats.Bgra32, null);
            var stride = ThumbnailWidth * 4;
            var pixels = new byte[ThumbnailHeight * stride];

            for (var y = 0; y < ThumbnailHeight; y++)
            {
                var t = ThumbnailHeight == 1 ? 0 : (double)y / (ThumbnailHeight - 1);
                var r = (byte)(start.R + (end.R - start.R) * t);
                var g = (byte)(start.G + (end.G - start.G) * t);
                var b = (byte)(start.B + (end.B - start.B) * t);

                for (var x = 0; x < ThumbnailWidth; x++)
                {
                    var offset = (y * stride) + (x * 4);
                    pixels[offset] = b;
                    pixels[offset + 1] = g;
                    pixels[offset + 2] = r;
                    pixels[offset + 3] = 255;
                }
            }

            writeable.WritePixels(new System.Windows.Int32Rect(0, 0, ThumbnailWidth, ThumbnailHeight), pixels, stride, 0);
            writeable.Freeze();
            return writeable;
        }

        private sealed class NoOpCommand : ICommand
        {
            public event System.EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter)
            {
            }
        }
    }
}
