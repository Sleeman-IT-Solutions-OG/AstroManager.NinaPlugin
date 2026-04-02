using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Interfaces;
using NINA.Sequencer.SequenceItem.Autofocus;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Astrometry;
using NINA.Equipment.Model;
using NINA.Core.Utility.WindowService;
using Shared.Model.DTO.Client;
using Shared.Model.DTO.Scheduler;
using Shared.Model.DTO.Settings;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using System.Collections.Specialized;
using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using AstroManager.NinaPlugin.Services.Commands.Handlers;

namespace AstroManager.NinaPlugin
{
    [Export(typeof(IPluginManifest))]
    public partial class AstroManagerPlugin : PluginBase, INotifyPropertyChanged
    {
        private readonly AstroManagerSettings _settings;
        private readonly AstroManagerApiClient _apiClient;
        private readonly ScheduledTargetStore _targetStore;
        private readonly AstroManagerDataStore _dataStore;
        private readonly HeartbeatService _heartbeatService;
        
        // NINA Equipment Mediators for remote control
        private readonly ISequenceMediator _sequenceMediator;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly ICameraMediator _cameraMediator;
        private readonly IFocuserMediator _focuserMediator;
        private readonly IGuiderMediator _guiderMediator;
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IRotatorMediator _rotatorMediator;
        private readonly IDomeMediator _domeMediator;
        private readonly IFlatDeviceMediator _flatDeviceMediator;
        private readonly IWeatherDataMediator _weatherDataMediator;
        private readonly ISafetyMonitorMediator _safetyMonitorMediator;
        private readonly IProfileService _profileService;
        private readonly IImageHistoryVM _imageHistoryVM;
        private readonly IAutoFocusVMFactory _autoFocusVMFactory;
        private readonly IPlateSolverFactory _plateSolverFactory;
        private readonly IWindowServiceFactory _windowServiceFactory;
        private readonly IApplicationStatusMediator _applicationStatusMediator;
        
        private string _connectionStatus = string.Empty;
        private WpfBrush _connectionStatusColor = WpfBrushes.Gray;
        private bool _hasError = false;
        private string _errorMessage = string.Empty;
        private string _heartbeatStatus = "Stopped";
        private WpfBrush _heartbeatStatusColor = WpfBrushes.Gray;
        private ObservableCollection<ScheduledTargetDto> _targets = new();
        private ScheduledTargetDto? _selectedTarget;
        private ScheduledTargetDto? _selectedTargetBackup;
        private ScheduledTargetDto? _pendingTargetSelection;
        private bool _isRefreshingTargetGrid = false;
        private bool _isNinaReady = false;
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
        private DateTime _lastSequenceFilesSnapshotUtc = DateTime.MinValue;
        private DateTime _lastSequenceTreeSnapshotUtc = DateTime.MinValue;
        private static readonly TimeSpan SequenceFilesSnapshotInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan SequenceTreeSnapshotInterval = TimeSpan.FromSeconds(10);

        private readonly object _focuserMoveLock = new();
        private CancellationTokenSource? _focuserMoveCts;
        
        private readonly object _rotatorMoveLock = new();
        private CancellationTokenSource? _rotatorMoveCts;
        
        // Offline capture queue for failed uploads
        private readonly OfflineCaptureQueue _offlineCaptureQueue;
        
        // Command handlers for refactored remote command execution
        private readonly EquipmentCommandHandler _equipmentCommandHandler;
        private readonly SequenceCommandHandler _sequenceCommandHandler;
        private readonly GuiderCommandHandler _guiderCommandHandler;
        private readonly TelescopeCommandHandler _telescopeCommandHandler;
        private readonly FocuserCommandHandler _focuserCommandHandler;
        private readonly RotatorCommandHandler _rotatorCommandHandler;
        private readonly CameraCommandHandler _cameraCommandHandler;
        private readonly FlatPanelCommandHandler _flatPanelCommandHandler;
        private readonly SystemCommandHandler _systemCommandHandler;
        private readonly ImagingCommandHandler _imagingCommandHandler;
        private readonly ConfigurationCommandHandler _configurationCommandHandler;
        private readonly ICommandHandler[] _commandHandlers;
        
        // Extracted services for better code organization
        private readonly Services.AutofocusLogWatcherService _afLogWatcherService;
        private readonly Services.ImageCaptureService _imageCaptureService;

