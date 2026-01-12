using System.Collections.Generic;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class DatasetListResponse
    {
        public DatasetListClasses? classes { get; set; }
    }

    public sealed class DatasetListClasses
    {
        public DatasetClassDto? ok { get; set; }
        public DatasetClassDto? ng { get; set; }
    }

    public sealed class DatasetListDto
    {
        public DatasetClassDto ok { get; set; } = new();
        public DatasetClassDto ng { get; set; } = new();
    }

    public sealed class DatasetClassDto
    {
        public int count { get; set; }
        public List<string> files { get; set; } = new();
        public Dictionary<string, bool> meta { get; set; } = new();
    }

    public sealed class CalibrateDatasetResponse
    {
        public string? status { get; set; }
        public double threshold { get; set; }
        public int score_percentile { get; set; }
        public double area_mm2_thr { get; set; }
        public int n_ok { get; set; }
        public int n_ng { get; set; }
        public string? request_id { get; set; }
        public string? recipe_id { get; set; }
        public string? role_id { get; set; }
        public string? roi_id { get; set; }
        public string? model_key { get; set; }
    }

    public sealed class InferDatasetResponse
    {
        public string? status { get; set; }
        public string? role_id { get; set; }
        public string? roi_id { get; set; }
        public string? recipe_id { get; set; }
        public string? model_key { get; set; }
        public string? request_id { get; set; }
        public int n_total { get; set; }
        public int n_errors { get; set; }
        public List<InferDatasetItem> items { get; set; } = new();
    }

    public sealed class InferDatasetItem
    {
        public string? label { get; set; }
        public string? filename { get; set; }
        public double? mm_per_px { get; set; }
        public double? score { get; set; }
        public double? threshold { get; set; }
        public List<Region>? regions { get; set; }
        public int n_regions { get; set; }
        public string? heatmap_png_base64 { get; set; }
        public string? error { get; set; }
    }
}
