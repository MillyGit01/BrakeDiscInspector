using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BrakeDiscInspector_GUI_ROI
{
    public enum RoiShape { Rectangle, Circle, Annulus }

    public enum RoiRole
    {
        Master1Pattern,
        Master1Search,
        Master2Pattern,
        Master2Search,
        Inspection
    }

    public class RoiModel : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string? Label { get; set; }
        public RoiShape Shape { get; set; } = RoiShape.Rectangle;
        public RoiRole Role { get; set; } = RoiRole.Inspection;

        [JsonPropertyName("AngleDeg")]
        public double AngleDeg { get; set; } = 0.0;

        // Permite deserializar tanto AngleDeg como angle_deg (snake_case) sin duplicar la escritura.
        [JsonPropertyName("angle_deg")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? AngleDegSerialized
        {
            get => null;
            set
            {
                if (value.HasValue)
                    AngleDeg = value.Value;
            }
        }

        // Rectángulo (X/Y representan el centro geométrico)
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        [JsonIgnore]
        public double Left
        {
            get => X - Width / 2.0;
            set => X = value + Width / 2.0;
        }

        [JsonIgnore]
        public double Top
        {
            get => Y - Height / 2.0;
            set => Y = value + Height / 2.0;
        }

        // Círculo / Annulus
        public double CX { get; set; }
        public double CY { get; set; }
        public double R { get; set; }      // radio para círculo o radio externo para annulus
        public double RInner { get; set; } // sólo annulus

        [JsonPropertyName("BaseImgW")]
        public double? BaseImgW { get; set; }

        [JsonPropertyName("BaseImgH")]
        public double? BaseImgH { get; set; }

        private bool _isFrozen = true; // default: frozen at app start, only in-memory

        [JsonIgnore]
        public bool IsFrozen
        {
            get => _isFrozen;
            set
            {
                if (_isFrozen == value) return;
                _isFrozen = value;
                OnPropertyChanged(nameof(IsFrozen));
            }
        }

        public (double cx, double cy) GetCenter()
        {
            return Shape switch
            {
                RoiShape.Rectangle => (X, Y),
                RoiShape.Circle or RoiShape.Annulus => (CX, CY),
                _ => (0, 0)
            };
        }

        public RoiModel Clone()
        {
            return new RoiModel
            {
                Id = Id,
                Label = Label,
                Shape = Shape,
                Role = Role,
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                CX = CX,
                CY = CY,
                R = R,
                RInner = RInner,
                AngleDeg = AngleDeg,
                BaseImgW = BaseImgW,
                BaseImgH = BaseImgH
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class Preset
    {
        public List<RoiModel> Rois { get; set; } = new();

        [JsonPropertyName("Inspection1")]
        public RoiModel? Inspection1 { get; set; }

        [JsonPropertyName("Inspection2")]
        public RoiModel? Inspection2 { get; set; }

        // Matching/config
        public int MatchThr { get; set; } = 85; // correlación 0..100
        public int TmThr { get; set; } = 85;
        public int RotRange { get; set; } = 10; // grados (+/-)
        public double ScaleMin { get; set; } = 0.95;
        public double ScaleMax { get; set; } = 1.05;
        public string Feature { get; set; } = "auto"; // auto|sift|orb|tm_rot (sin "geom")
        public int ModelInputSize { get; set; } = 600; // bloqueado a 600
        public double MmPerPx { get; set; } = 0.20;

        // Rutas fijas
        public string Home { get; set; } = @"\\wsl$\Ubuntu\home\millylinux\BrakeDiscDefect_BACKEND";
        public string DataDir { get; set; } = @"\\wsl$\Ubuntu\home\millylinux\BrakeDiscDefect_BACKEND\data";
        public string ModelDir { get; set; } = @"\\wsl$\Ubuntu\home\millylinux\BrakeDiscDefect_BACKEND\model";
        public string PreprocessConfigPath { get; set; } = @"\\wsl$\Ubuntu\home\millylinux\BrakeDiscDefect_BACKEND\preprocessing_config.txt";

    }
}
