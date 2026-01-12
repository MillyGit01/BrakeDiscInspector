using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class DatasetSample
    {
        public DatasetSample(string imagePath, string metadataPath, bool isNg, SampleMetadata metadata)
        {
            ImagePath = imagePath;
            MetadataPath = metadataPath;
            IsNg = isNg;
            Metadata = metadata;
        }

        public string ImagePath { get; }
        public string MetadataPath { get; }
        public bool IsNg { get; }
        public SampleMetadata Metadata { get; }

        private BitmapImage? _thumbnail;

        [JsonIgnore]
        public BitmapImage Thumbnail
        {
            get
            {
                if (_thumbnail != null)
                {
                    return _thumbnail;
                }

                if (!File.Exists(ImagePath))
                {
                    return new BitmapImage();
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ImagePath);
                bitmap.DecodePixelWidth = 160;
                bitmap.EndInit();
                bitmap.Freeze();
                _thumbnail = bitmap;
                return bitmap;
            }
        }

        public static bool TryRead(string imagePath, out DatasetSample? sample)
        {
            sample = null;
            try
            {
                var metadataPath = Path.ChangeExtension(imagePath, ".json");
                if (!File.Exists(imagePath) || !File.Exists(metadataPath))
                {
                    return false;
                }

                var json = File.ReadAllText(metadataPath);
                var metadata = JsonSerializer.Deserialize<SampleMetadata>(json, JsonOptions);
                if (metadata == null)
                {
                    return false;
                }

                var isNg = string.Equals(Path.GetFileName(Path.GetDirectoryName(imagePath)), "ng", StringComparison.OrdinalIgnoreCase);
                sample = new DatasetSample(imagePath, metadataPath, isNg, metadata);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }

    public sealed class SampleMetadata
    {
        public string role_id { get; set; } = string.Empty;
        public string roi_id { get; set; } = string.Empty;
        public string label { get; set; } = string.Empty;
        public string filename { get; set; } = string.Empty;
        public double mm_per_px { get; set; }
        public object? shape_json { get; set; }
        public string? created_at_utc { get; set; }
        public string? source_path { get; set; }
    }
}
