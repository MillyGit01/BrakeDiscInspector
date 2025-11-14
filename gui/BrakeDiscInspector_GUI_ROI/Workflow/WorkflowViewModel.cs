using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrakeDiscInspector_GUI_ROI;
using BrakeDiscInspector_GUI_ROI.Util;
using BrakeDiscInspector_GUI_ROI.Models;
using Forms = System.Windows.Forms;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class BatchRow : INotifyPropertyChanged
    {
        private BatchCellStatus _roi1 = BatchCellStatus.Unknown;
        private BatchCellStatus _roi2 = BatchCellStatus.Unknown;
        private BatchCellStatus _roi3 = BatchCellStatus.Unknown;
        private BatchCellStatus _roi4 = BatchCellStatus.Unknown;

        public BatchRow(string fullPath)
        {
            FullPath = fullPath;
            FileName = Path.GetFileName(fullPath);
        }

        public string FileName { get; }
        public string FullPath { get; }

        public BatchCellStatus ROI1
        {
            get => _roi1;
            set
            {
                if (_roi1 != value)
                {
                    _roi1 = value;
                    OnPropertyChanged();
                }
            }
        }

        public BatchCellStatus ROI2
        {
            get => _roi2;
            set
            {
                if (_roi2 != value)
                {
                    _roi2 = value;
                    OnPropertyChanged();
                }
            }
        }

        public BatchCellStatus ROI3
        {
            get => _roi3;
            set
            {
                if (_roi3 != value)
                {
                    _roi3 = value;
                    OnPropertyChanged();
                }
            }
        }

        public BatchCellStatus ROI4
        {
            get => _roi4;
            set
            {
                if (_roi4 != value)
                {
                    _roi4 = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class WorkflowViewModel : INotifyPropertyChanged
    {
        private readonly BackendClient _client;
        private readonly DatasetManager _datasetManager;
        private readonly Func<Task<RoiExportResult?>> _exportRoiAsync;
        private readonly Func<string?> _getSourceImagePath;
        private readonly Action<string> _log;
        private readonly Func<RoiExportResult, byte[], double, Task> _showHeatmapAsync;
        private readonly Action _clearHeatmap;
        private readonly Action<bool?> _updateGlobalBadge;
        private readonly Action<int>? _activateInspectionIndex;
        private readonly Func<string, CancellationToken, Task>? _repositionInspectionRoisAsync;

        private ObservableCollection<InspectionRoiConfig>? _inspectionRois;
        private RoiModel? _inspection1;
        private RoiModel? _inspection2;
        private RoiModel? _inspection3;
        private RoiModel? _inspection4;
        private InspectionRoiConfig? _selectedInspectionRoi;
        private bool? _hasFitEndpoint;
        private bool? _hasCalibrateEndpoint;

        private bool _isBusy;
        private bool _isImageLoaded;
        private string _roleId = "Master1";
        private string _roiId = "Inspection";
        private double _mmPerPx = 0.20;
        private string _backendBaseUrl;
        private string _fitSummary = "";
        private string _calibrationSummary = "";
        private double? _inferenceScore;
        private double? _inferenceThreshold;
        private double _localThreshold;
        private string _inferenceSummary = string.Empty;
        private double _heatmapOpacity = 0.6;
        private string _healthSummary = "";

        private bool _showMaster1Pattern = true;
        private bool _showMaster1Inspection = true;
        private bool _showMaster2Pattern = true;
        private bool _showMaster2Inspection = true;
        private bool _showInspectionRoi = true;

        private RoiExportResult? _lastExport;
        private byte[]? _lastHeatmapBytes;
        private InferResult? _lastInferResult;
        private CancellationTokenSource? _batchCts;
        private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
        private bool _isBatchRunning;
        private readonly ObservableCollection<BatchRow> _batchRows = new();
        private string? _batchFolder;
        private bool _canStartBatch;
        private string _batchSummary = string.Empty;
        private string _batchStatus = string.Empty;
        private ImageSource? _batchImageSource;
        private ImageSource? _batchHeatmapSource;
        private int _heatmapCutoffPercent = 50;
        private string _heatmapInfo = "Cutoff: 50%";
        private double _batchPausePerRoiSeconds = 0.0;
        private byte[]? _lastHeatmapPngBytes;
        private WriteableBitmap? _lastHeatmapBitmap;
        private int _batchHeatmapRoiIndex = 0;
        private double _baseImageActualWidth;
        private double _baseImageActualHeight;
        private double _canvasRoiActualWidth;
        private double _canvasRoiActualHeight;
        private int _baseImagePixelWidth;
        private int _baseImagePixelHeight;
        private bool _useCanvasPlacementForBatchHeatmap = true;
        private readonly Dictionary<InspectionRoiConfig, RoiDatasetAnalysis> _roiDatasetCache = new();
        private readonly Dictionary<InspectionRoiConfig, FitOkResult> _lastFitResultsByRoi = new();
        private readonly Dictionary<InspectionRoiConfig, CalibResult> _lastCalibResultsByRoi = new();
        private readonly Func<InspectionRoiConfig, string?>? _resolveModelDirectory;
        private readonly object _initLock = new();
        private Task? _initializationTask;
        private bool _initialized;
        private bool _isInitializing;

        private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        private static readonly HashSet<string> BatchImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".tif",
            ".tiff",
            ".webp"
        };

        public WorkflowViewModel(
            BackendClient client,
            DatasetManager datasetManager,
            Func<Task<RoiExportResult?>> exportRoiAsync,
            Func<string?> getSourceImagePath,
            Action<string> log,
            Func<RoiExportResult, byte[], double, Task> showHeatmapAsync,
            Action clearHeatmap,
            Action<bool?> updateGlobalBadge,
            Action<int>? activateInspectionIndex = null,
            Func<InspectionRoiConfig, string?>? resolveModelDirectory = null,
            Func<string, CancellationToken, Task>? repositionInspectionRoisAsync = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _datasetManager = datasetManager ?? throw new ArgumentNullException(nameof(datasetManager));
            _exportRoiAsync = exportRoiAsync ?? throw new ArgumentNullException(nameof(exportRoiAsync));
            _getSourceImagePath = getSourceImagePath ?? throw new ArgumentNullException(nameof(getSourceImagePath));
            _log = log ?? (_ => { });
            _showHeatmapAsync = showHeatmapAsync ?? throw new ArgumentNullException(nameof(showHeatmapAsync));
            _clearHeatmap = clearHeatmap ?? throw new ArgumentNullException(nameof(clearHeatmap));
            _updateGlobalBadge = updateGlobalBadge ?? (_ => { });
            _activateInspectionIndex = activateInspectionIndex;
            _resolveModelDirectory = resolveModelDirectory;
            _repositionInspectionRoisAsync = repositionInspectionRoisAsync;

            _backendBaseUrl = _client.BaseUrl;

            OkSamples = new ObservableCollection<DatasetSample>();
            NgSamples = new ObservableCollection<DatasetSample>();

            AddOkFromCurrentRoiCommand = CreateCommand(_ => AddSampleAsync(isNg: false));
            AddNgFromCurrentRoiCommand = CreateCommand(_ => AddSampleAsync(isNg: true));
            RemoveSelectedCommand = CreateCommand(_ => RemoveSelectedAsync(), _ => !IsBusy && (SelectedOkSample != null || SelectedNgSample != null));
            OpenDatasetFolderCommand = CreateCommand(_ => OpenDatasetFolderAsync(), _ => !IsBusy && SelectedInspectionRoi?.HasDatasetPath == true);
            TrainFitCommand = CreateCommand(_ => TrainAsync(), _ => !IsBusy && OkSamples.Count > 0);
            CalibrateCommand = CreateCommand(_ => CalibrateAsync(), _ => !IsBusy && OkSamples.Count > 0);
            InferFromCurrentRoiCommand = CreateCommand(_ => InferCurrentAsync(), _ => !IsBusy);
            RefreshDatasetCommand = CreateCommand(_ => RefreshDatasetAsync(), _ => !IsBusy);
            RefreshHealthCommand = CreateCommand(_ => RefreshHealthAsync(), _ => !IsBusy);

            BrowseBatchFolderCommand = new AsyncCommand(_ =>
            {
                DoBrowseBatchFolder();
                return Task.CompletedTask;
            }, _ => !IsBusy);
            StartBatchCommand = new AsyncCommand(_ =>
            {
                if (_isBatchRunning)
                {
                    _pauseGate.Set();
                    SetBatchStatusSafe("Running");
                    UpdateBatchCommandStates();
                    return Task.CompletedTask;
                }

                if (!CanStartBatch || IsBusy)
                {
                    return Task.CompletedTask;
                }

                var snapshot = GetBatchRowsSnapshot();
                InvokeOnUi(() =>
                {
                    _batchRows.Clear();
                    foreach (var row in snapshot)
                    {
                        row.ROI1 = BatchCellStatus.Unknown;
                        row.ROI2 = BatchCellStatus.Unknown;
                        row.ROI3 = BatchCellStatus.Unknown;
                        row.ROI4 = BatchCellStatus.Unknown;
                        _batchRows.Add(row);
                    }

                    OnPropertyChanged(nameof(BatchRows));
                });

                _batchCts?.Cancel();
                _batchCts?.Dispose();
                _batchCts = new CancellationTokenSource();
                _pauseGate.Set();
                _isBatchRunning = true;
                IsBusy = true;
                UpdateBatchCommandStates();
                SetBatchStatusSafe("Running");

                var runTask = RunBatchAsync(_batchCts.Token);
                runTask.ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        _log($"[batch] run failed: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
                    }
                }, TaskScheduler.Default);

                return Task.CompletedTask;
            }, _ => CanExecuteStart());

            PauseBatchCommand = new AsyncCommand(_ =>
            {
                if (_isBatchRunning && _pauseGate.IsSet)
                {
                    _pauseGate.Reset();
                    SetBatchStatusSafe("Paused");
                    UpdateBatchCommandStates();
                }

                return Task.CompletedTask;
            }, _ => _isBatchRunning && _pauseGate.IsSet);

            StopBatchCommand = new AsyncCommand(_ =>
            {
                StopBatch();
                return Task.CompletedTask;
            }, _ => _isBatchRunning);

            BrowseDatasetCommand = CreateCommand(_ => BrowseDatasetAsync(), _ => !IsBusy && SelectedInspectionRoi != null);
            TrainSelectedRoiCommand = CreateCommand(async _ => await TrainSelectedRoiAsync().ConfigureAwait(false), _ => !IsBusy && SelectedInspectionRoi != null);
            CalibrateSelectedRoiCommand = CreateCommand(async _ => await CalibrateSelectedRoiAsync().ConfigureAwait(false), _ => !IsBusy && CanCalibrateSelectedRoi());
            EvaluateSelectedRoiCommand = CreateCommand(_ => EvaluateSelectedRoiAsync(), _ => !IsBusy && SelectedInspectionRoi != null && SelectedInspectionRoi.Enabled);
            var inferEnabledCommand = CreateCommand(_ => InferEnabledRoisAsync(), _ => !IsBusy && HasAnyEnabledInspectionRoi());
            EvaluateAllRoisCommand = inferEnabledCommand;
            InferEnabledRoisCommand = inferEnabledCommand;

            UpdateCanStart();
            UpdateHeatmapThreshold();

            AddRoiToDatasetOkCommand = new AsyncCommand(async param =>
            {
                if (param is InspectionRoiConfig roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, isOk: true)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);

            AddRoiToDatasetNgCommand = new AsyncCommand(async param =>
            {
                if (param is InspectionRoiConfig roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, isOk: false)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);

            AddRoiToOkCommand = new AsyncCommand(async param =>
            {
                if (param is RoiModel roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, positive: true)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);

            AddRoiToNgCommand = new AsyncCommand(async param =>
            {
                if (param is RoiModel roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, positive: false)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);

            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ShowMaster1Pattern)
                    || e.PropertyName == nameof(ShowMaster1Inspection)
                    || e.PropertyName == nameof(ShowMaster2Pattern)
                    || e.PropertyName == nameof(ShowMaster2Inspection)
                    || e.PropertyName == nameof(ShowInspectionRoi))
                {
                    Application.Current?.Dispatcher.Invoke(RedrawOverlays);
                }
            };
        }

        public ObservableCollection<DatasetSample> OkSamples { get; }
        public ObservableCollection<DatasetSample> NgSamples { get; }

        public ObservableCollection<InspectionRoiConfig> InspectionRois { get; private set; } = new();

        public ObservableCollection<BatchRow> BatchRows => _batchRows;

        public string? BatchFolder
        {
            get => _batchFolder;
            set
            {
                if (!string.Equals(_batchFolder, value, StringComparison.Ordinal))
                {
                    _batchFolder = value;
                    OnPropertyChanged();
                    UpdateCanStart();
                }
            }
        }

        public bool CanStartBatch
        {
            get => _canStartBatch;
            private set
            {
                if (_canStartBatch != value)
                {
                    _canStartBatch = value;
                    OnPropertyChanged();
                    UpdateBatchCommandStates();
                }
            }
        }

        public string BatchSummary
        {
            get => _batchSummary;
            private set
            {
                if (!string.Equals(_batchSummary, value, StringComparison.Ordinal))
                {
                    _batchSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public string BatchStatus
        {
            get => _batchStatus;
            private set
            {
                if (!string.Equals(_batchStatus, value, StringComparison.Ordinal))
                {
                    _batchStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public ImageSource? BatchImageSource
        {
            get => _batchImageSource;
            private set
            {
                if (!Equals(_batchImageSource, value))
                {
                    _batchImageSource = value;
                    OnPropertyChanged();
                }
            }
        }

        public ImageSource? BatchHeatmapSource
        {
            get => _batchHeatmapSource;
            private set
            {
                if (!Equals(_batchHeatmapSource, value))
                {
                    _batchHeatmapSource = value;
                    OnPropertyChanged();
                }
            }
        }

        public int BatchHeatmapRoiIndex
        {
            get => _batchHeatmapRoiIndex;
            private set
            {
                if (_batchHeatmapRoiIndex != value)
                {
                    _batchHeatmapRoiIndex = value;
                    OnPropertyChanged();
                }
                else
                {
                    OnPropertyChanged(nameof(BatchHeatmapRoiIndex));
                }
            }
        }

        public double BaseImageActualWidth
        {
            get => _baseImageActualWidth;
            set
            {
                if (Math.Abs(_baseImageActualWidth - value) <= 0.01)
                {
                    return;
                }

                _baseImageActualWidth = value;
                OnPropertyChanged();
            }
        }

        public double BaseImageActualHeight
        {
            get => _baseImageActualHeight;
            set
            {
                if (Math.Abs(_baseImageActualHeight - value) <= 0.01)
                {
                    return;
                }

                _baseImageActualHeight = value;
                OnPropertyChanged();
            }
        }

        public double CanvasRoiActualWidth
        {
            get => _canvasRoiActualWidth;
            set
            {
                if (Math.Abs(_canvasRoiActualWidth - value) <= 0.01)
                {
                    return;
                }

                _canvasRoiActualWidth = value;
                OnPropertyChanged();
            }
        }

        public double CanvasRoiActualHeight
        {
            get => _canvasRoiActualHeight;
            set
            {
                if (Math.Abs(_canvasRoiActualHeight - value) <= 0.01)
                {
                    return;
                }

                _canvasRoiActualHeight = value;
                OnPropertyChanged();
            }
        }

        public int BaseImagePixelWidth
        {
            get => _baseImagePixelWidth;
            set
            {
                if (_baseImagePixelWidth == value)
                {
                    return;
                }

                _baseImagePixelWidth = value;
                OnPropertyChanged();
            }
        }

        public int BaseImagePixelHeight
        {
            get => _baseImagePixelHeight;
            set
            {
                if (_baseImagePixelHeight == value)
                {
                    return;
                }

                _baseImagePixelHeight = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Toggle to revert to the legacy placement logic when investigating regressions.
        /// Bind this from XAML (e.g. a CheckBox) for quick rollback tests.
        /// </summary>
        public bool UseCanvasPlacementForBatchHeatmap
        {
            get => _useCanvasPlacementForBatchHeatmap;
            set
            {
                if (_useCanvasPlacementForBatchHeatmap == value)
                {
                    return;
                }

                _useCanvasPlacementForBatchHeatmap = value;
                OnPropertyChanged();
            }
        }

        public int HeatmapCutoffPercent
        {
            get => _heatmapCutoffPercent;
            set
            {
                var clamped = Math.Clamp(value, 0, 100);
                if (_heatmapCutoffPercent != clamped)
                {
                    _heatmapCutoffPercent = clamped;
                    OnPropertyChanged();
                    UpdateHeatmapThreshold();
                }
            }
        }

        public string HeatmapInfo
        {
            get => _heatmapInfo;
            private set
            {
                if (!string.Equals(_heatmapInfo, value, StringComparison.Ordinal))
                {
                    _heatmapInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public double BatchPausePerRoiSeconds
        {
            get => _batchPausePerRoiSeconds;
            set
            {
                var clamped = Math.Min(10.0, Math.Max(0.0, value));
                if (Math.Abs(_batchPausePerRoiSeconds - clamped) > 1e-6)
                {
                    _batchPausePerRoiSeconds = clamped;
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowMaster1Pattern
        {
            get => _showMaster1Pattern;
            set
            {
                if (_showMaster1Pattern == value)
                {
                    return;
                }

                _showMaster1Pattern = value;
                OnPropertyChanged(nameof(ShowMaster1Pattern));
            }
        }

        public bool ShowMaster1Inspection
        {
            get => _showMaster1Inspection;
            set
            {
                if (_showMaster1Inspection == value)
                {
                    return;
                }

                _showMaster1Inspection = value;
                OnPropertyChanged(nameof(ShowMaster1Inspection));
            }
        }

        public bool ShowMaster2Pattern
        {
            get => _showMaster2Pattern;
            set
            {
                if (_showMaster2Pattern == value)
                {
                    return;
                }

                _showMaster2Pattern = value;
                OnPropertyChanged(nameof(ShowMaster2Pattern));
            }
        }

        public bool ShowMaster2Inspection
        {
            get => _showMaster2Inspection;
            set
            {
                if (_showMaster2Inspection == value)
                {
                    return;
                }

                _showMaster2Inspection = value;
                OnPropertyChanged(nameof(ShowMaster2Inspection));
            }
        }

        public bool ShowInspectionRoi
        {
            get => _showInspectionRoi;
            set
            {
                if (_showInspectionRoi == value)
                {
                    return;
                }

                _showInspectionRoi = value;
                OnPropertyChanged(nameof(ShowInspectionRoi));
            }
        }

        public bool IsImageLoaded
        {
            get => _isImageLoaded;
            set
            {
                if (_isImageLoaded == value)
                {
                    return;
                }

                _isImageLoaded = value;
                OnPropertyChanged();
            }
        }

        public RoiModel? Inspection1
        {
            get => _inspection1;
            private set => SetInspectionRoi(ref _inspection1, value, nameof(Inspection1));
        }

        public RoiModel? Inspection2
        {
            get => _inspection2;
            private set => SetInspectionRoi(ref _inspection2, value, nameof(Inspection2));
        }

        public RoiModel? Inspection3
        {
            get => _inspection3;
            private set => SetInspectionRoi(ref _inspection3, value, nameof(Inspection3));
        }

        public RoiModel? Inspection4
        {
            get => _inspection4;
            private set => SetInspectionRoi(ref _inspection4, value, nameof(Inspection4));
        }

        public InspectionRoiConfig? SelectedInspectionRoi
        {
            get => _selectedInspectionRoi;
            set
            {
                if (!ReferenceEquals(_selectedInspectionRoi, value))
                {
                    _selectedInspectionRoi = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedInspectionShape));
                    OnPropertyChanged(nameof(ActiveInspectionRoiModel));
                    OnPropertyChanged(nameof(ActiveInspectionRoiImageRectPx));
                    UpdateSelectedRoiState();
                    OpenDatasetFolderCommand.RaiseCanExecuteChanged();

                    if (value != null)
                    {
                        RoleId = "Inspection";
                        var normalizedKey = NormalizeInspectionKey(value.ModelKey, value.Index);
                        if (!string.Equals(RoiId, normalizedKey, StringComparison.Ordinal))
                        {
                            RoiId = normalizedKey;
                        }
                        if (!string.Equals(value.ModelKey, normalizedKey, StringComparison.Ordinal))
                        {
                            value.ModelKey = normalizedKey;
                        }

                        _log($"[ui] SelectedInspectionRoi -> {value.DisplayName} idx={value.Index} key={value.ModelKey}");
                        try
                        {
                            LocalThreshold = value.CalibratedThreshold ?? value.ThresholdDefault;
                            _log($"[ui] LocalThreshold <- {LocalThreshold:0.###} (SelectedInspectionRoi)");
                        }
                        catch
                        {
                            // tolerate missing properties on older builds
                        }

                        ClearInferenceUi("[ui] selection changed");
                        _ = ActivateCanvasIndexAsync(value.Index);
                    }
                }
            }
        }

        // Si SelectedInspectionRoi es null => "square"; si no, usa el enum (no anulable) y conviértelo a string.
        public string SelectedInspectionShape =>
            SelectedInspectionRoi != null
                ? SelectedInspectionRoi.Shape.ToString().ToLowerInvariant()
                : "square";

        public RoiModel? ActiveInspectionRoiModel => GetActiveInspectionRoiModel();

        public Int32Rect? ActiveInspectionRoiImageRectPx
        {
            get
            {
                var roi = ActiveInspectionRoiModel;
                return roi != null ? BuildImageRectPx(roi) : null;
            }
        }

        public void SetInspectionRoisCollection(ObservableCollection<InspectionRoiConfig>? rois)
        {
            if (ReferenceEquals(_inspectionRois, rois))
            {
                return;
            }

            if (_inspectionRois != null)
            {
                _inspectionRois.CollectionChanged -= InspectionRoisCollectionChanged;
                foreach (var roi in _inspectionRois)
                {
                    roi.PropertyChanged -= InspectionRoiPropertyChanged;
                }
            }

            _inspectionRois = rois;
            InspectionRois = rois ?? new ObservableCollection<InspectionRoiConfig>();
            OnPropertyChanged(nameof(InspectionRois));

            if (_inspectionRois != null)
            {
                _inspectionRois.CollectionChanged += InspectionRoisCollectionChanged;
                foreach (var roi in _inspectionRois)
                {
                    if (string.IsNullOrWhiteSpace(roi.Id))
                    {
                        roi.Id = $"Inspection_{roi.Index}";
                    }
                    roi.PropertyChanged += InspectionRoiPropertyChanged;
                    if (string.IsNullOrWhiteSpace(roi.DatasetStatus))
                    {
                        roi.DatasetStatus = roi.HasDatasetPath ? "Validating dataset..." : "Select a dataset";
                    }
                    if (roi.HasDatasetPath)
                    {
                        roi.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                        roi.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                        if (_initialized && !_isInitializing)
                        {
                            _ = RefreshRoiDatasetStateAsync(roi);
                        }
                    }
                    else
                    {
                        roi.DatasetReady = false;
                        roi.DatasetOkCount = 0;
                        roi.DatasetKoCount = 0;
                        roi.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                        roi.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                    }
                }
                if (_inspectionRois.Count > 0)
                {
                    SelectedInspectionRoi = _inspectionRois.FirstOrDefault();
                }
            }
            else
            {
                SelectedInspectionRoi = null;
            }

            UpdateSelectedRoiState();
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            UpdateGlobalBadge();
        }

        public Task InitializeAsync(CancellationToken ct = default)
        {
            lock (_initLock)
            {
                if (_initialized)
                {
                    return Task.CompletedTask;
                }

                if (_initializationTask == null || _initializationTask.IsCompleted)
                {
                    _initializationTask = InitializeCoreAsync(ct);
                }

                return _initializationTask;
            }
        }

        private async Task InitializeCoreAsync(CancellationToken ct)
        {
            if (_initialized)
            {
                return;
            }

            _isInitializing = true;
            GuiLog.Info($"[workflow] InitializeAsync START"); // CODEX: FormattableString compatibility.

            try
            {
                var snapshot = InspectionRois?.ToList() ?? new List<InspectionRoiConfig>();
                foreach (var roi in snapshot)
                {
                    ct.ThrowIfCancellationRequested();

                    if (roi == null)
                    {
                        continue;
                    }

                    var initPath = string.IsNullOrWhiteSpace(roi.DatasetPath) ? "<none>" : roi.DatasetPath;
                    GuiLog.Info($"[dataset:init] ROI='{roi.DisplayName}' path='{initPath}'");

                    if (!roi.HasDatasetPath)
                    {
                        continue;
                    }

                    await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
                }

                _initialized = true;
                GuiLog.Info($"[workflow] InitializeAsync END"); // CODEX: FormattableString compatibility.
            }
            catch (OperationCanceledException)
            {
                GuiLog.Warn($"[workflow] InitializeAsync cancelled"); // CODEX: FormattableString compatibility.
                throw;
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[workflow] InitializeAsync failed", ex); // CODEX: FormattableString compatibility.
                throw;
            }
            finally
            {
                _isInitializing = false;
            }
        }

        public void SetInspectionRoiModels(RoiModel? inspection1, RoiModel? inspection2, RoiModel? inspection3, RoiModel? inspection4)
        {
            Inspection1 = inspection1;
            Inspection2 = inspection2;
            Inspection3 = inspection3;
            Inspection4 = inspection4;
        }

        public void SetInspectionAutoRepositionEnabled(string? roiId, bool enable)
        {
            // Hook for future auto-reposition toggles. No-op by default.
        }

        public void SetInspectionAutoRepositionEnabled(int roiId, bool enable)
            => SetInspectionAutoRepositionEnabled(roiId.ToString(System.Globalization.CultureInfo.InvariantCulture), enable);

        private void InspectionRoisCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (InspectionRoiConfig roi in e.OldItems)
                {
                    roi.PropertyChanged -= InspectionRoiPropertyChanged;
                    _lastFitResultsByRoi.Remove(roi);
                    _lastCalibResultsByRoi.Remove(roi);
                }
            }

            if (e.NewItems != null)
            {
                foreach (InspectionRoiConfig roi in e.NewItems)
                {
                    roi.PropertyChanged += InspectionRoiPropertyChanged;
                }
            }

            if (_inspectionRois != null && (SelectedInspectionRoi == null || !_inspectionRois.Contains(SelectedInspectionRoi)))
            {
                SelectedInspectionRoi = _inspectionRois.FirstOrDefault();
            }

            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            UpdateGlobalBadge();
        }

        private void SetInspectionRoi(ref RoiModel? field, RoiModel? value, string propertyName)
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;

            OnPropertyChanged(propertyName);
            OnPropertyChanged(nameof(ActiveInspectionRoiModel));
            OnPropertyChanged(nameof(ActiveInspectionRoiImageRectPx));
        }

        private RoiModel? GetInspectionRoiModelByIndex(int index) => index switch
        {
            1 => Inspection1,
            2 => Inspection2,
            3 => Inspection3,
            4 => Inspection4,
            _ => null
        };

        public RoiModel? GetInspectionRoiByIndex(int idx)
        {
            return GetInspectionRoiModelByIndex(idx);
        }

        public Int32Rect? GetInspectionRoiImageRectPx(int idx)
        {
            var roi = GetInspectionRoiModelByIndex(idx);
            return roi != null ? BuildImageRectPx(roi) : null;
        }

        public Rect? GetInspectionRoiCanvasRect(int roiIndex)
        {
            if (!TryGetBatchPlacementTransform(out var uniformScale, out var offsetX, out var offsetY, out var legacyScaleX, out var legacyScaleY))
            {
                return null;
            }

            Rect? rectFromModel = null;

            if (UseCanvasPlacementForBatchHeatmap)
            {
                var roiModel = GetInspectionRoiModelByIndex(roiIndex);
                if (roiModel != null)
                {
                    switch (roiModel.Shape)
                    {
                        case RoiShape.Circle:
                        case RoiShape.Annulus:
                            {
                                double radius = Math.Max(1.0, roiModel.R * uniformScale);
                                double cx = offsetX + roiModel.CX * uniformScale;
                                double cy = offsetY + roiModel.CY * uniformScale;
                                rectFromModel = new Rect(cx - radius, cy - radius, radius * 2.0, radius * 2.0);
                                break;
                            }
                        default:
                            {
                                rectFromModel = new Rect(
                                    offsetX + roiModel.Left * uniformScale,
                                    offsetY + roiModel.Top * uniformScale,
                                    Math.Max(1.0, roiModel.Width * uniformScale),
                                    Math.Max(1.0, roiModel.Height * uniformScale));
                                break;
                            }
                    }
                }
            }

            if (rectFromModel != null)
            {
                return rectFromModel;
            }

            var rectPx = GetInspectionRoiImageRectPx(roiIndex);
            if (rectPx == null)
            {
                return null;
            }

            bool useLegacyScale = !UseCanvasPlacementForBatchHeatmap;
            double scaleX = useLegacyScale ? legacyScaleX : uniformScale;
            double scaleY = useLegacyScale ? legacyScaleY : uniformScale;

            if (scaleX <= 0 || scaleY <= 0)
            {
                return null;
            }

            var rect = rectPx.Value;
            double left = offsetX + rect.X * scaleX;
            double top = offsetY + rect.Y * scaleY;
            double width = Math.Max(1.0, rect.Width * scaleX);
            double height = Math.Max(1.0, rect.Height * scaleY);
            return new Rect(left, top, width, height);
        }

        public InspectionRoiConfig? GetInspectionRoiConfig(int idx)
        {
            return GetInspectionConfigByIndex(idx);
        }

        public void TraceBatchHeatmapPlacement(string reason, int roiIndex, Rect? canvasRect)
        {
            try
            {
                if (roiIndex <= 0)
                {
                    return;
                }

                var rectImg = GetInspectionRoiImageRectPx(roiIndex);
                var roiConfig = GetInspectionConfigByIndex(roiIndex);
                bool enabled = roiConfig?.Enabled ?? false;

                _log(FormattableString.Invariant(
                    $"[batch] img px=({BaseImagePixelWidth}x{BaseImagePixelHeight}) vis=({BaseImageActualWidth:0.##}x{BaseImageActualHeight:0.##}) canvas=({CanvasRoiActualWidth:0.##}x{CanvasRoiActualHeight:0.##})"));

                string rectImgText = rectImg.HasValue
                    ? FormattableString.Invariant($"{rectImg.Value.X:0.##},{rectImg.Value.Y:0.##},{rectImg.Value.Width:0.##},{rectImg.Value.Height:0.##}")
                    : "null";

                string rectCanvasText = canvasRect.HasValue
                    ? FormattableString.Invariant($"{canvasRect.Value.Left:0.##},{canvasRect.Value.Top:0.##},{canvasRect.Value.Width:0.##},{canvasRect.Value.Height:0.##}")
                    : "null";

                _log(FormattableString.Invariant($"[batch] roi idx={roiIndex} rectImg=({rectImgText}) rectCanvas=({rectCanvasText})"));
                _log(FormattableString.Invariant($"[batch] place reason={reason} enabled={enabled}"));
            }
            catch (Exception ex)
            {
                GuiLog.Warn(FormattableString.Invariant($"[batch] trace placement failed: {ex.Message}"));
            }
        }

        private bool TryGetBatchPlacementTransform(out double uniformScale, out double offsetX, out double offsetY, out double legacyScaleX, out double legacyScaleY)
        {
            uniformScale = 0;
            offsetX = 0;
            offsetY = 0;
            legacyScaleX = 0;
            legacyScaleY = 0;

            if (BaseImagePixelWidth <= 0 || BaseImagePixelHeight <= 0
                || BaseImageActualWidth <= 0 || BaseImageActualHeight <= 0
                || CanvasRoiActualWidth <= 0 || CanvasRoiActualHeight <= 0)
            {
                return false;
            }

            legacyScaleX = BaseImageActualWidth / BaseImagePixelWidth;
            legacyScaleY = BaseImageActualHeight / BaseImagePixelHeight;

            if (legacyScaleX <= 0 || legacyScaleY <= 0)
            {
                return false;
            }

            uniformScale = Math.Min(legacyScaleX, legacyScaleY);
            if (uniformScale <= 0)
            {
                return false;
            }

            double drawnW = BaseImagePixelWidth * uniformScale;
            double drawnH = BaseImagePixelHeight * uniformScale;

            double offInsideX = Math.Max(0.0, (BaseImageActualWidth - drawnW) * 0.5);
            double offInsideY = Math.Max(0.0, (BaseImageActualHeight - drawnH) * 0.5);
            double offOuterX = Math.Max(0.0, (CanvasRoiActualWidth - BaseImageActualWidth) * 0.5);
            double offOuterY = Math.Max(0.0, (CanvasRoiActualHeight - BaseImageActualHeight) * 0.5);

            offsetX = offOuterX + offInsideX;
            offsetY = offOuterY + offInsideY;
            return true;
        }

        private RoiModel? GetActiveInspectionRoiModel()
        {
            if (SelectedInspectionRoi != null)
            {
                var match = GetInspectionRoiModelByIndex(SelectedInspectionRoi.Index);
                if (match != null)
                {
                    return match;
                }
            }

            for (int i = 1; i <= 4; i++)
            {
                var candidate = GetInspectionRoiModelByIndex(i);
                if (candidate != null)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static Int32Rect? BuildImageRectPx(RoiModel roi)
        {
            if (roi == null)
            {
                return null;
            }

            double left;
            double top;
            double width;
            double height;

            switch (roi.Shape)
            {
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    if (roi.R <= 0)
                    {
                        return null;
                    }

                    width = roi.R * 2.0;
                    height = width;
                    left = roi.CX - roi.R;
                    top = roi.CY - roi.R;
                    break;

                default:
                    width = roi.Width;
                    height = roi.Height;
                    left = roi.Left;
                    top = roi.Top;
                    break;
            }

            if (double.IsNaN(left) || double.IsNaN(top) || double.IsNaN(width) || double.IsNaN(height)
                || double.IsInfinity(left) || double.IsInfinity(top) || double.IsInfinity(width) || double.IsInfinity(height))
            {
                return null;
            }

            if (width <= 0 || height <= 0)
            {
                return null;
            }

            double right = left + width;
            double bottom = top + height;

            if (roi.BaseImgW is double baseW && baseW > 0)
            {
                left = Math.Clamp(left, 0.0, baseW);
                right = Math.Clamp(right, 0.0, baseW);
            }

            if (roi.BaseImgH is double baseH && baseH > 0)
            {
                top = Math.Clamp(top, 0.0, baseH);
                bottom = Math.Clamp(bottom, 0.0, baseH);
            }

            double finalWidth = right - left;
            double finalHeight = bottom - top;

            if (finalWidth <= 0 || finalHeight <= 0)
            {
                return null;
            }

            int x = Math.Max(0, (int)Math.Round(left));
            int y = Math.Max(0, (int)Math.Round(top));
            int w = Math.Max(0, (int)Math.Round(finalWidth));
            int h = Math.Max(0, (int)Math.Round(finalHeight));

            if (roi.BaseImgW is double baseWInt && baseWInt > 0)
            {
                int maxX = Math.Max(0, (int)Math.Round(baseWInt));
                if (x > maxX)
                {
                    return null;
                }

                if (x + w > maxX)
                {
                    w = Math.Max(0, maxX - x);
                }
            }

            if (roi.BaseImgH is double baseHInt && baseHInt > 0)
            {
                int maxY = Math.Max(0, (int)Math.Round(baseHInt));
                if (y > maxY)
                {
                    return null;
                }

                if (y + h > maxY)
                {
                    h = Math.Max(0, maxY - y);
                }
            }

            if (w <= 0 || h <= 0)
            {
                return null;
            }

            return new Int32Rect(x, y, w, h);
        }

        private void InspectionRoiPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InspectionRoiConfig.Enabled))
            {
                EvaluateAllRoisCommand.RaiseCanExecuteChanged();
                EvaluateSelectedRoiCommand.RaiseCanExecuteChanged();
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.HasFitOk) && sender is InspectionRoiConfig changedFit && !changedFit.HasFitOk)
            {
                _lastFitResultsByRoi.Remove(changedFit);
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.CalibratedThreshold) && sender is InspectionRoiConfig changedCal && changedCal.CalibratedThreshold == null)
            {
                _lastCalibResultsByRoi.Remove(changedCal);
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.LastResultOk) || e.PropertyName == nameof(InspectionRoiConfig.Enabled))
            {
                UpdateGlobalBadge();
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.DatasetPath) && sender is InspectionRoiConfig roiChanged)
            {
                _roiDatasetCache.Remove(roiChanged);
                if (!roiChanged.HasDatasetPath)
                {
                    roiChanged.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                    roiChanged.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                    roiChanged.DatasetReady = false;
                    roiChanged.DatasetOkCount = 0;
                    roiChanged.DatasetKoCount = 0;
                    roiChanged.DatasetStatus = "Select a dataset";
                    _ = RefreshDatasetPreviewsForRoiAsync(roiChanged);
                }
                else
                {
                    roiChanged.DatasetStatus = "Validating dataset...";
                    roiChanged.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                    roiChanged.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                    _ = RefreshDatasetPreviewsForRoiAsync(roiChanged);
                    _ = RefreshRoiDatasetStateAsync(roiChanged);
                }

                CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
                OpenDatasetFolderCommand.RaiseCanExecuteChanged();
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.DatasetOkCount)
                || e.PropertyName == nameof(InspectionRoiConfig.IsDatasetLoading))
            {
                CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            }

            if (ReferenceEquals(sender, SelectedInspectionRoi))
            {
                OnPropertyChanged(nameof(SelectedInspectionRoi));
                OnPropertyChanged(nameof(ActiveInspectionRoiModel));
                OnPropertyChanged(nameof(ActiveInspectionRoiImageRectPx));
                if (e.PropertyName == nameof(InspectionRoiConfig.Shape))
                {
                    OnPropertyChanged(nameof(SelectedInspectionShape));
                }
            }
        }

        public DatasetSample? SelectedOkSample
        {
            get => _selectedOkSample;
            set
            {
                if (!Equals(value, _selectedOkSample))
                {
                    _selectedOkSample = value;
                    OnPropertyChanged();
                    RemoveSelectedCommand.RaiseCanExecuteChanged();
                }
            }
        }
        private DatasetSample? _selectedOkSample;

        public DatasetSample? SelectedNgSample
        {
            get => _selectedNgSample;
            set
            {
                if (!Equals(value, _selectedNgSample))
                {
                    _selectedNgSample = value;
                    OnPropertyChanged();
                    RemoveSelectedCommand.RaiseCanExecuteChanged();
                }
            }
        }
        private DatasetSample? _selectedNgSample;

        public AsyncCommand AddOkFromCurrentRoiCommand { get; }
        public AsyncCommand AddNgFromCurrentRoiCommand { get; }
        public AsyncCommand RemoveSelectedCommand { get; }
        public AsyncCommand OpenDatasetFolderCommand { get; }
        public AsyncCommand TrainFitCommand { get; }
        public AsyncCommand CalibrateCommand { get; }
        public AsyncCommand InferFromCurrentRoiCommand { get; }
        public AsyncCommand RefreshDatasetCommand { get; }
        public AsyncCommand RefreshHealthCommand { get; }
        public AsyncCommand BrowseDatasetCommand { get; }
        public AsyncCommand TrainSelectedRoiCommand { get; }
        public AsyncCommand CalibrateSelectedRoiCommand { get; }
        public AsyncCommand EvaluateSelectedRoiCommand { get; }
        public AsyncCommand EvaluateAllRoisCommand { get; }
        public AsyncCommand InferEnabledRoisCommand { get; }
        public AsyncCommand AddRoiToDatasetOkCommand { get; }
        public AsyncCommand AddRoiToDatasetNgCommand { get; }
        public AsyncCommand BrowseBatchFolderCommand { get; }
        public AsyncCommand StartBatchCommand { get; }
        public AsyncCommand PauseBatchCommand { get; }
        public AsyncCommand StopBatchCommand { get; }
        public ICommand AddRoiToOkCommand { get; }
        public ICommand AddRoiToNgCommand { get; }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged();
                    RaiseBusyChanged();
                }
            }
        }

        public string RoleId
        {
            get => _roleId;
            set
            {
                if (!string.Equals(_roleId, value, StringComparison.Ordinal))
                {
                    _roleId = value;
                    OnPropertyChanged();
                }
            }
        }

        public string RoiId
        {
            get => _roiId;
            set
            {
                if (!string.Equals(_roiId, value, StringComparison.Ordinal))
                {
                    _roiId = value;
                    OnPropertyChanged();
                }
            }
        }

        public double MmPerPx
        {
            get => _mmPerPx;
            set
            {
                if (Math.Abs(_mmPerPx - value) > 1e-6)
                {
                    _mmPerPx = value;
                    OnPropertyChanged();
                }
            }
        }

        public string BackendBaseUrl
        {
            get => _backendBaseUrl;
            set
            {
                if (!string.Equals(_backendBaseUrl, value, StringComparison.Ordinal))
                {
                    _backendBaseUrl = value;
                    OnPropertyChanged();
                    _client.BaseUrl = value;
                }
            }
        }

        public string FitSummary
        {
            get => _fitSummary;
            private set
            {
                if (!string.Equals(_fitSummary, value, StringComparison.Ordinal))
                {
                    _fitSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CalibrationSummary
        {
            get => _calibrationSummary;
            private set
            {
                if (!string.Equals(_calibrationSummary, value, StringComparison.Ordinal))
                {
                    _calibrationSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public double? InferenceScore
        {
            get => _inferenceScore;
            private set
            {
                if (_inferenceScore != value)
                {
                    _inferenceScore = value;
                    OnPropertyChanged();
                    UpdateInferenceSummary();
                }
            }
        }

        public double? InferenceThreshold
        {
            get => _inferenceThreshold;
            private set
            {
                if (_inferenceThreshold != value)
                {
                    _inferenceThreshold = value;
                    OnPropertyChanged();
                    UpdateInferenceSummary();
                }
            }
        }

        public double LocalThreshold
        {
            get => _localThreshold;
            set
            {
                if (Math.Abs(_localThreshold - value) > 1e-6)
                {
                    _localThreshold = value;
                    OnPropertyChanged();
                    UpdateInferenceSummary();
                    if (_lastExport != null && _lastHeatmapBytes != null)
                    {
                        _ = _showHeatmapAsync(_lastExport, _lastHeatmapBytes, HeatmapOpacity);
                    }
                }
            }
        }

        public string InferenceSummary
        {
            get => _inferenceSummary;
            private set
            {
                if (!string.Equals(_inferenceSummary, value, StringComparison.Ordinal))
                {
                    _inferenceSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<Region> Regions { get; } = new();

        public double HeatmapOpacity
        {
            get => _heatmapOpacity;
            set
            {
                if (Math.Abs(_heatmapOpacity - value) > 1e-3)
                {
                    _heatmapOpacity = value;
                    OnPropertyChanged();
                    if (_lastExport != null && _lastHeatmapBytes != null)
                    {
                        _ = _showHeatmapAsync(_lastExport, _lastHeatmapBytes, HeatmapOpacity);
                    }
                }
            }
        }

        public string HealthSummary
        {
            get => _healthSummary;
            private set
            {
                if (!string.Equals(_healthSummary, value, StringComparison.Ordinal))
                {
                    _healthSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        private AsyncCommand CreateCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            return new AsyncCommand(async param =>
            {
                await RunExclusiveAsync(() => execute(param));
            }, canExecute ?? (_ => !IsBusy));
        }

        private bool CanCalibrateSelectedRoi()
        {
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return false;
            }

            if (!roi.HasDatasetPath || roi.IsDatasetLoading)
            {
                return false;
            }

            return roi.DatasetOkCount > 0;
        }

        private async Task RunExclusiveAsync(Func<Task> action)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RaiseBusyChanged()
        {
            AddOkFromCurrentRoiCommand.RaiseCanExecuteChanged();
            AddNgFromCurrentRoiCommand.RaiseCanExecuteChanged();
            RemoveSelectedCommand.RaiseCanExecuteChanged();
            OpenDatasetFolderCommand.RaiseCanExecuteChanged();
            TrainFitCommand.RaiseCanExecuteChanged();
            CalibrateCommand.RaiseCanExecuteChanged();
            InferFromCurrentRoiCommand.RaiseCanExecuteChanged();
            RefreshDatasetCommand.RaiseCanExecuteChanged();
            RefreshHealthCommand.RaiseCanExecuteChanged();
            BrowseDatasetCommand.RaiseCanExecuteChanged();
            TrainSelectedRoiCommand.RaiseCanExecuteChanged();
            CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetOkCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetNgCommand.RaiseCanExecuteChanged();
            if (AddRoiToOkCommand is AsyncCommand asyncAddOk)
            {
                asyncAddOk.RaiseCanExecuteChanged();
            }
            if (AddRoiToNgCommand is AsyncCommand asyncAddNg)
            {
                asyncAddNg.RaiseCanExecuteChanged();
            }
            BrowseBatchFolderCommand?.RaiseCanExecuteChanged();
            UpdateBatchCommandStates();
        }

        private void UpdateSelectedRoiState()
        {
            BrowseDatasetCommand.RaiseCanExecuteChanged();
            TrainSelectedRoiCommand.RaiseCanExecuteChanged();
            CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetOkCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetNgCommand.RaiseCanExecuteChanged();
            if (AddRoiToOkCommand is AsyncCommand asyncAddOk)
            {
                asyncAddOk.RaiseCanExecuteChanged();
            }
            if (AddRoiToNgCommand is AsyncCommand asyncAddNg)
            {
                asyncAddNg.RaiseCanExecuteChanged();
            }
        }

        private async Task AddSampleAsync(bool isNg)
        {
            EnsureRoleRoi();
            _log($"[dataset] add {(isNg ? "NG" : "OK")} sample requested");

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                _log("[dataset] export cancelled");
                return;
            }

            var source = _getSourceImagePath() ?? string.Empty;
            var sample = await _datasetManager.SaveSampleAsync(RoleId, RoiId, isNg, export.PngBytes, export.ShapeJson, MmPerPx, source, export.RoiImage.AngleDeg).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (isNg)
                {
                    NgSamples.Add(sample);
                }
                else
                {
                    OkSamples.Add(sample);
                }
            });

            _log($"[dataset] saved {(isNg ? "NG" : "OK")} sample -> {sample.ImagePath}");
            TrainFitCommand.RaiseCanExecuteChanged();
            CalibrateCommand.RaiseCanExecuteChanged();
        }


        private async Task AddRoiToDatasetAsync(InspectionRoiConfig roi, bool isOk)
        {
            if (roi == null)
            {
                await ShowMessageAsync("No ROI selected.", "Dataset");
                return;
            }

            _activateInspectionIndex?.Invoke(roi.Index);

            if (string.IsNullOrWhiteSpace(roi.DatasetPath))
            {
                await ShowMessageAsync($"Dataset path not set for '{roi.DisplayName}'.", "Dataset");
                return;
            }

            GuiLog.Info($"AddToDataset (config) roi='{roi.DisplayName}' label={(isOk ? "OK" : "NG")} dataset='{roi.DatasetPath}' source='{_getSourceImagePath()}'");

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                await ShowMessageAsync("No image loaded.", "Dataset");
                GuiLog.Warn($"AddToDataset aborted: no export for roi='{roi.DisplayName}'");
                return;
            }

            var cropped = ExtractRoiBitmapWithShapeMask(export);
            if (cropped == null)
            {
                await ShowMessageAsync($"Could not crop ROI '{roi.DisplayName}'.", "Dataset");
                GuiLog.Warn($"AddToDataset aborted: crop failed for roi='{roi.DisplayName}'");
                return;
            }

            string labelDir = isOk ? "ok" : DetermineNegLabelDir(roi.DatasetPath!);
            string saveDir = Path.Combine(roi.DatasetPath!, labelDir);
            Directory.CreateDirectory(saveDir);
            string fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";
            string fullPath = Path.Combine(saveDir, fileName);

            try
            {
                SavePng(fullPath, cropped);
                GuiLog.Info($"AddToDataset saved roi='{roi.DisplayName}' -> '{fullPath}'");

                await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
                await RefreshDatasetPreviewsForRoiAsync(roi).ConfigureAwait(false);

                await ShowDatasetSavedAsync(roi.DisplayName, fullPath, isOk).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GuiLog.Error($"AddToDataset save failed roi='{roi.DisplayName}' dest='{fullPath}'", ex);
                await ShowMessageAsync(
                    $"Could not save dataset image. Revisa la carpeta y los permisos. (Ver logs)",
                    "Dataset",
                    MessageBoxImage.Error);
            }
        }

        private async Task AddRoiToDatasetAsync(RoiModel roi, bool positive)
        {
            if (roi == null)
            {
                await ShowMessageAsync("ROI inválido.", "Dataset");
                return;
            }

            ActivateInspectionIndexForRoi(roi);

            var config = FindInspectionConfigForRoi(roi);
            if (config != null)
            {
                await AddRoiToDatasetAsync(config, positive).ConfigureAwait(false);
                return;
            }

            GuiLog.Info($"AddToDataset (direct) roi='{roi.Label ?? roi.Id}' target={(positive ? "OK" : "NG")} source='{_getSourceImagePath()}'");

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                await ShowMessageAsync("No image loaded.", "Dataset");
                GuiLog.Warn($"AddToDataset aborted: no export for legacy roi"); // CODEX: FormattableString compatibility.
                return;
            }

            string role = roi.Role.ToString();
            string roiId = string.IsNullOrWhiteSpace(roi.Id) ? roi.Role.ToString() : roi.Id;

            try
            {
                var sample = await _datasetManager.SaveSampleAsync(role, roiId, isNg: !positive, export.PngBytes, export.ShapeJson, MmPerPx,
                    _getSourceImagePath() ?? string.Empty, export.RoiImage.AngleDeg).ConfigureAwait(false);

                await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
                GuiLog.Info($"AddToDataset (direct) saved {(positive ? "OK" : "NG")} -> '{sample.ImagePath}'");
                await ShowDatasetSavedAsync(roi.Label ?? roi.Role.ToString(), sample.ImagePath, positive).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GuiLog.Error($"AddToDataset legacy failed roi='{roi.Label ?? roi.Id}'", ex);
                await ShowMessageAsync(
                    "No se pudo guardar el ROI en el dataset. Revisa los logs para más detalles.",
                    "Dataset",
                    MessageBoxImage.Error);
            }
        }

        private void ActivateInspectionIndexForRoi(RoiModel roi)
        {
            var index = TryResolveInspectionIndex(roi);
            if (index.HasValue)
            {
                _activateInspectionIndex?.Invoke(index.Value);
            }
        }

        private static int? TryResolveInspectionIndex(RoiModel roi)
        {
            if (roi == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(roi.Id))
            {
                var parts = roi.Id.Split('_');
                if (parts.Length > 1 && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
                {
                    return parsedId;
                }
            }

            if (!string.IsNullOrWhiteSpace(roi.Label))
            {
                var labelParts = roi.Label.Split(' ');
                if (labelParts.Length > 1 && int.TryParse(labelParts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLabel))
                {
                    return parsedLabel;
                }
            }

            return null;
        }

        private async Task RefreshRoiDatasetStateAsync(RoiModel roi)
        {
            if (roi == null)
            {
                return;
            }

            try
            {
                var config = FindInspectionConfigForRoi(roi);
                if (config != null)
                {
                    await RefreshRoiDatasetStateAsync(config).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log($"[dataset] refresh state error for ROI '{roi.Label ?? roi.Id}': {ex.Message}");
            }
        }

        private InspectionRoiConfig? FindInspectionConfigForRoi(RoiModel roi)
        {
            if (roi == null || _inspectionRois == null || _inspectionRois.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(roi.Label))
            {
                var byLabel = _inspectionRois.FirstOrDefault(r =>
                    string.Equals(r.Name, roi.Label, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.DisplayName, roi.Label, StringComparison.OrdinalIgnoreCase));
                if (byLabel != null)
                {
                    return byLabel;
                }
            }

            if (!string.IsNullOrWhiteSpace(roi.Id))
            {
                var byKey = _inspectionRois.FirstOrDefault(r => string.Equals(r.ModelKey, roi.Id, StringComparison.OrdinalIgnoreCase));
                if (byKey != null)
                {
                    return byKey;
                }
            }

            if (!string.IsNullOrWhiteSpace(roi.Id)
                && int.TryParse(roi.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
            {
                var byIndex = _inspectionRois.FirstOrDefault(r => r.Index == numericId);
                if (byIndex != null)
                {
                    return byIndex;
                }
            }

            return SelectedInspectionRoi ?? _inspectionRois.FirstOrDefault();
        }

        private async Task RemoveSelectedAsync()
        {
            var toRemove = new List<DatasetSample>();
            if (SelectedOkSample != null)
            {
                toRemove.Add(SelectedOkSample);
            }
            if (SelectedNgSample != null)
            {
                toRemove.Add(SelectedNgSample);
            }

            if (toRemove.Count == 0)
            {
                return;
            }

            foreach (var sample in toRemove)
            {
                _datasetManager.DeleteSample(sample);
                _log($"[dataset] removed sample {sample.ImagePath}");
            }

            await RefreshDatasetAsync().ConfigureAwait(false);
        }

        private async Task OpenDatasetFolderAsync()
        {
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                await ShowMessageAsync("Select an Inspection ROI first.", "Dataset");
                return;
            }

            if (string.IsNullOrWhiteSpace(roi.DatasetPath))
            {
                await ShowMessageAsync($"Dataset path not set for '{roi.DisplayName}'.", "Dataset");
                return;
            }

            var datasetPath = roi.DatasetPath!;

            try
            {
                Directory.CreateDirectory(datasetPath);
            }
            catch (Exception ex)
            {
                _log($"[dataset] failed to ensure directory '{datasetPath}': {ex.Message}");
            }

            await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = datasetPath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    _log($"[dataset] failed to open folder '{datasetPath}': {ex.Message}");
                }
            }).ConfigureAwait(false);
        }

        private async Task RefreshDatasetAsync()
        {
            EnsureRoleRoi();
            _log("[dataset] refreshing lists");
            var ok = await _datasetManager.LoadSamplesAsync(RoleId, RoiId, isNg: false).ConfigureAwait(false);
            var ng = await _datasetManager.LoadSamplesAsync(RoleId, RoiId, isNg: true).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                OkSamples.Clear();
                foreach (var sample in ok)
                    OkSamples.Add(sample);

                NgSamples.Clear();
                foreach (var sample in ng)
                    NgSamples.Add(sample);
            });

            TrainFitCommand.RaiseCanExecuteChanged();
            CalibrateCommand.RaiseCanExecuteChanged();
        }

        private async Task TrainAsync()
        {
            EnsureRoleRoi();
            var images = OkSamples.Select(s => s.ImagePath).ToList();
            if (images.Count == 0)
            {
                FitSummary = "No OK samples";
                return;
            }

            _log($"[fit] sending {images.Count} samples to fit_ok");
            try
            {
                var result = await _client.FitOkAsync(RoleId, RoiId, MmPerPx, images).ConfigureAwait(false);
                FitSummary = $"Embeddings={result.n_embeddings} Coreset={result.coreset_size} TokenShape=[{string.Join(',', result.token_shape ?? Array.Empty<int>())}]";
                _log("[fit] completed " + FitSummary);
            }
            catch (HttpRequestException ex)
            {
                if (IsAlreadyTrainedError(ex))
                {
                    FitSummary = "Model already trained";
                    _log($"[fit] backend reported already trained: {ex.Message}");
                    return;
                }

                FitSummary = "Train failed";
                await ShowMessageAsync($"Training failed: {ex.Message}", caption: "Train error");
            }
        }

        private async Task CalibrateAsync()
        {
            EnsureRoleRoi();
            var ok = OkSamples.ToList();
            if (ok.Count == 0)
            {
                CalibrationSummary = "Need OK samples";
                return;
            }

            var okScores = new List<double>();
            var ngScores = new List<double>();

            _log($"[calibrate] evaluating {ok.Count} OK samples");
            foreach (var sample in ok)
            {
                var infer = await _client.InferAsync(RoleId, RoiId, MmPerPx, sample.ImagePath, sample.Metadata.shape_json).ConfigureAwait(false);
                okScores.Add(infer.score);
            }

            var ngList = NgSamples.ToList();
            if (ngList.Count > 0)
            {
                _log($"[calibrate] evaluating {ngList.Count} NG samples");
                foreach (var sample in ngList)
                {
                    var infer = await _client.InferAsync(RoleId, RoiId, MmPerPx, sample.ImagePath, sample.Metadata.shape_json).ConfigureAwait(false);
                    ngScores.Add(infer.score);
                }
            }

            var calib = await _client.CalibrateAsync(RoleId, RoiId, MmPerPx, okScores, ngScores.Count > 0 ? ngScores : null).ConfigureAwait(false);
            CalibrationSummary = $"Threshold={calib.threshold:0.###} OKµ={calib.ok_mean:0.###} NGµ={calib.ng_mean:0.###} Percentile={calib.score_percentile}";
            _log("[calibrate] " + CalibrationSummary);
        }

        private async Task InferCurrentAsync()
        {
            EnsureRoleRoi();
            _log("[infer] exporting current ROI");
            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                _log("[infer] export cancelled");
                return;
            }

            var inferFileName = $"roi_{DateTime.UtcNow:yyyyMMddHHmmssfff}.png";
            _log($"[infer] POST role={RoleId} roi={RoiId} bytes={export.PngBytes.Length} imgHash={export.ImageHash} cropHash={export.CropHash} rect=({export.CropRect.X},{export.CropRect.Y},{export.CropRect.Width},{export.CropRect.Height})");

            var result = await _client.InferAsync(RoleId, RoiId, MmPerPx, export.PngBytes, inferFileName, export.ShapeJson).ConfigureAwait(false);
            _lastExport = export;
            _lastInferResult = result;
            InferenceScore = result.score;
            InferenceThreshold = result.threshold;

            // ANTES:
            // LocalThreshold = result.threshold > 0 ? result.threshold : LocalThreshold;

            // AHORA (elige una de las dos):
            // Opción 1:
            if (result.threshold.HasValue && result.threshold.Value > 0)
            {
                LocalThreshold = result.threshold.Value;
            }
            // Opción 2 (C# 9+):
            // LocalThreshold = result.threshold is > 0 var t ? t : LocalThreshold;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Regions.Clear();
                if (result.regions != null)
                {
                    foreach (var region in result.regions)
                    {
                        Regions.Add(region);
                    }
                }
            });


            if (!string.IsNullOrWhiteSpace(result.heatmap_png_base64))
            {
                _lastHeatmapBytes = Convert.FromBase64String(result.heatmap_png_base64);
                await _showHeatmapAsync(export, _lastHeatmapBytes, HeatmapOpacity).ConfigureAwait(false);
            }
            else
            {
                _lastHeatmapBytes = null;
                _clearHeatmap();
            }
        }

        private async Task RefreshHealthAsync()
        {
            var info = await _client.GetHealthAsync().ConfigureAwait(false);
            if (info == null)
            {
                HealthSummary = "Backend offline";
                return;
            }

            HealthSummary = $"{info.status ?? "ok"} — {info.device} — {info.model} ({info.version})";
        }

        private async Task BrowseDatasetAsync()
        {
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return;
            }

            var choice = await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    "Choose Yes to browse a dataset folder (requires ok/ko).\nChoose No to load a CSV (filename,label).",
                    "Dataset source",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes));

            if (choice == MessageBoxResult.Cancel)
            {
                return;
            }

            if (choice == MessageBoxResult.Yes)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    using var dialog = new Forms.FolderBrowserDialog
                    {
                        Description = "Select dataset folder",
                        UseDescriptionForTitle = true,
                    };

                    if (!string.IsNullOrWhiteSpace(roi.DatasetPath) && Directory.Exists(roi.DatasetPath))
                    {
                        dialog.SelectedPath = roi.DatasetPath;
                    }

                    if (dialog.ShowDialog() == Forms.DialogResult.OK)
                    {
                        roi.DatasetPath = dialog.SelectedPath;
                    }
                });
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select dataset CSV",
                    Filter = "Dataset CSV (*.csv)|*.csv|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (!string.IsNullOrWhiteSpace(roi.DatasetPath) && File.Exists(roi.DatasetPath))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(roi.DatasetPath);
                    dialog.FileName = Path.GetFileName(roi.DatasetPath);
                }

                if (dialog.ShowDialog() == true)
                {
                    roi.DatasetPath = dialog.FileName;
                }
            });
        }



        private BitmapSource? ExtractRoiBitmapWithShapeMask(RoiExportResult export)
        {
            return LoadBitmapFromBytes(export.PngBytes);
        }

        private static BitmapSource? LoadBitmapFromBytes(byte[] data)

        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            try
            {
                using var ms = new MemoryStream(data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static string RoiFolderName(string displayName)
            => displayName.Replace(" ", "_");

        private static string DetermineNegLabelDir(string datasetRoot)
        {
            var ng = Path.Combine(datasetRoot, "ng");
            var ko = Path.Combine(datasetRoot, "ko");
            if (Directory.Exists(ng)) return "ng";
            if (Directory.Exists(ko)) return "ko";
            return "ng";
        }

        private static IEnumerable<string> EnumerateDatasetFiles(string rootDir, string labelDir, string roiDisplayName, int take = 24)
        {
            var roiFolder = RoiFolderName(roiDisplayName);
            var subDir = Path.Combine(rootDir, labelDir, roiFolder);
            string targetDir;
            if (Directory.Exists(subDir))
            {
                targetDir = subDir;
            }
            else
            {
                targetDir = Path.Combine(rootDir, labelDir);
            }

            if (!Directory.Exists(targetDir))
            {
                Debug.WriteLine($"[thumbs] no dir: {targetDir}");
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(targetDir, "*.png", SearchOption.TopDirectoryOnly)
                                         .OrderByDescending(File.GetCreationTimeUtc)
                                         .Take(take))
            {
                yield return file;
            }
        }

        private static BitmapSource? LoadBitmapSource(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame == null)
            {
                return null;
            }

            frame.Freeze();
            return frame;
        }

        public async Task RefreshDatasetPreviewsForRoiAsync(InspectionRoiConfig roi, int take = 24)
        {
            if (roi == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(roi.DatasetPath))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    roi.OkPreview = new ObservableCollection<DatasetPreviewItem>();
                    roi.NgPreview = new ObservableCollection<DatasetPreviewItem>();
                });
                return;
            }

            var okItems = new ObservableCollection<DatasetPreviewItem>();
            foreach (var path in EnumerateDatasetFiles(roi.DatasetPath!, "ok", roi.DisplayName, take))
            {
                var bmp = LoadBitmapSource(path);
                if (bmp != null)
                {
                    okItems.Add(new DatasetPreviewItem { Path = path, Thumbnail = bmp });
                }
            }

            var negLabel = DetermineNegLabelDir(roi.DatasetPath!);
            var ngItems = new ObservableCollection<DatasetPreviewItem>();
            foreach (var path in EnumerateDatasetFiles(roi.DatasetPath!, negLabel, roi.DisplayName, take))
            {
                var bmp = LoadBitmapSource(path);
                if (bmp != null)
                {
                    ngItems.Add(new DatasetPreviewItem { Path = path, Thumbnail = bmp });
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                roi.OkPreview = okItems;
                roi.NgPreview = ngItems;
                Debug.WriteLine($"[thumbs] {roi.DisplayName} ok={okItems.Count} {negLabel}={ngItems.Count}");
            });
        }

        private static void SavePng(string path, BitmapSource bmp)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }

        private static async Task ShowDatasetSavedAsync(string? roiName, string path, bool isOk)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var caption = isOk ? "Add to OK" : "Add to NG";
                var prefix = string.IsNullOrWhiteSpace(roiName) ? string.Empty : $"{roiName}: ";
                var label = isOk ? "OK" : "NG";
                var message = prefix + "guardado en " + label + ":" + Environment.NewLine + path;
                MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private async Task<RoiDatasetAnalysis> EnsureDatasetAnalysisAsync(InspectionRoiConfig roi, bool forceRefresh = false)
        {
            if (!forceRefresh && _roiDatasetCache.TryGetValue(roi, out var cached))
            {
                if (string.Equals(cached.DatasetPath, roi.DatasetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return cached;
                }
            }

            return await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
        }

        private async Task<RoiDatasetAnalysis> RefreshRoiDatasetStateAsync(InspectionRoiConfig roi)
        {
            var datasetPath = roi.DatasetPath;
            roi.IsDatasetLoading = true;
            roi.DatasetReady = false;
            roi.DatasetStatus = roi.HasDatasetPath ? "Validating dataset..." : "Select a dataset";

            var refreshPath = string.IsNullOrWhiteSpace(datasetPath) ? "<none>" : datasetPath;
            GuiLog.Info($"[dataset] Refresh START roi='{roi.DisplayName}' path='{refreshPath}'");

            var analysis = await Task.Run(() => AnalyzeDatasetPath(datasetPath)).ConfigureAwait(false);
            _roiDatasetCache[roi] = analysis;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                roi.IsDatasetLoading = false;
                roi.DatasetOkCount = analysis.OkCount;
                roi.DatasetKoCount = analysis.KoCount;
                roi.DatasetReady = analysis.IsValid;
                roi.DatasetStatus = analysis.StatusMessage;
            });

            GuiLog.Info($"[dataset] Refresh END roi='{roi.DisplayName}' ok={analysis.OkCount} ng={analysis.KoCount} ready={analysis.IsValid} status='{analysis.StatusMessage}'");

            await RefreshDatasetPreviewsForRoiAsync(roi).ConfigureAwait(false);

            return analysis;
        }

        private static RoiDatasetAnalysis AnalyzeDatasetPath(string? datasetPath)
        {
            if (string.IsNullOrWhiteSpace(datasetPath))
            {
                return RoiDatasetAnalysis.Empty("Select a dataset");
            }

            if (Directory.Exists(datasetPath))
            {
                return AnalyzeFolderDataset(datasetPath);
            }

            if (File.Exists(datasetPath))
            {
                if (string.Equals(Path.GetExtension(datasetPath), ".csv", StringComparison.OrdinalIgnoreCase))
                {
                    return AnalyzeCsvDataset(datasetPath);
                }

                return RoiDatasetAnalysis.Empty("Dataset file must be a CSV with filename,label columns");
            }

            return RoiDatasetAnalysis.Empty("Dataset path not found");
        }

        private static RoiDatasetAnalysis AnalyzeFolderDataset(string folderPath)
        {
            try
            {
                var okDir = Path.Combine(folderPath, "ok");
                if (!Directory.Exists(okDir))
                {
                    return RoiDatasetAnalysis.Empty("Folder must contain /ok and /ko subfolders");
                }

                var negLabel = DetermineNegLabelDir(folderPath);
                var negDir = Path.Combine(folderPath, negLabel);
                if (!Directory.Exists(negDir))
                {
                    return RoiDatasetAnalysis.Empty("Folder must contain /ok and /ko subfolders");
                }

                var okFiles = EnumerateImages(okDir);
                var koFiles = EnumerateImages(negDir);

                var entries = okFiles.Select(path => new DatasetEntry(path, true))
                    .Concat(koFiles.Select(path => new DatasetEntry(path, false)))
                    .ToList();

                var preview = new List<(string Path, bool IsOk)>();
                preview.AddRange(okFiles.Take(3).Select(p => (p, true)));
                preview.AddRange(koFiles.Take(3).Select(p => (p, false)));

                bool ready = okFiles.Count > 0 && koFiles.Count > 0;
                string status = ready ? (negLabel == "ng" ? "Dataset Ready ✅ (using /ng)" : "Dataset Ready ✅")
                    : (okFiles.Count == 0 ? "Dataset missing OK samples" : "Dataset missing KO samples");

                return new RoiDatasetAnalysis(folderPath, entries, okFiles.Count, koFiles.Count, ready, status, preview);
            }
            catch (Exception ex)
            {
                return RoiDatasetAnalysis.Empty($"Dataset error: {ex.Message}");
            }
        }

        private static RoiDatasetAnalysis AnalyzeCsvDataset(string csvPath)
        {
            try
            {
                var entries = new List<DatasetEntry>();
                var preview = new List<(string Path, bool IsOk)>();
                int okCount = 0, koCount = 0;
                int okPreview = 0, koPreview = 0;

                using var reader = new StreamReader(csvPath);
                string? header = reader.ReadLine();
                if (header == null)
                {
                    return RoiDatasetAnalysis.Empty("CSV is empty");
                }

                var headers = header.Split(new[] { ',', ';', '\t' }, StringSplitOptions.None)
                    .Select(h => h.Trim().ToLowerInvariant())
                    .ToArray();

                int filenameIndex = Array.FindIndex(headers, h => h is "filename" or "file" or "path");
                int labelIndex = Array.FindIndex(headers, h => h is "label" or "class" or "target");

                if (filenameIndex < 0 || labelIndex < 0)
                {
                    return RoiDatasetAnalysis.Empty("CSV must contain 'filename' and 'label' columns");
                }

                string? line;
                var baseDir = Path.GetDirectoryName(csvPath) ?? string.Empty;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var parts = line.Split(new[] { ',', ';', '\t' }, StringSplitOptions.None);
                    if (parts.Length <= Math.Max(filenameIndex, labelIndex))
                    {
                        continue;
                    }

                    var rawPath = parts[filenameIndex].Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(rawPath))
                    {
                        continue;
                    }

                    var resolvedPath = Path.IsPathRooted(rawPath)
                        ? rawPath
                        : Path.GetFullPath(Path.Combine(baseDir, rawPath));

                    if (!File.Exists(resolvedPath))
                    {
                        continue;
                    }

                    var labelRaw = parts[labelIndex].Trim().Trim('"');
                    var parsedLabel = TryParseDatasetLabel(labelRaw);
                    if (parsedLabel == null)
                    {
                        continue;
                    }

                    bool isOk = parsedLabel.Value;
                    entries.Add(new DatasetEntry(resolvedPath, isOk));
                    if (isOk)
                    {
                        okCount++;
                        if (okPreview < 3)
                        {
                            preview.Add((resolvedPath, true));
                            okPreview++;
                        }
                    }
                    else
                    {
                        koCount++;
                        if (koPreview < 3)
                        {
                            preview.Add((resolvedPath, false));
                            koPreview++;
                        }
                    }
                }

                bool ready = okCount > 0 && koCount > 0;
                string status = ready ? "Dataset Ready ✅" : "CSV needs both OK and KO samples";
                return new RoiDatasetAnalysis(csvPath, entries, okCount, koCount, ready, status, preview);
            }
            catch (Exception ex)
            {
                return RoiDatasetAnalysis.Empty($"CSV error: {ex.Message}");
            }
        }

        private static bool? TryParseDatasetLabel(string labelRaw)
        {
            if (string.IsNullOrWhiteSpace(labelRaw))
            {
                return null;
            }

            var normalized = labelRaw.Trim().Trim('"').ToLowerInvariant();
            return normalized switch
            {
                "ok" or "good" or "pass" or "1" or "true" => true,
                "ko" or "ng" or "nok" or "fail" or "0" or "false" => false,
                _ => null
            };
        }

        private static List<string> EnumerateImages(string directory)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(directory))
            {
                return new List<string>();
            }

            string[] patterns = { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff" };
            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
                {
                    set.Add(file);
                }
            }

            var list = set.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private async Task<bool> TrainSelectedRoiAsync(CancellationToken ct = default)
        {
            EnsureRoleRoi();
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return false;
            }

            if (!roi.HasDatasetPath)
            {
                await ShowMessageAsync("Select a dataset path before training.");
                return false;
            }

            var analysis = await EnsureDatasetAnalysisAsync(roi).ConfigureAwait(false);
            if (analysis.OkCount == 0)
            {
                var message = string.IsNullOrWhiteSpace(analysis.StatusMessage)
                    ? "Dataset has no OK samples for training."
                    : analysis.StatusMessage;
                await ShowMessageAsync(message, caption: "Dataset not ready");
                return false;
            }

            if (!analysis.IsValid && !string.IsNullOrWhiteSpace(analysis.StatusMessage))
            {
                _log($"[train] dataset warning: {analysis.StatusMessage}");
            }

            var okImages = analysis.Entries.Where(e => e.IsOk).Select(e => e.Path).Where(File.Exists).ToList();
            if (okImages.Count == 0)
            {
                await ShowMessageAsync($"No OK images found in dataset for ROI '{roi.Name}'.");
                return false;
            }

            if (!await EnsureFitEndpointAsync().ConfigureAwait(false))
            {
                await ShowMessageAsync("Backend /fit_ok endpoint is not available.");
                return false;
            }

            try
            {
                var result = await _client.FitOkAsync(RoleId, roi.ModelKey, MmPerPx, okImages, roi.TrainMemoryFit, ct).ConfigureAwait(false);
                var memoryHint = roi.TrainMemoryFit ? " (memory-fit)" : string.Empty;
                FitSummary = $"Embeddings={result.n_embeddings} Coreset={result.coreset_size} TokenShape=[{string.Join(',', result.token_shape ?? Array.Empty<int>())}]" + memoryHint;
                roi.HasFitOk = true;
                _lastFitResultsByRoi[roi] = result;
                await SaveModelManifestAsync(roi, result, null, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (IsAlreadyTrainedError(ex))
                {
                    FitSummary = "Model already trained";
                    _log($"[train] backend reported already trained: {ex.Message}");
                    roi.HasFitOk = true;
                    return true;
                }

                FitSummary = "Train failed";
                await ShowMessageAsync($"Training failed: {ex.Message}", caption: "Train error");
                return false;
            }

            return true;
        }

        private static bool IsAlreadyTrainedError(HttpRequestException ex)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.Message))
            {
                return false;
            }

            var message = ex.Message;
            return message.Contains("already trained", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("ya entrenad", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("memoria existente", StringComparison.OrdinalIgnoreCase);
        }

        private async Task CalibrateSelectedRoiAsync(CancellationToken ct = default)
        {
            EnsureRoleRoi();
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                await ShowMessageAsync("Select an Inspection ROI first.", "Calibrate");
                return;
            }

            if (!await EnsureTrainedBeforeCalibrateAsync(roi, ct).ConfigureAwait(false))
            {
                return;
            }

            var analysis = await EnsureDatasetAnalysisAsync(roi).ConfigureAwait(false);
            if (!analysis.IsValid && analysis.OkCount == 0)
            {
                await ShowMessageAsync(analysis.StatusMessage, caption: "Dataset not ready");
                return;
            }

            var okEntries = analysis.Entries.Where(e => e.IsOk).ToList();
            if (okEntries.Count == 0)
            {
                await ShowMessageAsync("Dataset has no OK samples for calibration.", "Calibrate");
                return;
            }

            var koEntries = analysis.Entries.Where(e => !e.IsOk).ToList();

            var okScores = new List<double>();
            foreach (var entry in okEntries)
            {
                var score = await InferScoreForCalibrationAsync(roi, entry.Path, ct).ConfigureAwait(false);
                if (!score.HasValue)
                {
                    return;
                }

                okScores.Add(score.Value);
            }

            var ngScores = new List<double>();
            foreach (var entry in koEntries)
            {
                var score = await InferScoreForCalibrationAsync(roi, entry.Path, ct).ConfigureAwait(false);
                if (!score.HasValue)
                {
                    return;
                }

                ngScores.Add(score.Value);
            }

            double? threshold = null;
            CalibResult? calibResult = null;
            if (await EnsureCalibrateEndpointAsync().ConfigureAwait(false))
            {
                try
                {
                    var calib = await _client.CalibrateAsync(RoleId, roi.ModelKey, MmPerPx, okScores, ngScores.Count > 0 ? ngScores : null, ct: ct).ConfigureAwait(false);
                    threshold = calib.threshold;
                    calibResult = calib;
                    CalibrationSummary = $"Threshold={calib.threshold:0.###} OKµ={calib.ok_mean:0.###} NGµ={calib.ng_mean:0.###} Percentile={calib.score_percentile:0.###}";
                }
                catch (HttpRequestException ex)
                {
                    _log("[calibrate] backend error: " + ex.Message);
                }
            }

            if (threshold == null)
            {
                threshold = ComputeYoudenThreshold(okScores, ngScores, roi.ThresholdDefault);
                CalibrationSummary = $"Threshold={threshold:0.###} (local)";
                calibResult = new CalibResult
                {
                    threshold = threshold,
                    ok_mean = okScores.Count > 0 ? okScores.Average() : 0.0,
                    ng_mean = ngScores.Count > 0 ? ngScores.Average() : 0.0,
                    score_percentile = 0.0,
                    area_mm2_thr = 0.0
                };
            }

            roi.CalibratedThreshold = threshold;
            OnPropertyChanged(nameof(SelectedInspectionRoi));
            UpdateGlobalBadge();

            if (calibResult != null)
            {
                _lastCalibResultsByRoi[roi] = calibResult;
            }

            await SaveModelManifestAsync(roi, GetLastFitResult(roi), calibResult, ct).ConfigureAwait(false);
        }

        public void ResetModelStates()
        {
            _lastFitResultsByRoi.Clear();
            _lastCalibResultsByRoi.Clear();
            FitSummary = string.Empty;
            CalibrationSummary = string.Empty;
            InferenceSummary = string.Empty;
            _lastExport = null;
            _lastInferResult = null;
            _lastHeatmapBytes = null;
            ClearInferenceUi("[model] reset via canvas clear");

            if (_inspectionRois != null)
            {
                foreach (var roi in _inspectionRois)
                {
                    roi.HasFitOk = false;
                    roi.CalibratedThreshold = null;
                    roi.LastScore = null;
                    roi.LastResultOk = null;
                    roi.LastEvaluatedAt = null;
                }
            }

            UpdateGlobalBadge();
        }

        public async Task<bool> LoadModelManifestAsync(InspectionRoiConfig roi, string filePath)
        {
            if (roi == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                await ShowMessageAsync("Model file not found.", "Load model", MessageBoxImage.Warning);
                return false;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var manifest = JsonSerializer.Deserialize<InspectionModelManifest>(json, ManifestJsonOptions);
                if (manifest == null)
                {
                    throw new InvalidOperationException("Empty model manifest.");
                }

                if (manifest.roi_index > 0 && manifest.roi_index != roi.Index)
                {
                    await ShowMessageAsync(
                        $"El modelo seleccionado corresponde a Inspection {manifest.roi_index}.",
                        "Load model",
                        MessageBoxImage.Warning);
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!string.IsNullOrWhiteSpace(manifest.model_key)
                        && !string.Equals(roi.ModelKey, manifest.model_key, StringComparison.OrdinalIgnoreCase))
                    {
                        roi.ModelKey = manifest.model_key;
                    }

                    roi.HasFitOk = manifest.fit != null;
                    roi.CalibratedThreshold = manifest.calibration?.threshold;
                    roi.LastScore = null;
                    roi.LastResultOk = null;
                    roi.LastEvaluatedAt = null;
                });

                ClearInferenceUi("[model] load manifest");

                var manifestFit = manifest.fit;
                var needsFit = manifestFit == null;

                _lastFitResultsByRoi.Remove(roi);
                FitSummary = string.Empty;

                if (needsFit)
                {
                    if (await EnsureFitEndpointAsync().ConfigureAwait(false))
                    {
                        var okPaths = OkSamples?
                            .Where(s => !s.IsNg
                                        && s.Metadata != null
                                        && !string.IsNullOrWhiteSpace(s.Metadata.roi_id)
                                        && string.Equals(
                                            NormalizeInspectionKeyFromMetadata(s.Metadata.roi_id) ?? string.Empty,
                                            roi.ModelKey,
                                            StringComparison.OrdinalIgnoreCase))
                            .Select(s => s.ImagePath)
                            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList()
                            ?? new List<string>();

                        if (okPaths.Count > 0)
                        {
                            try
                            {
                                _log($"[fit] auto-fit tras LoadModel → {okPaths.Count} OK samples, roi={roi.ModelKey}");
                                var fit = await _client.FitOkAsync(RoleId, roi.ModelKey, MmPerPx, okPaths, roi.TrainMemoryFit)
                                                       .ConfigureAwait(false);

                                _lastFitResultsByRoi[roi] = fit;
                                FitSummary = $"Embeddings={fit.n_embeddings} Coreset={fit.coreset_size} TokenShape=[{string.Join(',', fit.token_shape ?? Array.Empty<int>())}]";
                                await Application.Current.Dispatcher.InvokeAsync(() => roi.HasFitOk = true);
                            }
                            catch (Exception ex)
                            {
                                _log($"[fit] auto-fit tras LoadModel falló: {ex.Message}");
                            }
                        }
                        else
                        {
                            _log("[fit] auto-fit omitido: no hay OK samples para este ROI.");
                        }
                    }
                    else
                    {
                        _log("[fit] auto-fit omitido: backend sin endpoint /fit_ok disponible.");
                    }
                }
                else
                {
                    _lastFitResultsByRoi[roi] = manifestFit;
                    FitSummary = $"Embeddings={manifestFit.n_embeddings} Coreset={manifestFit.coreset_size} TokenShape=[{string.Join(',', manifestFit.token_shape ?? Array.Empty<int>())}]";
                }

                if (manifest.calibration != null)
                {
                    _lastCalibResultsByRoi[roi] = manifest.calibration;
                    CalibrationSummary = $"Threshold={(manifest.calibration.threshold ?? double.NaN):0.###} OKµ={manifest.calibration.ok_mean:0.###} NGµ={manifest.calibration.ng_mean:0.###} Percentile={manifest.calibration.score_percentile:0.###}";

                    if (manifest.calibration.threshold is double thr && thr > 0)
                    {
                        LocalThreshold = thr;
                    }
                }
                else
                {
                    _lastCalibResultsByRoi.Remove(roi);
                    CalibrationSummary = string.Empty;
                }

                SelectedInspectionRoi = roi;
                OnPropertyChanged(nameof(SelectedInspectionRoi));
                UpdateGlobalBadge();

                _log($"[model] manifest cargado '{filePath}' para ROI '{roi.Name}' (idx={roi.Index})");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[model] load manifest failed: {ex.Message}");
                await ShowMessageAsync(
                    $"No se pudo cargar el modelo.\n{ex.Message}",
                    "Load model",
                    MessageBoxImage.Error);
                return false;
            }
        }

        private FitOkResult? GetLastFitResult(InspectionRoiConfig roi)
            => roi != null && _lastFitResultsByRoi.TryGetValue(roi, out var value) ? value : null;

        private async Task SaveModelManifestAsync(InspectionRoiConfig roi, FitOkResult? fit, CalibResult? calib, CancellationToken ct)
        {
            if (roi == null)
            {
                return;
            }

            if (fit == null && calib == null)
            {
                return;
            }

            if (_resolveModelDirectory == null)
            {
                return;
            }

            string? targetDirectory;
            try
            {
                targetDirectory = _resolveModelDirectory(roi);
            }
            catch (Exception ex)
            {
                _log($"[model] error resolving directory: {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(targetDirectory);

                var manifest = new InspectionModelManifest
                {
                    manifest_version = "1.0",
                    role_id = RoleId,
                    model_key = roi.ModelKey,
                    roi_index = roi.Index,
                    roi_name = roi.Name,
                    mm_per_px = MmPerPx,
                    trained_at_utc = fit != null ? DateTimeOffset.UtcNow : null,
                    calibrated_at_utc = calib != null ? DateTimeOffset.UtcNow : null,
                    fit = fit ?? GetLastFitResult(roi),
                    calibration = calib ?? (_lastCalibResultsByRoi.TryGetValue(roi, out var lastCalib) ? lastCalib : null)
                };

                if (manifest.fit == null && manifest.calibration == null)
                {
                    return;
                }

                string fileName = $"model_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                string manifestPath = Path.Combine(targetDirectory, fileName);
                string json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
                await File.WriteAllTextAsync(manifestPath, json, ct).ConfigureAwait(false);

                try
                {
                    var latestPath = Path.Combine(targetDirectory, "latest_model.json");
                    File.Copy(manifestPath, latestPath, overwrite: true);
                }
                catch (Exception copyEx)
                {
                    _log($"[model] no se pudo actualizar latest_model.json: {copyEx.Message}");
                }

                _log($"[model] manifest guardado en '{manifestPath}'");
            }
            catch (Exception ex)
            {
                _log($"[model] error al guardar manifest: {ex.Message}");
            }
        }

        private async Task<bool> EnsureTrainedBeforeCalibrateAsync(InspectionRoiConfig? roi, CancellationToken ct)
        {
            if (roi == null)
            {
                await ShowMessageAsync("No ROI selected.", "Calibrate");
                return false;
            }

            if (string.IsNullOrWhiteSpace(roi.DatasetPath))
            {
                await ShowMessageAsync($"Dataset path is empty for ROI '{roi.Name}'. Select a dataset first.", "Calibrate");
                return false;
            }

            try
            {
                return await TrainSelectedRoiAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync($"Training failed before calibration.\n{ex.Message}", "Calibrate");
                return false;
            }
        }

        private async Task<double?> InferScoreForCalibrationAsync(InspectionRoiConfig roi, string samplePath, CancellationToken ct)
        {
            bool retried = false;
            while (true)
            {
                try
                {
                    var infer = await _client.InferAsync(RoleId, roi.ModelKey, MmPerPx, samplePath, null, ct).ConfigureAwait(false);
                    return infer.score;
                }
                catch (HttpRequestException ex) when (!retried && ex.Message.Contains("Memoria no encontrada", StringComparison.OrdinalIgnoreCase))
                {
                    retried = true;
                    var retrained = await TrainSelectedRoiAsync(ct).ConfigureAwait(false);
                    if (!retrained)
                    {
                        await ShowMessageAsync("Training failed while retrying calibration inference.", "Calibrate");
                        return null;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
                {
                    await ShowMessageAsync($"Inference failed for '{Path.GetFileName(samplePath)}'.\n{ex.Message}", "Calibrate");
                    return null;
                }
            }
        }

        private async Task EvaluateSelectedRoiAsync()
        {
            var roiCfg = SelectedInspectionRoi;
            if (roiCfg == null)
            {
                return;
            }

            await ActivateCanvasIndexAsync(roiCfg.Index).ConfigureAwait(false);

            await EvaluateRoiAsync(roiCfg, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task InferEnabledRoisAsync()
        {
            if (_inspectionRois == null)
            {
                return;
            }

            foreach (var roi in _inspectionRois.Where(r => r.Enabled))
            {
                await EvaluateRoiAsync(roi, CancellationToken.None).ConfigureAwait(false);
            }

            UpdateGlobalBadge();
        }

        private async Task EvaluateAllRoisAsync()
        {
            await InferEnabledRoisAsync().ConfigureAwait(false);
        }

        private async Task EvaluateRoiAsync(InspectionRoiConfig? roi, CancellationToken ct)
        {
            EnsureRoleRoi();
            if (roi == null || !roi.Enabled)
            {
                return;
            }

            if (!ValidateInferenceInputs(roi, out var validationError))
            {
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    await ShowMessageAsync(validationError, caption: "Cannot evaluate");
                }
                return;
            }

            await ActivateCanvasIndexAsync(roi.Index).ConfigureAwait(false);
            _log($"[eval] start -> name='{roi.Name}' idx={roi.Index} modelKey='{roi.ModelKey}'");

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                _log("[eval] export cancelled");
                return;
            }

            try
            {
                var exRoi = export.RoiImage;
                var crop = export.CropRect;
                _log($"[eval] export -> slotIdx={roi.Index} roiId='{exRoi?.Id}' shape={exRoi?.Shape} " +
                     $"crop=({crop.X},{crop.Y},{crop.Width},{crop.Height})");
            }
            catch
            {
                // logging best-effort
            }

            if (export.PngBytes == null || export.PngBytes.Length == 0)
            {
                _log("[eval] export produced empty payload");
                await ShowMessageAsync("ROI crop is empty. Save the ROI layout and try again.", caption: "Inference aborted");
                return;
            }

            var inferFileName = $"roi_{DateTime.UtcNow:yyyyMMddHHmmssfff}.png";
            var resolvedRoiId = NormalizeInspectionKey(
                string.Equals(RoleId, "Inspection", StringComparison.OrdinalIgnoreCase) ? RoiId : roi.ModelKey,
                roi.Index);

            if (!string.IsNullOrWhiteSpace(roi.ModelKey) &&
                !string.Equals(roi.ModelKey, resolvedRoiId, StringComparison.Ordinal))
            {
                roi.ModelKey = resolvedRoiId;
            }

            if (!string.Equals(RoiId, resolvedRoiId, StringComparison.Ordinal))
            {
                RoiId = resolvedRoiId;
            }

            async Task ApplyInferResultAsync(InferResult result)
            {
                _lastExport = export;
                _lastInferResult = result;
                InferenceScore = result.score;
                InferenceThreshold = result.threshold;

                if (result.threshold is double thresholdValue && thresholdValue > 0)
                {
                    LocalThreshold = thresholdValue;
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Regions.Clear();
                    if (result.regions != null)
                    {
                        foreach (var region in result.regions)
                        {
                            Regions.Add(region);
                        }
                    }
                });

                if (!string.IsNullOrWhiteSpace(result.heatmap_png_base64))
                {
                    _lastHeatmapBytes = Convert.FromBase64String(result.heatmap_png_base64);
                    await _showHeatmapAsync(export, _lastHeatmapBytes, HeatmapOpacity).ConfigureAwait(false);
                }
                else
                {
                    _lastHeatmapBytes = null;
                    _clearHeatmap();
                }

                roi.LastScore = result.score;
                var decisionThreshold = roi.CalibratedThreshold ?? roi.ThresholdDefault;
                bool isNg = result.score > decisionThreshold;
                roi.LastResultOk = !isNg;
                roi.LastEvaluatedAt = DateTime.UtcNow;
                _log($"[eval] done idx={roi.Index} key='{roi.ModelKey}' score={result.score:0.###} thr={decisionThreshold:0.###} => {(isNg ? "NG" : "OK")}");
                OnPropertyChanged(nameof(SelectedInspectionRoi));
                UpdateGlobalBadge();
            }

            async Task ResetAfterFailureAsync(string message, string caption, string? extraLog = null)
            {
                _lastExport = export;
                _lastInferResult = null;
                _lastHeatmapBytes = null;
                InferenceScore = null;
                InferenceThreshold = null;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Regions.Clear();
                });
                _clearHeatmap();
                roi.LastScore = null;
                roi.LastResultOk = null;
                roi.LastEvaluatedAt = DateTime.UtcNow;
                InferenceSummary = $"Inference failed: {message}";
                if (!string.IsNullOrWhiteSpace(extraLog))
                {
                    _log(extraLog);
                }
                _log($"[eval] FAILED idx={roi.Index} key='{roi.ModelKey}' -> {message}");
                await ShowMessageAsync(message, caption);
                UpdateGlobalBadge();
            }

            try
            {
                if (string.Equals(RoleId, "Inspection", StringComparison.OrdinalIgnoreCase))
                {
                    await _client.EnsureFittedAsync(RoleId, RoiId, MmPerPx, ct: ct).ConfigureAwait(false);
                }

                var result = await _client.InferAsync(RoleId, resolvedRoiId, MmPerPx, export.PngBytes, inferFileName, export.ShapeJson, ct).ConfigureAwait(false);
                await ApplyInferResultAsync(result).ConfigureAwait(false);
            }
            catch (BackendMemoryNotFittedException)
            {
                var okPaths = OkSamples?
                    .Where(s => !s.IsNg && s.Metadata?.roi_id != null)
                    .Where(s =>
                    {
                        var normalized = NormalizeInspectionKeyFromMetadata(s.Metadata!.roi_id);
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            return string.Equals(normalized, resolvedRoiId, StringComparison.OrdinalIgnoreCase);
                        }

                        return string.Equals(s.Metadata.roi_id.Trim(), resolvedRoiId, StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(s => s.ImagePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct()
                    .ToList() ?? new List<string>();

                if (okPaths.Count == 0)
                {
                    await ResetAfterFailureAsync("Falta memoria OK para este ROI y no hay OK samples disponibles.", "Evaluate ROI").ConfigureAwait(false);
                    return;
                }

                try
                {
                    await _client.FitOkAsync(RoleId, RoiId, MmPerPx, okPaths, memoryFit: false, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await ResetAfterFailureAsync("Error en fit_ok. Revisa los OK samples.", "Evaluate ROI", $"[fit_ok] error: {ex.Message}").ConfigureAwait(false);
                    return;
                }

                try
                {
                    var retryResult = await _client.InferAsync(RoleId, resolvedRoiId, MmPerPx, export.PngBytes, inferFileName, export.ShapeJson, ct).ConfigureAwait(false);
                    await ApplyInferResultAsync(retryResult).ConfigureAwait(false);
                }
                catch (BackendBadRequestException brex)
                {
                    var detail = string.IsNullOrWhiteSpace(brex.Detail) ? brex.Message : brex.Detail!;
                    await ResetAfterFailureAsync($"❌ Backend 400: {detail}", "Evaluate ROI", $"[/infer 400] {detail}").ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    var message = ExtractBackendError(ex);
                    await ResetAfterFailureAsync(message, "Inference error").ConfigureAwait(false);
                }

                return;
            }
            catch (BackendBadRequestException brex)
            {
                var detail = string.IsNullOrWhiteSpace(brex.Detail) ? brex.Message : brex.Detail!;
                await ResetAfterFailureAsync($"❌ Backend 400: {detail}", "Evaluate ROI", $"[/infer 400] {detail}").ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var message = ExtractBackendError(ex);
                await ResetAfterFailureAsync(message, "Inference error").ConfigureAwait(false);
            }
        }

        private async Task ActivateCanvasIndexAsync(int index)
        {
            try
            {
                _log($"[ui] ActivateCanvasIndexAsync({index})");
                _activateInspectionIndex?.Invoke(index);
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task.ConfigureAwait(false);
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[ui] ActivateCanvasIndexAsync error: {ex.Message}");
            }
        }

        private void ClearInferenceUi(string reason)
        {
            try
            {
                _log($"{reason} -> clearing inference/heatmap");
            }
            catch
            {
                // ignore logging failures
            }

            _lastExport = null;
            _lastInferResult = null;
            _lastHeatmapBytes = null;
            InferenceScore = null;
            InferenceThreshold = null;
            InferenceSummary = string.Empty;

            try
            {
                _clearHeatmap();
            }
            catch
            {
                // ignore
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Regions.Clear();
            }
            else if (dispatcher.CheckAccess())
            {
                Regions.Clear();
            }
            else
            {
                dispatcher.InvokeAsync(() => Regions.Clear());
            }
        }

        public void OnInspectionTabChanged(int index)
        {
            try
            {
                var roiCfg = _inspectionRois?.FirstOrDefault(r => r.Index == index);
                if (roiCfg != null)
                {
                    _log($"[ui] OnInspectionTabChanged -> {roiCfg.DisplayName} idx={index}");
                    SelectedInspectionRoi = roiCfg;
                }
            }
            catch (Exception ex)
            {
                _log($"[ui] OnInspectionTabChanged error: {ex.Message}");
            }
        }

        private bool ValidateInferenceInputs(InspectionRoiConfig roi, out string? message)
        {
            if (_inspectionRois == null || !_inspectionRois.Contains(roi))
            {
                message = "Save the ROI layout before evaluating.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(roi.ModelKey))
            {
                message = "ModelKey is required for inference.";
                return false;
            }

            if (string.Equals(RoleId, "Inspection", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(RoiId) ||
                    !Regex.IsMatch(RoiId, @"^inspection-[1-4]$", RegexOptions.IgnoreCase))
                {
                    message = "Solo son válidos ROI de inspección: inspection-1..inspection-4.";
                    return false;
                }
            }

            if (!roi.HasFitOk)
            {
                message = $"Run fit_ok for ROI '{roi.Name}' before inference.";
                return false;
            }

            var sourceImage = _getSourceImagePath();
            if (string.IsNullOrWhiteSpace(sourceImage) || !File.Exists(sourceImage))
            {
                message = "Load an inspection image before evaluating.";
                return false;
            }

            message = null;
            return true;
        }

        private static string ExtractBackendError(HttpRequestException ex)
        {
            var message = ex.Message;
            int colonIndex = message.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < message.Length - 1)
            {
                var tail = message[(colonIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    return tail;
                }
            }

            return message;
        }

        private void StopBatch()
        {
            try
            {
                _batchCts?.Cancel();
            }
            catch
            {
            }

            _pauseGate.Set();
            _isBatchRunning = false;
            SetBatchStatusSafe("Stopped");
            UpdateBatchCommandStates();
        }

        private async Task RunBatchAsync(CancellationToken ct)
        {
            EnsureRoleRoi();

            var rows = GetBatchRowsSnapshot();
            int total = rows.Count;

            if (string.IsNullOrWhiteSpace(BatchFolder) || !Directory.Exists(BatchFolder) || total == 0)
            {
                UpdateBatchProgress(0, total);
                SetBatchStatusSafe(total == 0 ? "No images to process." : "Folder not found.");
                _isBatchRunning = false;
                _pauseGate.Set();
                _batchCts?.Dispose();
                _batchCts = null;
                IsBusy = false;
                UpdateBatchCommandStates();
                return;
            }

            var ensuredModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int processed = 0;

            UpdateBatchProgress(0, total);
            SetBatchStatusSafe("Running...");

            try
            {
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    _pauseGate.Wait(ct);

                    processed++;
                    SetBatchStatusSafe($"[{processed}/{total}] {row.FileName}");

                    if (!File.Exists(row.FullPath))
                    {
                        _log($"[batch] file missing: '{row.FullPath}'");
                        for (int roiIndex = 1; roiIndex <= 4; roiIndex++)
                        {
                            UpdateBatchRowStatus(row, roiIndex, BatchCellStatus.Nok);
                        }
                        UpdateBatchProgress(processed, total);
                        continue;
                    }

                    SetBatchBaseImage(row.FullPath);

                    await RepositionInspectionRoisForImageAsync(row.FullPath, ct).ConfigureAwait(false);

                    for (int roiIndex = 1; roiIndex <= 4; roiIndex++)
                    {
                        ct.ThrowIfCancellationRequested();
                        _pauseGate.Wait(ct);

                        var config = GetInspectionConfigByIndex(roiIndex);
                        if (config == null)
                        {
                            UpdateBatchRowStatus(row, roiIndex, BatchCellStatus.Nok);
                            continue;
                        }

                        _log($"[batch] processing file='{row.FileName}' roiIdx={config.Index} enabled={config.Enabled}");

                        if (!config.Enabled)
                        {
                            UpdateBatchRowStatus(row, config.Index, BatchCellStatus.Unknown);
                            InvokeOnUi(ClearBatchHeatmap);
                            _clearHeatmap();
                            _log($"[batch] skip disabled roi idx={config.Index} '{config.DisplayName}'");
                            continue;
                        }

                        try
                        {
                            var export = await ExportRoiFromFileAsync(config, row.FullPath).ConfigureAwait(false);

                            var resolvedRoiId = NormalizeInspectionKey(config.ModelKey, config.Index);
                            if (!string.Equals(config.ModelKey, resolvedRoiId, StringComparison.Ordinal))
                            {
                                config.ModelKey = resolvedRoiId;
                            }

                            if (ensuredModels.Add(resolvedRoiId))
                            {
                                try
                                {
                                    await _client.EnsureFittedAsync(RoleId, resolvedRoiId, MmPerPx).ConfigureAwait(false);
                                }
                                catch (BackendMemoryNotFittedException ex)
                                {
                                    _log($"[batch] ensure_fitted missing roi='{resolvedRoiId}': {ex.Message}");
                                    UpdateBatchRowStatus(row, roiIndex, BatchCellStatus.Nok);
                                    continue;
                                }
                            }

                            UpdateBatchHeatmapIndex(config.Index);

                            var result = await _client.InferAsync(RoleId, resolvedRoiId, MmPerPx, export.Bytes, export.FileName, export.ShapeJson).ConfigureAwait(false);

                            UpdateHeatmapFromResult(result, config.Index);

                            InvokeOnUi(() =>
                            {
                                var canvasRect = GetInspectionRoiCanvasRect(config.Index);
                                TraceBatchHeatmapPlacement("per-roi", config.Index, canvasRect);
                            });

                            double decisionThreshold = result.threshold
                                ?? config.CalibratedThreshold
                                ?? config.ThresholdDefault;

                            if (decisionThreshold <= 0 && LocalThreshold > 0)
                            {
                                decisionThreshold = LocalThreshold;
                            }

                            bool isNg = result.score > decisionThreshold;
                            UpdateBatchRowStatus(row, config.Index, isNg ? BatchCellStatus.Nok : BatchCellStatus.Ok);

                            if (BatchPausePerRoiSeconds > 0)
                            {
                                var pause = TimeSpan.FromSeconds(BatchPausePerRoiSeconds);
                                if (pause > TimeSpan.Zero)
                                {
                                    await Task.Delay(pause, ct).ConfigureAwait(false);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _log($"[batch] ROI{roiIndex} failed for '{row.FileName}': {ex.Message}");
                            UpdateBatchRowStatus(row, roiIndex, BatchCellStatus.Nok);
                        }
                    }

                    UpdateBatchProgress(processed, total);
                }

                _log($"[batch] completed processed={processed}/{total}");
                SetBatchStatusSafe("Completed");
            }
            catch (OperationCanceledException)
            {
                if (!string.Equals(BatchStatus, "Stopped", StringComparison.OrdinalIgnoreCase))
                {
                    SetBatchStatusSafe("Cancelled");
                }
            }
            catch (Exception ex)
            {
                _log($"[batch] unexpected error: {ex.Message}");
                SetBatchStatusSafe($"Error: {ex.Message}");
            }
            finally
            {
                _isBatchRunning = false;
                _pauseGate.Set();
                _batchCts?.Dispose();
                _batchCts = null;
                IsBusy = false;
                UpdateBatchCommandStates();
            }
        }

        private void UpdateBatchHeatmapIndex(int roiIndex)
        {
            InvokeOnUi(() => BatchHeatmapRoiIndex = Math.Max(1, Math.Min(4, roiIndex)));
        }

        private async Task RepositionInspectionRoisForImageAsync(string imagePath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || _repositionInspectionRoisAsync == null)
            {
                return;
            }

            try
            {
                await _repositionInspectionRoisAsync(imagePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[batch] reposition failed for '{imagePath}': {ex.Message}");
            }
        }

        private bool CanExecuteStart()
        {
            if (_isBatchRunning)
            {
                return !_pauseGate.IsSet;
            }

            return CanStartBatch && !IsBusy;
        }

        private void UpdateBatchCommandStates()
        {
            StartBatchCommand?.RaiseCanExecuteChanged();
            PauseBatchCommand?.RaiseCanExecuteChanged();
            StopBatchCommand?.RaiseCanExecuteChanged();
        }

        private void UpdateBatchProgress(int processed, int total)
        {
            string summary = total > 0
                ? $"{total} images found · {processed}/{total} processed"
                : "No images found";
            SetBatchSummarySafe(summary);
        }

        private void SetBatchStatusSafe(string message)
        {
            InvokeOnUi(() => BatchStatus = message);
        }

        private void SetBatchSummarySafe(string summary)
        {
            InvokeOnUi(() => BatchSummary = summary);
        }

        private void UpdateCanStart()
        {
            var folder = _batchFolder;
            bool canStart = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && _batchRows.Count > 0;
            CanStartBatch = canStart;
        }

        private void DoBrowseBatchFolder()
        {
            try
            {
                using var dlg = new Forms.FolderBrowserDialog
                {
                    Description = "Select image folder",
                    UseDescriptionForTitle = true
                };
                var result = dlg.ShowDialog();
                if (result == Forms.DialogResult.OK)
                {
                    BatchFolder = dlg.SelectedPath;
                    LoadBatchListFromFolder(dlg.SelectedPath);
                }
            }
            catch (Exception ex)
            {
                _log($"[batch] browse failed: {ex.Message}");
            }
        }

        private void LoadBatchListFromFolder(string dir)
        {
            List<string> files;
            try
            {
                _log($"[batch] dir={dir} exists={Directory.Exists(dir)}");
                files = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(p => BatchImageExtensions.Contains(Path.GetExtension(p)))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                for (int i = 0; i < Math.Min(12, files.Count); i++)
                {
                    _log($"[batch] file[{i}] {files[i]}");
                }
            }
            catch (Exception ex)
            {
                _log($"[batch] load folder failed: {ex.Message}");
                InvokeOnUi(() =>
                {
                    _batchRows.Clear();
                    BatchSummary = $"error: {ex.Message}";
                    BatchStatus = string.Empty;
                    BatchImageSource = null;
                    ClearBatchHeatmap();
                    UpdateCanStart();
                });
                return;
            }

            InvokeOnUi(() =>
            {
                _batchRows.Clear();
                foreach (var file in files)
                {
                    _batchRows.Add(new BatchRow(file));
                }

                BatchSummary = files.Count == 0 ? $"no images in {dir}" : $"{files.Count} images found in {dir}";
                BatchStatus = string.Empty;
                BatchImageSource = null;
                ClearBatchHeatmap();
                UpdateCanStart();
            });
        }

        private List<BatchRow> GetBatchRowsSnapshot()
        {
            List<BatchRow>? snapshot = null;
            InvokeOnUi(() => snapshot = _batchRows.ToList());
            return snapshot ?? new List<BatchRow>();
        }

        private InspectionRoiConfig? GetInspectionConfigByIndex(int index)
            => _inspectionRois?.FirstOrDefault(r => r.Index == index);

        private RoiModel? GetInspectionModelByIndex(int index)
            => index switch
            {
                1 => Inspection1,
                2 => Inspection2,
                3 => Inspection3,
                4 => Inspection4,
                _ => null
            };

        private void UpdateBatchRowStatus(BatchRow row, int roiIndex, BatchCellStatus status)
        {
            if (row == null)
            {
                return;
            }

            InvokeOnUi(() =>
            {
                switch (roiIndex)
                {
                    case 1:
                        row.ROI1 = status;
                        break;
                    case 2:
                        row.ROI2 = status;
                        break;
                    case 3:
                        row.ROI3 = status;
                        break;
                    case 4:
                        row.ROI4 = status;
                        break;
                }
            });
        }

        private static BitmapSource NormalizeDpiTo96(BitmapSource src)
        {
            if (src == null)
            {
                throw new ArgumentNullException(nameof(src));
            }

            if (Math.Abs(src.DpiX - 96.0) < 0.01 && Math.Abs(src.DpiY - 96.0) < 0.01)
            {
                return src;
            }

            var converted = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32
                ? src
                : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            if (converted.CanFreeze)
            {
                converted.Freeze();
            }

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = (width * converted.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var writable = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            writable.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
            writable.Freeze();
            return writable;
        }

        private static BitmapSource DecodeImageTo96Dpi(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return NormalizeDpiTo96(bmp);
        }

        private static WriteableBitmap ApplyRedGreenPalette(BitmapSource grayLike, int cutoffPercent)
        {
            if (grayLike == null)
            {
                throw new ArgumentNullException(nameof(grayLike));
            }

            var gray = grayLike.Format == PixelFormats.Gray8
                ? grayLike
                : new FormatConvertedBitmap(grayLike, PixelFormats.Gray8, null, 0);

            if (gray.CanFreeze)
            {
                gray.Freeze();
            }

            int width = gray.PixelWidth;
            int height = gray.PixelHeight;
            int strideGray = (width * gray.Format.BitsPerPixel + 7) / 8;
            var grayBuffer = new byte[strideGray * height];
            gray.CopyPixels(grayBuffer, strideGray, 0);

            var output = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            int strideOut = (width * output.Format.BitsPerPixel + 7) / 8;
            var outBuffer = new byte[strideOut * height];

            int cutoffByte = Math.Clamp((int)(cutoffPercent * 255 / 100.0), 0, 255);

            for (int y = 0; y < height; y++)
            {
                int grayRow = y * strideGray;
                int outRow = y * strideOut;
                for (int x = 0; x < width; x++)
                {
                    byte value = grayBuffer[grayRow + x];
                    int offset = outRow + x * 4;

                    if (value == 0)
                    {
                        outBuffer[offset + 0] = 0;
                        outBuffer[offset + 1] = 0;
                        outBuffer[offset + 2] = 0;
                        outBuffer[offset + 3] = 0;
                        continue;
                    }

                    byte alpha = value;

                    if (value >= cutoffByte)
                    {
                        outBuffer[offset + 0] = 0;
                        outBuffer[offset + 1] = 0;
                        outBuffer[offset + 2] = alpha;
                        outBuffer[offset + 3] = alpha;
                    }
                    else
                    {
                        outBuffer[offset + 0] = 0;
                        outBuffer[offset + 1] = alpha;
                        outBuffer[offset + 2] = 0;
                        outBuffer[offset + 3] = alpha;
                    }
                }
            }

            output.WritePixels(new Int32Rect(0, 0, width, height), outBuffer, strideOut, 0);
            output.Freeze();
            return output;
        }

        private static void VmLog(string msg)
        {
            Debug.WriteLine(msg);
            GuiLog.Info(msg);
        }

        private static void LogImg(string tag, ImageSource? src)
        {
            try
            {
                if (src is BitmapSource b)
                {
                    VmLog($"[heatmap:{tag}] type={b.GetType().Name} px={b.PixelWidth}x{b.PixelHeight} dpi=({b.DpiX:0.##},{b.DpiY:0.##}) fmt={b.Format} frozen={b.IsFrozen}");
                }
                else
                {
                    VmLog($"[heatmap:{tag}] src={(src == null ? "NULL" : src.GetType().Name)}");
                }
            }
            catch
            {
            }
        }

        private void LogHeatmapState(string tag)
        {
            try
            {
                var bytes = _lastHeatmapPngBytes?.Length ?? 0;
                VmLog($"[heatmap:{tag}] cutoff={HeatmapCutoffPercent}% bytes={bytes}");
                if (_lastHeatmapBitmap != null)
                {
                    VmLog($"[heatmap:{tag}] wb px={_lastHeatmapBitmap.PixelWidth}x{_lastHeatmapBitmap.PixelHeight} dpi=({_lastHeatmapBitmap.DpiX:0.##},{_lastHeatmapBitmap.DpiY:0.##})");
                }
            }
            catch
            {
            }
        }

        private void SetBatchBaseImage(string path)
        {
            InvokeOnUi(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    var base96 = NormalizeDpiTo96(bmp);
                    BatchImageSource = base96;
                    LogImg("base:set", BatchImageSource);
                }
                catch (Exception ex)
                {
                    _log($"[batch] image load failed: {ex.Message}");
                    BatchImageSource = null;
                }

                ClearBatchHeatmap();
            });
        }

        public void SetBatchHeatmapForRoi(byte[]? heatmapPngBytes, int roiIndex)
        {
            BatchHeatmapRoiIndex = Math.Max(1, Math.Min(4, roiIndex));
            _lastHeatmapPngBytes = heatmapPngBytes ?? Array.Empty<byte>();

            try
            {
                UpdateHeatmapThreshold();
                GuiLog.Info($"[batch-hm:VM] set roiIdx={BatchHeatmapRoiIndex} bytes={_lastHeatmapPngBytes.Length}");
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[batch-hm:VM] SetBatchHeatmapForRoi failed", ex); // CODEX: FormattableString compatibility.
            }
        }

        private void UpdateHeatmapFromResult(InferResult result, int roiIndex)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.heatmap_png_base64))
            {
                InvokeOnUi(ClearBatchHeatmap);
                return;
            }

            try
            {
                var heatmapBytes = Convert.FromBase64String(result.heatmap_png_base64);

                InvokeOnUi(() =>
                {
                    try
                    {
                        SetBatchHeatmapForRoi(heatmapBytes, roiIndex);
                        LogImg("hm:set-after-update", BatchHeatmapSource);
                        LogHeatmapState("hm:after-update");
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Error($"[heatmap] failed to decode batch heatmap", ex); // CODEX: FormattableString compatibility.
                        ClearBatchHeatmap();
                    }
                });
            }
            catch (FormatException ex)
            {
                _log($"[batch] invalid heatmap payload: {ex.Message}");
                InvokeOnUi(ClearBatchHeatmap);
            }
        }

        private void UpdateHeatmapThreshold()
        {
            HeatmapInfo = $"Cutoff: {HeatmapCutoffPercent}%";

            if (_lastHeatmapPngBytes == null || _lastHeatmapPngBytes.Length == 0)
            {
                BatchHeatmapSource = null;
                OnPropertyChanged(nameof(BatchHeatmapSource));
                return;
            }

            try
            {
                var decoded = DecodeImageTo96Dpi(_lastHeatmapPngBytes!);
                _lastHeatmapBitmap = ApplyRedGreenPalette(decoded, HeatmapCutoffPercent);
                BatchHeatmapSource = _lastHeatmapBitmap;
                OnPropertyChanged(nameof(BatchHeatmapSource));
                LogImg("hm:decoded96", decoded);
                LogImg("hm:colored96", _lastHeatmapBitmap);
                LogHeatmapState("hm:threshold");
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[heatmap] UpdateHeatmapThreshold failed", ex); // CODEX: FormattableString compatibility.
            }
        }

        private void ClearBatchHeatmap()
        {
            BatchHeatmapSource = null;
            BatchHeatmapRoiIndex = 0;
            _lastHeatmapBitmap = null;
            _lastHeatmapPngBytes = null;
        }

        private async Task<(byte[] Bytes, string FileName, string ShapeJson)> ExportRoiFromFileAsync(InspectionRoiConfig roiConfig, string fullPath)
        {
            if (roiConfig == null)
            {
                throw new ArgumentNullException(nameof(roiConfig));
            }

            var roiModel = GetInspectionModelByIndex(roiConfig.Index);
            if (roiModel == null)
            {
                throw new InvalidOperationException($"ROI model missing for index {roiConfig.Index}.");
            }

            return await Task.Run(() =>
            {
                if (!BackendAPI.TryPrepareCanonicalRoi(fullPath, roiModel, out var payload, out var fileName, _log) || payload == null)
                {
                    throw new InvalidOperationException($"ROI export failed for '{fullPath}'.");
                }

                var shapeJson = payload.ShapeJson ?? string.Empty;
                return (payload.PngBytes, fileName, shapeJson);
            }).ConfigureAwait(false);
        }

        private static void InvokeOnUi(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                action();
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action);
            }
        }

        private void UpdateInferenceSummary()
        {
            if (InferenceScore == null)
            {
                InferenceSummary = string.Empty;
                return;
            }

            double thr = LocalThreshold > 0 ? LocalThreshold : (InferenceThreshold ?? 0);
            var sb = new StringBuilder();
            sb.AppendFormat("Score={0:0.###}", InferenceScore.Value);
            if (InferenceThreshold.HasValue)
            {
                sb.AppendFormat(" BackendThr={0:0.###}", InferenceThreshold.Value);
            }
            if (thr > 0)
            {
                sb.AppendFormat(" LocalThr={0:0.###}", thr);
                sb.Append(InferenceScore.Value > thr ? " → NG" : " → OK");
            }
            InferenceSummary = sb.ToString();
        }

        private static string NormalizeInspectionKey(string? key, int index)
        {
            var clamped = index;
            if (clamped < 1) clamped = 1;
            if (clamped > 4) clamped = 4;

            if (!string.IsNullOrWhiteSpace(key))
            {
                var match = Regex.Match(key, @"inspection[\s_\-]?([1-4])", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return $"inspection-{match.Groups[1].Value}";
                }
            }

            return $"inspection-{clamped}";
        }

        private static string? NormalizeInspectionKeyFromMetadata(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var match = Regex.Match(key, @"inspection[\s_\-]?([1-4])", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return $"inspection-{match.Groups[1].Value}";
            }

            return null;
        }

        private void EnsureRoleRoi()
        {
            if (string.IsNullOrWhiteSpace(RoleId))
                RoleId = "DefaultRole";
            if (string.IsNullOrWhiteSpace(RoiId))
                RoiId = "DefaultRoi";
            _datasetManager.EnsureRoleRoiDirectories(RoleId, RoiId);
        }

        private async Task<bool> EnsureFitEndpointAsync()
        {
            if (_hasFitEndpoint.HasValue)
            {
                return _hasFitEndpoint.Value;
            }

            _hasFitEndpoint = await _client.SupportsEndpointAsync("fit_ok").ConfigureAwait(false);
            return _hasFitEndpoint.Value;
        }

        private async Task<bool> EnsureCalibrateEndpointAsync()
        {
            if (_hasCalibrateEndpoint.HasValue)
            {
                return _hasCalibrateEndpoint.Value;
            }

            _hasCalibrateEndpoint = await _client.SupportsEndpointAsync("calibrate_ng").ConfigureAwait(false);
            return _hasCalibrateEndpoint.Value;
        }

        private void UpdateGlobalBadge()
        {
            try
            {
                var state = CalcGlobalDiskOk();
                if (Application.Current?.Dispatcher == null)
                {
                    _updateGlobalBadge(state);
                    return;
                }

                if (Application.Current.Dispatcher.CheckAccess())
                {
                    _updateGlobalBadge(state);
                }
                else
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => _updateGlobalBadge(state)));
                }
            }
            catch
            {
                // ignore badge errors
            }
        }

        private bool? CalcGlobalDiskOk()
        {
            if (_inspectionRois == null)
            {
                return null;
            }

            var enabled = _inspectionRois.Where(r => r.Enabled).ToList();
            if (enabled.Count == 0)
            {
                return null;
            }

            if (enabled.Any(r => r.LastResultOk == false))
            {
                return false;
            }

            if (enabled.All(r => r.LastResultOk == true))
            {
                return true;
            }

            return null;
        }

        private static async Task ShowMessageAsync(
            string message,
            string caption = "BrakeDiscInspector",
            MessageBoxImage icon = MessageBoxImage.Information)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, caption, MessageBoxButton.OK, icon);
            });
        }

        private static double ComputeYoudenThreshold(IReadOnlyCollection<double> okScores, IReadOnlyCollection<double> ngScores, double defaultValue)
        {
            if (okScores.Count == 0)
            {
                return defaultValue;
            }

            if (ngScores.Count == 0)
            {
                return okScores.Average();
            }

            var all = okScores.Select(s => (Score: s, IsOk: true))
                .Concat(ngScores.Select(s => (Score: s, IsOk: false)))
                .OrderBy(pair => pair.Score)
                .ToArray();

            int totalOk = okScores.Count;
            int totalNg = ngScores.Count;
            int okAbove = totalOk;
            int ngAbove = totalNg;

            double bestJ = double.NegativeInfinity;
            double bestThr = defaultValue;

            for (int i = 0; i < all.Length; i++)
            {
                var current = all[i];
                if (current.IsOk)
                {
                    okAbove--;
                }
                else
                {
                    ngAbove--;
                }

                double thr = current.Score;
                double tpr = totalOk > 0 ? (double)okAbove / totalOk : 0;
                double fpr = totalNg > 0 ? (double)ngAbove / totalNg : 0;
                double j = tpr - fpr;
                if (j > bestJ)
                {
                    bestJ = j;
                    bestThr = thr;
                }
            }

            return bestThr;
        }

        private sealed record RoiDatasetAnalysis(
            string DatasetPath,
            List<DatasetEntry> Entries,
            int OkCount,
            int KoCount,
            bool IsValid,
            string StatusMessage,
            List<(string Path, bool IsOk)> PreviewItems)
        {
            public static RoiDatasetAnalysis Empty(string status)
                => new(string.Empty, new List<DatasetEntry>(), 0, 0, false, status, new List<(string, bool)>());
        }

        private sealed class InspectionModelManifest
        {
            public string? manifest_version { get; set; }
            public string? role_id { get; set; }
            public string? model_key { get; set; }
            public int roi_index { get; set; }
            public string? roi_name { get; set; }
            public double? mm_per_px { get; set; }
            public DateTimeOffset? trained_at_utc { get; set; }
            public DateTimeOffset? calibrated_at_utc { get; set; }
            public FitOkResult? fit { get; set; }
            public CalibResult? calibration { get; set; }
        }

        private sealed record DatasetEntry(string Path, bool IsOk);

        private bool HasAnyEnabledInspectionRoi()
        {
            return _inspectionRois != null && _inspectionRois.Any(r => r.Enabled);
        }

        public event EventHandler? OverlayVisibilityChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void RedrawOverlays()
        {
            OverlayVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