        [ImportingConstructor]
        public AstroManagerPlugin(
            AstroManagerSettings settings, 
            AstroManagerApiClient apiClient,
            ScheduledTargetStore targetStore,
            AstroManagerDataStore dataStore,
            HeartbeatService heartbeatService,
            ISequenceMediator sequenceMediator,
            ITelescopeMediator telescopeMediator,
            ICameraMediator cameraMediator,
            IFocuserMediator focuserMediator,
            IGuiderMediator guiderMediator,
            IImageSaveMediator imageSaveMediator,
            IImagingMediator imagingMediator,
            IFilterWheelMediator filterWheelMediator,
            IRotatorMediator rotatorMediator,
            IDomeMediator domeMediator,
            IFlatDeviceMediator flatDeviceMediator,
            IWeatherDataMediator weatherDataMediator,
            ISafetyMonitorMediator safetyMonitorMediator,
            IProfileService profileService,
            IImageHistoryVM imageHistoryVM,
            IAutoFocusVMFactory autoFocusVMFactory,
            IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory,
            IApplicationStatusMediator applicationStatusMediator)
        {
            _settings = settings;
            _apiClient = apiClient;
            _targetStore = targetStore;
            _dataStore = dataStore;
            _heartbeatService = heartbeatService;
            _sequenceMediator = sequenceMediator;
            _telescopeMediator = telescopeMediator;
            _cameraMediator = cameraMediator;
            _focuserMediator = focuserMediator;
            _guiderMediator = guiderMediator;
            _imageSaveMediator = imageSaveMediator;
            _imagingMediator = imagingMediator;
            _filterWheelMediator = filterWheelMediator;
            _rotatorMediator = rotatorMediator;
            _domeMediator = domeMediator;
            _flatDeviceMediator = flatDeviceMediator;
            _weatherDataMediator = weatherDataMediator;
            _safetyMonitorMediator = safetyMonitorMediator;
            _profileService = profileService;
            _imageHistoryVM = imageHistoryVM;
            _autoFocusVMFactory = autoFocusVMFactory;
            _plateSolverFactory = plateSolverFactory;
            _windowServiceFactory = windowServiceFactory;
            _applicationStatusMediator = applicationStatusMediator;

            // Subscribe to heartbeat status changes
            _heartbeatService.StatusChanged += OnHeartbeatStatusChanged;
            _heartbeatService.CommandReceived += OnRemoteCommandReceived;
            _heartbeatService.RefreshRequested += OnRefreshRequested;
            _heartbeatService.BeforeStatusUpdate += OnBeforeStatusUpdate;
            _heartbeatService.SchedulerModeChanged += OnSchedulerModeChanged;
            
            // Subscribe to imaging mediator for immediate image capture notifications (includes manual captures)
            _imagingMediator.ImagePrepared += OnImagePrepared;
            
            // Initialize offline capture queue for failed uploads
            _offlineCaptureQueue = new OfflineCaptureQueue();
            _offlineCaptureQueue.OnRetryUpload += async dto => await _apiClient.UploadImageThumbnailAsync(dto);
            
            // Initialize command handlers
            _equipmentCommandHandler = new EquipmentCommandHandler(
                _telescopeMediator,
                _cameraMediator,
                _focuserMediator,
                _filterWheelMediator,
                _guiderMediator,
                _rotatorMediator,
                _flatDeviceMediator,
                _safetyMonitorMediator,
                _weatherDataMediator,
                UpdateEquipmentStatus);
            
            _sequenceCommandHandler = new SequenceCommandHandler(
                _sequenceMediator,
                _profileService,
                _heartbeatService);
            
            _guiderCommandHandler = new GuiderCommandHandler(_guiderMediator, _heartbeatService);
            
            _telescopeCommandHandler = new TelescopeCommandHandler(
                _telescopeMediator,
                _cameraMediator,
                _guiderMediator,
                _rotatorMediator,
                _filterWheelMediator,
                _imagingMediator,
                _profileService,
                _plateSolverFactory,
                _windowServiceFactory,
                (msg, status) => _heartbeatService.SetOperationStatus(msg, status: status),
                () => _heartbeatService.ForceStatusUpdateAsync());
            
            _focuserCommandHandler = new FocuserCommandHandler(_focuserMediator);
            
            _rotatorCommandHandler = new RotatorCommandHandler(_rotatorMediator, _profileService);
            
            _cameraCommandHandler = new CameraCommandHandler(
                _cameraMediator,
                _filterWheelMediator,
                _profileService);
            
            _flatPanelCommandHandler = new FlatPanelCommandHandler(_flatDeviceMediator);
            
            _systemCommandHandler = new SystemCommandHandler(
                _sequenceMediator,
                _telescopeMediator,
                _cameraMediator,
                _focuserMediator,
                _guiderMediator,
                _filterWheelMediator,
                _rotatorMediator,
                UpdateEquipmentStatus);
            
            _imagingCommandHandler = new ImagingCommandHandler(
                _cameraMediator,
                _focuserMediator,
                _telescopeMediator,
                _filterWheelMediator,
                _imagingMediator,
                _imageSaveMediator,
                _profileService,
                _autoFocusVMFactory,
                _windowServiceFactory,
                _imageHistoryVM,
                _plateSolverFactory,
                _heartbeatService);
            
            _configurationCommandHandler = new ConfigurationCommandHandler(
                _sequenceMediator,
                LoadSettingsFromApiAsync,
                _apiClient.StartSessionAsync);
            
            // Register all handlers for unified command dispatch
            _commandHandlers = new ICommandHandler[]
            {
                _equipmentCommandHandler,
                _sequenceCommandHandler,
                _guiderCommandHandler,
                _telescopeCommandHandler,
                _focuserCommandHandler,
                _rotatorCommandHandler,
                _cameraCommandHandler,
                _flatPanelCommandHandler,
                _systemCommandHandler,
                _imagingCommandHandler,
                _configurationCommandHandler
            };
            
            // Initialize extracted services
            _afLogWatcherService = new Services.AutofocusLogWatcherService(
                _heartbeatService,
                () => _isShuttingDown);
            
            _imageCaptureService = new Services.ImageCaptureService(
                _apiClient,
                _heartbeatService,
                _telescopeMediator,
                _rotatorMediator,
                _focuserMediator,
                _guiderMediator,
                _weatherDataMediator,
                _offlineCaptureQueue,
                _settings,
                RaisePropertyChanged,
                value => LastCapturedImage = value);
            
            // Subscribe to image saved events for thumbnail upload (must be after _imageCaptureService init)
            _imageSaveMediator.ImageSaved += _imageCaptureService.OnImageSavedAsync;
            
            // Subscribe to AutoFocusPoints to capture ALL AF runs (sequence-triggered, scheduled, etc.)
            if (_imageHistoryVM != null)
            {
                try
                {
                    dynamic historyVM = _imageHistoryVM;
                    var afPoints = historyVM.AutoFocusPoints as System.Collections.Specialized.INotifyCollectionChanged;
                    if (afPoints != null)
                    {
                        afPoints.CollectionChanged += _imageCaptureService.OnAutoFocusPointsCollectionChanged;
                        Logger.Info("AstroManager: Subscribed to AutoFocusPoints.CollectionChanged for global AF detection");
                    }
                    
                    // Subscribe to ImageHistory changes to capture plate solve PA
                    var imageHistory = historyVM.ImageHistory as System.Collections.Specialized.INotifyCollectionChanged;
                    if (imageHistory != null)
                    {
                        imageHistory.CollectionChanged += _imageCaptureService.OnImageHistoryCollectionChanged;
                        Logger.Info("AstroManager: Subscribed to ImageHistory.CollectionChanged for plate solve PA capture");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"AstroManager: Could not subscribe to AutoFocusPoints/ImageHistory: {ex.Message}");
                }
            }
            
            // Subscribe to equipment connection changes for automatic status updates
            _cameraMediator.GetInfo().PropertyChanged += OnEquipmentPropertyChanged;
            _telescopeMediator.GetInfo().PropertyChanged += OnEquipmentPropertyChanged;
            _focuserMediator.GetInfo().PropertyChanged += OnEquipmentPropertyChanged;
            _filterWheelMediator.GetInfo().PropertyChanged += OnEquipmentPropertyChanged;
            _guiderMediator.GetInfo().PropertyChanged += OnEquipmentPropertyChanged;
            if (_rotatorMediator != null)
                _rotatorMediator.GetInfo().PropertyChanged += OnEquipmentPropertyChanged;
            
            // Start focuser/rotator position polling (GetInfo() returns snapshot, so PropertyChanged doesn't work for position)
            StartFocuserPolling();
            StartRotatorPolling();
            
            // Start AF log folder watcher to detect external autofocus runs
            _afLogWatcherService.Start();

            // Register the scheduler template ResourceDictionary
            try
            {
                var schedulerTemplate = new AstroManagerTargetSchedulerTemplate();
                System.Windows.Application.Current?.Resources?.MergedDictionaries?.Add(schedulerTemplate);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load scheduler template: {ex.Message}");
            }
            
            // Subscribe to multiple shutdown events for reliability
            // Must use Dispatcher.Invoke to subscribe to WPF events from background thread
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            try
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (System.Windows.Application.Current != null)
                    {
                        System.Windows.Application.Current.Exit += OnApplicationExit;
                        System.Windows.Application.Current.Dispatcher.ShutdownStarted += OnDispatcherShutdown;
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Failed to subscribe to shutdown events: {ex.Message}");
            }

            // Initialize commands
            TestConnectionCommand = new RelayCommand(async _ => await TestConnectionAsync());
            SyncTargetsCommand = new RelayCommand(async _ => await SyncTargetsAsync());
            BrowseExportPathCommand = new RelayCommand(_ => BrowseExportPath());
            ExportTargetsCommand = new RelayCommand(_ => ExportTargets());
            ImportTargetsCommand = new RelayCommand(_ => ImportTargets());
            ClearCacheCommand = new RelayCommand(_ => ClearCache());
            StartHeartbeatCommand = new RelayCommand(_ => StartHeartbeat());
            StopHeartbeatCommand = new RelayCommand(_ => StopHeartbeat());
            RefreshTargetsListCommand = new RelayCommand(async _ => await SyncTargetsAsync());
            SaveTargetCommand = new RelayCommand(async _ => await SaveSelectedTargetAsync(), _ => HasSelectedTarget);
            CancelTargetEditCommand = new RelayCommand(_ => CancelTargetEdit(), _ => HasSelectedTarget);
            DeleteTargetCommand = new RelayCommand(async _ => await DeleteSelectedTargetAsync(), _ => HasSelectedTarget);
            ExportSettingsCommand = new RelayCommand(_ => ExportSettings());
            ImportSettingsCommand = new RelayCommand(_ => ImportSettings());
            OpenAstroManagerCommand = new RelayCommand(_ => OpenAstroManager());
            OpenDocumentationCommand = new RelayCommand(_ => OpenDocumentation());
            LoadSettingsFromApiCommand = new RelayCommand(async _ => await LoadSettingsFromApiAsync());
            
            // Imaging Goal CRUD
            AddImagingGoalCommand = new RelayCommand(async _ => await AddImagingGoalAsync(), _ => HasSelectedTarget);
            SaveImagingGoalCommand = new RelayCommand(async _ => await SaveImagingGoalAsync(), _ => HasSelectedImagingGoal);
            CancelImagingGoalEditCommand = new RelayCommand(_ => CancelImagingGoalEdit(), _ => HasSelectedImagingGoal);
            DeleteImagingGoalCommand = new RelayCommand(_ => ConfirmDeleteImagingGoal(), _ => HasSelectedImagingGoal);
            
            // Panel Custom Goal CRUD
            AddPanelCustomGoalCommand = new RelayCommand(async _ => await AddPanelCustomGoalAsync(), _ => HasSelectedPanel && SelectedTargetUseCustomPanelGoals);
            SavePanelCustomGoalCommand = new RelayCommand(async _ => await SavePanelCustomGoalsAsync(), _ => HasSelectedPanel && SelectedTargetUseCustomPanelGoals);
            DeletePanelCustomGoalCommand = new RelayCommand(async _ => await DeletePanelCustomGoalAsync(), _ => HasSelectedPanelGoal);
            
            // Panel Goal Edit (for both base and custom goals)
            SavePanelGoalCommand = new RelayCommand(async _ => await SavePanelGoalAsync(), _ => HasSelectedPanelGoal);
            CancelPanelGoalEditCommand = new RelayCommand(_ => CancelPanelGoalEdit());
            DeletePanelGoalCommand = new RelayCommand(async _ => await DeletePanelGoalAsync(), _ => HasSelectedPanelGoal && (SelectedPanelGoal?.IsCustomGoal ?? false));
            
            // Exposure Template CRUD
            AddExposureTemplateCommand = new RelayCommand(async _ => await AddExposureTemplateAsync());
            SaveExposureTemplateCommand = new RelayCommand(async _ => await SaveExposureTemplateAsync(), _ => HasSelectedExposureTemplate);
            DeleteExposureTemplateCommand = new RelayCommand(async p => await DeleteExposureTemplateAsync(p as ExposureTemplateDto));
            CopyExposureTemplateCommand = new RelayCommand(async p => await CopyExposureTemplateAsync(p as ExposureTemplateDto));
            CloseExposureTemplateEditCommand = new RelayCommand(_ => CancelExposureTemplateEdit());
            
            // Scheduler Config CRUD
            SaveSchedulerConfigCommand = new RelayCommand(async _ => await SaveSchedulerConfigAsync(), _ => HasSelectedSchedulerConfiguration);
            AddSchedulerConfigCommand = new RelayCommand(async _ => await AddSchedulerConfigAsync());
            DeleteSchedulerConfigCommand = new RelayCommand(async p => await DeleteSchedulerConfigAsync(p as SchedulerConfigurationDto));
            CopySchedulerConfigCommand = new RelayCommand(async p => await CopySchedulerConfigAsync(p as SchedulerConfigurationDto));
            CloseSchedulerConfigEditCommand = new RelayCommand(_ => CancelSchedulerConfigEdit());
            
            // Scheduler Preview (Generate from algorithm)
            GenerateSchedulerPreviewCommand = new RelayCommand(async _ => await GenerateSchedulerPreviewAsync(), _ => SelectedPreviewConfigId.HasValue && !IsLoadingPreview);
            PreviousPreviewDateCommand = new RelayCommand(_ => GeneratePreviewDate = GeneratePreviewDate.AddDays(-1));
            NextPreviewDateCommand = new RelayCommand(_ => GeneratePreviewDate = GeneratePreviewDate.AddDays(1));
            TodayPreviewDateCommand = new RelayCommand(_ => GeneratePreviewDate = DateTime.Today);
            SelectPreviewTargetCommand = new RelayCommand(param => SelectPreviewTarget(param));
            
            // Scheduler Target Template CRUD
            AddSchedulerTargetTemplateCommand = new RelayCommand(async _ => await AddSchedulerTargetTemplateAsync());
            SaveSchedulerTargetTemplateCommand = new RelayCommand(async _ => await SaveSchedulerTargetTemplateAsync(), _ => HasSelectedSchedulerTargetTemplate);
            DeleteSchedulerTargetTemplateCommand = new RelayCommand(async p => await DeleteSchedulerTargetTemplateAsync(p as SchedulerTargetTemplateDto));
            CopySchedulerTargetTemplateCommand = new RelayCommand(async p => await CopySchedulerTargetTemplateAsync(p as SchedulerTargetTemplateDto));
            CloseSchedulerTargetTemplateEditCommand = new RelayCommand(_ => CancelSchedulerTargetTemplateEdit());
            SetDefaultSchedulerConfigCommand = new RelayCommand(async p => await SetDefaultSchedulerConfigAsync(p as SchedulerConfigurationDto));
            
            // Moon Avoidance Profile CRUD
            AddMoonAvoidanceProfileCommand = new RelayCommand(async _ => await AddMoonAvoidanceProfileAsync());
            SaveMoonAvoidanceProfileCommand = new RelayCommand(async _ => await SaveMoonAvoidanceProfileAsync(), _ => HasSelectedMoonAvoidanceProfile);
            DeleteMoonAvoidanceProfileCommand = new RelayCommand(async p => await DeleteMoonAvoidanceProfileAsync(p as MoonAvoidanceProfileDto));
            CopyMoonAvoidanceProfileCommand = new RelayCommand(async p => await CopyMoonAvoidanceProfileAsync(p as MoonAvoidanceProfileDto));
            CloseMoonAvoidanceProfileEditCommand = new RelayCommand(_ => CancelMoonAvoidanceProfileEdit());
            
            // Confirmation dialog commands
            ConfirmDialogYesCommand = new RelayCommand(_ => { _confirmDialogAction?.Invoke(); ShowConfirmDialog = false; _confirmDialogNoAction = null; });
            ConfirmDialogNoCommand = new RelayCommand(_ => { _confirmDialogNoAction?.Invoke(); ShowConfirmDialog = false; _confirmDialogNoAction = null; });
            
            // Scheduler Preview
            PreviousNightCommand = new RelayCommand(_ => PreviewDate = PreviewDate.AddDays(-1));
            NextNightCommand = new RelayCommand(_ => PreviewDate = PreviewDate.AddDays(1));
            LoadPreviewCommand = new RelayCommand(async _ => await LoadSchedulerPreviewAsync());
            
            // Data type selection commands
            SelectExposureTemplatesCommand = new RelayCommand(_ => SelectedDataType = "ExposureTemplates");
            SelectSchedulerConfigCommand = new RelayCommand(_ => SelectedDataType = "SchedulerConfig");
            SelectSchedulerTargetTemplatesCommand = new RelayCommand(_ => SelectedDataType = "SchedulerTargetTemplates");
            SelectMoonAvoidanceCommand = new RelayCommand(_ => SelectedDataType = "MoonAvoidance");
            SelectObservatoryCommand = new RelayCommand(_ => SelectedDataType = "Observatory");
            
            // Clear target template command
            ClearTargetTemplateCommand = new RelayCommand(_ => { SelectedTargetSchedulerTemplate = null; });
            
            // Queue management commands
            RefreshQueueCommand = new RelayCommand(async _ => await RefreshQueueAsync());
            AddToQueueCommand = new RelayCommand(async _ => await AddSelectedToQueueAsync(), _ => SelectedAvailableTarget != null);
            RemoveFromQueueCommand = new RelayCommand(async _ => await RemoveSelectedFromQueueAsync(), _ => SelectedQueueItem != null);
            MoveUpCommand = new RelayCommand(async _ => await MoveQueueItemUpAsync(), _ => CanMoveUp);
            MoveDownCommand = new RelayCommand(async _ => await MoveQueueItemDownAsync(), _ => CanMoveDown);
            ClearQueueCommand = new RelayCommand(async _ => await ClearQueueAsync(), _ => TargetQueue.Count > 0);
            
            // Load cached targets on startup
            RefreshTargetsList();

            // Auto-connect on startup if enabled (license key required)
            if (_settings.AutoConnectOnStartup && !string.IsNullOrEmpty(_settings.LicenseKey))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        Logger.Info("AstroManager: Auto-connecting on startup...");
                        
                        // Test connection first
                        var (success, message, _) = await _apiClient.TestConnectionAsync();
                        if (success)
                        {
                            Logger.Info("AstroManager: Connection successful, loading settings...");
                            ClearError();
                            
                            // Initialize shared log with API client for remote logging
                            SharedSchedulerLog.Instance.Initialize(_apiClient);
                            
                            // Auto-load exposure templates and scheduler config
                            await LoadSettingsFromApiAsync();
                            
                            // Sync targets if enabled
                            if (_settings.AutoSyncOnStartup)
                            {
                                Logger.Info("AstroManager: Auto-syncing targets...");
                                await SyncTargetsAsync();
                            }
                            else
                            {
                                // Still load queue even without full sync
                                await RefreshQueueAsync();
                            }
                            
                            // Start heartbeat if enabled
                            if (_settings.EnableHeartbeat)
                            {
                                Logger.Info("AstroManager: Starting heartbeat...");
                                _heartbeatService.Start();
                            }
                            
                            _isNinaReady = true; // Mark NINA as ready after auto-connect
                            Logger.Info("AstroManager: Auto-connect completed successfully.");
                        }
                        else
                        {
                            Logger.Warning("AstroManager: Auto-connect failed - connection test unsuccessful.");
                            SetError(message);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"AstroManager: Auto-connect error: {ex.Message}");
                        SetError($"Connection error: {ex.Message}");
                    }
                });
            }
            else if (string.IsNullOrEmpty(_settings.LicenseKey))
            {
                // No license key configured
                SetError("No license key");
            }
        }

        private void OnHeartbeatStatusChanged(object? sender, HeartbeatStatusChangedEventArgs e)
        {
            HeartbeatStatus = e.Status;
            HeartbeatStatusColor = e.Status switch
            {
                "Connected" => WpfBrushes.Green,
                "Running" => WpfBrushes.Green,
                "Stopped" => WpfBrushes.Gray,
                _ when e.Status.StartsWith("Error") => WpfBrushes.Red,
                _ => WpfBrushes.Orange
            };
            RaisePropertyChanged(nameof(LastHeartbeatTime));
        }
        
        private void OnSchedulerModeChanged(object? sender, SchedulerModeChangedEventArgs e)
        {
            Logger.Info($"AstroManager: Scheduler mode changed from server: {e.OldMode} -> {e.NewMode}");
            _currentSchedulerMode = e.NewMode;
            RaisePropertyChanged(nameof(IsManualMode));
            RaisePropertyChanged(nameof(SchedulerModeDisplay));
            RaisePropertyChanged(nameof(SchedulerModeDescription));
        }
        
        private async void OnRefreshRequested(object? sender, EventArgs e)
        {
            Logger.Info("AstroManager: Periodic refresh triggered by heartbeat service");
            try
            {
                await LoadSettingsFromApiAsync();
                ConnectionStatus = $"Auto-refreshed at {DateTime.Now:HH:mm:ss}";
                ConnectionStatusColor = WpfBrushes.Green;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Periodic refresh failed: {ex.Message}");
            }
        }
        
        private DateTime _lastEquipmentUpdate = DateTime.MinValue;
        private DateTime _lastWeatherUpdate = DateTime.MinValue;
        private const int WeatherUpdateIntervalSeconds = 120; // 2 minutes between weather updates
        private bool _isShuttingDown = false;
        
        // Focuser position polling (GetInfo() returns snapshot, not live object)
        private System.Timers.Timer? _focuserPollingTimer;
        private int? _lastKnownFocuserPosition;
        
        // Rotator position polling (GetInfo() returns snapshot, not live object)
        private System.Timers.Timer? _rotatorPollingTimer;
        private double? _lastKnownRotatorPosition;
        
        // Platesolve/Sync detection via coordinate monitoring
        private double? _lastKnownRA;
        private double? _lastKnownDec;
        private DateTime _lastCoordinateCheck = DateTime.MinValue;
        private bool _wasSlewing = false;
        private const double SyncDetectionThresholdArcsec = 10.0; // Detect syncs > 10 arcsec
        
        // Cached plate solve PA from ImageHistory (used when coordinate sync is detected)
        private double? _cachedPlateSolvePA;
        private double? _cachedPixelScale;
        private DateTime _cachedPlateSolveTime = DateTime.MinValue;
        
        // AF run guard - prevent multiple simultaneous AF runs
        private bool _isAutofocusRunning = false;
        
        // Guider calibration tracking
        private bool _isCalibrating = false;
        
        // NOTE: AF log watcher fields moved to Services.AutofocusLogWatcherService
        
        private async void OnEquipmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Don't process equipment updates during shutdown
            if (_isShuttingDown) return;
            
            // Only trigger update for connection-related properties and key equipment values
            if (e.PropertyName == "Connected" || e.PropertyName == "AtPark" || e.PropertyName == "Tracking" || 
                e.PropertyName == "Coordinates" || e.PropertyName == "SelectedFilter" || e.PropertyName == "Position")
            {
                // Check for platesolve/sync detection on coordinate changes
                if (e.PropertyName == "Coordinates")
                {
                    CheckForPlateSolveSync();
                }
                
                // Debounce - don't update more than once per second
                if ((DateTime.Now - _lastEquipmentUpdate).TotalMilliseconds > 1000)
                {
                    _lastEquipmentUpdate = DateTime.Now;
                    Logger.Debug($"AstroManager: Equipment property changed: {e.PropertyName}");
                    UpdateEquipmentStatus();
                    // Force immediate send to API
                    await _heartbeatService.ForceStatusUpdateAsync();
                }
            }
        }
        
        /// <summary>
        /// Detect platesolve/sync events by monitoring coordinate jumps when not slewing
        /// </summary>
        private void CheckForPlateSolveSync()
        {
            try
            {
                var telescopeInfo = _telescopeMediator.GetInfo();
                if (!telescopeInfo.Connected || telescopeInfo.Coordinates == null)
                    return;
                
                var currentRA = telescopeInfo.Coordinates.RA;
                var currentDec = telescopeInfo.Coordinates.Dec;
                var isSlewing = telescopeInfo.Slewing;
                var rotatorInfo = _rotatorMediator?.GetInfo();
                var positionAngle = rotatorInfo?.Connected == true ? rotatorInfo.Position : (double?)null;
                
                // Track slewing state
                if (isSlewing)
                {
                    _wasSlewing = true;
                    _lastKnownRA = currentRA;
                    _lastKnownDec = currentDec;
                    return;
                }
                
                // If we have previous coordinates and we're not slewing
                if (_lastKnownRA.HasValue && _lastKnownDec.HasValue && !_wasSlewing)
                {
                    // Calculate separation in arcseconds
                    var deltaRA = (currentRA - _lastKnownRA.Value) * 15.0 * 3600.0 * Math.Cos(currentDec * Math.PI / 180.0); // RA in arcsec
                    var deltaDec = (currentDec - _lastKnownDec.Value) * 3600.0; // Dec in arcsec
                    var separation = Math.Sqrt(deltaRA * deltaRA + deltaDec * deltaDec);
                    
                    // If coordinate jumped > threshold without slewing, it's likely a sync from platesolve
                    if (separation > SyncDetectionThresholdArcsec)
                    {
                        // Try to get Sky PA from the last plate solve result in image history
                        double? skyPA = null;
                        double? pixelScale = null;
                        try
                        {
                            // Check image history for most recent plate solved image
                            var lastImage = _imageHistoryVM?.ImageHistory?.FirstOrDefault();
                            if (lastImage != null)
                            {
                                var imageType = lastImage.GetType();

                                // Try to get plate solve info via reflection
                                try
                                {
                                    // Try different property names NINA might use
                                    var psResultProp = imageType.GetProperty("PlateSolveResult") 
                                        ?? imageType.GetProperty("PlateSolveInfo")
                                        ?? imageType.GetProperty("PlateSolve");
                                    
                                    if (psResultProp != null)
                                    {
                                        var plateSolveResult = psResultProp.GetValue(lastImage);
                                        if (plateSolveResult != null)
                                        {
                                            var psType = plateSolveResult.GetType();

                                            // Try different property names for position angle
                                            var paProp = psType.GetProperty("PositionAngle") 
                                                ?? psType.GetProperty("Rotation")
                                                ?? psType.GetProperty("Orientation");
                                            var scaleProp = psType.GetProperty("Pixscale") 
                                                ?? psType.GetProperty("PixelScale")
                                                ?? psType.GetProperty("ArcSecPerPixel");
                                            
                                            if (paProp != null)
                                            {
                                                skyPA = (double?)paProp.GetValue(plateSolveResult);
                                            }
                                            if (scaleProp != null)
                                            {
                                                pixelScale = (double?)scaleProp.GetValue(plateSolveResult);
                                            }
                                            
                                            if (skyPA.HasValue || pixelScale.HasValue)
                                            {
                                                Logger.Info($"AstroManager: Got Sky PA from ImageHistory: {skyPA:F2}°, PixelScale: {pixelScale:F3}\"/px");
                                            }
                                        }
                                    }
                                }
                                catch (Exception propEx)
                                {
                                    Logger.Debug($"AstroManager: Error accessing plate solve properties: {propEx.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"AstroManager: Could not get Sky PA from image history: {ex.Message}");
                        }
                        
                        // Fallback chain: Sky PA from image -> Cached PA from recent solve -> Rotator position
                        // Use cached PA if it was captured within the last 60 seconds (likely from the same solve)
                        var useCachedPA = !skyPA.HasValue && _cachedPlateSolvePA.HasValue 
                            && (DateTime.UtcNow - _cachedPlateSolveTime).TotalSeconds < 60;
                        if (useCachedPA)
                        {
                            skyPA = _cachedPlateSolvePA;
                            pixelScale = pixelScale ?? _cachedPixelScale;
                        }
                        
                        var rotationToUse = skyPA ?? positionAngle;
                        
                        // Report the sync as a platesolve result
                        // Format RA as HH:MM:SS.ss and Dec as ±DD°MM'SS.s"
                        var raHours = (int)currentRA;
                        var raMinutes = (int)((currentRA - raHours) * 60);
                        var raSeconds = ((currentRA - raHours) * 60 - raMinutes) * 60;
                        var raHms = $"{raHours:D2}:{raMinutes:D2}:{raSeconds:00.00}";
                        
                        var decSign = currentDec >= 0 ? "+" : "-";
                        var decAbs = Math.Abs(currentDec);
                        var decDegrees = (int)decAbs;
                        var decMinutes = (int)((decAbs - decDegrees) * 60);
                        var decSeconds = ((decAbs - decDegrees) * 60 - decMinutes) * 60;
                        var decDms = $"{decSign}{decDegrees:D2}°{decMinutes:D2}'{decSeconds:00.0}\"";
                        
                        var psReport = new Shared.Model.DTO.Client.PlateSolveReportDto
                        {
                            CompletedAt = DateTime.UtcNow,
                            Success = true,
                            SolvedRa = currentRA,
                            SolvedDec = currentDec,
                            RaFormatted = raHms,
                            DecFormatted = decDms,
                            Rotation = rotationToUse,
                            PixelScale = pixelScale,
                            SeparationArcsec = separation,
                            RaSeparationArcsec = deltaRA,
                            DecSeparationArcsec = deltaDec,
                            WasSynced = true,
                            SolveDurationSeconds = 0 // Unknown for detected solves
                        };
                        _heartbeatService.SetPlateSolveReport(psReport);
                        _ = _heartbeatService.ForceStatusUpdateAsync();
                    }
                }
                
                // Update tracking state
                _wasSlewing = false;
                _lastKnownRA = currentRA;
                _lastKnownDec = currentDec;
            }
            catch (Exception ex)
            {
                Logger.Debug($"AstroManager: Error in sync detection: {ex.Message}");
            }
        }
        
        private void OnBeforeStatusUpdate(object? sender, EventArgs e)
        {
            // Refresh equipment status before each heartbeat
            UpdateEquipmentStatus();

            try
            {
                var now = DateTime.UtcNow;

                if (now - _lastSequenceTreeSnapshotUtc >= SequenceTreeSnapshotInterval)
                {
                    var treeSnapshot = _sequenceCommandHandler.GetSequenceTreeSnapshot();
                    _heartbeatService.SetSequenceTreeSnapshot(treeSnapshot);
                    _lastSequenceTreeSnapshotUtc = now;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"AstroManager: Failed to update sequence snapshots before heartbeat: {ex.Message}");
            }
        }
        
        
        private void StartFocuserPolling()
        {
            _focuserPollingTimer = new System.Timers.Timer(500); // Poll every 500ms
            _focuserPollingTimer.Elapsed += OnFocuserPollingTick;
            _focuserPollingTimer.AutoReset = true;
            _focuserPollingTimer.Start();
            Logger.Debug("AstroManager: Started focuser position polling");
        }
        
        private void StopFocuserPolling()
        {
            if (_focuserPollingTimer != null)
            {
                _focuserPollingTimer.Stop();
                _focuserPollingTimer.Elapsed -= OnFocuserPollingTick;
                _focuserPollingTimer.Dispose();
                _focuserPollingTimer = null;
                Logger.Debug("AstroManager: Stopped focuser position polling");
            }
        }
        
        private async void OnFocuserPollingTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (_isShuttingDown) return;
            
            try
            {
                var focuserInfo = _focuserMediator.GetInfo();
                if (focuserInfo?.Connected == true)
                {
                    var currentPosition = focuserInfo.Position;
                    if (_lastKnownFocuserPosition != currentPosition)
                    {
                        Logger.Info($"AstroManager: Focuser position changed: {_lastKnownFocuserPosition} -> {currentPosition}");
                        _lastKnownFocuserPosition = currentPosition;
                        
                        if ((DateTime.Now - _lastEquipmentUpdate).TotalMilliseconds > 1000)
                        {
                            _lastEquipmentUpdate = DateTime.Now;
                            UpdateEquipmentStatus();
                            await _heartbeatService.ForceStatusUpdateAsync();
                        }
                    }
                }
                else
                {
                    _lastKnownFocuserPosition = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"AstroManager: Focuser polling error: {ex.Message}");
            }
        }
        
        private void StartRotatorPolling()
        {
            _rotatorPollingTimer = new System.Timers.Timer(500); // Poll every 500ms
            _rotatorPollingTimer.Elapsed += OnRotatorPollingTick;
            _rotatorPollingTimer.AutoReset = true;
            _rotatorPollingTimer.Start();
            Logger.Debug("AstroManager: Started rotator position polling");
        }
        
        private void StopRotatorPolling()
        {
            if (_rotatorPollingTimer != null)
            {
                _rotatorPollingTimer.Stop();
                _rotatorPollingTimer.Elapsed -= OnRotatorPollingTick;
                _rotatorPollingTimer.Dispose();
                _rotatorPollingTimer = null;
                Logger.Debug("AstroManager: Stopped rotator position polling");
            }
        }
        
        private async void OnRotatorPollingTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (_isShuttingDown) return;
            
            try
            {
                var rotatorInfo = _rotatorMediator.GetInfo();
                if (rotatorInfo?.Connected == true)
                {
                    var currentPosition = rotatorInfo.MechanicalPosition;
                    // Only trigger update if position changed by more than 0.1 degrees (to avoid noise)
                    if (_lastKnownRotatorPosition == null || Math.Abs((_lastKnownRotatorPosition.Value - currentPosition)) > 0.1)
                    {
                        Logger.Info($"AstroManager: Rotator position changed: {_lastKnownRotatorPosition:F1} -> {currentPosition:F1}");
                        _lastKnownRotatorPosition = currentPosition;
                        
                        // Debounce - don't update more than once per second
                        if ((DateTime.Now - _lastEquipmentUpdate).TotalMilliseconds > 1000)
                        {
                            _lastEquipmentUpdate = DateTime.Now;
                            UpdateEquipmentStatus();
                            await _heartbeatService.ForceStatusUpdateAsync();
                        }
                    }
                }
                else
                {
                    // Rotator disconnected - reset last known position
                    _lastKnownRotatorPosition = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"AstroManager: Rotator polling error: {ex.Message}");
            }
        }
        
        // NOTE: AF Log Watcher functionality moved to Services.AutofocusLogWatcherService

        private async void OnRemoteCommandReceived(object? sender, RemoteCommandReceivedEventArgs e)
        {
            var command = e.Command;
            Logger.Info($"AstroManager: Received remote command: {command.CommandType}");
            
            // Wait for NINA to be ready if we just started up
            if (!_isNinaReady)
            {
                Logger.Info($"AstroManager: Waiting for NINA startup to complete before processing command...");
                await Task.Delay(StartupDelay);
                _isNinaReady = true;
                Logger.Info($"AstroManager: NINA startup delay completed, processing command");
            }
            
            // Update status to show command received
            LastRemoteCommand = $"{command.CommandType} ({DateTime.Now:HH:mm:ss})";
            RaisePropertyChanged(nameof(LastRemoteCommand));
            
            try
            {
                // Report executing status
                await _heartbeatService.ReportCommandResultAsync(command.Id, 
                    Shared.Model.DTO.Client.RemoteCommandStatus.Executing, "Started execution");
                
                // Execute the command
                var result = await ExecuteRemoteCommandAsync(command);
                
                // Report completion
                await _heartbeatService.ReportCommandResultAsync(command.Id, 
                    result.Success ? Shared.Model.DTO.Client.RemoteCommandStatus.Completed 
                                   : Shared.Model.DTO.Client.RemoteCommandStatus.Failed,
                    result.Message);
                
                Logger.Info($"AstroManager: Command {command.CommandType} {(result.Success ? "completed" : "failed")}: {result.Message}");
                
                // Force immediate status update after command completes
                await _heartbeatService.ForceStatusUpdateAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Error executing command {command.CommandType}: {ex.Message}");
                await _heartbeatService.ReportCommandResultAsync(command.Id, 
                    Shared.Model.DTO.Client.RemoteCommandStatus.Failed, ex.Message);
                
                // Force status update even on error
                await _heartbeatService.ForceStatusUpdateAsync();
            }
        }

        private async Task<(bool Success, string Message)> ExecuteRemoteCommandAsync(Shared.Model.DTO.Client.RemoteCommandDto command)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            
            try
            {
                // Delegate to command handlers (unified dispatch)
                foreach (var handler in _commandHandlers)
                {
                    if (handler.CanHandle(command.CommandType))
                    {
                        var result = await handler.ExecuteAsync(command, cts.Token);
                        return (result.Success, result.Message);
                    }
                }
                
                // No handler found for this command type
                Logger.Warning($"AstroManager: Unknown command type: {command.CommandType}");
                return (false, $"Unknown command type: {command.CommandType}");
            }
            catch (OperationCanceledException)
            {
                return (false, "Command timed out after 5 minutes");
            }
        }

        // NOTE: Remote command execution methods have been moved to dedicated handlers:
        // - ImagingCommandHandler: autofocus, plate solve, exposure commands
        // - ConfigurationCommandHandler: refresh config, restart session commands

        #region Image Capture and Thumbnail Upload

        private void OnImagePrepared(object? sender, ImagePreparedEventArgs e)
        {
            // ImagePreparedEventArgs doesn't have MetaData property for FITS header injection
            // The scheduler context (GoalId, CaptureId) is captured in OnImageSaved via SharedSchedulerState
        }

        public string LastCapturedImage { get; private set; } = "None";

        #endregion

        public string LastRemoteCommand { get; private set; } = "None";

        #region Settings Properties

        public string LicenseKey
        {
            get => _settings.LicenseKey;
            set
            {
                _settings.LicenseKey = value;
                RaisePropertyChanged();
            }
        }

        // ApiUrl is now hardcoded - read-only
        public string ApiUrl => _settings.ApiUrl;

        public bool UseCachedTargetsOnConnectionLoss
        {
            get => _settings.UseCachedTargetsOnConnectionLoss;
            set
            {
                _settings.UseCachedTargetsOnConnectionLoss = value;
                RaisePropertyChanged();
            }
        }

        public string ExportImportPath
        {
            get => _settings.ExportImportPath;
            set
            {
                _settings.ExportImportPath = value;
                RaisePropertyChanged();
            }
        }

        // AutoSyncOnStartup is now always true - read-only
        public bool AutoSyncOnStartup => _settings.AutoSyncOnStartup;

        public bool AutoExportAfterSync
        {
            get => _settings.AutoExportAfterSync;
            set
            {
                _settings.AutoExportAfterSync = value;
                RaisePropertyChanged();
            }
        }

        public string ConnectionStatus
        {
            get => _connectionStatus;
            set
            {
                _connectionStatus = value;
                RaisePropertyChanged();
            }
        }

        public WpfBrush ConnectionStatusColor
        {
            get => _connectionStatusColor;
            set
            {
                _connectionStatusColor = value;
                RaisePropertyChanged();
            }
        }

        public bool HasError
        {
            get => _hasError;
            set
            {
                _hasError = value;
                RaisePropertyChanged();
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                _errorMessage = value;
                RaisePropertyChanged();
            }
        }

        public int CachedTargetCount => _targetStore.Count;

        public string LastSyncTime => _settings.LastSyncTime.HasValue 
            ? _settings.LastSyncTime.Value.ToLocalTime().ToString("g") 
            : "Never";

        public string ObservatoryName => _settings.ObservatoryName ?? "Not configured";
        public string EquipmentName => _settings.EquipmentName ?? "Not configured";

        // Heartbeat properties - now hardcoded/read-only
        public bool AutoConnectOnStartup => _settings.AutoConnectOnStartup;
        public bool EnableHeartbeat => _settings.EnableHeartbeat;

        public int HeartbeatIntervalSeconds
        {
            get => _settings.HeartbeatIntervalSeconds;
            set
            {
                _settings.HeartbeatIntervalSeconds = value;
                RaisePropertyChanged();
            }
        }
        
        // EnableRealTimeConnection is now always true - read-only
        public bool EnableRealTimeConnection => _settings.EnableRealTimeConnection;
        
        // EnableImageUpload is now always true - read-only (thumbnails are essential for progress tracking)
        public bool EnableImageUpload => _settings.EnableImageUpload;
        
        // Scheduler Mode properties
        private SchedulerMode _currentSchedulerMode = SchedulerMode.Auto;
        
        public bool IsManualMode
        {
            get => _currentSchedulerMode == SchedulerMode.Manual;
            set
            {
                var newMode = value ? SchedulerMode.Manual : SchedulerMode.Auto;
                if (_currentSchedulerMode != newMode)
                {
                    _currentSchedulerMode = newMode;
                    _heartbeatService.SetSchedulerMode(newMode); // Keep heartbeat service in sync
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SchedulerModeDisplay));
                    RaisePropertyChanged(nameof(SchedulerModeDescription));
                    
                    // Prevent polling from overriding during API call
                    _heartbeatService.BeginSchedulerModeUpdate();
                    
                    // Sync mode to API
                    Task.Run(async () =>
                    {
                        try
                        {
                            var success = await _apiClient.SetSchedulerModeAsync(newMode);
                            if (success)
                            {
                                Logger.Info($"AstroManager: Scheduler mode set to {newMode}");
                            }
                            else
                            {
                                Logger.Warning($"AstroManager: Failed to set scheduler mode to {newMode}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"AstroManager: Error setting scheduler mode: {ex.Message}");
                        }
                        finally
                        {
                            // Re-enable polling after API call completes
                            _heartbeatService.EndSchedulerModeUpdate();
                        }
                    });
                }
            }
        }
        
        public string SchedulerModeDisplay => _currentSchedulerMode == SchedulerMode.Manual 
            ? "Manual Control" 
            : "Auto Scheduler";
        
        public string SchedulerModeDescription => _currentSchedulerMode == SchedulerMode.Manual
            ? "You control which targets are imaged via the queue below."
            : "AstroManager scheduler automatically picks the best targets to image.";
        
        // Queue Management
        private ObservableCollection<ClientTargetQueueDto> _targetQueue = new();
        private ObservableCollection<ScheduledTargetDto> _availableTargets = new();
        private ClientTargetQueueDto? _selectedQueueItem;
        private ScheduledTargetDto? _selectedAvailableTarget;
        private bool _isLoadingQueue;
        
        public ObservableCollection<ClientTargetQueueDto> TargetQueue
        {
            get => _targetQueue;
            set { _targetQueue = value; RaisePropertyChanged(); }
        }
        
        public ObservableCollection<ScheduledTargetDto> AvailableTargets
        {
            get => _availableTargets;
            set { _availableTargets = value; RaisePropertyChanged(); }
        }
        
        public ClientTargetQueueDto? SelectedQueueItem
        {
            get => _selectedQueueItem;
            set { _selectedQueueItem = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(CanMoveUp)); RaisePropertyChanged(nameof(CanMoveDown)); }
        }
        
        public ScheduledTargetDto? SelectedAvailableTarget
        {
            get => _selectedAvailableTarget;
            set { _selectedAvailableTarget = value; RaisePropertyChanged(); }
        }
        
        public bool IsLoadingQueue
        {
            get => _isLoadingQueue;
            set { _isLoadingQueue = value; RaisePropertyChanged(); }
        }
        
        public bool CanMoveUp => SelectedQueueItem != null && SelectedQueueItem.QueueOrder > 1;
        public bool CanMoveDown => SelectedQueueItem != null && SelectedQueueItem.QueueOrder < TargetQueue.Count;
        
        public ICommand RefreshQueueCommand { get; private set; }
        public ICommand AddToQueueCommand { get; private set; }
        public ICommand RemoveFromQueueCommand { get; private set; }
        public ICommand MoveUpCommand { get; private set; }
        public ICommand MoveDownCommand { get; private set; }
        public ICommand ClearQueueCommand { get; private set; }
        
        // AutoRefreshIntervalMinutes removed - sync interval is now used instead
        public int AutoRefreshIntervalMinutes => _settings.AutoRefreshIntervalMinutes;

        public string HeartbeatStatus
        {
            get => _heartbeatStatus;
            set
            {
                _heartbeatStatus = value;
                RaisePropertyChanged();
            }
        }

        public WpfBrush HeartbeatStatusColor
        {
            get => _heartbeatStatusColor;
            set
            {
                _heartbeatStatusColor = value;
                RaisePropertyChanged();
            }
        }

        public string LastHeartbeatTime => _heartbeatService.LastHeartbeat.HasValue
            ? _heartbeatService.LastHeartbeat.Value.ToLocalTime().ToString("HH:mm:ss")
            : "Never";

        public int ExposureTemplateCount => _exposureTemplates?.Count ?? 0;
        
        // Data Editor Section - selected data type
        private string _selectedDataType = "ExposureTemplates";
        public string SelectedDataType
        {
            get => _selectedDataType;
            set 
            { 
                _selectedDataType = value; 
                RaisePropertyChanged(); 
                RaisePropertyChanged(nameof(IsExposureTemplatesSelected)); 
                RaisePropertyChanged(nameof(IsSchedulerConfigSelected)); 
                RaisePropertyChanged(nameof(IsSchedulerTargetTemplatesSelected)); 
                RaisePropertyChanged(nameof(IsMoonAvoidanceSelected)); 
                RaisePropertyChanged(nameof(IsObservatorySelected)); 
            }
        }
        
        public bool IsExposureTemplatesSelected
        {
            get => _selectedDataType == "ExposureTemplates";
            set { if (value) { SelectedDataType = "ExposureTemplates"; } }
        }
        
        public bool IsSchedulerConfigSelected
        {
            get => _selectedDataType == "SchedulerConfig";
            set { if (value) { SelectedDataType = "SchedulerConfig"; } }
        }
        
        public bool IsSchedulerTargetTemplatesSelected
        {
            get => _selectedDataType == "SchedulerTargetTemplates";
            set { if (value) { SelectedDataType = "SchedulerTargetTemplates"; } }
        }
        
        public bool IsMoonAvoidanceSelected
        {
            get => _selectedDataType == "MoonAvoidance";
            set { if (value) { SelectedDataType = "MoonAvoidance"; } }
        }
        
        public bool IsObservatorySelected
        {
            get => _selectedDataType == "Observatory";
            set { if (value) { SelectedDataType = "Observatory"; } }
        }
        
        // Observatory properties (uses LicensedObservatory loaded from API)
        public ObservatoryDto? Observatory => LicensedObservatory;
        public bool HasObservatory => LicensedObservatory != null;
        public bool ObservatoryHasCustomHorizon => LicensedObservatory?.CustomHorizonPoints?.Any() == true;
        public int ObservatoryCustomHorizonPointCount => LicensedObservatory?.CustomHorizonPoints?.Count ?? 0;
        
        // Commands for data type selection buttons
        public ICommand SelectExposureTemplatesCommand { get; }
        public ICommand SelectSchedulerConfigCommand { get; }
        public ICommand SelectSchedulerTargetTemplatesCommand { get; }
        public ICommand SelectMoonAvoidanceCommand { get; }
        public ICommand SelectObservatoryCommand { get; }
        
        // Exposure Templates from API
        private ObservableCollection<ExposureTemplateDto> _exposureTemplates = new();
        public ObservableCollection<ExposureTemplateDto> ExposureTemplates
        {
            get => _exposureTemplates;
            set 
            { 
                _exposureTemplates = value; 
                RaisePropertyChanged(); 
                RaisePropertyChanged(nameof(ExposureTemplateCount)); 
                RaisePropertyChanged(nameof(HasFilterMismatch));
                RaisePropertyChanged(nameof(FilterMismatchWarning));
            }
        }
        
        // Scheduler Configuration from API
        private SchedulerConfigurationDto? _schedulerConfig;
        public SchedulerConfigurationDto? SchedulerConfig
        {
            get => _schedulerConfig;
            set { _schedulerConfig = value; RaisePropertyChanged(); }
        }
        
        // Scheduler Configurations list for CRUD
        private ObservableCollection<SchedulerConfigurationDto> _schedulerConfigurations = new();
        public ObservableCollection<SchedulerConfigurationDto> SchedulerConfigurations
        {
            get => _schedulerConfigurations;
            set { _schedulerConfigurations = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(SchedulerConfigCount)); }
        }
        public int SchedulerConfigCount => _schedulerConfigurations?.Count ?? 0;
        
        private SchedulerConfigurationDto? _selectedSchedulerConfiguration;
        private SchedulerConfigurationDto? _selectedSchedulerConfigurationBackup;
        public SchedulerConfigurationDto? SelectedSchedulerConfiguration
        {
            get => _selectedSchedulerConfiguration;
            set 
            { 
                // Check for unsaved changes before switching
                if (value != _selectedSchedulerConfiguration && HasUnsavedSchedulerConfigChanges())
                {
                    _pendingSchedulerConfigSelection = value;
                    ShowDiscardChangesDialog("Scheduler Configuration", () => {
                        CancelSchedulerConfigEdit();
                        ApplySchedulerConfigSelection(_pendingSchedulerConfigSelection);
                        _pendingSchedulerConfigSelection = null;
                    });
                    return;
                }
                
                ApplySchedulerConfigSelection(value);
            }
        }
        
        private void ApplySchedulerConfigSelection(SchedulerConfigurationDto? value)
        {
            _selectedSchedulerConfiguration = value;
            // Create backup when selecting for editing
            if (value != null)
            {
                _selectedSchedulerConfigurationBackup = new SchedulerConfigurationDto
                {
                    Id = value.Id,
                    Name = value.Name,
                    PrimaryStrategy = value.PrimaryStrategy,
                    SecondaryStrategy = value.SecondaryStrategy,
                    TertiaryStrategy = value.TertiaryStrategy,
                    MinAltitudeDegrees = value.MinAltitudeDegrees,
                    FilterShootingPattern = value.FilterShootingPattern,
                    FilterBatchSize = value.FilterBatchSize,
                    GoalCompletionBehavior = value.GoalCompletionBehavior,
                    LowerPriorityTo = value.LowerPriorityTo,
                    ImagingEfficiencyPercent = value.ImagingEfficiencyPercent,
                    MinSessionDurationMinutes = value.MinSessionDurationMinutes,
                    MaxSequenceTimeMinutes = value.MaxSequenceTimeMinutes,
                    MaxHoursPerTargetPerNight = value.MaxHoursPerTargetPerNight,
                    MaxTotalHoursPerTarget = value.MaxTotalHoursPerTarget,
                    UseMoonAvoidance = value.UseMoonAvoidance,
                    AlwaysStopWhenNoTargetsForNight = value.AlwaysStopWhenNoTargetsForNight,
                    EnableSafetyMonitorCheck = value.EnableSafetyMonitorCheck,
                    EnableGuidingRmsCheck = value.EnableGuidingRmsCheck,
                    MaxGuidingRmsArcSec = value.MaxGuidingRmsArcSec,
                    EnableCloudCoverCheck = value.EnableCloudCoverCheck,
                    MaxCloudCoverPercent = value.MaxCloudCoverPercent,
                    EnableRainRateCheck = value.EnableRainRateCheck,
                    MaxRainRate = value.MaxRainRate,
                    EnableMountAltitudeCheck = value.EnableMountAltitudeCheck,
                    MinMountAltitudeDegrees = value.MinMountAltitudeDegrees,
                    EnableCoolerPowerCheck = value.EnableCoolerPowerCheck,
                    MaxCoolerPowerPercent = value.MaxCoolerPowerPercent,
                    ReduceCoolingOnHighCoolerPower = value.ReduceCoolingOnHighCoolerPower,
                    CoolerWarmupDeltaDegrees = value.CoolerWarmupDeltaDegrees,
                    ViolationAction = value.ViolationAction,
                    ViolationRetryMinutes = value.ViolationRetryMinutes,
                    IsDefault = value.IsDefault
                };
            }
            else
            {
                _selectedSchedulerConfigurationBackup = null;
            }
            RaisePropertyChanged(nameof(SelectedSchedulerConfiguration)); 
            RaisePropertyChanged(nameof(HasSelectedSchedulerConfiguration));
            RaisePropertyChanged(nameof(ConfigFilterShootingPattern));
            RaisePropertyChanged(nameof(ConfigGoalCompletionBehavior));
            RaisePropertyChanged(nameof(IsBatchPatternSelectedForConfig));
            RaisePropertyChanged(nameof(IsLowerPrioritySelectedForConfig));
        }
        public bool HasSelectedSchedulerConfiguration => _selectedSchedulerConfiguration != null;
        
        // Visibility properties for conditional fields in Scheduler Config editor
        public bool IsLowerPrioritySelectedForConfig => 
            _selectedSchedulerConfiguration?.GoalCompletionBehavior == "LowerPriority";
        
        public bool IsBatchPatternSelectedForConfig => 
            _selectedSchedulerConfiguration?.FilterShootingPattern == "Batch";
        
        // Wrapper properties for dropdown bindings that trigger visibility updates
        public string? ConfigFilterShootingPattern
        {
            get => _selectedSchedulerConfiguration?.FilterShootingPattern;
            set
            {
                if (_selectedSchedulerConfiguration != null && _selectedSchedulerConfiguration.FilterShootingPattern != value)
                {
                    _selectedSchedulerConfiguration.FilterShootingPattern = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(IsBatchPatternSelectedForConfig));
                }
            }
        }
        
        public string? ConfigGoalCompletionBehavior
        {
            get => _selectedSchedulerConfiguration?.GoalCompletionBehavior;
            set
            {
                if (_selectedSchedulerConfiguration != null && _selectedSchedulerConfiguration.GoalCompletionBehavior != value)
                {
                    _selectedSchedulerConfiguration.GoalCompletionBehavior = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(IsLowerPrioritySelectedForConfig));
                }
            }
        }
        
        // Scheduler Preview DTO (from API)
        private SchedulerPreviewDto? _schedulerPreview;
        public SchedulerPreviewDto? SchedulerPreview
        {
            get => _schedulerPreview;
            set 
            { 
                _schedulerPreview = value; 
                RaisePropertyChanged(); 
                RaisePropertyChanged(nameof(HasSchedulerPreview)); 
                UpdateSchedulerPreviewSessions();
            }
        }
        public bool HasSchedulerPreview => _schedulerPreview?.Success == true;
        public bool HasScheduledSessions => _schedulerPreview?.Sessions?.Any() == true;
        public bool HasSkippedTargets => _schedulerPreview?.SkippedTargets?.Any() == true;
        public int SkippedTargetsCount => _schedulerPreview?.SkippedTargets?.Count ?? 0;
        public bool HasUnscheduledSlots => _schedulerPreview?.UnscheduledSlots?.Any() == true;
        
        private ObservableCollection<SchedulerPreviewSessionDto> _schedulerPreviewSessions = new();
        public ObservableCollection<SchedulerPreviewSessionDto> SchedulerPreviewSessions
        {
            get => _schedulerPreviewSessions;
            private set { _schedulerPreviewSessions = value; RaisePropertyChanged(); }
        }
        
        private ObservableCollection<SchedulerPreviewSkippedTargetDto> _schedulerPreviewSkippedTargets = new();
        public ObservableCollection<SchedulerPreviewSkippedTargetDto> SchedulerPreviewSkippedTargets
        {
            get => _schedulerPreviewSkippedTargets;
            private set { _schedulerPreviewSkippedTargets = value; RaisePropertyChanged(); }
        }
        
        private ObservableCollection<UnscheduledSlotDto> _schedulerPreviewUnscheduledSlots = new();
        public ObservableCollection<UnscheduledSlotDto> SchedulerPreviewUnscheduledSlots
        {
            get => _schedulerPreviewUnscheduledSlots;
            private set { _schedulerPreviewUnscheduledSlots = value; RaisePropertyChanged(); }
        }
        
        // Selected target for altitude chart display
        private Guid? _selectedPreviewTargetId;
        private SchedulerPreviewSessionDto? _selectedPreviewSession;
        
        public Guid? SelectedPreviewTargetId
        {
            get => _selectedPreviewTargetId;
            set
            {
                _selectedPreviewTargetId = value;
                // Find and store the specific session (by index in the list matching this target)
                _selectedPreviewSession = value.HasValue 
                    ? _schedulerPreviewSessions.FirstOrDefault(s => s.TargetId == value)
                    : null;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(SelectedPreviewTargetName));
                RaisePropertyChanged(nameof(SelectedPreviewSessionExposurePlan));
                RaisePropertyChanged(nameof(SelectedPreviewSessionFilterProgress));
                RaisePropertyChanged(nameof(SelectedPreviewSessionHasTransit));
                RaisePropertyChanged(nameof(SelectedPreviewSessionTransitTime));
                RaisePropertyChanged(nameof(SelectedPreviewSessionMeridianFlipWindow));
                RaisePropertyChanged(nameof(SelectedPreviewTargetAltitudeData));
                RaisePropertyChanged(nameof(HasSelectedPreviewTarget));
                RaisePropertyChanged(nameof(HasSelectedSkippedTarget));
                RaisePropertyChanged(nameof(SelectedSkippedTargetName));
                RaisePropertyChanged(nameof(SelectedSkippedTargetAsSessions));
            }
        }
        
        // Select a specific session by index (used when clicking a session row)
        public void SelectPreviewSessionByIndex(int index)
        {
            if (index >= 0 && index < _schedulerPreviewSessions.Count)
            {
                _selectedPreviewSession = _schedulerPreviewSessions[index];
                _selectedPreviewTargetId = _selectedPreviewSession.TargetId;
                RaisePropertyChanged(nameof(SelectedPreviewTargetId));
                RaisePropertyChanged(nameof(SelectedPreviewTargetName));
                RaisePropertyChanged(nameof(SelectedPreviewSessionExposurePlan));
                RaisePropertyChanged(nameof(SelectedPreviewSessionFilterProgress));
                RaisePropertyChanged(nameof(SelectedPreviewSessionHasTransit));
                RaisePropertyChanged(nameof(SelectedPreviewSessionTransitTime));
                RaisePropertyChanged(nameof(SelectedPreviewSessionMeridianFlipWindow));
                RaisePropertyChanged(nameof(SelectedPreviewTargetAltitudeData));
                RaisePropertyChanged(nameof(HasSelectedPreviewTarget));
                RaisePropertyChanged(nameof(HasSelectedSkippedTarget));
                RaisePropertyChanged(nameof(SelectedSkippedTargetName));
                RaisePropertyChanged(nameof(SelectedSkippedTargetAsSessions));
            }
        }
        
        public bool HasSelectedPreviewTarget => _selectedPreviewTargetId.HasValue;
        
        // Skipped target chart properties
        public bool HasSelectedSkippedTarget => _selectedPreviewTargetId.HasValue && 
            _schedulerPreviewSkippedTargets.Any(s => s.TargetId == _selectedPreviewTargetId);
        
        public string? SelectedSkippedTargetName
        {
            get
            {
                if (!_selectedPreviewTargetId.HasValue) return null;
                var skipped = _schedulerPreviewSkippedTargets.FirstOrDefault(s => s.TargetId == _selectedPreviewTargetId);
                return skipped?.TargetName;
            }
        }
        
        // Convert selected skipped target to session format for the altitude chart
        public ObservableCollection<SchedulerPreviewSessionDto>? SelectedSkippedTargetAsSessions
        {
            get
            {
                if (!_selectedPreviewTargetId.HasValue) return null;
                var skipped = _schedulerPreviewSkippedTargets.FirstOrDefault(s => s.TargetId == _selectedPreviewTargetId);
                if (skipped?.AltitudeData == null || !skipped.AltitudeData.Any()) return null;
                
                // Create a pseudo-session that spans the entire night with the skipped target's altitude data
                var pseudoSession = new SchedulerPreviewSessionDto
                {
                    Id = Guid.NewGuid(),
                    TargetId = skipped.TargetId,
                    TargetName = skipped.TargetName,
                    StartTimeUtc = skipped.AltitudeData.Min(a => a.TimeUtc),
                    EndTimeUtc = skipped.AltitudeData.Max(a => a.TimeUtc),
                    Filter = Shared.Model.DTO.Settings.ECameraFilter.L, // Default filter for display
                    AltitudeData = skipped.AltitudeData,
                    AverageAltitude = skipped.AltitudeData.Average(a => a.Altitude)
                };
                
                return new ObservableCollection<SchedulerPreviewSessionDto> { pseudoSession };
            }
        }
        
        public string? SelectedPreviewTargetName
        {
            get
            {
                if (!_selectedPreviewTargetId.HasValue) return null;
                var session = _schedulerPreviewSessions.FirstOrDefault(s => s.TargetId == _selectedPreviewTargetId);
                if (session != null) return session.TargetName;
                var skipped = _schedulerPreviewSkippedTargets.FirstOrDefault(s => s.TargetId == _selectedPreviewTargetId);
                return skipped?.TargetName;
            }
        }
        
        public string? SelectedPreviewSessionExposurePlan
        {
            get => _selectedPreviewSession?.ExposurePlan;
        }
        
        public string? SelectedPreviewSessionFilterProgress
        {
            get => _selectedPreviewSession?.FilterProgressDisplay;
        }
        
        public bool SelectedPreviewSessionHasTransit
        {
            get => _selectedPreviewSession?.TransitTimeUtc.HasValue == true;
        }
        
        public string? SelectedPreviewSessionTransitTime
        {
            get => _selectedPreviewSession?.TransitTimeLocal?.ToString("HH:mm");
        }
        
        public string? SelectedPreviewSessionMeridianFlipWindow
        {
            get
            {
                if (_selectedPreviewSession?.MeridianFlipStartUtc == null || 
                    _selectedPreviewSession?.MeridianFlipEndUtc == null)
                    return null;
                    
                var start = _selectedPreviewSession.MeridianFlipStartUtc.Value.ToLocalTime();
                var end = _selectedPreviewSession.MeridianFlipEndUtc.Value.ToLocalTime();
                var hasFlip = _selectedPreviewSession.HasMeridianFlip ? " ⚠️ FLIP IN SESSION" : "";
                return $"  (Flip window: {start:HH:mm}-{end:HH:mm}){hasFlip}";
            }
        }
        
        public List<AltitudeDataPoint>? SelectedPreviewTargetAltitudeData
        {
            get
            {
                if (!_selectedPreviewTargetId.HasValue) return null;
                var skipped = _schedulerPreviewSkippedTargets.FirstOrDefault(s => s.TargetId == _selectedPreviewTargetId);
                return skipped?.AltitudeData;
            }
        }
        
        /// <summary>
        /// Filter observability windows for the selected target - shows when each filter can be used
        /// considering only horizon and moon avoidance constraints
        /// </summary>
        public TargetFilterObservabilityDto? SelectedTargetFilterObservability
        {
            get
            {
                if (!_selectedPreviewTargetId.HasValue) return null;
                return ComputeFilterObservabilityForTarget(_selectedPreviewTargetId.Value);
            }
        }
        
        private TargetFilterObservabilityDto? ComputeFilterObservabilityForTarget(Guid targetId)
        {
            // Find the target from either sessions or skipped targets
            var target = Targets.FirstOrDefault(t => t.Id == targetId);
            if (target == null) return null;
            
            // Get FULL NIGHT altitude data for filter observability calculations
            // Use FullNightAltitudeData from sessions (covers entire night, not just session time)
            // Or AltitudeData from skipped targets (already covers full night)
            List<AltitudeDataPoint>? altitudeData = null;
            var session = _schedulerPreviewSessions.FirstOrDefault(s => s.TargetId == targetId);
            var skipped = _schedulerPreviewSkippedTargets.FirstOrDefault(s => s.TargetId == targetId);
            
            if (session?.FullNightAltitudeData != null && session.FullNightAltitudeData.Any())
                altitudeData = session.FullNightAltitudeData;
            else if (skipped?.AltitudeData != null && skipped.AltitudeData.Any())
                altitudeData = skipped.AltitudeData;
            
            if (altitudeData == null || !altitudeData.Any()) return null;
            
            var result = new TargetFilterObservabilityDto
            {
                TargetId = targetId,
                TargetName = target.Name,
                AltitudeData = altitudeData
            };
            
            // Calculate rise/set times and max altitude
            var aboveHorizon = altitudeData.Where(p => p.Altitude >= (SchedulerConfig?.MinAltitudeDegrees ?? 30)).ToList();
            if (aboveHorizon.Any())
            {
                var first = aboveHorizon.OrderBy(p => p.TimeUtc).First();
                var last = aboveHorizon.OrderBy(p => p.TimeUtc).Last();
                var max = aboveHorizon.OrderByDescending(p => p.Altitude).First();
                
                result.RiseTimeLocal = first.TimeUtc.ToLocalTime();
                result.SetTimeLocal = last.TimeUtc.ToLocalTime();
                result.MaxAltitude = max.Altitude;
                result.MaxAltitudeTimeLocal = max.TimeUtc.ToLocalTime();
            }
            
            // Get active filters for this target (imaging goals not complete)
            var activeFilters = target.ImagingGoals?
                .Where(g => g.IsEnabled && g.CompletedExposures < g.GoalExposureCount)
                .Select(g => g.Filter.ToString())
                .Distinct()
                .ToList() ?? new List<string>();
            
            if (!activeFilters.Any()) return result;
            
            // Build moon avoidance profile mappings
            var filterProfiles = new Dictionary<string, MoonAvoidanceProfileDto?>();
            if (ExposureTemplates != null && MoonAvoidanceProfiles != null)
            {
                foreach (var filterName in activeFilters)
                {
                    if (Enum.TryParse<ECameraFilter>(filterName, out var filterEnum))
                    {
                        var template = ExposureTemplates.FirstOrDefault(t => t.Filter == filterEnum && t.MoonAvoidanceProfileId.HasValue);
                        if (template != null)
                        {
                            var profile = MoonAvoidanceProfiles.FirstOrDefault(p => p.Id == template.MoonAvoidanceProfileId);
                            filterProfiles[filterName] = profile;
                        }
                    }
                }
            }
            
            // For each filter, compute observable windows
            foreach (var filterName in activeFilters)
            {
                var windows = new List<FilterObservabilityWindow>();
                FilterObservabilityWindow? currentWindow = null;
                
                var profile = filterProfiles.GetValueOrDefault(filterName);
                
                foreach (var point in altitudeData.OrderBy(p => p.TimeUtc))
                {
                    bool isObservable = point.Altitude >= (SchedulerConfig?.MinAltitudeDegrees ?? 30);
                    bool isBlockedByMoon = false;
                    string? blockReason = null;
                    
                    // Check moon avoidance
                    // Use moon illumination from the scheduler preview if available
                    var moonIllumination = _schedulerPreview?.MoonPhasePercent / 100.0 ?? 0.5;
                    if (isObservable && profile != null && point.MoonAltitude.HasValue && point.MoonDistance.HasValue)
                    {
                        if (point.MoonAltitude.Value >= profile.MinMoonAltitudeDegrees)
                        {
                            var requiredDistance = profile.CalculateAvoidanceDistance(moonIllumination);
                            if (point.MoonDistance.Value < requiredDistance)
                            {
                                isBlockedByMoon = true;
                                blockReason = $"Moon too close ({point.MoonDistance.Value:F0}° < {requiredDistance:F0}° required)";
                            }
                        }
                    }
                    
                    bool canShoot = isObservable && !isBlockedByMoon;
                    
                    if (canShoot)
                    {
                        if (currentWindow == null)
                        {
                            currentWindow = new FilterObservabilityWindow
                            {
                                Filter = filterName,
                                StartTimeUtc = point.TimeUtc
                            };
                        }
                        currentWindow.EndTimeUtc = point.TimeUtc;
                    }
                    else
                    {
                        if (currentWindow != null)
                        {
                            // End the current window
                            currentWindow.EndReason = isBlockedByMoon ? blockReason : "Below horizon";
                            if (currentWindow.DurationMinutes >= 5) // Only include windows >= 5 minutes
                                windows.Add(currentWindow);
                            currentWindow = null;
                        }
                    }
                }
                
                // Don't forget the last window
                if (currentWindow != null)
                {
                    currentWindow.EndReason = "Dawn";
                    if (currentWindow.DurationMinutes >= 5)
                        windows.Add(currentWindow);
                }
                
                if (windows.Any())
                {
                    result.FilterWindows[filterName] = windows;
                }
            }
            
            return result;
        }
        
        public ICommand SelectPreviewTargetCommand { get; }
        
        private void SelectPreviewTarget(object? param)
        {
            // Handle both session object (from session list) and Guid (from skipped targets)
            if (param is SchedulerPreviewSessionDto session)
            {
                // Toggle: if same session clicked, deselect
                if (_selectedPreviewSession == session)
                {
                    _selectedPreviewSession = null;
                    _selectedPreviewTargetId = null;
                }
                else
                {
                    _selectedPreviewSession = session;
                    _selectedPreviewTargetId = session.TargetId;
                }
            }
            else if (param is Guid targetId)
            {
                // Legacy: for skipped targets that still use Guid
                if (targetId == _selectedPreviewTargetId)
                {
                    _selectedPreviewTargetId = null;
                    _selectedPreviewSession = null;
                }
                else
                {
                    _selectedPreviewTargetId = targetId;
                    _selectedPreviewSession = _schedulerPreviewSessions.FirstOrDefault(s => s.TargetId == targetId);
                }
            }
            
            RaisePropertyChanged(nameof(SelectedPreviewTargetId));
            RaisePropertyChanged(nameof(SelectedPreviewTargetName));
            RaisePropertyChanged(nameof(SelectedPreviewSessionExposurePlan));
            RaisePropertyChanged(nameof(SelectedPreviewSessionFilterProgress));
            RaisePropertyChanged(nameof(SelectedPreviewSessionHasTransit));
            RaisePropertyChanged(nameof(SelectedPreviewSessionTransitTime));
            RaisePropertyChanged(nameof(SelectedPreviewSessionMeridianFlipWindow));
            RaisePropertyChanged(nameof(SelectedPreviewTargetAltitudeData));
            RaisePropertyChanged(nameof(SelectedTargetFilterObservability));
            RaisePropertyChanged(nameof(HasSelectedPreviewTarget));
            RaisePropertyChanged(nameof(HasSelectedSkippedTarget));
            RaisePropertyChanged(nameof(SelectedSkippedTargetName));
            RaisePropertyChanged(nameof(SelectedSkippedTargetAsSessions));
        }
        
        private void UpdateSchedulerPreviewSessions()
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                // Create new collections to trigger DependencyProperty change detection
                var newSessions = new ObservableCollection<SchedulerPreviewSessionDto>();
                if (_schedulerPreview?.Sessions != null)
                {
                    foreach (var session in _schedulerPreview.Sessions)
                    {
                        newSessions.Add(session);
                    }
                }
                SchedulerPreviewSessions = newSessions;
                
                var newSkippedTargets = new ObservableCollection<SchedulerPreviewSkippedTargetDto>();
                if (_schedulerPreview?.SkippedTargets != null)
                {
                    foreach (var skipped in _schedulerPreview.SkippedTargets)
                    {
                        newSkippedTargets.Add(skipped);
                    }
                }
                SchedulerPreviewSkippedTargets = newSkippedTargets;
                
                var newUnscheduledSlots = new ObservableCollection<UnscheduledSlotDto>();
                if (_schedulerPreview?.UnscheduledSlots != null)
                {
                    foreach (var slot in _schedulerPreview.UnscheduledSlots)
                    {
                        newUnscheduledSlots.Add(slot);
                    }
                }
                SchedulerPreviewUnscheduledSlots = newUnscheduledSlots;
            });
            RaisePropertyChanged(nameof(HasScheduledSessions));
            RaisePropertyChanged(nameof(HasSkippedTargets));
            RaisePropertyChanged(nameof(SkippedTargetsCount));
            RaisePropertyChanged(nameof(HasUnscheduledSlots));
        }
        
        private Guid? _selectedPreviewConfigId;
        public Guid? SelectedPreviewConfigId
        {
            get => _selectedPreviewConfigId;
            set { _selectedPreviewConfigId = value; RaisePropertyChanged(); }
        }
        
        // Preview date selection
        private DateTime _generatePreviewDate = DateTime.Today;
        public DateTime GeneratePreviewDate
        {
            get => _generatePreviewDate;
            set { _generatePreviewDate = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(GeneratePreviewDateDisplay)); }
        }
        public string GeneratePreviewDateDisplay => _generatePreviewDate.ToString("ddd, MMM d, yyyy");
        
        // Start time options for preview
        public List<string> PreviewStartTimeOptions { get; } = new List<string> 
        { 
            "18:00", "19:00", "20:00", "21:00", "22:00", "23:00", "00:00", "01:00", "02:00", "03:00", "04:00", "05:00", "06:00", "07:00", "Now" 
        };
        
        private string _selectedPreviewStartTime = "Now";
        public string SelectedPreviewStartTime
        {
            get => _selectedPreviewStartTime;
            set { _selectedPreviewStartTime = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(ChartStartTime)); }
        }
        
        /// <summary>
        /// Computed chart start time based on SelectedPreviewStartTime.
        /// Returns DateTime in UTC for the selected start time, or null if "Now" is not applicable yet.
        /// </summary>
        public DateTime? ChartStartTime
        {
            get
            {
                if (string.IsNullOrEmpty(_selectedPreviewStartTime))
                    return null;
                
                // "Now" means use current time (UTC)
                if (_selectedPreviewStartTime == "Now")
                    return DateTime.UtcNow;
                
                // Parse HH:mm format and combine with GeneratePreviewDate
                if (TimeSpan.TryParse(_selectedPreviewStartTime, out var time))
                {
                    // Determine which date to use based on time
                    // If time is before 12:00, it's likely next day (after midnight)
                    var baseDate = GeneratePreviewDate;
                    if (time.Hours < 12)
                        baseDate = baseDate.AddDays(1);
                    
                    var localTime = baseDate.Date + time;
                    return localTime.ToUniversalTime();
                }
                
                return null;
            }
        }
        
        // Location mismatch warning
        private bool _showLocationMismatch;
        public bool ShowLocationMismatch
        {
            get => _showLocationMismatch;
            set { _showLocationMismatch = value; RaisePropertyChanged(); }
        }
        
        private string _locationMismatchWarning = "";
        public string LocationMismatchWarning
        {
            get => _locationMismatchWarning;
            set { _locationMismatchWarning = value; RaisePropertyChanged(); }
        }
        
        // Licensed observatory for location comparison
        private ObservatoryDto? _licensedObservatory;
        public ObservatoryDto? LicensedObservatory
        {
            get => _licensedObservatory;
            set { _licensedObservatory = value; RaisePropertyChanged(); }
        }
        
        // Scheduler Target Templates from API
        private ObservableCollection<SchedulerTargetTemplateDto> _schedulerTargetTemplates = new();
        public ObservableCollection<SchedulerTargetTemplateDto> SchedulerTargetTemplates
        {
            get => _schedulerTargetTemplates;
            set { _schedulerTargetTemplates = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(SchedulerTargetTemplateCount)); }
        }
        public int SchedulerTargetTemplateCount => _schedulerTargetTemplates?.Count ?? 0;
        
        private SchedulerTargetTemplateDto? _selectedSchedulerTargetTemplate;
        private SchedulerTargetTemplateDto? _selectedSchedulerTargetTemplateBackup;
        private SchedulerTargetTemplateDto? _pendingSchedulerTargetTemplateSelection;
        
        public SchedulerTargetTemplateDto? SelectedSchedulerTargetTemplate
        {
            get => _selectedSchedulerTargetTemplate;
            set 
            { 
                if (value != _selectedSchedulerTargetTemplate && HasUnsavedSchedulerTargetTemplateChanges())
                {
                    _pendingSchedulerTargetTemplateSelection = value;
                    ShowDiscardChangesDialog("Scheduler Target Template", () => {
                        CancelSchedulerTargetTemplateEdit();
                        ApplySchedulerTargetTemplateSelection(_pendingSchedulerTargetTemplateSelection);
                        _pendingSchedulerTargetTemplateSelection = null;
                    });
                    return;
                }
                
                ApplySchedulerTargetTemplateSelection(value);
            }
        }
        
        private void ApplySchedulerTargetTemplateSelection(SchedulerTargetTemplateDto? value)
        {
            _selectedSchedulerTargetTemplate = value;
            if (value != null)
            {
                _selectedSchedulerTargetTemplateBackup = new SchedulerTargetTemplateDto
                {
                    Id = value.Id,
                    Name = value.Name,
                    Description = value.Description,
                    EquipmentId = value.EquipmentId,
                    FilterShootingPattern = value.FilterShootingPattern,
                    FilterBatchSize = value.FilterBatchSize,
                    MinSessionDurationMinutes = value.MinSessionDurationMinutes,
                    MinAltitude = value.MinAltitude,
                    MaxHoursPerNight = value.MaxHoursPerNight,
                    MaxSequenceTimeMinutes = value.MaxSequenceTimeMinutes,
                    GoalCompletionBehaviour = value.GoalCompletionBehaviour,
                    LowerPriorityTo = value.LowerPriorityTo,
                    UseMoonAvoidance = value.UseMoonAvoidance,
                    MoonAvoidanceProfilesJson = value.MoonAvoidanceProfilesJson,
                    MinStartTime = value.MinStartTime,
                    MaxStartTime = value.MaxStartTime,
                    MinMoonPhasePercent = value.MinMoonPhasePercent,
                    MaxMoonPhasePercent = value.MaxMoonPhasePercent,
                    DisplayOrder = value.DisplayOrder
                };
            }
            else
            {
                _selectedSchedulerTargetTemplateBackup = null;
            }
            RaisePropertyChanged(nameof(SelectedSchedulerTargetTemplate)); 
            RaisePropertyChanged(nameof(HasSelectedSchedulerTargetTemplate));
            RaisePropertyChanged(nameof(TemplateGoalCompletionBehaviour));
            RaisePropertyChanged(nameof(TemplateFilterShootingPattern));
            RaisePropertyChanged(nameof(IsLowerPrioritySelectedForTemplate));
            RaisePropertyChanged(nameof(IsBatchPatternSelectedForTemplate));
        }
        public bool HasSelectedSchedulerTargetTemplate => _selectedSchedulerTargetTemplate != null;
        
        // Visibility properties for conditional fields in Scheduler Target Template editor
        public bool IsLowerPrioritySelectedForTemplate => 
            _selectedSchedulerTargetTemplate?.GoalCompletionBehaviour == "LowerPriority";
        
        public bool IsBatchPatternSelectedForTemplate => 
            _selectedSchedulerTargetTemplate?.FilterShootingPattern == "Batch";
        
        // Wrapper properties for dropdown bindings that trigger visibility updates
        public string? TemplateGoalCompletionBehaviour
        {
            get => _selectedSchedulerTargetTemplate?.GoalCompletionBehaviour;
            set
            {
                if (_selectedSchedulerTargetTemplate != null && _selectedSchedulerTargetTemplate.GoalCompletionBehaviour != value)
                {
                    _selectedSchedulerTargetTemplate.GoalCompletionBehaviour = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(IsLowerPrioritySelectedForTemplate));
                }
            }
        }
        
        public string? TemplateFilterShootingPattern
        {
            get => _selectedSchedulerTargetTemplate?.FilterShootingPattern;
            set
            {
                if (_selectedSchedulerTargetTemplate != null && _selectedSchedulerTargetTemplate.FilterShootingPattern != value)
                {
                    _selectedSchedulerTargetTemplate.FilterShootingPattern = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(IsBatchPatternSelectedForTemplate));
                }
            }
        }
        
        // Confirmation Dialog properties for NINA-style delete confirmations
        private bool _showConfirmDialog;
        public bool ShowConfirmDialog
        {
            get => _showConfirmDialog;
            set { _showConfirmDialog = value; RaisePropertyChanged(); }
        }
        
        private string _confirmDialogTitle = "";
        public string ConfirmDialogTitle
        {
            get => _confirmDialogTitle;
            set { _confirmDialogTitle = value; RaisePropertyChanged(); }
        }
        
        private string _confirmDialogMessage = "";
        public string ConfirmDialogMessage
        {
            get => _confirmDialogMessage;
            set { _confirmDialogMessage = value; RaisePropertyChanged(); }
        }
        
        private Action? _confirmDialogAction;
        private Action? _confirmDialogNoAction;
        public ICommand ConfirmDialogYesCommand { get; }
        public ICommand ConfirmDialogNoCommand { get; }
        
        // Pending selection fields for row change confirmation
        private ExposureTemplateDto? _pendingExposureTemplateSelection;
        private SchedulerConfigurationDto? _pendingSchedulerConfigSelection;
        private MoonAvoidanceProfileDto? _pendingMoonAvoidanceProfileSelection;
        
        // Moon Avoidance Profiles from API
        private ObservableCollection<MoonAvoidanceProfileDto> _moonAvoidanceProfiles = new();
        public ObservableCollection<MoonAvoidanceProfileDto> MoonAvoidanceProfiles
        {
            get => _moonAvoidanceProfiles;
            set { _moonAvoidanceProfiles = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(MoonAvoidanceProfileCount)); }
        }
        public int MoonAvoidanceProfileCount => _moonAvoidanceProfiles?.Count ?? 0;
        
        private MoonAvoidanceProfileDto? _selectedMoonAvoidanceProfile;
        private MoonAvoidanceProfileDto? _selectedMoonAvoidanceProfileBackup;
        public MoonAvoidanceProfileDto? SelectedMoonAvoidanceProfile
        {
            get => _selectedMoonAvoidanceProfile;
            set 
            { 
                // Check for unsaved changes before switching
                if (value != _selectedMoonAvoidanceProfile && HasUnsavedMoonAvoidanceProfileChanges())
                {
                    _pendingMoonAvoidanceProfileSelection = value;
                    ShowDiscardChangesDialog("Moon Avoidance Profile", () => {
                        CancelMoonAvoidanceProfileEdit();
                        ApplyMoonAvoidanceProfileSelection(_pendingMoonAvoidanceProfileSelection);
                        _pendingMoonAvoidanceProfileSelection = null;
                    });
                    return;
                }
                
                ApplyMoonAvoidanceProfileSelection(value);
            }
        }
        
        private void ApplyMoonAvoidanceProfileSelection(MoonAvoidanceProfileDto? value)
        {
            _selectedMoonAvoidanceProfile = value;
            // Create backup when selecting for editing
            if (value != null)
            {
                _selectedMoonAvoidanceProfileBackup = new MoonAvoidanceProfileDto
                {
                    Id = value.Id,
                    Name = value.Name,
                    FullMoonDistanceDegrees = value.FullMoonDistanceDegrees,
                    WidthInDays = value.WidthInDays,
                    MinMoonAltitudeDegrees = value.MinMoonAltitudeDegrees,
                    IsSystemDefault = value.IsSystemDefault
                };
            }
            else
            {
                _selectedMoonAvoidanceProfileBackup = null;
            }
            RaisePropertyChanged(nameof(SelectedMoonAvoidanceProfile)); 
            RaisePropertyChanged(nameof(HasSelectedMoonAvoidanceProfile));
            RaisePropertyChanged(nameof(IsSelectedMoonAvoidanceProfileEditable)); 
        }
        public bool HasSelectedMoonAvoidanceProfile => _selectedMoonAvoidanceProfile != null;
        public bool IsSelectedMoonAvoidanceProfileEditable => _selectedMoonAvoidanceProfile != null && !_selectedMoonAvoidanceProfile.IsSystemDefault;
        
        // Moon Avoidance Profile for exposure template (selected by ID)
        private bool _isRefreshingExposureTemplateGrid = false;
        public Guid? SelectedExposureTemplateMoonAvoidanceProfileId
        {
            get => _selectedExposureTemplate?.MoonAvoidanceProfileId;
            set 
            { 
                if (_selectedExposureTemplate != null) 
                { 
                    _selectedExposureTemplate.MoonAvoidanceProfileId = value;
                    _selectedExposureTemplate.MoonAvoidanceProfileName = MoonAvoidanceProfiles.FirstOrDefault(p => p.Id == value)?.Name;
                    RaisePropertyChanged();
                    // Force grid to refresh by refreshing the collection view
                    _isRefreshingExposureTemplateGrid = true;
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        System.Windows.Data.CollectionViewSource.GetDefaultView(ExposureTemplates)?.Refresh();
                        _isRefreshingExposureTemplateGrid = false;
                    }));
                } 
            }
        }
        
        // Commands for Scheduler Config CRUD
        public ICommand AddSchedulerConfigCommand { get; }
        public ICommand DeleteSchedulerConfigCommand { get; }
        public ICommand CopySchedulerConfigCommand { get; }
        public ICommand CloseSchedulerConfigEditCommand { get; }
        public ICommand SetDefaultSchedulerConfigCommand { get; }
        
        // Commands for Scheduler Preview (Generate from algorithm)
        public ICommand GenerateSchedulerPreviewCommand { get; }
        public ICommand PreviousPreviewDateCommand { get; }
        public ICommand NextPreviewDateCommand { get; }
        public ICommand TodayPreviewDateCommand { get; }
        
        // Commands for Scheduler Target Template CRUD
        public ICommand AddSchedulerTargetTemplateCommand { get; }
        public ICommand SaveSchedulerTargetTemplateCommand { get; }
        public ICommand DeleteSchedulerTargetTemplateCommand { get; }
        public ICommand CopySchedulerTargetTemplateCommand { get; }
        public ICommand CloseSchedulerTargetTemplateEditCommand { get; }
        
        // Commands for Moon Avoidance Profile CRUD
        public ICommand AddMoonAvoidanceProfileCommand { get; }
        public ICommand SaveMoonAvoidanceProfileCommand { get; }
        public ICommand DeleteMoonAvoidanceProfileCommand { get; }
        public ICommand CopyMoonAvoidanceProfileCommand { get; }
        public ICommand CloseMoonAvoidanceProfileEditCommand { get; }

        // Target list properties
        private string _targetSearchText = string.Empty;
        public string TargetSearchText
        {
            get => _targetSearchText;
            set
            {
                _targetSearchText = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasTargetSearchText));
                RaisePropertyChanged(nameof(FilteredTargets));
                RefreshGroupedTargetsView();
            }
        }
        
        public bool HasTargetSearchText => !string.IsNullOrWhiteSpace(_targetSearchText);
        
        // Group by options
        public static string[] TargetGroupByOptions => new[] { "None", "Status", "Type", "Template", "Priority" };
        private string _targetGroupBy = "None";
        public string TargetGroupBy
        {
            get => _targetGroupBy;
            set
            {
                _targetGroupBy = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FilteredTargets));
                RaisePropertyChanged(nameof(IsGroupingActive));
                RefreshGroupedTargetsView();
            }
        }
        
        // Filter options
        public static string[] TargetFilterOptions => new[] { "All", "Active", "Paused", "Completed", "< 50%", ">= 50%", ">= 90%", "Has Template", "No Template" };
        private string _targetFilterBy = "All";
        public string TargetFilterBy
        {
            get => _targetFilterBy;
            set
            {
                _targetFilterBy = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FilteredTargets));
                RefreshGroupedTargetsView();
            }
        }
        
        public IEnumerable<ScheduledTargetDto> FilteredTargets
        {
            get
            {
                var result = _targets.AsEnumerable();
                
                // Apply search filter
                if (!string.IsNullOrWhiteSpace(_targetSearchText))
                {
                    result = result.Where(t => t.Name?.Contains(_targetSearchText, StringComparison.OrdinalIgnoreCase) == true 
                                            || t.ObjectType?.Contains(_targetSearchText, StringComparison.OrdinalIgnoreCase) == true);
                }
                
                // Apply filter
                result = _targetFilterBy switch
                {
                    "Active" => result.Where(t => t.Status == Shared.Model.Enums.ScheduledTargetStatus.Active),
                    "Paused" => result.Where(t => t.Status == Shared.Model.Enums.ScheduledTargetStatus.Paused),
                    "Completed" => result.Where(t => t.Status == Shared.Model.Enums.ScheduledTargetStatus.Completed),
                    "< 50%" => result.Where(t => t.CompletionPercentage < 50),
                    ">= 50%" => result.Where(t => t.CompletionPercentage >= 50),
                    ">= 90%" => result.Where(t => t.CompletionPercentage >= 90),
                    "Has Template" => result.Where(t => t.SchedulerTargetTemplateId.HasValue),
                    "No Template" => result.Where(t => !t.SchedulerTargetTemplateId.HasValue),
                    _ => result
                };
                
                // Apply grouping (ordering)
                result = _targetGroupBy switch
                {
                    "Status" => result.OrderBy(t => t.Status).ThenBy(t => t.Name),
                    "Type" => result.OrderBy(t => t.ObjectType ?? "").ThenBy(t => t.Name),
                    "Template" => result.OrderBy(t => t.SchedulerTargetTemplateId.HasValue ? 0 : 1)
                                       .ThenBy(t => SchedulerTargetTemplates.FirstOrDefault(tpl => tpl.Id == t.SchedulerTargetTemplateId)?.Name ?? "zzz")
                                       .ThenBy(t => t.Name),
                    "Priority" => result.OrderBy(t => t.Priority).ThenBy(t => t.Name),
                    _ => result.OrderBy(t => t.Name)
                };
                
                return result;
            }
        }
        
        public bool IsGroupingActive => _targetGroupBy != "None";
        
        public string GetTargetGroupName(ScheduledTargetDto target)
        {
            return _targetGroupBy switch
            {
                "Status" => target.Status.ToString(),
                "Type" => target.ObjectType ?? "Unknown",
                "Template" => target.SchedulerTargetTemplateId.HasValue 
                    ? SchedulerTargetTemplates.FirstOrDefault(t => t.Id == target.SchedulerTargetTemplateId)?.Name ?? "Template"
                    : "No Template",
                "Priority" => target.Priority <= 25 ? "High Priority" : target.Priority <= 75 ? "Normal Priority" : "Low Priority",
                _ => ""
            };
        }
        
        private System.Windows.Data.ListCollectionView? _groupedTargetsView;
        
        public System.Windows.Data.ListCollectionView GroupedTargetsView
        {
            get
            {
                if (_groupedTargetsView == null)
                {
                    _groupedTargetsView = new System.Windows.Data.ListCollectionView(FilteredTargets.ToList());
                }
                return _groupedTargetsView;
            }
        }
        
        private void RefreshGroupedTargetsView()
        {
            var filteredList = FilteredTargets.ToList();
            _groupedTargetsView = new System.Windows.Data.ListCollectionView(filteredList);
            
            if (_targetGroupBy != "None")
            {
                _groupedTargetsView.GroupDescriptions.Clear();
                _groupedTargetsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(null, new TargetGroupConverter(this)));
            }
            
            RaisePropertyChanged(nameof(GroupedTargetsView));
            RefreshCollapsibleTargetGroups();
        }
        
        // Collapsible target groups for Options view
        private ObservableCollection<OptionsTargetGroup> _collapsibleTargetGroups = new();
        public ObservableCollection<OptionsTargetGroup> CollapsibleTargetGroups
        {
            get => _collapsibleTargetGroups;
            set { _collapsibleTargetGroups = value; RaisePropertyChanged(); }
        }
        
        private void RefreshCollapsibleTargetGroups()
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                var filteredList = FilteredTargets.ToList();
                
                // Preserve expanded state from existing groups
                var expandedGroups = _collapsibleTargetGroups
                    .Where(g => g.IsExpanded)
                    .Select(g => g.GroupName)
                    .ToHashSet();
                
                _collapsibleTargetGroups.Clear();
                
                if (_targetGroupBy == "None")
                {
                    // No grouping - single group with all targets
                    var group = new OptionsTargetGroup
                    {
                        GroupName = "All Targets",
                        IsExpanded = expandedGroups.Contains("All Targets") || expandedGroups.Count == 0
                    };
                    foreach (var target in filteredList)
                    {
                        group.Targets.Add(target);
                    }
                    _collapsibleTargetGroups.Add(group);
                }
                else
                {
                    // Group by selected property
                    var groups = filteredList
                        .GroupBy(t => GetTargetGroupName(t))
                        .OrderBy(g => g.Key);
                        
                    foreach (var g in groups)
                    {
                        var group = new OptionsTargetGroup
                        {
                            GroupName = g.Key,
                            IsExpanded = expandedGroups.Contains(g.Key) // Collapsed by default, preserve if was expanded
                        };
                        foreach (var target in g.OrderBy(t => t.Priority))
                        {
                            group.Targets.Add(target);
                        }
                        _collapsibleTargetGroups.Add(group);
                    }
                }
                
                RaisePropertyChanged(nameof(CollapsibleTargetGroups));
            });
        }
        
        public ObservableCollection<ScheduledTargetDto> Targets
        {
            get => _targets;
            set
            {
                _targets = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FilteredTargets));
            }
        }

        public ScheduledTargetDto? SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                // Skip unsaved changes check if we're refreshing the grid
                if (_isRefreshingTargetGrid)
                {
                    ApplyTargetSelection(value);
                    return;
                }
                
                // Check for unsaved imaging goal changes before switching targets
                if (value != _selectedTarget && HasUnsavedImagingGoalChanges())
                {
                    _pendingTargetSelection = value;
                    ShowDiscardChangesDialog("Imaging Goal", () => {
                        CancelImagingGoalEdit();
                        ApplyTargetSelection(_pendingTargetSelection);
                        _pendingTargetSelection = null;
                    });
                    return;
                }
                
                // Check for unsaved target changes before switching
                if (value != _selectedTarget && HasUnsavedTargetChanges())
                {
                    _pendingTargetSelection = value;
                    ShowDiscardChangesDialog("Scheduled Target", () => {
                        CancelTargetEdit();
                        ApplyTargetSelection(_pendingTargetSelection);
                        _pendingTargetSelection = null;
                    });
                    return;
                }
                
                ApplyTargetSelection(value);
            }
        }
        
        private void ApplyTargetSelection(ScheduledTargetDto? value)
        {
            _selectedTarget = value;
            // Create backup when selecting a target for editing
            if (value != null)
            {
                _selectedTargetBackup = new ScheduledTargetDto
                {
                    Id = value.Id,
                    Name = value.Name,
                    RightAscension = value.RightAscension,
                    Declination = value.Declination,
                    Priority = value.Priority,
                    RepeatCount = value.RepeatCount,
                    Status = value.Status,
                    SchedulerTargetTemplateId = value.SchedulerTargetTemplateId,
                    Description = value.Description,
                    PA = value.PA,
                    Notes = value.Notes,
                    // Mosaic settings
                    IsMosaic = value.IsMosaic,
                    MosaicPanelsX = value.MosaicPanelsX,
                    MosaicPanelsY = value.MosaicPanelsY,
                    MosaicOverlapPercent = value.MosaicOverlapPercent,
                    MosaicUseRotator = value.MosaicUseRotator,
                    UseCustomPanelGoals = value.UseCustomPanelGoals,
                    MosaicShootingStrategy = value.MosaicShootingStrategy,
                    MosaicPanelOrderingMethod = value.MosaicPanelOrderingMethod,
                    GoalOrderingMethod = value.GoalOrderingMethod
                };
            }
            else
            {
                _selectedTargetBackup = null;
            }
            
            RaisePropertyChanged(nameof(SelectedTarget));
            RaisePropertyChanged(nameof(HasSelectedTarget));
            RaisePropertyChanged(nameof(SelectedTargetName));
            RaisePropertyChanged(nameof(SelectedTargetRA));
            RaisePropertyChanged(nameof(SelectedTargetDec));
            RaisePropertyChanged(nameof(SelectedTargetGoalsCount));
            RaisePropertyChanged(nameof(SelectedTargetStatus));
            RaisePropertyChanged(nameof(SelectedTargetPriority));
            RaisePropertyChanged(nameof(SelectedTargetObjectType));
            RaisePropertyChanged(nameof(SelectedTargetIsMosaic));
            RaisePropertyChanged(nameof(SelectedTargetUseCustomPanelGoals));
            RaisePropertyChanged(nameof(SelectedTargetMosaicInfo));
            RaisePropertyChanged(nameof(SelectedTargetHasPanels));
            RaisePropertyChanged(nameof(SelectedTargetPanels));
            SelectedPanel = null; // Clear panel selection when target changes
            RaisePropertyChanged(nameof(SelectedTargetImagingGoals));
            RaisePropertyChanged(nameof(SelectedTargetTotalProgress));
            RaisePropertyChanged(nameof(SelectedTargetTotalTime));
            RaisePropertyChanged(nameof(SelectedTargetCompletedTime));
            RaisePropertyChanged(nameof(EditableRA));
            RaisePropertyChanged(nameof(EditableRAHours));
            RaisePropertyChanged(nameof(EditableRAMinutes));
            RaisePropertyChanged(nameof(EditableRASeconds));
            RaisePropertyChanged(nameof(EditableDec));
            RaisePropertyChanged(nameof(EditableDecDegrees));
            RaisePropertyChanged(nameof(EditableDecMinutes));
            RaisePropertyChanged(nameof(EditableDecSeconds));
            RaisePropertyChanged(nameof(EditablePositionAngle));
            RaisePropertyChanged(nameof(EditableTargetName));
            RaisePropertyChanged(nameof(EditablePriority));
            RaisePropertyChanged(nameof(EditableRepeatCount));
            RaisePropertyChanged(nameof(EditableStatus));
            RaisePropertyChanged(nameof(SelectedTargetSchedulerTemplate));
            RaisePropertyChanged(nameof(HasSelectedTargetSchedulerTemplate));
            RaisePropertyChanged(nameof(EditableDescription));
            RaisePropertyChanged(nameof(EditablePA));
            RaisePropertyChanged(nameof(EditableNotes));
            // Mosaic settings
            RaisePropertyChanged(nameof(EditableIsMosaic));
            RaisePropertyChanged(nameof(ShowMosaicSettings));
            RaisePropertyChanged(nameof(EditableMosaicPanelsX));
            RaisePropertyChanged(nameof(EditableMosaicPanelsY));
            RaisePropertyChanged(nameof(EditableMosaicOverlapPercent));
            RaisePropertyChanged(nameof(EditableMosaicUseRotator));
            RaisePropertyChanged(nameof(EditableUseCustomPanelGoals));
            RaisePropertyChanged(nameof(EditableMosaicShootingStrategy));
            RaisePropertyChanged(nameof(EditableMosaicPanelOrderingMethod));
            RaisePropertyChanged(nameof(EditableGoalOrderingMethod));
            // Clear imaging goal selection when target changes
            SelectedImagingGoal = null;
            
            // Load image stats for selected target
            if (_selectedTarget != null)
            {
                _ = LoadImageStatsAsync(_selectedTarget.Id);
                
                // For mosaic targets without panels loaded, fetch them from API
                if (_selectedTarget.IsMosaic && (_selectedTarget.Panels == null || _selectedTarget.Panels.Count == 0))
                {
                    _ = LoadPanelsForTargetAsync(_selectedTarget.Id);
                }
            }
            else
            {
                CapturedImageSummary = null;
            }
        }
        
        private async Task LoadPanelsForTargetAsync(Guid targetId)
        {
            if (string.IsNullOrEmpty(_settings.LicenseKey)) return;
            
            try
            {
                var targetWithPanels = await _apiClient.GetTargetByIdAsync(targetId);
                if (targetWithPanels?.Panels != null && _selectedTarget != null && _selectedTarget.Id == targetId)
                {
                    _selectedTarget.Panels = targetWithPanels.Panels;
                    _targetStore.UpdateTarget(_selectedTarget);
                    RaisePropertyChanged(nameof(SelectedTargetPanels));
                    RaisePropertyChanged(nameof(SelectedTargetHasPanels));
                    Logger.Info($"AstroManager: Loaded {targetWithPanels.Panels.Count} panels for mosaic target");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Failed to load panels: {ex.Message}");
            }
        }
        
        private void CancelTargetEdit()
        {
            _isRefreshingTargetGrid = true;
            if (_selectedTarget != null && _selectedTargetBackup != null)
            {
                // Restore from backup
                _selectedTarget.Name = _selectedTargetBackup.Name;
                _selectedTarget.RightAscension = _selectedTargetBackup.RightAscension;
                _selectedTarget.Declination = _selectedTargetBackup.Declination;
                _selectedTarget.Priority = _selectedTargetBackup.Priority;
                _selectedTarget.RepeatCount = _selectedTargetBackup.RepeatCount;
                _selectedTarget.Status = _selectedTargetBackup.Status;
                _selectedTarget.SchedulerTargetTemplateId = _selectedTargetBackup.SchedulerTargetTemplateId;
                _selectedTarget.Description = _selectedTargetBackup.Description;
                _selectedTarget.PA = _selectedTargetBackup.PA;
                _selectedTarget.Notes = _selectedTargetBackup.Notes;
                // Mosaic settings
                _selectedTarget.IsMosaic = _selectedTargetBackup.IsMosaic;
                _selectedTarget.MosaicPanelsX = _selectedTargetBackup.MosaicPanelsX;
                _selectedTarget.MosaicPanelsY = _selectedTargetBackup.MosaicPanelsY;
                _selectedTarget.MosaicOverlapPercent = _selectedTargetBackup.MosaicOverlapPercent;
                _selectedTarget.MosaicUseRotator = _selectedTargetBackup.MosaicUseRotator;
                _selectedTarget.UseCustomPanelGoals = _selectedTargetBackup.UseCustomPanelGoals;
                _selectedTarget.MosaicShootingStrategy = _selectedTargetBackup.MosaicShootingStrategy;
                _selectedTarget.MosaicPanelOrderingMethod = _selectedTargetBackup.MosaicPanelOrderingMethod;
                _selectedTarget.GoalOrderingMethod = _selectedTargetBackup.GoalOrderingMethod;
            }
            _isRefreshingTargetGrid = false;
        }

        private void UpdateSelectedTargetBackup()
        {
            if (_selectedTarget == null) return;
            
            _selectedTargetBackup = new ScheduledTargetDto
            {
                Id = _selectedTarget.Id,
                Name = _selectedTarget.Name,
                RightAscension = _selectedTarget.RightAscension,
                Declination = _selectedTarget.Declination,
                Priority = _selectedTarget.Priority,
                RepeatCount = _selectedTarget.RepeatCount,
                Status = _selectedTarget.Status,
                SchedulerTargetTemplateId = _selectedTarget.SchedulerTargetTemplateId,
                Description = _selectedTarget.Description,
                PA = _selectedTarget.PA,
                Notes = _selectedTarget.Notes,
                // Mosaic settings
                IsMosaic = _selectedTarget.IsMosaic,
                MosaicPanelsX = _selectedTarget.MosaicPanelsX,
                MosaicPanelsY = _selectedTarget.MosaicPanelsY,
                MosaicOverlapPercent = _selectedTarget.MosaicOverlapPercent,
                MosaicUseRotator = _selectedTarget.MosaicUseRotator,
                UseCustomPanelGoals = _selectedTarget.UseCustomPanelGoals,
                MosaicShootingStrategy = _selectedTarget.MosaicShootingStrategy,
                MosaicPanelOrderingMethod = _selectedTarget.MosaicPanelOrderingMethod,
                GoalOrderingMethod = _selectedTarget.GoalOrderingMethod
            };
        }

        public bool HasSelectedTarget => _selectedTarget != null;
        
        public string SelectedTargetName => _selectedTarget?.Name ?? string.Empty;
        
        public string SelectedTargetRA => _selectedTarget != null 
            ? FormatRA(_selectedTarget.RightAscension) 
            : string.Empty;
        
        public string SelectedTargetDec => _selectedTarget != null 
            ? FormatDec(_selectedTarget.Declination) 
            : string.Empty;
        
        public int SelectedTargetGoalsCount => _selectedTarget?.ImagingGoals?.Count ?? 0;
        
        public string SelectedTargetStatus => _selectedTarget?.Status.ToString() ?? string.Empty;
        
        public string SelectedTargetPriority => _selectedTarget?.Priority.ToString() ?? string.Empty;
        
        public string SelectedTargetObjectType => _selectedTarget?.ObjectType ?? string.Empty;
        
        public bool SelectedTargetIsMosaic => _selectedTarget?.IsMosaic ?? false;
        
        public bool SelectedTargetUseCustomPanelGoals => _selectedTarget?.UseCustomPanelGoals ?? false;
        
        public string SelectedTargetMosaicInfo => _selectedTarget?.IsMosaic == true 
            ? $"{_selectedTarget.MosaicPanelsX}x{_selectedTarget.MosaicPanelsY} ({_selectedTarget.TotalPanelCount} panels)"
            : string.Empty;
        
        public bool SelectedTargetHasPanels => _selectedTarget?.HasPanels ?? false;
        
        public ObservableCollection<ScheduledTargetPanelDto> SelectedTargetPanels
        {
            get
            {
                var panels = new ObservableCollection<ScheduledTargetPanelDto>();
                if (_selectedTarget?.Panels != null)
                {
                    Logger.Debug($"AstroManager: SelectedTargetPanels - Target has {_selectedTarget.Panels.Count} panels");
                    foreach (var panel in _selectedTarget.Panels.OrderBy(p => p.ShootingOrder ?? p.PanelNumber))
                    {
                        panels.Add(panel);
                    }
                }
                else
                {
                    Logger.Debug($"AstroManager: SelectedTargetPanels - Target has no panels (Panels is null: {_selectedTarget?.Panels == null}, IsMosaic: {_selectedTarget?.IsMosaic})");
                }
                return panels;
            }
        }
        
        // Selected panel for viewing details
        private ScheduledTargetPanelDto? _selectedPanel;
        public ScheduledTargetPanelDto? SelectedPanel
        {
            get => _selectedPanel;
            set
            {
                _selectedPanel = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasSelectedPanel));
                RaisePropertyChanged(nameof(SelectedPanelImagingGoals));
                RaisePropertyChanged(nameof(SelectedPanelBaseGoals));
                RaisePropertyChanged(nameof(SelectedPanelCustomGoals));
                RaisePropertyChanged(nameof(SelectedPanelIsEnabled));
                RaisePropertyChanged(nameof(SelectedPanelShootingOrder));
                RaisePropertyChanged(nameof(SelectedPanelRaDisplay));
                RaisePropertyChanged(nameof(SelectedPanelDecDisplay));
                RaisePropertyChanged(nameof(SelectedPanelPaDisplay));
            }
        }
        
        public bool HasSelectedPanel => _selectedPanel != null;
        
        // Panel coordinate display properties (read-only)
        public string SelectedPanelRaDisplay
        {
            get
            {
                if (_selectedPanel == null) return "-";
                var hours = _selectedPanel.RaHours;
                var h = (int)hours;
                var m = (int)((hours - h) * 60);
                var s = ((hours - h) * 60 - m) * 60;
                return $"{h:D2}h {m:D2}m {s:F1}s";
            }
        }
        
        public string SelectedPanelDecDisplay
        {
            get
            {
                if (_selectedPanel == null) return "-";
                var dec = _selectedPanel.DecDegrees;
                var sign = dec >= 0 ? "+" : "-";
                dec = Math.Abs(dec);
                var d = (int)dec;
                var m = (int)((dec - d) * 60);
                var s = ((dec - d) * 60 - m) * 60;
                return $"{sign}{d:D2}° {m:D2}' {s:F1}\"";
            }
        }
        
        public string SelectedPanelPaDisplay
        {
            get
            {
                if (_selectedPanel == null || _selectedTarget == null) return "-";
                return $"{_selectedTarget.PA:F1}°";
            }
        }
        
        // All imaging goals for selected panel
        public ObservableCollection<PanelImagingGoalDto> SelectedPanelImagingGoals
        {
            get
            {
                var goals = new ObservableCollection<PanelImagingGoalDto>();
                if (_selectedPanel?.ImagingGoals != null)
                {
                    foreach (var goal in _selectedPanel.ImagingGoals.OrderBy(g => g.IsCustomGoal).ThenBy(g => g.Filter.ToString()))
                    {
                        goals.Add(goal);
                    }
                }
                return goals;
            }
        }
        
        // Base goals for selected panel (synced from parent target) - with RepeatCount applied
        public ObservableCollection<PanelImagingGoalDisplayWrapper> SelectedPanelBaseGoals
        {
            get
            {
                var goals = new ObservableCollection<PanelImagingGoalDisplayWrapper>();
                if (_selectedPanel?.ImagingGoals != null && _selectedTarget != null)
                {
                    var repeatCount = Math.Max(1, _selectedTarget.RepeatCount);
                    foreach (var goal in _selectedPanel.ImagingGoals.Where(g => !g.IsCustomGoal).OrderBy(g => g.Filter.ToString()))
                    {
                        goals.Add(new PanelImagingGoalDisplayWrapper(goal, repeatCount));
                    }
                }
                return goals;
            }
        }
        
        // Custom goals for selected panel (panel-specific overrides) - with RepeatCount applied
        public ObservableCollection<PanelImagingGoalDisplayWrapper> SelectedPanelCustomGoals
        {
            get
            {
                var goals = new ObservableCollection<PanelImagingGoalDisplayWrapper>();
                if (_selectedPanel?.ImagingGoals != null && _selectedTarget != null)
                {
                    var repeatCount = Math.Max(1, _selectedTarget.RepeatCount);
                    foreach (var goal in _selectedPanel.ImagingGoals.Where(g => g.IsCustomGoal).OrderBy(g => g.Filter.ToString()))
                    {
                        goals.Add(new PanelImagingGoalDisplayWrapper(goal, repeatCount));
                    }
                }
                return goals;
            }
        }
        
        public bool SelectedPanelHasCustomGoals => _selectedPanel?.ImagingGoals?.Any(g => g.IsCustomGoal) ?? false;
        
        // Panel enabled/disabled state
        public bool SelectedPanelIsEnabled
        {
            get => _selectedPanel?.IsEnabled ?? true;
            set
            {
                if (_selectedPanel != null && _selectedPanel.IsEnabled != value)
                {
                    _selectedPanel.IsEnabled = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedTargetPanels)); // Refresh panel dropdown
                    
                    // Save to server
                    _ = SavePanelEnabledStateAsync();
                }
            }
        }
        
        // Panel shooting order (for manual panel ordering)
        public string? SelectedPanelShootingOrder
        {
            get => _selectedPanel?.ShootingOrder?.ToString();
            set
            {
                if (_selectedPanel != null && _selectedTarget?.Panels != null)
                {
                    int? newValue = null;
                    if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out var parsed) && parsed > 0)
                    {
                        newValue = parsed;
                    }
                    
                    if (_selectedPanel.ShootingOrder != newValue)
                    {
                        var oldValue = _selectedPanel.ShootingOrder;
                        
                        // Check if another panel has this value - if so, swap
                        var conflictingPanel = newValue.HasValue 
                            ? _selectedTarget.Panels.FirstOrDefault(p => p.Id != _selectedPanel.Id && p.ShootingOrder == newValue)
                            : null;
                        
                        if (conflictingPanel != null)
                        {
                            // Swap: give the other panel our old value
                            conflictingPanel.ShootingOrder = oldValue;
                        }
                        
                        _selectedPanel.ShootingOrder = newValue;
                        RaisePropertyChanged();
                        RaisePropertyChanged(nameof(SelectedTargetPanels)); // Refresh panel dropdown to show new order
                        
                        // Save to server (both panels if swapped)
                        _ = SavePanelShootingOrderAsync(conflictingPanel);
                    }
                }
            }
        }
        
        // Selected panel goal for editing with backup for cancel
        private PanelImagingGoalDto? _selectedPanelGoal;
        private PanelImagingGoalDto? _selectedPanelGoalBackup;
        private PanelImagingGoalDto? _pendingPanelGoalSelection;
        public PanelImagingGoalDto? SelectedPanelGoal
        {
            get => _selectedPanelGoal;
            set
            {
                // Check for unsaved changes before switching to a DIFFERENT goal (compare by Id, not reference)
                var isSameGoal = (value == null && _selectedPanelGoal == null) || 
                                 (value != null && _selectedPanelGoal != null && value.Id == _selectedPanelGoal.Id);
                
                if (!isSameGoal && HasUnsavedPanelGoalChanges())
                {
                    _pendingPanelGoalSelection = value;
                    ShowDiscardChangesDialog("Panel Goal", () => {
                        CancelPanelGoalEdit();
                        ApplyPanelGoalSelection(_pendingPanelGoalSelection);
                        _pendingPanelGoalSelection = null;
                    });
                    return;
                }
                
                ApplyPanelGoalSelection(value);
            }
        }
        
        private void ApplyPanelGoalSelection(PanelImagingGoalDto? value)
        {
            _selectedPanelGoal = value;
            if (value != null)
            {
                _selectedPanelGoalBackup = new PanelImagingGoalDto
                {
                    Id = value.Id,
                    ExposureTemplateId = value.ExposureTemplateId,
                    ExposureTemplate = value.ExposureTemplate,
                    GoalExposureCount = value.GoalExposureCount,
                    IsEnabled = value.IsEnabled,
                    IsCustomGoal = value.IsCustomGoal
                };
            }
            else
            {
                _selectedPanelGoalBackup = null;
            }
            RaisePropertyChanged(nameof(SelectedPanelGoal));
            RaisePropertyChanged(nameof(HasSelectedPanelGoal));
            RaisePropertyChanged(nameof(EditablePanelGoalExposureTemplateId));
            RaisePropertyChanged(nameof(EditablePanelGoalCount));
            RaisePropertyChanged(nameof(EditablePanelGoalIsEnabled));
            RaisePropertyChanged(nameof(SelectedPanelGoalMoonAvoidanceProfileName));
        }
        public bool HasSelectedPanelGoal => _selectedPanelGoal != null;
        
        // Get the moon avoidance profile name for the selected panel goal (from its ExposureTemplate)
        public string? SelectedPanelGoalMoonAvoidanceProfileName => 
            _selectedPanelGoal?.ExposureTemplate?.MoonAvoidanceProfileId != null 
                ? MoonAvoidanceProfiles.FirstOrDefault(p => p.Id == _selectedPanelGoal.ExposureTemplate.MoonAvoidanceProfileId)?.Name 
                : null;
        
        // Editable panel goal properties - uses ExposureTemplate
        public Guid? EditablePanelGoalExposureTemplateId
        {
            get => _selectedPanelGoal?.ExposureTemplateId;
            set
            {
                if (_selectedPanelGoal != null && value.HasValue && _selectedPanelGoal.ExposureTemplateId != value.Value)
                {
                    _selectedPanelGoal.ExposureTemplateId = value.Value;
                    // Update the template reference for display
                    _selectedPanelGoal.ExposureTemplate = ExposureTemplates.FirstOrDefault(t => t.Id == value.Value);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedPanelGoal)); // Refresh template details display
                    RaisePropertyChanged(nameof(SelectedPanelGoalMoonAvoidanceProfileName));
                    RaisePropertyChanged(nameof(SelectedPanelCustomGoals));
                }
            }
        }
        
        public int EditablePanelGoalCount
        {
            get => _selectedPanelGoal?.GoalExposureCount ?? 0;
            set
            {
                if (_selectedPanelGoal != null && _selectedPanelGoal.GoalExposureCount != value)
                {
                    _selectedPanelGoal.GoalExposureCount = value;
                    RaisePropertyChanged();
                }
            }
        }
        
        public bool EditablePanelGoalIsEnabled
        {
            get => _selectedPanelGoal?.IsEnabled ?? true;
            set
            {
                if (_selectedPanelGoal != null && _selectedPanelGoal.IsEnabled != value)
                {
                    _selectedPanelGoal.IsEnabled = value;
                    RaisePropertyChanged();
                }
            }
        }
        
        public ObservableCollection<ImagingGoalDisplayWrapper> SelectedTargetImagingGoals
        {
            get
            {
                var goals = new ObservableCollection<ImagingGoalDisplayWrapper>();
                if (_selectedTarget?.ImagingGoals != null)
                {
                    var repeatCount = Math.Max(1, _selectedTarget.RepeatCount);
                    foreach (var goal in _selectedTarget.ImagingGoals.OrderBy(g => g.Filter.ToString()))
                    {
                        goals.Add(new ImagingGoalDisplayWrapper(goal, repeatCount));
                    }
                }
                return goals;
            }
        }
        
        public double SelectedTargetTotalProgress
        {
            get
            {
                if (_selectedTarget?.ImagingGoals == null || !_selectedTarget.ImagingGoals.Any())
                    return 0;
                
                // Apply RepeatCount to total goal
                var repeatCount = Math.Max(1, _selectedTarget.RepeatCount);
                var totalGoal = _selectedTarget.ImagingGoals.Sum(g => g.GoalExposureCount) * repeatCount;
                var totalCompleted = _selectedTarget.ImagingGoals.Sum(g => g.CompletedExposures);
                
                return totalGoal > 0 ? Math.Round((double)totalCompleted / totalGoal * 100, 1) : 0;
            }
        }
        
        public string SelectedTargetTotalTime
        {
            get
            {
                if (_selectedTarget?.ImagingGoals == null || !_selectedTarget.ImagingGoals.Any())
                    return "0h 0m";
                
                // Apply RepeatCount multiplier (e.g., 7 means shoot all goals 7 times)
                var repeatCount = Math.Max(1, _selectedTarget.RepeatCount);
                var totalMinutes = _selectedTarget.ImagingGoals.Sum(g => g.GoalTimeMinutes) * repeatCount;
                var hours = (int)(totalMinutes / 60);
                var mins = (int)(totalMinutes % 60);
                
                // Show repeat indicator if RepeatCount > 1
                var repeatIndicator = repeatCount > 1 ? $" (×{repeatCount})" : "";
                return $"{hours}h {mins}m{repeatIndicator}";
            }
        }
        
        public string SelectedTargetCompletedTime
        {
            get
            {
                if (_selectedTarget?.ImagingGoals == null || !_selectedTarget.ImagingGoals.Any())
                    return "0h 0m";
                
                var completedMinutes = _selectedTarget.ImagingGoals.Sum(g => g.CompletedTimeMinutes);
                var hours = (int)(completedMinutes / 60);
                var mins = (int)(completedMinutes % 60);
                return $"{hours}h {mins}m";
            }
        }
        
        public string SelectedTargetRemainingTime
        {
            get
            {
                if (_selectedTarget?.ImagingGoals == null || !_selectedTarget.ImagingGoals.Any())
                    return "0h 0m";
                
                // Apply RepeatCount: Remaining = (Goal * RepeatCount) - Completed
                var repeatCount = Math.Max(1, _selectedTarget.RepeatCount);
                var totalGoalMinutes = _selectedTarget.ImagingGoals.Sum(g => g.GoalTimeMinutes) * repeatCount;
                var completedMinutes = _selectedTarget.ImagingGoals.Sum(g => g.CompletedTimeMinutes);
                var remainingMinutes = Math.Max(0, totalGoalMinutes - completedMinutes);
                var hours = (int)(remainingMinutes / 60);
                var mins = (int)(remainingMinutes % 60);
                return $"{hours}h {mins}m";
            }
        }
        
        public double SelectedTargetProgressPercent
        {
            get
            {
                if (_selectedTarget?.ImagingGoals == null || !_selectedTarget.ImagingGoals.Any())
                    return 0;
                
                // Apply RepeatCount to total goal
                var repeatCount = Math.Max(1, _selectedTarget.RepeatCount);
                var totalGoalMinutes = _selectedTarget.ImagingGoals.Sum(g => g.GoalTimeMinutes) * repeatCount;
                if (totalGoalMinutes <= 0) return 0;
                
                var completedMinutes = _selectedTarget.ImagingGoals.Sum(g => g.CompletedTimeMinutes);
                return Math.Min(100, (completedMinutes / totalGoalMinutes) * 100);
            }
        }

        // Editable Position Angle
        public string EditablePositionAngle
        {
            get => _selectedTarget?.PA?.ToString("F1") ?? string.Empty;
            set
            {
                if (_selectedTarget != null)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        _selectedTarget.PA = null;
                    }
                    else if (double.TryParse(value, out var pa))
                    {
                        _selectedTarget.PA = Math.Clamp(pa, 0, 360);
                    }
                    RaisePropertyChanged();
                }
            }
        }

        // Editable Target Name
        public string EditableTargetName
        {
            get => _selectedTarget?.Name ?? string.Empty;
            set
            {
                if (_selectedTarget != null && !string.IsNullOrWhiteSpace(value))
                {
                    _selectedTarget.Name = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedTargetName));
                }
            }
        }

        // Editable Priority
        public int EditablePriority
        {
            get => _selectedTarget?.Priority ?? 50;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.Priority = Math.Clamp(value, 1, 99);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedTargetPriority));
                }
            }
        }

        // Editable Repeat Count (SlotsRepeat)
        public int EditableRepeatCount
        {
            get => _selectedTarget?.RepeatCount ?? 1;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.RepeatCount = Math.Max(1, value);
                    RaisePropertyChanged();
                    
                    // Refresh imaging goals display (wrappers recalculate with new RepeatCount)
                    RaisePropertyChanged(nameof(SelectedTargetImagingGoals));
                    RaisePropertyChanged(nameof(SelectedTargetTotalProgress));
                    RaisePropertyChanged(nameof(SelectedTargetTotalTime));
                    RaisePropertyChanged(nameof(SelectedTargetCompletedTime));
                    RaisePropertyChanged(nameof(SelectedTargetRemainingTime));
                    RaisePropertyChanged(nameof(SelectedTargetProgressPercent));
                    RaisePropertyChanged(nameof(SelectedPanelBaseGoals));
                    RaisePropertyChanged(nameof(SelectedPanelCustomGoals));
                }
            }
        }

        // Editable Description
        public string EditableDescription
        {
            get => _selectedTarget?.Description ?? string.Empty;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.Description = value;
                    RaisePropertyChanged();
                }
            }
        }

        // Editable Position Angle
        public double? EditablePA
        {
            get => _selectedTarget?.PA;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.PA = value;
                    RaisePropertyChanged();
                }
            }
        }

        // Editable Notes
        public string EditableNotes
        {
            get => _selectedTarget?.Notes ?? string.Empty;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.Notes = value;
                    RaisePropertyChanged();
                }
            }
        }

        // --- MOSAIC SETTINGS ---
        public bool EditableIsMosaic
        {
            get => _selectedTarget?.IsMosaic ?? false;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.IsMosaic = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(ShowMosaicSettings));
                    // Update display properties for Mosaic Panels section
                    RaisePropertyChanged(nameof(SelectedTargetIsMosaic));
                    RaisePropertyChanged(nameof(SelectedTargetMosaicInfo));
                    RaisePropertyChanged(nameof(SelectedTargetHasPanels));
                    RaisePropertyChanged(nameof(SelectedTargetPanels));
                }
            }
        }
        
        public bool ShowMosaicSettings => _selectedTarget?.IsMosaic ?? false;

        public int EditableMosaicPanelsX
        {
            get => _selectedTarget?.MosaicPanelsX ?? 1;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.MosaicPanelsX = Math.Max(1, value);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedTargetMosaicInfo));
                }
            }
        }

        public int EditableMosaicPanelsY
        {
            get => _selectedTarget?.MosaicPanelsY ?? 1;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.MosaicPanelsY = Math.Max(1, value);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedTargetMosaicInfo));
                }
            }
        }

        public double EditableMosaicOverlapPercent
        {
            get => _selectedTarget?.MosaicOverlapPercent ?? 10.0;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.MosaicOverlapPercent = Math.Clamp(value, 0, 100);
                    RaisePropertyChanged();
                }
            }
        }

        public bool EditableMosaicUseRotator
        {
            get => _selectedTarget?.MosaicUseRotator ?? false;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.MosaicUseRotator = value;
                    RaisePropertyChanged();
                }
            }
        }

        public bool EditableUseCustomPanelGoals
        {
            get => _selectedTarget?.UseCustomPanelGoals ?? false;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.UseCustomPanelGoals = value;
                    RaisePropertyChanged();
                    // Update display properties for Mosaic Panels section
                    RaisePropertyChanged(nameof(SelectedTargetUseCustomPanelGoals));
                    RaisePropertyChanged(nameof(SelectedPanelHasCustomGoals));
                }
            }
        }

        public Shared.Model.Enums.MosaicShootingStrategy EditableMosaicShootingStrategy
        {
            get => _selectedTarget?.MosaicShootingStrategy ?? Shared.Model.Enums.MosaicShootingStrategy.Parallel;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.MosaicShootingStrategy = value;
                    RaisePropertyChanged();
                }
            }
        }

        public Shared.Model.Enums.MosaicPanelOrderingMethod EditableMosaicPanelOrderingMethod
        {
            get => _selectedTarget?.MosaicPanelOrderingMethod ?? Shared.Model.Enums.MosaicPanelOrderingMethod.Manual;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.MosaicPanelOrderingMethod = value;
                    RaisePropertyChanged();
                }
            }
        }

        public Shared.Model.Enums.GoalOrderingMethod EditableGoalOrderingMethod
        {
            get => _selectedTarget?.GoalOrderingMethod ?? Shared.Model.Enums.GoalOrderingMethod.BaseGoalsFirst;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.GoalOrderingMethod = value;
                    RaisePropertyChanged();
                }
            }
        }

        // Static arrays for mosaic dropdowns with display values
        public static KeyValuePair<Shared.Model.Enums.MosaicShootingStrategy, string>[] MosaicShootingStrategyDisplayValues => new[]
        {
            new KeyValuePair<Shared.Model.Enums.MosaicShootingStrategy, string>(Shared.Model.Enums.MosaicShootingStrategy.Sequential, "Sequential"),
            new KeyValuePair<Shared.Model.Enums.MosaicShootingStrategy, string>(Shared.Model.Enums.MosaicShootingStrategy.Parallel, "Parallel")
        };
        
        public static KeyValuePair<Shared.Model.Enums.MosaicPanelOrderingMethod, string>[] MosaicPanelOrderingDisplayValues => new[]
        {
            new KeyValuePair<Shared.Model.Enums.MosaicPanelOrderingMethod, string>(Shared.Model.Enums.MosaicPanelOrderingMethod.Manual, "Manual"),
            new KeyValuePair<Shared.Model.Enums.MosaicPanelOrderingMethod, string>(Shared.Model.Enums.MosaicPanelOrderingMethod.AutoMinObservability, "Auto (Min Observability)"),
            new KeyValuePair<Shared.Model.Enums.MosaicPanelOrderingMethod, string>(Shared.Model.Enums.MosaicPanelOrderingMethod.AutoMaxObservability, "Auto (Max Observability)")
        };
        
        public static KeyValuePair<Shared.Model.Enums.GoalOrderingMethod, string>[] GoalOrderingDisplayValues => new[]
        {
            new KeyValuePair<Shared.Model.Enums.GoalOrderingMethod, string>(Shared.Model.Enums.GoalOrderingMethod.BaseGoalsFirst, "Base Goals First"),
            new KeyValuePair<Shared.Model.Enums.GoalOrderingMethod, string>(Shared.Model.Enums.GoalOrderingMethod.CustomGoalsFirst, "Custom Goals First"),
            new KeyValuePair<Shared.Model.Enums.GoalOrderingMethod, string>(Shared.Model.Enums.GoalOrderingMethod.ByFilterPriority, "By Filter Priority")
        };

        // Static arrays for scheduler dropdowns with display values
        public static KeyValuePair<Shared.Model.Enums.FilterShootingPattern, string>[] FilterShootingPatternDisplayValues => new[]
        {
            new KeyValuePair<Shared.Model.Enums.FilterShootingPattern, string>(Shared.Model.Enums.FilterShootingPattern.Loop, "Parallel (Round Robin)"),
            new KeyValuePair<Shared.Model.Enums.FilterShootingPattern, string>(Shared.Model.Enums.FilterShootingPattern.Batch, "Batch"),
            new KeyValuePair<Shared.Model.Enums.FilterShootingPattern, string>(Shared.Model.Enums.FilterShootingPattern.Complete, "Sequential (One Filter)")
        };
        
        public static KeyValuePair<Shared.Model.Enums.GoalCompletionBehavior, string>[] GoalCompletionBehaviorDisplayValues => new[]
        {
            new KeyValuePair<Shared.Model.Enums.GoalCompletionBehavior, string>(Shared.Model.Enums.GoalCompletionBehavior.Continue, "Continue Imaging"),
            new KeyValuePair<Shared.Model.Enums.GoalCompletionBehavior, string>(Shared.Model.Enums.GoalCompletionBehavior.Stop, "Stop / Skip"),
            new KeyValuePair<Shared.Model.Enums.GoalCompletionBehavior, string>(Shared.Model.Enums.GoalCompletionBehavior.LowerPriority, "Lower Priority")
        };
        
        public static KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy, string>[] TargetSelectionStrategyDisplayValues => new[]
        {
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy, string>(Shared.Model.Enums.TargetSelectionStrategy.PriorityFirst, "Priority First"),
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy, string>(Shared.Model.Enums.TargetSelectionStrategy.AltitudeFirst, "Altitude First"),
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy, string>(Shared.Model.Enums.TargetSelectionStrategy.TimeFirst, "Time First"),
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy, string>(Shared.Model.Enums.TargetSelectionStrategy.HighestTimeFirst, "Highest Time First"),
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy, string>(Shared.Model.Enums.TargetSelectionStrategy.MoonAvoidanceFirst, "Moon Avoidance First")
        };
        
        public static KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy?, string>[] TargetSelectionStrategyDisplayValuesWithNull => new[]
        {
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy?, string>(null, "(None)"),
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy?, string>(Shared.Model.Enums.TargetSelectionStrategy.PriorityFirst, "Priority First"),
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy?, string>(Shared.Model.Enums.TargetSelectionStrategy.AltitudeFirst, "Altitude First"),
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy?, string>(Shared.Model.Enums.TargetSelectionStrategy.TimeFirst, "Time First"),
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy?, string>(Shared.Model.Enums.TargetSelectionStrategy.HighestTimeFirst, "Highest Time First"),
            new KeyValuePair<Shared.Model.Enums.TargetSelectionStrategy?, string>(Shared.Model.Enums.TargetSelectionStrategy.MoonAvoidanceFirst, "Moon Avoidance First")
        };

        public static KeyValuePair<Shared.Model.Enums.SchedulerViolationAction, string>[] SchedulerViolationActionDisplayValues => new[]
        {
            new KeyValuePair<Shared.Model.Enums.SchedulerViolationAction, string>(Shared.Model.Enums.SchedulerViolationAction.StopScheduler, "Stop Sequence"),
            new KeyValuePair<Shared.Model.Enums.SchedulerViolationAction, string>(Shared.Model.Enums.SchedulerViolationAction.ParkAndRetry, "Park + Retry"),
            new KeyValuePair<Shared.Model.Enums.SchedulerViolationAction, string>(Shared.Model.Enums.SchedulerViolationAction.StopTrackingAndRetry, "Stop Tracking + Retry")
        };

        // Editable Imaging Goal properties
        public Guid? EditableImagingGoalExposureTemplateId
        {
            get => _selectedImagingGoal?.ExposureTemplateId;
            set
            {
                if (_selectedImagingGoal != null && value.HasValue)
                {
                    _selectedImagingGoal.ExposureTemplateId = value.Value;
                    // Update the ExposureTemplate reference
                    _selectedImagingGoal.ExposureTemplate = ExposureTemplates?.FirstOrDefault(t => t.Id == value.Value);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedImagingGoal));
                }
            }
        }

        public int EditableImagingGoalCount
        {
            get => _selectedImagingGoal?.GoalExposureCount ?? 50;
            set
            {
                if (_selectedImagingGoal != null)
                {
                    _selectedImagingGoal.GoalExposureCount = Math.Max(1, value);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedImagingGoal));
                }
            }
        }

        public bool EditableImagingGoalIsEnabled
        {
            get => _selectedImagingGoal?.IsEnabled ?? true;
            set
            {
                if (_selectedImagingGoal != null)
                {
                    _selectedImagingGoal.IsEnabled = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedImagingGoal));
                    RaisePropertyChanged(nameof(SelectedTargetImagingGoals));
                }
            }
        }

        // Status values for dropdown
        public static string[] StatusValues => Enum.GetNames(typeof(Shared.Model.Enums.ScheduledTargetStatus));
        
        public string EditableStatus
        {
            get => _selectedTarget?.Status.ToString() ?? "Active";
            set
            {
                if (_selectedTarget != null && Enum.TryParse<Shared.Model.Enums.ScheduledTargetStatus>(value, out var status))
                {
                    _selectedTarget.Status = status;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedTargetStatus));
                }
            }
        }

        // Scheduler Template Selection for Target
        public SchedulerTargetTemplateDto? SelectedTargetSchedulerTemplate
        {
            get => _selectedTarget?.SchedulerTargetTemplateId != null 
                ? SchedulerTargetTemplates.FirstOrDefault(t => t.Id == _selectedTarget.SchedulerTargetTemplateId) 
                : null;
            set
            {
                if (_selectedTarget != null)
                {
                    _selectedTarget.SchedulerTargetTemplateId = value?.Id;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(HasSelectedTargetSchedulerTemplate));
                }
            }
        }
        
        public bool HasSelectedTargetSchedulerTemplate => _selectedTarget?.SchedulerTargetTemplateId != null;
        
        public ICommand ClearTargetTemplateCommand { get; }
        
        // Target selection strategies for dropdown
        public static Shared.Model.Enums.TargetSelectionStrategy[] TargetSelectionStrategies => 
            Enum.GetValues<Shared.Model.Enums.TargetSelectionStrategy>();
        
        // Target selection strategies with null option for secondary/tertiary
        public static Shared.Model.Enums.TargetSelectionStrategy?[] TargetSelectionStrategiesWithNull => 
            new Shared.Model.Enums.TargetSelectionStrategy?[] { null }
                .Concat(Enum.GetValues<Shared.Model.Enums.TargetSelectionStrategy>().Cast<Shared.Model.Enums.TargetSelectionStrategy?>())
                .ToArray();

        // Selected imaging goal for editing with backup for cancel
        private ImagingGoalDto? _selectedImagingGoal;
        private ImagingGoalDto? _selectedImagingGoalBackup;
        private ImagingGoalDto? _pendingImagingGoalSelection;
        
        // Wrapper for ListBox selection - extracts underlying goal when wrapper is selected
        private ImagingGoalDisplayWrapper? _selectedImagingGoalWrapper;
        public ImagingGoalDisplayWrapper? SelectedImagingGoalWrapper
        {
            get => _selectedImagingGoalWrapper;
            set
            {
                _selectedImagingGoalWrapper = value;
                // Extract underlying goal for editing
                SelectedImagingGoal = value?.UnderlyingGoal;
                RaisePropertyChanged();
            }
        }
        
        public ImagingGoalDto? SelectedImagingGoal
        {
            get => _selectedImagingGoal;
            set 
            {
                // Check for unsaved changes before switching to a DIFFERENT goal (compare by Id, not reference)
                var isSameGoal = (value == null && _selectedImagingGoal == null) || 
                                 (value != null && _selectedImagingGoal != null && value.Id == _selectedImagingGoal.Id);
                
                if (!isSameGoal && HasUnsavedImagingGoalChanges())
                {
                    _pendingImagingGoalSelection = value;
                    ShowDiscardChangesDialog("Imaging Goal", () => {
                        CancelImagingGoalEdit();
                        ApplyImagingGoalSelection(_pendingImagingGoalSelection);
                        _pendingImagingGoalSelection = null;
                    });
                    return;
                }
                
                ApplyImagingGoalSelection(value);
            }
        }
        
        private void ApplyImagingGoalSelection(ImagingGoalDto? value)
        {
            _selectedImagingGoal = value;
            if (value != null)
            {
                _selectedImagingGoalBackup = new ImagingGoalDto
                {
                    Id = value.Id,
                    ExposureTemplateId = value.ExposureTemplateId,
                    ExposureTemplate = value.ExposureTemplate,
                    GoalExposureCount = value.GoalExposureCount,
                    IsEnabled = value.IsEnabled
                };
            }
            else
            {
                _selectedImagingGoalBackup = null;
            }
            RaisePropertyChanged(nameof(SelectedImagingGoal)); 
            RaisePropertyChanged(nameof(HasSelectedImagingGoal));
            RaisePropertyChanged(nameof(EditableImagingGoalCount));
            RaisePropertyChanged(nameof(EditableImagingGoalExposureTemplateId));
            RaisePropertyChanged(nameof(EditableImagingGoalIsEnabled));
        }
        public bool HasSelectedImagingGoal => _selectedImagingGoal != null;
        
        // Get the moon avoidance profile name for the selected imaging goal (from its ExposureTemplate)
        public string? SelectedImagingGoalMoonAvoidanceProfileName => 
            _selectedImagingGoal?.ExposureTemplate?.MoonAvoidanceProfileId != null 
                ? MoonAvoidanceProfiles.FirstOrDefault(p => p.Id == _selectedImagingGoal.ExposureTemplate.MoonAvoidanceProfileId)?.Name 
                : null;
        
        // Imaging goal pending deletion for confirmation dialog
        private ImagingGoalDto? _imagingGoalToDelete;

        // Selected exposure template for editing with backup for cancel
        private ExposureTemplateDto? _selectedExposureTemplate;
        private ExposureTemplateDto? _selectedExposureTemplateBackup;
        public ExposureTemplateDto? SelectedExposureTemplate
        {
            get => _selectedExposureTemplate;
            set 
            { 
                // Skip unsaved changes check if we're just refreshing the grid
                if (_isRefreshingExposureTemplateGrid)
                    return;
                
                // Check for unsaved changes before switching
                if (value != _selectedExposureTemplate && HasUnsavedExposureTemplateChanges())
                {
                    _pendingExposureTemplateSelection = value;
                    ShowDiscardChangesDialog("Exposure Template", () => {
                        CancelExposureTemplateEdit();
                        ApplyExposureTemplateSelection(_pendingExposureTemplateSelection);
                        _pendingExposureTemplateSelection = null;
                    });
                    return;
                }
                
                ApplyExposureTemplateSelection(value);
            }
        }
        
        private void ApplyExposureTemplateSelection(ExposureTemplateDto? value)
        {
            _selectedExposureTemplate = value;
            // Create backup when selecting a template for editing
            if (value != null)
            {
                _selectedExposureTemplateBackup = new ExposureTemplateDto
                {
                    Id = value.Id,
                    Name = value.Name,
                    Filter = value.Filter,
                    ExposureTimeSeconds = value.ExposureTimeSeconds,
                    Binning = value.Binning,
                    Gain = value.Gain,
                    Offset = value.Offset,
                    DefaultFilterPriority = value.DefaultFilterPriority,
                    ReadoutMode = value.ReadoutMode,
                    DitherEveryX = value.DitherEveryX,
                    MinAltitude = value.MinAltitude,
                    AcceptableTwilight = value.AcceptableTwilight,
                    MoonAvoidanceProfileId = value.MoonAvoidanceProfileId,
                    MoonAvoidanceProfileName = value.MoonAvoidanceProfileName,
                    IsActive = value.IsActive
                };
            }
            else
            {
                _selectedExposureTemplateBackup = null;
            }
            RaisePropertyChanged(nameof(SelectedExposureTemplate)); 
            RaisePropertyChanged(nameof(HasSelectedExposureTemplate));
            RaisePropertyChanged(nameof(SelectedExposureTemplateMoonAvoidanceProfileId)); 
        }
        public bool HasSelectedExposureTemplate => _selectedExposureTemplate != null;

        // Captured Image Statistics
        private CapturedImageSummaryDto? _capturedImageSummary;
        public CapturedImageSummaryDto? CapturedImageSummary
        {
            get => _capturedImageSummary;
            set { _capturedImageSummary = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasImageStats)); }
        }
        public bool HasImageStats => _capturedImageSummary != null && _capturedImageSummary.TotalImages > 0;

        // Image stats display properties
        public string ImageStatsIntegration => _capturedImageSummary != null 
            ? $"{_capturedImageSummary.TotalIntegrationMinutes / 60.0:F1}h ({_capturedImageSummary.AcceptedImages} frames)"
            : "No images";
        public string ImageStatsAvgFwhm => _capturedImageSummary?.AverageFwhm?.ToString("F2") ?? "-";
        public string ImageStatsBestFwhm => _capturedImageSummary?.BestFwhm?.ToString("F2") ?? "-";
        public string ImageStatsAvgHfd => _capturedImageSummary?.AverageHfd?.ToString("F2") ?? "-";
        public string ImageStatsAvgSnr => _capturedImageSummary?.AverageSnr?.ToString("F1") ?? "-";
        public string ImageStatsStarCount => _capturedImageSummary?.AverageStarCount?.ToString("F0") ?? "-";
        public string ImageStatsEccentricity => _capturedImageSummary?.AverageEccentricity?.ToString("F3") ?? "-";
        public string ImageStatsAcceptanceRate => _capturedImageSummary != null && _capturedImageSummary.TotalImages > 0
            ? $"{(double)_capturedImageSummary.AcceptedImages / _capturedImageSummary.TotalImages * 100:F0}%"
            : "-";

        // Scheduler Preview
        private DateTime _previewDate = DateTime.Today;
        public DateTime PreviewDate
        {
            get => _previewDate;
            set
            {
                _previewDate = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(PreviewDateDisplay));
                _ = LoadSchedulerPreviewAsync();
            }
        }
        public string PreviewDateDisplay => _previewDate.ToString("ddd, MMM dd yyyy");

        private ObservableCollection<ScheduledSessionDto> _previewSessions = new();
        public ObservableCollection<ScheduledSessionDto> PreviewSessions
        {
            get => _previewSessions;
            set { _previewSessions = value; RaisePropertyChanged(); }
        }

        private bool _isLoadingPreview;
        public bool IsLoadingPreview
        {
            get => _isLoadingPreview;
            set { _isLoadingPreview = value; RaisePropertyChanged(); }
        }

        public bool HasPreviewSessions => _previewSessions.Count > 0;
        public int PreviewSessionsCount => _previewSessions.Count;
        public string PreviewTotalTime
        {
            get
            {
                var totalMins = _previewSessions.Sum(s => s.PlannedDurationMinutes);
                return $"{totalMins / 60.0:F1}h";
            }
        }

        public ICommand PreviousNightCommand { get; private set; }
        public ICommand NextNightCommand { get; private set; }
        public ICommand LoadPreviewCommand { get; private set; }


        #endregion

        #region Commands

        public ICommand TestConnectionCommand { get; }
        public ICommand SyncTargetsCommand { get; }
        public ICommand BrowseExportPathCommand { get; }
        public ICommand ExportTargetsCommand { get; }
        public ICommand ImportTargetsCommand { get; }
        public ICommand ClearCacheCommand { get; }
        public ICommand StartHeartbeatCommand { get; }
        public ICommand StopHeartbeatCommand { get; }
        public ICommand RefreshTargetsListCommand { get; }
        public ICommand SaveTargetCommand { get; }
        public ICommand CancelTargetEditCommand { get; }
        public ICommand DeleteTargetCommand { get; }
        public ICommand ExportSettingsCommand { get; }
        public ICommand ImportSettingsCommand { get; }
        public ICommand OpenAstroManagerCommand { get; }
        public ICommand OpenDocumentationCommand { get; }
        public ICommand LoadSettingsFromApiCommand { get; }
        
        // Imaging Goal CRUD commands
        public ICommand AddImagingGoalCommand { get; }
        public ICommand SaveImagingGoalCommand { get; }
        public ICommand CancelImagingGoalEditCommand { get; }
        public ICommand DeleteImagingGoalCommand { get; }
        
        // Panel Custom Goal CRUD commands
        public ICommand AddPanelCustomGoalCommand { get; }
        public ICommand SavePanelCustomGoalCommand { get; }
        public ICommand DeletePanelCustomGoalCommand { get; }
        
        // Panel Goal Edit commands (for both base and custom goals)
        public ICommand SavePanelGoalCommand { get; }
        public ICommand CancelPanelGoalEditCommand { get; }
        public ICommand DeletePanelGoalCommand { get; }
        
        // Exposure Template CRUD commands
        public ICommand AddExposureTemplateCommand { get; }
        public ICommand SaveExposureTemplateCommand { get; }
        public ICommand DeleteExposureTemplateCommand { get; }
        public ICommand CopyExposureTemplateCommand { get; }
        public ICommand CloseExposureTemplateEditCommand { get; }
        
        // Scheduler Configuration CRUD commands
        public ICommand SaveSchedulerConfigCommand { get; }

        #endregion



        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    /// <summary>
    /// Simple relay command implementation
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
    }
    
    /// <summary>
    /// Converter for grouping targets by the selected group property
    /// </summary>
    public class TargetGroupConverter : System.Windows.Data.IValueConverter
    {
        private readonly AstroManagerPlugin _plugin;
        
        public TargetGroupConverter(AstroManagerPlugin plugin)
        {
            _plugin = plugin;
        }
        
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is ScheduledTargetDto target)
            {
                return _plugin.GetTargetGroupName(target);
            }
            return "Unknown";
        }
        
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    /// <summary>
    /// Group of targets for collapsible display in Options view
    /// </summary>
    public class OptionsTargetGroup : NINA.Core.Utility.BaseINPC
    {
        public string GroupName { get; set; } = string.Empty;
        public ObservableCollection<ScheduledTargetDto> Targets { get; } = new ObservableCollection<ScheduledTargetDto>();
        public int TargetCount => Targets.Count;

        private bool _isExpanded = false;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; RaisePropertyChanged(); }
        }
    }
    
    /// <summary>
    /// Wrapper for ImagingGoalDto that applies RepeatCount multiplier to goal values
    /// </summary>
    public class ImagingGoalDisplayWrapper
    {
        private readonly ImagingGoalDto _goal;
        private readonly int _repeatCount;
        
        public ImagingGoalDisplayWrapper(ImagingGoalDto goal, int repeatCount)
        {
            _goal = goal;
            _repeatCount = Math.Max(1, repeatCount);
        }
        
        // Pass-through properties (unchanged)
        public Shared.Model.DTO.Settings.ECameraFilter Filter => _goal.Filter;
        public int ExposureTimeSeconds => _goal.ExposureTimeSeconds;
        public bool IsEnabled => _goal.IsEnabled;
        public int FilterPriority => _goal.FilterPriority;
        public Guid? ExposureTemplateId => _goal.ExposureTemplateId;
        
        // Adjusted properties (with RepeatCount)
        public int GoalExposureCount => _goal.GoalExposureCount * _repeatCount;
        public int CompletedExposures => _goal.CompletedExposures;
        public int RemainingExposures => Math.Max(0, GoalExposureCount - CompletedExposures);
        public double CompletionPercentage => GoalExposureCount > 0 
            ? Math.Round((double)CompletedExposures / GoalExposureCount * 100, 1) 
            : 0;
        
        // Access to underlying DTO for editing
        public ImagingGoalDto UnderlyingGoal => _goal;
    }
    
    /// <summary>
    /// Wrapper for PanelImagingGoalDto that applies RepeatCount multiplier to goal values
    /// </summary>
    public class PanelImagingGoalDisplayWrapper
    {
        private readonly PanelImagingGoalDto _goal;
        private readonly int _repeatCount;
        
        public PanelImagingGoalDisplayWrapper(PanelImagingGoalDto goal, int repeatCount)
        {
            _goal = goal;
            _repeatCount = Math.Max(1, repeatCount);
        }
        
        // Pass-through properties (unchanged)
        public Shared.Model.DTO.Settings.ECameraFilter Filter => _goal.Filter;
        public int ExposureTimeSeconds => _goal.ExposureTimeSeconds;
        public bool IsEnabled => _goal.IsEnabled;
        public int FilterPriority => _goal.FilterPriority;
        public Guid? ExposureTemplateId => _goal.ExposureTemplateId;
        public bool IsCustomGoal => _goal.IsCustomGoal;
        
        // Adjusted properties (with RepeatCount)
        public int GoalExposureCount => _goal.GoalExposureCount * _repeatCount;
        public int CompletedExposures => _goal.CompletedExposures;
        public int RemainingExposures => Math.Max(0, GoalExposureCount - CompletedExposures);
        public double CompletionPercentage => GoalExposureCount > 0 
            ? Math.Round((double)CompletedExposures / GoalExposureCount * 100, 1) 
            : 0;
        
        // Access to underlying DTO for editing
        public PanelImagingGoalDto UnderlyingGoal => _goal;
    }
    
    /// <summary>
    /// Logger wrapper for shared services to work in NINA context
    /// </summary>
    internal class NinaLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var prefix = $"[{typeof(T).Name}] ";
            switch (logLevel)
            {
                case Microsoft.Extensions.Logging.LogLevel.Error:
                case Microsoft.Extensions.Logging.LogLevel.Critical:
                    Logger.Error(prefix + message);
                    break;
                case Microsoft.Extensions.Logging.LogLevel.Warning:
                    Logger.Warning(prefix + message);
                    break;
                case Microsoft.Extensions.Logging.LogLevel.Information:
                    Logger.Info(prefix + message);
                    break;
                default:
                    Logger.Debug(prefix + message);
                    break;
            }
        }
    }
}
