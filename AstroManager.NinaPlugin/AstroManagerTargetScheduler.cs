using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Newtonsoft.Json;
using NINA.Core.Utility;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Platesolving;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Interfaces;
using NINA.Astrometry.Interfaces;
using Shared.Model.DTO.Client;
using Shared.Model.DTO.Scheduler;
using Shared.Model.DTO.Settings;
using Shared.Model.Enums;
using NINA.Core.Utility.WindowService;
using AstroManager.NinaPlugin.Services;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Log level for scheduler status entries
    /// </summary>
    public enum SchedulerLogLevel
    {
        Info,
        Warning,
        Error,
        Success
    }
    
    /// <summary>
    /// A single log entry for the scheduler status display
    /// </summary>
    public class SchedulerLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; } = "";
        public SchedulerLogLevel Level { get; set; } = SchedulerLogLevel.Info;
        
        public string FormattedTime => Timestamp.ToString("HH:mm:ss");
        public string LevelIcon => Level switch
        {
            SchedulerLogLevel.Error => "❌",
            SchedulerLogLevel.Warning => "⚠️",
            SchedulerLogLevel.Success => "✅",
            _ => "ℹ️"
        };
    }

    public class SchedulerTargetProgressRow
    {
        public string ItemName { get; set; } = string.Empty;
        public string FilterName { get; set; } = string.Empty;
        public string StartDisplay { get; set; } = string.Empty;
        public string EndDisplay { get; set; } = string.Empty;
    }
    
    /// <summary>
    /// AstroManager Target Scheduler - Similar to Target Scheduler plugin
    /// Uses NINA's loop condition mechanism for flexible control.
    /// Each execution processes one slot from AstroManager API.
    /// Add loop conditions like "Loop until time" for automated stopping.
    /// </summary>
    [Export(typeof(ISequenceItem))]
    [Export(typeof(ISequenceContainer))]
    [ExportMetadata("Name", "AstroManager Target Scheduler (legacy)")]
    [ExportMetadata("Description", "Legacy AstroManager scheduler instruction. Prefer AstroManager Scheduler Container for new sequences.")]
    [ExportMetadata("Icon", "SpaceShuttle")]
    [ExportMetadata("Category", "AstroManager")]
    public class AstroManagerTargetScheduler : SequentialContainer, IDeepSkyObjectContainer, IValidatable
    {
        private static readonly TimeSpan PeriodicSafetyCheckInterval = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ConfigurationRefreshInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ExposureSafetyCheckInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SafetyRetryRecheckInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan LastCaptureMetricMaxAge = TimeSpan.FromHours(1);
        private const int GuiderStateConfirmationSamples = 2;
        private static readonly HashSet<string> PreSlotHardSafetyMetricKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "safetyMonitorSafe",
            "safetyMonitorConnected",
            "weatherConnected",
            "mountConnected",
            "cameraConnected",
            "guiderConnected",
            "mountAltitudeDegrees",
            "rainRate",
            "weatherRainRate",
            "cloudCoverPercent",
            "cloudCover",
            "weatherCloudCover",
            "weatherSkyQuality",
            "skyQuality",
            "sqm",
            "guidingRmsArcSec",
            "lastCaptureHfr",
            "lastCaptureStarCount",
            "coolerPowerPercent"
        };
        private static readonly HashSet<string> DuringExposureMetricKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "safetyMonitorSafe",
            "safetyMonitorConnected",
            "guiderGuiding",
            "guiderSettling"
        };
        private static readonly HashSet<string> LastCaptureMetricKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "lastCaptureHfr",
            "lastCaptureStarCount"
        };

        private enum RuntimeSafetyEvaluationPhase
        {
            PreSlot,
            DuringExposure,
            PostSlot,
            PeriodicWait
        }

        private readonly AstroManagerApiClient _apiClient;
        private readonly HeartbeatService _heartbeatService;
        private readonly IProfileService _profileService;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly IGuiderMediator _guiderMediator;
        private readonly IDomeMediator _domeMediator;
        private readonly IDomeFollower _domeFollower;
        private readonly ISafetyMonitorMediator _safetyMonitorMediator;
        private readonly IWeatherDataMediator _weatherDataMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly ICameraMediator _cameraMediator;
        private readonly IFocuserMediator _focuserMediator;
        private readonly IRotatorMediator _rotatorMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly IImageHistoryVM _imageHistoryVM;
        private readonly ISequenceMediator _sequenceMediator;
        private readonly IPlateSolverFactory _plateSolverFactory;
        private readonly IWindowServiceFactory _windowServiceFactory;
        private readonly IAutoFocusVMFactory _autoFocusVMFactory;
        private readonly ScheduledTargetStore _targetStore;
        private readonly AstroManagerSettings _settings;
        private readonly OfflineSlotCalculator _offlineCalculator;
        private Shared.Model.DTO.Settings.ObservatoryDto? _cachedObservatory;
        
        // Current slot state for slot-based scheduling
        private NextSlotDto? _currentSlot;
        private Guid? _currentTargetId;
        private Guid? _currentPanelId;
        private Guid? _currentQueueItemId;
        private string? _currentFilter;
        private string _currentTargetName = "None";
        private string _statusMessage = "Idle";
        private int _totalExposuresTaken = 0;
        private int _sessionExposuresTaken = 0;
        private int _exposuresSinceLastDither = 0; // Resets after dither OR slew
        
        // Error retry tracking - STATIC to persist across instance resets
        private static int _errorRetryCount = 0;
        private static Guid? _errorTargetId = null; // Track which target has errors
        
        // Track successful slew/center completion - if slew failed, we must re-slew on retry
        private static Guid? _lastSuccessfulSlewTargetId = null;
        private static Guid? _lastSuccessfulSlewPanelId = null;
        
        // Session state for loop condition support
        private bool _sessionStarted = false;
        private bool _shouldContinue = true;
        private bool _receivedStopSignal = false; // Persists across SequenceBlockInitialize to prevent reset
        private DateTime? _sessionNightEndTwilightLocal = null;
        private bool _trackingStoppedForNoSlot = false; // Track if we stopped tracking due to no slot
        private DateTime _lastConfigurationRefreshUtc = DateTime.MinValue;
        private readonly Dictionary<string, string> _amToNinaFilterNameCache = new(StringComparer.OrdinalIgnoreCase);
        private bool? _lastGuiderGuidingSample;
        private int _guiderGuidingSampleCount;
        private bool? _stableGuiderGuidingState;
        private bool? _lastGuiderSettlingSample;
        private int _guiderSettlingSampleCount;
        private bool? _stableGuiderSettlingState;
        private readonly HashSet<Guid> _completedTargetEventsRaised = new();
        
        #region Custom Event Containers
        
        /// <summary>
        /// Instructions to run before starting a new target (after slew/center).
        /// Users can add autofocus, plate solve, etc. instructions here.
        /// </summary>
        [JsonProperty]
        public EventInstructionContainer BeforeTargetContainer { get; set; }

        /// <summary>
        /// Instructions to run before scheduler-directed wait periods.
        /// Useful for parking or other observatory state changes while idle.
        /// </summary>
        [JsonProperty]
        public EventInstructionContainer BeforeWaitContainer { get; set; }

        /// <summary>
        /// Instructions to run after scheduler-directed wait periods.
        /// Useful for unpark, warm-up checks, or other resume logic.
        /// </summary>
        [JsonProperty]
        public EventInstructionContainer AfterWaitContainer { get; set; }
        
        /// <summary>
        /// Instructions to run after each exposure completes.
        /// Users can add HFR logging, custom processing, etc. here.
        /// </summary>
        [JsonProperty]
        public EventInstructionContainer AfterEachExposureContainer { get; set; }
        
        /// <summary>
        /// Instructions to run after a target completes all imaging.
        /// Users can add cleanup, notification, etc. instructions here.
        /// </summary>
        [JsonProperty]
        public EventInstructionContainer AfterTargetContainer { get; set; }

        /// <summary>
        /// Instructions to run after every completed target plan segment.
        /// In AstroManager's slot-based runtime, a plan segment maps to a completed exposure slot.
        /// </summary>
        [JsonProperty]
        public EventInstructionContainer AfterEachTargetContainer { get; set; }

        /// <summary>
        /// Instructions to run when a target reaches full completion across all active imaging goals.
        /// </summary>
        [JsonProperty]
        public EventInstructionContainer AfterTargetCompleteContainer { get; set; }
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Status log entries for display in UI (shared with dock panel)
        /// </summary>
        public ObservableCollection<SchedulerLogEntry> StatusLog => SharedSchedulerLog.Instance.LogEntries;
        public ObservableCollection<SchedulerTargetProgressRow> TargetProgressItems { get; } = new();

        public string ProjectTargetDisplay { get; private set; } = string.Empty;
        public string CoordinatesDisplay { get; private set; } = string.Empty;
        public string StopAtDisplay { get; private set; } = string.Empty;
        public DateTime? AstronomicalDusk { get; private set; }
        public DateTime? AstronomicalDawn { get; private set; }
        public double ObserverLatitude => _profileService.ActiveProfile.AstrometrySettings.Latitude;
        public double ObserverLongitude => _profileService.ActiveProfile.AstrometrySettings.Longitude;
        public double MinAltitude => 30.0;
        public Coordinates? CurrentTargetCoordinates => Target?.InputCoordinates?.Coordinates;
        public bool HasTargetContext => Target != null && !string.IsNullOrWhiteSpace(Target.TargetName);
        
        /// <summary>
        /// Add a log entry with timestamp (uses shared log)
        /// </summary>
        public void AddLogEntry(string message, SchedulerLogLevel level = SchedulerLogLevel.Info)
        {
            SharedSchedulerLog.Instance.AddEntry(message, level);
        }

        private RuntimeStopSafetyPolicyDto? GetEffectiveRuntimeStopSafetyPolicy(SchedulerConfigurationDto? fallbackConfig, out string source)
        {
            var assignedPolicy = _apiClient.CurrentClientConfiguration?.RuntimeStopSafetyPolicy;
            if (assignedPolicy != null)
            {
                source = $"Assigned policy '{assignedPolicy.Name}' (Id={assignedPolicy.Id}, Rules={assignedPolicy.Rules?.Count ?? 0})";
                return assignedPolicy;
            }

            if (fallbackConfig == null)
            {
                source = "No assigned runtime safety policy and no scheduler config fallback";
                return null;
            }

            // Legacy fallback: map scheduler configuration inline safety fields.
            source = $"Legacy fallback from scheduler config '{fallbackConfig.Name}' (inline policy fields)";
            return new RuntimeStopSafetyPolicyDto
            {
                Name = fallbackConfig.Name,
                AlwaysStopWhenNoTargetsForNight = fallbackConfig.AlwaysStopWhenNoTargetsForNight
            };
        }

        private async Task<(RuntimeStopSafetyPolicyDto? Policy, string Source)> GetEffectiveRuntimeStopSafetyPolicyWithRefreshAsync(
            SchedulerConfigurationDto? fallbackConfig)
        {
            var policy = GetEffectiveRuntimeStopSafetyPolicy(fallbackConfig, out var source);
            if (policy != null)
            {
                return (policy, source);
            }

            var config = _apiClient.CurrentClientConfiguration;
            var hasAssignedPolicyReference = config?.RuntimeStopSafetyPolicyId.HasValue == true
                || !string.IsNullOrWhiteSpace(config?.RuntimeStopSafetyPolicyName);

            if (!hasAssignedPolicyReference)
            {
                return (policy, source);
            }

            var refreshed = await _apiClient.RefreshClientConfigurationAsync();
            var refreshedPolicy = GetEffectiveRuntimeStopSafetyPolicy(fallbackConfig, out var refreshedSource);
            if (refreshedPolicy != null)
            {
                return (refreshedPolicy, $"{refreshedSource} (after forced client config refresh)");
            }

            return (refreshedPolicy, $"{refreshedSource}; forced refresh attempted={(refreshed ? "success" : "failed")}");
        }

        private async Task EnsureMountReadyForExposureAsync(NextSlotDto slot, CancellationToken token)
        {
            try
            {
                var telescopeInfo = _telescopeMediator.GetInfo();
                if (telescopeInfo == null || !telescopeInfo.Connected)
                {
                    return;
                }

                var mountStateChanged = false;

                if (telescopeInfo.CanPark && telescopeInfo.AtPark)
                {
                    AddLogEntry("Mount is parked after slot received - unparking before exposure", SchedulerLogLevel.Warning);
                    await _telescopeMediator.UnparkTelescope(null, token);
                    mountStateChanged = true;
                    telescopeInfo = _telescopeMediator.GetInfo();
                }

                if (telescopeInfo != null && telescopeInfo.Connected)
                {
                    var siderealMode = telescopeInfo.TrackingModes?.FirstOrDefault(m =>
                        m != null && m.ToString().IndexOf("sidereal", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (siderealMode != null)
                    {
                        _telescopeMediator.SetTrackingMode(siderealMode.Value);
                    }

                    if (telescopeInfo.CanSetTrackingEnabled && !telescopeInfo.TrackingEnabled)
                    {
                        AddLogEntry("Tracking is OFF after slot received - enabling sidereal tracking", SchedulerLogLevel.Info);
                        _telescopeMediator.SetTrackingEnabled(true);
                        mountStateChanged = true;
                    }
                }

                _trackingStoppedForNoSlot = false;

                if (mountStateChanged && slot.TargetId.HasValue)
                {
                    AddLogEntry("Forcing reslew+center after mount state recovery", SchedulerLogLevel.Info);
                    _currentTargetId = null;
                    _currentPanelId = null;
                    _lastSuccessfulSlewTargetId = null;
                    _lastSuccessfulSlewPanelId = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[SAFETY-CHECK] Failed to prepare mount after slot received: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets the currently selected filter name from NINA filter wheel (NINA naming).
        /// Falls back to the scheduler's cached value if wheel info is unavailable.
        /// </summary>
        private string? GetCurrentNinaFilterName()
        {
            try
            {
                var selectedFilter = _filterWheelMediator.GetInfo()?.SelectedFilter?.Name;
                if (!string.IsNullOrWhiteSpace(selectedFilter))
                {
                    _currentFilter = selectedFilter;
                    return selectedFilter;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[FILTER-SWITCH] Failed to read current filter from NINA filter wheel: {ex.Message}");
            }

            return _currentFilter;
        }

        private void UpdateFilterNameCache(NextSlotDto? slot)
        {
            if (slot?.SlotType != SlotType.Exposure || string.IsNullOrWhiteSpace(slot.Filter) || string.IsNullOrWhiteSpace(slot.NinaFilterName))
            {
                return;
            }

            _amToNinaFilterNameCache[slot.Filter] = slot.NinaFilterName;
        }

        private string? ResolveNinaFilterNameForSlot(string? amFilterName)
        {
            if (string.IsNullOrWhiteSpace(amFilterName))
            {
                return null;
            }

            if (_amToNinaFilterNameCache.TryGetValue(amFilterName, out var cachedName) && !string.IsNullOrWhiteSpace(cachedName))
            {
                return cachedName;
            }

            var availableFilters = _profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
            if (availableFilters == null || !availableFilters.Any())
            {
                return amFilterName;
            }

            var directMatch = availableFilters.FirstOrDefault(f => f.Name.Equals(amFilterName, StringComparison.OrdinalIgnoreCase))?.Name;
            if (!string.IsNullOrWhiteSpace(directMatch))
            {
                _amToNinaFilterNameCache[amFilterName] = directMatch;
                return directMatch;
            }

            string Canonicalize(string value)
            {
                return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            }

            var amCanonical = Canonicalize(amFilterName);
            var aliases = amCanonical switch
            {
                "l" or "lum" or "luminance" => new[] { "l", "lum", "luminance" },
                "ha" or "halpha" => new[] { "ha", "halpha", "ha3nm", "ha5nm", "ha7nm" },
                "oiii" or "o3" => new[] { "oiii", "o3", "oiii3nm", "oiii5nm", "oiii7nm" },
                "sii" or "s2" => new[] { "sii", "s2", "sii3nm", "sii5nm", "sii7nm" },
                _ => new[] { amCanonical }
            };

            var aliasMatch = availableFilters
                .Select(f => f.Name)
                .FirstOrDefault(name =>
                {
                    var candidate = Canonicalize(name);
                    return aliases.Any(alias => candidate.Contains(alias, StringComparison.OrdinalIgnoreCase));
                });

            if (!string.IsNullOrWhiteSpace(aliasMatch))
            {
                _amToNinaFilterNameCache[amFilterName] = aliasMatch;
                Logger.Info($"[FILTER-SWITCH] Resolved AM filter '{amFilterName}' to NINA filter '{aliasMatch}' using local fallback mapping");
                return aliasMatch;
            }

            return amFilterName;
        }
        
        /// <summary>
        /// Clear the status log
        /// </summary>
        public void ClearLog()
        {
            SharedSchedulerLog.Instance.Clear();
        }
        
        // Scheduler Configuration Selection
        private Guid? _selectedConfigurationId;
        
        [JsonProperty]
        public Guid? SelectedConfigurationId
        {
            get => _selectedConfigurationId;
            set
            {
                _selectedConfigurationId = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(SelectedConfigurationName));
            }
        }

        /// <summary>
        /// Reload scheduler configurations periodically during a running session so
        /// config changes made in AstroManager are applied without restarting NINA sequence.
        /// </summary>
        private async Task MaybeRefreshConfigurationsAsync()
        {
            if (!_sessionStarted)
            {
                return;
            }

            if (_lastConfigurationRefreshUtc != DateTime.MinValue
                && (DateTime.UtcNow - _lastConfigurationRefreshUtc) < ConfigurationRefreshInterval)
            {
                return;
            }

            try
            {
                await _apiClient.RefreshClientConfigurationAsync();

                // Only reload the scheduler configuration list when needed.
                // Runtime safety policy updates come from client configuration refresh above.
                if (!SelectedConfigurationId.HasValue
                    || SelectedConfigurationId == Guid.Empty
                    || AvailableConfigurations.Count == 0)
                {
                    await LoadConfigurationsAsync();
                }

                _lastConfigurationRefreshUtc = DateTime.UtcNow;
                Logger.Debug("AstroManager Scheduler: Refreshed client runtime safety/config snapshot mid-session");
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager Scheduler: Mid-session configuration refresh failed: {ex.Message}");
            }
        }
        
        public string SelectedConfigurationName => 
            _selectedConfigurationId.HasValue 
                ? AvailableConfigurations?.FirstOrDefault(c => c.Id == _selectedConfigurationId)?.Name ?? "Loading..."
                : "(Default)";
        
        // Available configurations loaded from API
        private ObservableCollection<SchedulerConfigurationDto> _availableConfigurations = new();
        public ObservableCollection<SchedulerConfigurationDto> AvailableConfigurations
        {
            get => _availableConfigurations;
            set { _availableConfigurations = value; RaisePropertyChanged(); }
        }
        
        public string CurrentTargetName
        {
            get => _currentTargetName;
            set { _currentTargetName = value; RaisePropertyChanged(); }
        }
        
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; RaisePropertyChanged(); }
        }
        
        public int TotalExposuresTaken
        {
            get => _totalExposuresTaken;
            set { _totalExposuresTaken = value; RaisePropertyChanged(); }
        }
        
        public int SessionExposuresTaken
        {
            get => _sessionExposuresTaken;
            set { _sessionExposuresTaken = value; RaisePropertyChanged(); }
        }
        
        // IDeepSkyObjectContainer implementation - Target property for NINA trigger support
        private InputTarget _target;
        private ExposureContainer? _exposureContainer;
        public InputTarget Target
        {
            get => _target;
            set
            {
                _target = value;
                RaisePropertyChanged();
            }
        }
        
        // NighttimeData required by some NINA components
        public NighttimeData NighttimeData { get; private set; }
        
        // Current target PA info (editable - syncs back to current target)
        private double? _currentPositionAngle;
        public double? CurrentPositionAngle
        {
            get => _currentPositionAngle;
            set 
            { 
                _currentPositionAngle = value; 
                RaisePropertyChanged(); 
                RaisePropertyChanged(nameof(CurrentPositionAngleDisplay)); 
            }
        }
        
        // For binding to TextBox (nullable double doesn't bind well)
        public string CurrentPositionAngleText
        {
            get => _currentPositionAngle?.ToString("F1") ?? "";
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    CurrentPositionAngle = null;
                else if (double.TryParse(value, out var pa))
                    CurrentPositionAngle = pa;
                RaisePropertyChanged();
            }
        }
        
        public string CurrentPositionAngleDisplay => _currentPositionAngle.HasValue 
            ? $"{_currentPositionAngle.Value:F1}°" 
            : "—";
        
        // Commands
        public ICommand RefreshConfigurationsCommand { get; }
        
        #endregion

        [ImportingConstructor]
        public AstroManagerTargetScheduler(
            AstroManagerApiClient apiClient,
            HeartbeatService heartbeatService,
            IProfileService profileService,
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            IDomeMediator domeMediator,
            IDomeFollower domeFollower,
            ISafetyMonitorMediator safetyMonitorMediator,
            IWeatherDataMediator weatherDataMediator,
            IFilterWheelMediator filterWheelMediator,
            ICameraMediator cameraMediator,
            IFocuserMediator focuserMediator,
            IRotatorMediator rotatorMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM,
            ISequenceMediator sequenceMediator,
            IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory,
            IAutoFocusVMFactory autoFocusVMFactory,
            ScheduledTargetStore targetStore) 
            : base()
        {
            _apiClient = apiClient;
            _heartbeatService = heartbeatService;
            _profileService = profileService;
            _telescopeMediator = telescopeMediator;
            _guiderMediator = guiderMediator;
            _domeMediator = domeMediator;
            _domeFollower = domeFollower;
            _safetyMonitorMediator = safetyMonitorMediator;
            _weatherDataMediator = weatherDataMediator;
            _filterWheelMediator = filterWheelMediator;
            _cameraMediator = cameraMediator;
            _focuserMediator = focuserMediator;
            _rotatorMediator = rotatorMediator;
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _imageHistoryVM = imageHistoryVM;
            _sequenceMediator = sequenceMediator;
            _plateSolverFactory = plateSolverFactory;
            _windowServiceFactory = windowServiceFactory;
            _autoFocusVMFactory = autoFocusVMFactory;
            _targetStore = targetStore;
            _settings = _apiClient.GetSettings();
            _offlineCalculator = new OfflineSlotCalculator(targetStore, _settings);
            
            // Initialize commands
            RefreshConfigurationsCommand = new RelayCommand(async _ => await LoadConfigurationsAsync());
            
            // Initialize empty Target for IDeepSkyObjectContainer
            NighttimeData = null!;
            Target = CreateEmptyTarget();
            RefreshNighttimeWindowData();
            
            EnsureEventContainersInitialized();
            
            // Auto-load configurations when created
            _ = LoadConfigurationsAsync();
        }

        protected AstroManagerTargetScheduler(AstroManagerTargetScheduler source)
            : this(
                source._apiClient,
                source._heartbeatService,
                source._profileService,
                source._telescopeMediator,
                source._guiderMediator,
                source._domeMediator,
                source._domeFollower,
                source._safetyMonitorMediator,
                source._weatherDataMediator,
                source._filterWheelMediator,
                source._cameraMediator,
                source._focuserMediator,
                source._rotatorMediator,
                source._imagingMediator,
                source._imageSaveMediator,
                source._imageHistoryVM,
                source._sequenceMediator,
                source._plateSolverFactory,
                source._windowServiceFactory,
                source._autoFocusVMFactory,
                source._targetStore)
        {
        }
        
        /// <summary>
        /// Called after JSON deserialization to ensure all containers are properly initialized.
        /// This is critical for NINA's drag-drop behavior to work correctly.
        /// </summary>
        [OnDeserialized]
        public void OnDeserializedMethod(StreamingContext context)
        {
            EnsureEventContainersInitialized();
        }

        protected void EnsureEventContainersInitialized()
        {
            BeforeWaitContainer = EnsureEventContainer(BeforeWaitContainer, EventContainerType.BeforeWait);
            AfterWaitContainer = EnsureEventContainer(AfterWaitContainer, EventContainerType.AfterWait);
            BeforeTargetContainer = EnsureEventContainer(BeforeTargetContainer, EventContainerType.BeforeNewTarget);
            AfterEachExposureContainer = EnsureEventContainer(AfterEachExposureContainer, EventContainerType.AfterEachExposure);
            AfterTargetContainer = EnsureEventContainer(AfterTargetContainer, EventContainerType.AfterTarget);
            AfterEachTargetContainer = EnsureEventContainer(AfterEachTargetContainer, EventContainerType.AfterEachTarget);
            AfterTargetCompleteContainer = EnsureEventContainer(AfterTargetCompleteContainer, EventContainerType.AfterTargetComplete);
        }

        private EventInstructionContainer EnsureEventContainer(EventInstructionContainer? container, EventContainerType containerType)
        {
            if (container == null)
            {
                return new EventInstructionContainer(containerType, this);
            }

            container.ResetParent(this);
            container.EventContainerType = containerType;
            if (string.IsNullOrEmpty(container.Name))
            {
                container.Name = containerType.ToString();
            }

            if (string.IsNullOrEmpty(container.Category))
            {
                container.Category = "AstroManager";
            }

            return container;
        }
        
        /// <summary>
        /// Create an empty InputTarget with default coordinates
        /// </summary>
        private InputTarget CreateEmptyTarget()
        {
            var profile = _profileService.ActiveProfile;
            var target = new InputTarget(
                Angle.ByDegree(profile.AstrometrySettings.Latitude),
                Angle.ByDegree(profile.AstrometrySettings.Longitude),
                profile.AstrometrySettings.Horizon);
            target.TargetName = string.Empty;
            target.InputCoordinates.Coordinates = new Coordinates(Angle.Zero, Angle.Zero, Epoch.J2000);
            target.PositionAngle = 0;
            target.DeepSkyObject = new DeepSkyObject(string.Empty, target.InputCoordinates.Coordinates, profile.AstrometrySettings.Horizon);
            return target;
        }
        
        /// <summary>
        /// Set the current target for IDeepSkyObjectContainer - enables proper trigger support
        /// </summary>
        private void SetTarget(NextSlotDto slot)
        {
            if (slot == null)
            {
                Logger.Debug("AstroManager Scheduler: SetTarget called with null slot, clearing target");
                Target = CreateEmptyTarget();
                return;
            }
            
            var profile = _profileService.ActiveProfile;
            var inputTarget = new InputTarget(
                Angle.ByDegree(profile.AstrometrySettings.Latitude),
                Angle.ByDegree(profile.AstrometrySettings.Longitude),
                profile.AstrometrySettings.Horizon);
            
            // Set target name and coordinates
            // Include panel number in target name for mosaic panels (e.g., IC2944_P2)
            // This ensures NINA saves images in separate folders per panel
            var targetName = slot.TargetName ?? "Unknown";
            if (slot.PanelNumber.HasValue)
            {
                targetName = $"{targetName}_P{slot.PanelNumber}";
            }
            inputTarget.TargetName = targetName;
            inputTarget.InputCoordinates = new InputCoordinates(
                new Coordinates(
                    Angle.ByHours(slot.RightAscensionHours),
                    Angle.ByDegree(slot.DeclinationDegrees),
                    Epoch.J2000));
            inputTarget.PositionAngle = slot.PositionAngle ?? 0;
            inputTarget.Expanded = true;
            inputTarget.DeepSkyObject = CreateDeepSkyObject(targetName, inputTarget.InputCoordinates.Coordinates);
            
            Target = inputTarget;
            UpdateTargetPresentation(slot);
            
            Logger.Info($"AstroManager Scheduler: SetTarget - Name={targetName}, RA={slot.RightAscensionHours:F4}h, Dec={slot.DeclinationDegrees:F2}°, PA={slot.PositionAngle ?? 0:F1}°");
            
            // Reset CenterAfterDrift trigger in parent containers
            ResetCenterAfterDrift();
        }
        
        /// <summary>
        /// Clear the target (call when session ends or no active target)
        /// </summary>
        private void ClearTarget()
        {
            Target = CreateEmptyTarget();
            CurrentTargetName = "None";
            RunOnUiThread(() =>
            {
                ProjectTargetDisplay = string.Empty;
                CoordinatesDisplay = string.Empty;
                StopAtDisplay = string.Empty;
                TargetProgressItems.Clear();
                RaiseTargetContextPropertiesChanged();
            });
        }

        private DeepSkyObject CreateDeepSkyObject(string targetName, Coordinates coordinates)
        {
            var profile = _profileService.ActiveProfile;
            var dso = new DeepSkyObject(string.Empty, coordinates, profile.AstrometrySettings.Horizon);
            dso.Name = targetName;
            dso.SetDateAndPosition(DateTime.Now, profile.AstrometrySettings.Latitude, profile.AstrometrySettings.Longitude);
            dso.Refresh();
            return dso;
        }

        private void UpdateTargetPresentation(NextSlotDto slot)
        {
            var targetDisplay = slot.TargetName ?? "Unknown";
            if (!string.IsNullOrWhiteSpace(slot.PanelName))
            {
                targetDisplay = $"{targetDisplay} / {slot.PanelName}";
            }

            var coordinatesDisplay = $"RA {slot.RightAscensionHours:F4}h  Dec {slot.DeclinationDegrees:F2}°";
            var stopAtDisplay = slot.WaitUntilUtc.HasValue
                ? slot.WaitUntilUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : slot.Message ?? string.Empty;
            var progressRow = new SchedulerTargetProgressRow
            {
                ItemName = targetDisplay,
                FilterName = slot.Filter ?? string.Empty,
                StartDisplay = $"{slot.CompletedExposures}/{slot.TotalGoalExposures}",
                EndDisplay = $"{slot.ExposureTimeSeconds:F0}s"
            };

            RunOnUiThread(() =>
            {
                ProjectTargetDisplay = targetDisplay;
                CoordinatesDisplay = coordinatesDisplay;
                StopAtDisplay = stopAtDisplay;
                TargetProgressItems.Clear();
                TargetProgressItems.Add(progressRow);
                RaiseTargetContextPropertiesChanged();
            });
        }

        private void RaiseTargetContextPropertiesChanged()
        {
            RaisePropertyChanged(nameof(ProjectTargetDisplay));
            RaisePropertyChanged(nameof(CoordinatesDisplay));
            RaisePropertyChanged(nameof(StopAtDisplay));
            RaisePropertyChanged(nameof(CurrentTargetCoordinates));
            RaisePropertyChanged(nameof(HasTargetContext));
            RaisePropertyChanged(nameof(TargetProgressItems));
            RaisePropertyChanged(nameof(Target));
        }

        private void RefreshNighttimeWindowData()
        {
            try
            {
                var latitude = _profileService.ActiveProfile.AstrometrySettings.Latitude;
                var longitude = _profileService.ActiveProfile.AstrometrySettings.Longitude;
                var now = DateTime.UtcNow;
                var tonight = now.Hour < 12 ? now.Date.AddDays(-1) : now.Date;

                var duskEvent = AstroUtil.GetSunRiseAndSet(tonight, latitude, longitude);
                var dawnEvent = AstroUtil.GetSunRiseAndSet(tonight.AddDays(1), latitude, longitude);

                var dusk = duskEvent?.Set?.AddHours(1.5) ?? tonight.AddHours(19);
                var dawn = dawnEvent?.Rise?.AddHours(-1.5) ?? tonight.AddDays(1).AddHours(5);

                if (now > dawn)
                {
                    tonight = tonight.AddDays(1);
                    duskEvent = AstroUtil.GetSunRiseAndSet(tonight, latitude, longitude);
                    dawnEvent = AstroUtil.GetSunRiseAndSet(tonight.AddDays(1), latitude, longitude);
                    dusk = duskEvent?.Set?.AddHours(1.5) ?? tonight.AddHours(19);
                    dawn = dawnEvent?.Rise?.AddHours(-1.5) ?? tonight.AddDays(1).AddHours(5);
                }

                RunOnUiThread(() =>
                {
                    AstronomicalDusk = dusk;
                    AstronomicalDawn = dawn;
                    RaisePropertyChanged(nameof(AstronomicalDusk));
                    RaisePropertyChanged(nameof(AstronomicalDawn));
                });
            }
            catch (Exception ex)
            {
                Logger.Debug($"AstroManager Scheduler: Failed to refresh nighttime window data: {ex.Message}");
            }
        }

        private void RunOnUiThread(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }
        
        /// <summary>
        /// Reset CenterAfterDrift trigger in parent containers with current target coordinates
        /// This is required for the trigger to work properly with dynamic target selection
        /// </summary>
        private void ResetCenterAfterDrift()
        {
            if (Target == null || string.IsNullOrEmpty(Target.TargetName))
            {
                return;
            }
            
            // Walk up the parent chain to find CenterAfterDrift trigger
            var container = Parent as SequenceContainer;
            while (container != null)
            {
                var triggers = container.GetTriggersSnapshot();
                foreach (var trigger in triggers)
                {
                    if (trigger is CenterAfterDriftTrigger cadTrigger)
                    {
                        Logger.Info($"AstroManager Scheduler: Resetting CenterAfterDrift trigger with coordinates for {Target.TargetName}");
                        cadTrigger.Coordinates = Target.InputCoordinates.Clone();
                        cadTrigger.Inherited = true;
                        cadTrigger.SequenceBlockInitialize();
                        return; // Only reset the first one found
                    }
                }
                container = container.Parent as SequenceContainer;
            }
        }

        protected virtual AstroManagerTargetScheduler CreateCloneInstance()
        {
            return new AstroManagerTargetScheduler(
                _apiClient,
                _heartbeatService,
                _profileService,
                _telescopeMediator,
                _guiderMediator,
                _domeMediator,
                _domeFollower,
                _safetyMonitorMediator,
                _weatherDataMediator,
                _filterWheelMediator,
                _cameraMediator,
                _focuserMediator,
                _rotatorMediator,
                _imagingMediator,
                _imageSaveMediator,
                _imageHistoryVM,
                _sequenceMediator,
                _plateSolverFactory,
                _windowServiceFactory,
                _autoFocusVMFactory,
                _targetStore);
        }

        protected void CopyCloneStateTo(AstroManagerTargetScheduler clone)
        {
            clone.Icon = Icon;
            clone.Name = Name;
            clone.Category = Category;
            clone.Description = Description;
            clone.SelectedConfigurationId = SelectedConfigurationId;
            clone.Items = new ObservableCollection<ISequenceItem>(Items.Select(i => (ISequenceItem)i.Clone()));
            clone.Triggers = new ObservableCollection<ISequenceTrigger>(Triggers.Select(t => (ISequenceTrigger)t.Clone()));
            clone.Conditions = new ObservableCollection<ISequenceCondition>(Conditions.Select(c => (ISequenceCondition)c.Clone()));

            clone.BeforeWaitContainer = (EventInstructionContainer)BeforeWaitContainer.Clone();
            clone.AfterWaitContainer = (EventInstructionContainer)AfterWaitContainer.Clone();
            clone.BeforeTargetContainer = (EventInstructionContainer)BeforeTargetContainer.Clone();
            clone.AfterEachExposureContainer = (EventInstructionContainer)AfterEachExposureContainer.Clone();
            clone.AfterTargetContainer = (EventInstructionContainer)AfterTargetContainer.Clone();
            clone.AfterEachTargetContainer = (EventInstructionContainer)AfterEachTargetContainer.Clone();
            clone.AfterTargetCompleteContainer = (EventInstructionContainer)AfterTargetCompleteContainer.Clone();
            clone.EnsureEventContainersInitialized();

            foreach (var item in clone.Items)
            {
                item?.AttachNewParent(clone);
            }

            foreach (var trigger in clone.Triggers)
            {
                trigger?.AttachNewParent(clone);
            }

            foreach (var condition in clone.Conditions)
            {
                condition?.AttachNewParent(clone);
            }
        }

        public override object Clone()
        {
            var clone = CreateCloneInstance();
            CopyCloneStateTo(clone);
            return clone;
        }

        public override bool Validate()
        {
            var issues = new List<string>();

            var triggersValid = ValidateEntities(GetTriggersSnapshot());
            var conditionsValid = ValidateEntities(GetConditionsSnapshot());
            var itemsValid = ValidateEntities(Items);

            var beforeWaitValid = BeforeWaitContainer.Validate();
            var afterWaitValid = AfterWaitContainer.Validate();
            var beforeTargetValid = BeforeTargetContainer.Validate();
            var afterEachExposureValid = AfterEachExposureContainer.Validate();
            var afterTargetValid = AfterTargetContainer.Validate();
            var afterEachTargetValid = AfterEachTargetContainer.Validate();
            var afterTargetCompleteValid = AfterTargetCompleteContainer.Validate();

            if (!triggersValid || !conditionsValid || !itemsValid)
            {
                issues.Add("One or more scheduler items, conditions, or triggers is not valid");
            }

            if (!beforeWaitValid
                || !afterWaitValid
                || !beforeTargetValid
                || !afterEachExposureValid
                || !afterTargetValid
                || !afterEachTargetValid
                || !afterTargetCompleteValid)
            {
                issues.Add("One or more AstroManager event containers is not valid");
            }

            Issues = issues;
            return issues.Count == 0;
        }

        private static bool ValidateEntities<T>(IEnumerable<T> entities)
        {
            var valid = true;

            foreach (var entity in entities)
            {
                if (entity is IValidatable validatable && !validatable.Validate())
                {
                    valid = false;
                }
            }

            return valid;
        }
        
        /// <summary>
        /// Load available scheduler configurations from API
        /// </summary>
        public async Task LoadConfigurationsAsync()
        {
            try
            {
                Logger.Debug("AstroManager Scheduler: Loading configurations...");
                var configs = await _apiClient.GetSchedulerConfigurationsAsync();
                if (configs != null)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        AvailableConfigurations.Clear();
                        // Add "(Use Server Default)" as first option
                        AvailableConfigurations.Add(new Shared.Model.DTO.Scheduler.SchedulerConfigurationDto
                        {
                            Id = Guid.Empty,
                            Name = "(Use Server Default)",
                            IsDefault = false
                        });
                        foreach (var config in configs.OrderBy(c => c.Name))
                        {
                            AvailableConfigurations.Add(config);
                        }
                    });
                    
                    // Default to "Use Server Default" (Guid.Empty) unless user has explicitly selected one
                    if (!SelectedConfigurationId.HasValue)
                    {
                        SelectedConfigurationId = Guid.Empty; // Use server default
                    }
                    
                    RaisePropertyChanged(nameof(SelectedConfigurationName));
                    Logger.Info($"AstroManager Scheduler: Loaded {configs.Count} configurations (+ server default option)");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager Scheduler: Failed to load configurations: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get the effective configuration ID to use (resolves server default)
        /// </summary>
        private Guid? GetEffectiveConfigurationId()
        {
            // If user selected a specific config, use it
            if (SelectedConfigurationId.HasValue && SelectedConfigurationId.Value != Guid.Empty)
            {
                return SelectedConfigurationId;
            }
            
            // Check for server-side per-client default configuration (set in AstroManager web RC UI)
            var clientDefaultConfigId = _apiClient.Settings?.DefaultSchedulerConfigurationId;
            if (clientDefaultConfigId.HasValue && clientDefaultConfigId.Value != Guid.Empty)
            {
                // Verify this config exists in our available configurations
                if (AvailableConfigurations?.Any(c => c.Id == clientDefaultConfigId.Value) == true)
                {
                    return clientDefaultConfigId;
                }
            }
            
            // Fallback to the user's global default config (marked as IsDefault)
            var defaultConfig = AvailableConfigurations?.FirstOrDefault(c => c.IsDefault && c.Id != Guid.Empty);
            return defaultConfig?.Id;
        }
        
        /// <summary>
        /// Get the effective configuration name (resolves server default)
        /// </summary>
        private string GetEffectiveConfigurationName()
        {
            if (SelectedConfigurationId.HasValue && SelectedConfigurationId.Value != Guid.Empty)
            {
                return AvailableConfigurations?.FirstOrDefault(c => c.Id == SelectedConfigurationId)?.Name ?? "Unknown";
            }
            
            // Check for server-side per-client default configuration
            var clientDefaultConfigId = _apiClient.Settings?.DefaultSchedulerConfigurationId;
            if (clientDefaultConfigId.HasValue && clientDefaultConfigId.Value != Guid.Empty)
            {
                var clientConfig = AvailableConfigurations?.FirstOrDefault(c => c.Id == clientDefaultConfigId.Value);
                if (clientConfig != null)
                {
                    return $"{clientConfig.Name} (Client Default)";
                }
            }
            
            // Fallback to user's global default
            var defaultConfig = AvailableConfigurations?.FirstOrDefault(c => c.IsDefault && c.Id != Guid.Empty);
            return defaultConfig?.Name ?? "(Server Default)";
        }

        /// <summary>
        /// Get the effective configuration object (resolves server default).
        /// </summary>
        private SchedulerConfigurationDto? GetEffectiveConfiguration()
        {
            if (SelectedConfigurationId.HasValue && SelectedConfigurationId.Value != Guid.Empty)
            {
                return AvailableConfigurations?.FirstOrDefault(c => c.Id == SelectedConfigurationId.Value);
            }

            var clientDefaultConfigId = _apiClient.Settings?.DefaultSchedulerConfigurationId;
            if (clientDefaultConfigId.HasValue && clientDefaultConfigId.Value != Guid.Empty)
            {
                var clientConfig = AvailableConfigurations?.FirstOrDefault(c => c.Id == clientDefaultConfigId.Value);
                if (clientConfig != null)
                {
                    return clientConfig;
                }
            }

            return AvailableConfigurations?.FirstOrDefault(c => c.IsDefault && c.Id != Guid.Empty);
        }

        /// <summary>
        /// Check if the scheduler should continue looping.
        /// Returns false when API returns Stop or all targets complete.
        /// </summary>
        public bool ShouldContinue => _shouldContinue;
        
        /// <summary>
        /// Main execution - processes ONE slot per call.
        /// NINA's loop conditions control when to stop.
        /// </summary>
        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            // Check license key FIRST - prevent any scheduling without valid license
            if (!_apiClient.HasLicenseKey)
            {
                AddLogEntry("No license key configured! Please enter your license key in the AstroManager plugin settings.", SchedulerLogLevel.Error);
                StatusMessage = "ERROR: No license key";
                _shouldContinue = false;
                throw new InvalidOperationException("AstroManager requires a valid license key. Please configure your license key in the plugin settings before using the scheduler.");
            }
            
            // Load configurations if not already loaded
            if (!AvailableConfigurations.Any())
            {
                AddLogEntry("Loading scheduler configurations...");
                await LoadConfigurationsAsync();
                AddLogEntry($"Loaded {AvailableConfigurations.Count} configurations");
            }
            
            // Start session on first execution
            if (!_sessionStarted)
            {
                await _apiClient.RefreshClientConfigurationAsync();

                // Refresh configurations from server to get current default (in case user changed it in AM)
                if (SelectedConfigurationId == Guid.Empty)
                {
                    Logger.Info("AstroManager Scheduler: Using server default - refreshing configurations to get current default");
                    await LoadConfigurationsAsync();
                }
                
                var effectiveConfigId = GetEffectiveConfigurationId();
                var configName = GetEffectiveConfigurationName();
                AddLogEntry($"Starting session with '{configName}' configuration", SchedulerLogLevel.Info);
                var runtimePolicyName = _apiClient.CurrentClientConfiguration?.RuntimeStopSafetyPolicyName ?? _settings.RuntimeStopSafetyPolicyName;
                if (!string.IsNullOrWhiteSpace(runtimePolicyName))
                {
                    Logger.Debug($"AstroManager Scheduler: Runtime safety policy configured: {runtimePolicyName}");
                }
                StatusMessage = $"Starting ({configName})...";
                SessionExposuresTaken = 0;
                _exposuresSinceLastDither = 0;
                // Note: _errorRetryCount NOT reset here - preserved across session restarts for retry counting
                _shouldContinue = true;
                
                // Tell heartbeat service which config we're using (for Remote Client display)
                var isUsingDefault = !SelectedConfigurationId.HasValue || SelectedConfigurationId.Value == Guid.Empty;
                _heartbeatService.SetSchedulerConfigurationName(configName, isUsingDefault);
                
                try
                {
                    await _apiClient.StartSessionAsync(effectiveConfigId);
                    AddLogEntry("Session started successfully", SchedulerLogLevel.Success);
                    _sessionStarted = true;
                    InitializeSessionNightEndTwilight();
                    SharedSchedulerState.Instance.SetSchedulerRunning(true);
                }
                catch (Exception ex)
                {
                    AddLogEntry($"Failed to start session: {ex.Message}", SchedulerLogLevel.Error);
                    _shouldContinue = false;
                    return;
                }
            }

            // Periodically reload configs so server-side edits are picked up mid-sequence.
            await MaybeRefreshConfigurationsAsync();

            // Allow RC to stop only the AM scheduler instruction while leaving the NINA sequence running.
            if (SharedSchedulerState.Instance.ConsumeStopSchedulerRequest())
            {
                AddLogEntry("AM scheduler stop requested from Remote Control", SchedulerLogLevel.Warning);
                StatusMessage = "AM scheduler stopped by remote command";
                _shouldContinue = false;
                _receivedStopSignal = true; // Keep loop condition false for this run
                SharedSchedulerState.Instance.Clear();
                _heartbeatService.SetCurrentState("Scheduler Stopped", null, null, 0, 0, status: "Idle");
                _ = _heartbeatService.ForceStatusUpdateAsync();
                return;
            }

            try 
            {
                if (IsPastCurrentNightTwilight(out var twilightReason))
                {
                    AddLogEntry($"🌅 NIGHT ENDED: {twilightReason}", SchedulerLogLevel.Info);
                    StatusMessage = "🌅 Night over - past twilight";
                    _shouldContinue = false;
                    _receivedStopSignal = true;
                    SharedSchedulerState.Instance.Clear();
                    _heartbeatService.SetCurrentState("Night Ended", null, null, 0, 0, status: "Idle");
                    _ = _heartbeatService.ForceStatusUpdateAsync();
                    progress.Report(new ApplicationStatus { Status = StatusMessage });
                    return;
                }

                // Fetch next slot from AstroManager
                // Keep target info while fetching (don't clear it) so UI shows we're still on this target
                var isOfflineMode = _offlineCalculator.ShouldUseOfflineMode();
                StatusMessage = isOfflineMode ? "Offline mode - calculating locally..." : "Fetching next slot...";
                AddLogEntry(isOfflineMode ? "Calculating next slot offline..." : "Requesting next slot from API...");
                progress.Report(new ApplicationStatus { Status = isOfflineMode ? "Offline mode - calculating slot..." : "Fetching next exposure slot..." });
                
                // Update heartbeat to show "Fetching" state while keeping current target info
                if (_currentTargetId.HasValue)
                {
                    _heartbeatService.SetCurrentState(isOfflineMode ? "Offline Mode" : "Fetching Next Slot", CurrentTargetName, _currentFilter, 0, 0,
                        targetId: _currentTargetId, imagingGoalId: null, panelId: _currentPanelId,
                        panelName: null, exposureTimeSeconds: null);
                }
                
                NextSlotDto? slot = null;
                
                // If in offline mode, try to reconnect first
                if (isOfflineMode)
                {
                    AddLogEntry("Attempting to reconnect to API...", SchedulerLogLevel.Info);
                    var reconnected = await TryReconnectAsync(progress, token);
                    
                    if (reconnected)
                    {
                        isOfflineMode = false; // We're back online!
                        StatusMessage = "Reconnected - fetching next slot...";
                        AddLogEntry("Reconnected to API successfully!", SchedulerLogLevel.Success);
                    }
                    else
                    {
                        AddLogEntry("Reconnection failed - staying in offline mode", SchedulerLogLevel.Warning);
                    }
                }
                
                // Try API if we're online (or just reconnected)
                if (!isOfflineMode)
                {
                    if (_cachedObservatory == null)
                    {
                        await TryRefreshObservatoryCacheAsync();
                    }

                    var currentFilterForApi = GetCurrentNinaFilterName();
                    slot = await _apiClient.GetNextSlotAsync(
                        GetEffectiveConfigurationId(), 
                        _currentTargetId, 
                        _currentPanelId, 
                        currentFilterForApi);
                    
                    // Report API result to offline calculator
                    _offlineCalculator.ReportApiResult(slot != null);
                }
                
                // Fall back to offline calculation if API failed or we're in offline mode
                if (slot == null && (_offlineCalculator.ShouldUseOfflineMode() || isOfflineMode))
                {
                    var offlineCoordinates = await ResolveOfflineCoordinatesAsync();
                    if (offlineCoordinates.HasValue)
                    {
                        var (latitude, longitude, source) = offlineCoordinates.Value;
                        AddLogEntry($"[OFFLINE] Calculating slot using {_targetStore.Count} cached targets ({source})", SchedulerLogLevel.Warning);
                        slot = _offlineCalculator.CalculateNextSlotOffline(
                            _currentTargetId,
                            _currentPanelId,
                            _currentFilter,
                            latitude,
                            longitude);
                        
                        if (slot?.SlotType == SlotType.Exposure)
                        {
                            AddLogEntry($"[OFFLINE] Selected: {slot.TargetName} - {slot.Filter}", SchedulerLogLevel.Info);
                        }
                    }
                    else
                    {
                        AddLogEntry("[OFFLINE] No observatory info available - cannot calculate offline", SchedulerLogLevel.Error);
                    }
                }

                if (slot == null)
                {
                    var offlineNoSlot = _offlineCalculator.ShouldUseOfflineMode() || isOfflineMode;
                    if (offlineNoSlot)
                    {
                        AddLogEntry("No slot available while offline - stopping AstroManager scheduler", SchedulerLogLevel.Warning);
                        StatusMessage = "Stopped: Offline and no valid slots";
                        _shouldContinue = false;
                        _receivedStopSignal = true;
                        SharedSchedulerState.Instance.Clear();
                        _heartbeatService.SetCurrentState("Session Ended", null, null, 0, 0, status: "Idle");
                        _ = _heartbeatService.ForceStatusUpdateAsync();
                        progress.Report(new ApplicationStatus { Status = StatusMessage });
                        return;
                    }

                    AddLogEntry("No slot available from API - retrying", SchedulerLogLevel.Warning);
                    StatusMessage = "No slot available - retrying";
                    await Task.Delay(5000, token);
                    return; // Let loop condition decide whether to continue
                }

                _currentSlot = slot;
                UpdateFilterNameCache(slot);

                if (slot.SlotType == SlotType.Exposure
                    && slot.ShouldAutomateFilterChanges
                    && string.IsNullOrWhiteSpace(slot.NinaFilterName))
                {
                    slot.NinaFilterName = ResolveNinaFilterNameForSlot(slot.Filter);
                    var targetFilter = !string.IsNullOrWhiteSpace(slot.NinaFilterName) ? slot.NinaFilterName : slot.Filter;
                    slot.RequiresFilterChange = !string.Equals(_currentFilter, targetFilter, StringComparison.OrdinalIgnoreCase);
                }
                
                // Set current queue item ID immediately so error handling can update status
                if (slot.QueueItemId.HasValue)
                {
                    _currentQueueItemId = slot.QueueItemId;
                }
                
                // Update shared state so image upload knows which goal is being captured
                SharedSchedulerState.Instance.SetCurrentSlot(
                    slot.TargetId,
                    slot.TargetName,
                    slot.ImagingGoalId,
                    slot.PanelId,
                    slot.PanelNumber,
                    slot.Filter,
                    slot.PreferSchedulerFilterForCaptureAttribution,
                    slot.ExposureTimeSeconds);
                
                // Log detailed slot info for debugging
                Logger.Info($"[SLOT-RECEIVED] Type={slot.SlotType}, Target={slot.TargetName}, TargetId={slot.TargetId}, GoalId={slot.ImagingGoalId}, Panel={slot.PanelName}, PanelId={slot.PanelId}");
                Logger.Info($"[SLOT-RECEIVED] Filter={slot.Filter}, ExpTime={slot.ExposureTimeSeconds}s, Progress={slot.CompletedExposures}/{slot.TotalGoalExposures}, RequiresSlew={slot.RequiresSlew}, RequiresFilter={slot.RequiresFilterChange}");
                AddLogEntry($"Received slot: Type={slot.SlotType}, Target={slot.TargetName ?? "None"}, QueueItem={slot.QueueItemId?.ToString() ?? "None"}, Message={slot.Message ?? "None"}");

                var configSnapshot = _apiClient.CurrentClientConfiguration;
                Logger.Debug(
                    $"AstroManager Scheduler: Safety policy snapshot - PolicyId={configSnapshot?.RuntimeStopSafetyPolicyId?.ToString() ?? "null"}, PolicyName={configSnapshot?.RuntimeStopSafetyPolicyName ?? "null"}, EmbeddedPolicy={(configSnapshot?.RuntimeStopSafetyPolicy != null ? "present" : "null")}, EmbeddedRules={configSnapshot?.RuntimeStopSafetyPolicy?.Rules?.Count ?? 0}");

                var effectiveConfig = GetEffectiveConfiguration();
                var (effectiveRuntimePolicy, runtimePolicySource) = await GetEffectiveRuntimeStopSafetyPolicyWithRefreshAsync(effectiveConfig);
                Logger.Debug($"AstroManager Scheduler: Safety policy source: {runtimePolicySource}");

                await HandleTransitionIntoNextSlotAsync(slot, progress, token);

                // Handle slot type
                switch (slot.SlotType)
                {
                    case SlotType.Exposure:
                        var slotDisplayName = slot.TargetName + (slot.PanelName != null ? $" ({slot.PanelName.Replace("Panel ", "P").Replace("Panel_", "P")})" : "");
                        Logger.Info($"[SLOT-EXECUTE] Starting exposure: {slotDisplayName} {slot.Filter} {slot.ExposureTimeSeconds}s (Goal {slot.ImagingGoalId}, Progress={slot.CompletedExposures}/{slot.TotalGoalExposures})");
                        
                        // Execute pending commands if requested (user-triggered from UI)
                        if (slot.CalibrateGuiderFirst)
                        {
                            AddLogEntry("🎯 Running guider calibration (user-triggered)...", SchedulerLogLevel.Info);
                            await ExecuteGuiderCalibrationAsync(progress, token);
                            AddLogEntry("✅ Guider calibration complete", SchedulerLogLevel.Success);
                        }
                        
                        if (slot.RunAutofocusFirst)
                        {
                            AddLogEntry("🔭 Running autofocus (user-triggered)...", SchedulerLogLevel.Info);
                            await ExecuteAutofocusAsync(progress, token);
                            AddLogEntry("✅ Autofocus complete", SchedulerLogLevel.Success);
                        }
                        
                        AddLogEntry($"Executing exposure: {slotDisplayName} - {slot.Filter} ({slot.ExposureTimeSeconds}s)", SchedulerLogLevel.Info);
                        await ExecuteExposureSlotAsync(slot, effectiveRuntimePolicy, progress, token);
                        AddLogEntry($"Exposure complete: {slotDisplayName} - {slot.Filter}", SchedulerLogLevel.Success);
                        _errorRetryCount = 0; // Reset on success
                        _errorTargetId = null; // Clear error target tracking
                        
                        // Check for pending commands (user-triggered mid-session via local queue)
                        // These are queued by ImagingCommandHandler when scheduler is running
                        if (SharedSchedulerState.Instance.ConsumeGuiderCalibrationRequest())
                        {
                            Logger.Info("AstroManager Scheduler: Executing pending guider calibration (queued mid-session)");
                            AddLogEntry("🎯 Running guider calibration (queued mid-session)...", SchedulerLogLevel.Info);
                            await ExecuteGuiderCalibrationAsync(progress, token);
                            AddLogEntry("✅ Guider calibration complete", SchedulerLogLevel.Success);
                        }
                        
                        if (SharedSchedulerState.Instance.ConsumeAutofocusRequest())
                        {
                            Logger.Info("AstroManager Scheduler: Executing pending autofocus (queued mid-session)");
                            AddLogEntry("🔭 Running autofocus (queued mid-session)...", SchedulerLogLevel.Info);
                            await ExecuteAutofocusAsync(progress, token);
                            AddLogEntry("✅ Autofocus complete", SchedulerLogLevel.Success);
                        }

                        if (effectiveRuntimePolicy != null)
                        {
                            Logger.Debug("AstroManager Scheduler: Running post-slot runtime safety evaluation");
                            var postSlotHandled = await EvaluateRuntimeStopChecksAsync(effectiveRuntimePolicy, slot, RuntimeSafetyEvaluationPhase.PostSlot, progress, token);
                            if (postSlotHandled)
                            {
                                return;
                            }
                        }
                        
                        break;
                        
                    case SlotType.Wait:
                        var waitMins = slot.WaitMinutes > 0 ? slot.WaitMinutes : 1;

                        if ((_offlineCalculator.ShouldUseOfflineMode() || isOfflineMode)
                            && (slot.Message?.IndexOf("No cached targets", StringComparison.OrdinalIgnoreCase) >= 0
                                || slot.Message?.IndexOf("No observable targets", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            AddLogEntry($"[OFFLINE] {slot.Message} - stopping scheduler", SchedulerLogLevel.Warning);
                            StatusMessage = "Stopped: Offline and no targets for current night";
                            _shouldContinue = false;
                            _receivedStopSignal = true;
                            SharedSchedulerState.Instance.Clear();
                            _heartbeatService.SetCurrentState("Session Ended", null, null, 0, 0, status: "Idle");
                            _ = _heartbeatService.ForceStatusUpdateAsync();
                            progress.Report(new ApplicationStatus { Status = StatusMessage });
                            break;
                        }
                        
                        // Build display message with local time if WaitUntilUtc is provided
                        string waitMessage;
                        if (slot.WaitUntilUtc.HasValue)
                        {
                            var localTime = slot.WaitUntilUtc.Value.ToLocalTime();
                            waitMessage = $"Waiting until {localTime:ddd MMM dd HH:mm}";
                            Logger.Info($"[WAIT] Until {localTime:yyyy-MM-dd HH:mm:ss}, {waitMins} minutes");
                        }
                        else
                        {
                            var reason = slot.Message ?? "No reason specified";
                            waitMessage = $"Waiting {waitMins} min: {reason}";
                            Logger.Info($"[WAIT] {waitMins} minutes: {reason}");
                        }
                        
                        // Stop tracking if waiting more than 5 minutes to prevent mount wear
                        if (waitMins >= 5 && !_trackingStoppedForNoSlot)
                        {
                            var telescopeInfo = _telescopeMediator.GetInfo();
                            if (telescopeInfo.Connected && telescopeInfo.CanSetTrackingEnabled && telescopeInfo.TrackingEnabled)
                            {
                                AddLogEntry($"Stopping tracking during {waitMins}min wait to prevent mount wear", SchedulerLogLevel.Info);
                                _telescopeMediator.SetTrackingEnabled(false);
                                _trackingStoppedForNoSlot = true;
                            }
                        }
                        
                        AddLogEntry(waitMessage, SchedulerLogLevel.Info);
                        StatusMessage = waitMessage;
                        progress.Report(new ApplicationStatus { Status = waitMessage });

                        await ExecuteEventContainerAsync(BeforeWaitContainer, progress, token);

                        var waitDuration = TimeSpan.FromMinutes(waitMins);
                        if (effectiveRuntimePolicy != null && waitDuration > PeriodicSafetyCheckInterval)
                        {
                            var remaining = waitDuration;
                            while (remaining > TimeSpan.Zero)
                            {
                                var chunk = remaining > PeriodicSafetyCheckInterval ? PeriodicSafetyCheckInterval : remaining;
                                await Task.Delay(chunk, token);
                                remaining -= chunk;

                                if (remaining <= TimeSpan.Zero)
                                {
                                    break;
                                }

                                Logger.Debug("AstroManager Scheduler: Periodic runtime safety re-check during wait slot");
                                var waitHandled = await EvaluateRuntimeStopChecksAsync(effectiveRuntimePolicy, slot, RuntimeSafetyEvaluationPhase.PeriodicWait, progress, token);
                                if (waitHandled)
                                {
                                    return;
                                }
                            }
                        }
                        else
                        {
                            await Task.Delay(waitDuration, token);
                        }

                        await ExecuteEventContainerAsync(AfterWaitContainer, progress, token);

                        break;
                        
                    case SlotType.Park:
                        AddLogEntry("API requested park - ending session", SchedulerLogLevel.Warning);
                        StatusMessage = "Parking...";
                        _shouldContinue = false; // Signal to stop looping
                        _receivedStopSignal = true; // Prevent SequenceBlockInitialize from resetting
                        Logger.Info("AstroManager Scheduler: Park signal received, _receivedStopSignal=true");
                        break;
                        
                    case SlotType.Stop:
                        var stopMessage = slot.Message ?? "No reason specified";
                        var shouldStopScheduler = true; // Default: stop unless we decide to continue
                        
                        // Handle different stop reasons with appropriate logging
                        switch (slot.StopReason)
                        {
                            case StopReason.NoMoreTargetsTonight:
                                AddLogEntry($"⚠️ NO MORE TARGETS TONIGHT: {stopMessage}", SchedulerLogLevel.Warning);
                                StatusMessage = "⚠️ No more targets tonight";
                                Logger.Warning($"AstroManager Scheduler: Session ending - no more targets observable tonight");
                                break;
                                
                            case StopReason.PastAstronomicalDawn:
                                // Night is over - stop the scheduler (don't wait for next night)
                                AddLogEntry($"🌅 NIGHT ENDED: {stopMessage}", SchedulerLogLevel.Info);
                                StatusMessage = "🌅 Night over - past astronomical dawn";
                                Logger.Info($"AstroManager Scheduler: Session ending - past astronomical dawn");
                                break;
                                
                            case StopReason.AllTargetsComplete:
                                AddLogEntry($"✅ ALL TARGETS COMPLETE: {stopMessage}", SchedulerLogLevel.Success);
                                StatusMessage = "✅ All targets complete!";
                                Logger.Info($"AstroManager Scheduler: Session complete - all targets finished");
                                break;
                                
                            default:
                                AddLogEntry($"API signaled STOP: {stopMessage}", SchedulerLogLevel.Warning);
                                StatusMessage = slot.Message ?? "Complete";
                                break;
                        }
                        
                        if (shouldStopScheduler)
                        {
                            // Update heartbeat to reflect session ended - clear target info and set to Idle
                            _heartbeatService.SetCurrentState("Session Ended", null, null, 0, 0, status: "Idle");
                            _ = _heartbeatService.ForceStatusUpdateAsync();
                            
                            progress.Report(new ApplicationStatus { Status = StatusMessage });
                            _shouldContinue = false; // Signal to stop looping
                            _receivedStopSignal = true; // Prevent SequenceBlockInitialize from resetting
                            Logger.Info($"AstroManager Scheduler: Stop signal received, _receivedStopSignal=true");
                        }
                        break;
                        
                    default:
                        AddLogEntry($"Unknown slot type: {slot.SlotType} - stopping", SchedulerLogLevel.Error);
                        _shouldContinue = false;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                AddLogEntry("Operation cancelled by user", SchedulerLogLevel.Warning);
                StatusMessage = "Cancelled";
                _shouldContinue = false;
                // Update heartbeat to reflect cancelled state
                _heartbeatService.SetCurrentState("Cancelled", null, null, 0, 0, status: "Idle");
                _ = _heartbeatService.ForceStatusUpdateAsync();
                SharedSchedulerState.Instance.Clear(); // Clear scheduler state so manual captures don't associate with old target
                throw; // Re-throw to let NINA handle cancellation
            }
            catch (Exception ex)
            {
                AddLogEntry($"Error in slot execution: {ex.Message}", SchedulerLogLevel.Error);
                await HandleErrorAsync(ex, progress, token);
            }
        }

        private async Task HandleTransitionIntoNextSlotAsync(
            NextSlotDto nextSlot,
            IProgress<ApplicationStatus> progress,
            CancellationToken token)
        {
            if (!_currentTargetId.HasValue)
            {
                return;
            }

            var leavingCurrentTarget =
                nextSlot.SlotType != SlotType.Exposure
                || nextSlot.TargetId != _currentTargetId
                || nextSlot.PanelId != _currentPanelId;

            if (!leavingCurrentTarget)
            {
                return;
            }

            await ExecuteEventContainerAsync(AfterTargetContainer, progress, token);

            if (nextSlot.SlotType != SlotType.Exposure)
            {
                ClearTarget();
                _currentTargetId = null;
                _currentPanelId = null;
                _currentQueueItemId = null;
            }
        }
        
        /// <summary>
        /// Called when the sequence item is interrupted or finished.
        /// Cleans up the session with the API.
        /// </summary>
        public override void AfterParentChanged()
        {
            base.AfterParentChanged();
            EnsureEventContainersInitialized();
            // Reset session state when moved/removed
            if (Parent == null && _sessionStarted)
            {
                _ = CleanupSessionAsync();
            }
        }
        
        /// <summary>
        /// Calculate the next nautical twilight dusk (evening twilight start) for waiting until next night
        /// Uses NINA's NighttimeCalculator for proper twilight calculation
        /// </summary>
        private DateTime? CalculateNextAstronomicalDusk()
        {
            try
            {
                var now = DateTime.Now;
                
                // Use NINA's NighttimeCalculator with profile service for proper twilight calculation
                var calculator = new NighttimeCalculator(_profileService);
                
                // Get nighttime data for today using reference date
                var referenceDate = NighttimeCalculator.GetReferenceDate(now.Date);
                var nighttimeData = calculator.Calculate(referenceDate);
                    
                DateTime? dusk = nighttimeData?.NauticalTwilightRiseAndSet?.Set;
                
                // If dusk is in the past, try tomorrow
                if (!dusk.HasValue || dusk.Value < now)
                {
                    referenceDate = NighttimeCalculator.GetReferenceDate(now.Date.AddDays(1));
                    nighttimeData = calculator.Calculate(referenceDate);
                    dusk = nighttimeData?.NauticalTwilightRiseAndSet?.Set;
                }
                
                if (dusk.HasValue)
                {
                    Logger.Info($"CalculateNextAstronomicalDusk: Next nautical twilight calculated at {dusk.Value:yyyy-MM-dd HH:mm}");
                }
                
                return dusk;
            }
            catch (Exception ex)
            {
                Logger.Error($"CalculateNextAstronomicalDusk: Failed to calculate next twilight: {ex.Message}");
                return null;
            }
        }

        private bool IsPastCurrentNightTwilight(out string reason)
        {
            reason = "Past twilight for this session";

            try
            {
                var now = DateTime.Now;
                if (!_sessionNightEndTwilightLocal.HasValue)
                {
                    _sessionNightEndTwilightLocal = CalculateSessionNightEndTwilight(now);
                }

                if (!_sessionNightEndTwilightLocal.HasValue)
                {
                    return false;
                }

                if (now >= _sessionNightEndTwilightLocal.Value)
                {
                    reason = $"Current time {now:yyyy-MM-dd HH:mm} is past session twilight {_sessionNightEndTwilightLocal.Value:yyyy-MM-dd HH:mm}";
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[TWILIGHT-CHECK] Failed twilight safety check: {ex.Message}");
                return false;
            }
        }

        private async Task<string> ReconnectEquipmentForSafetyAsync(
            bool reconnectAll,
            RuntimeSafetyReconnectComponent? reconnectComponent,
            CancellationToken token)
        {
            var reconnected = new List<string>();
            var failed = new List<string>();

            async Task TryReconnectAsync(string equipmentName, Func<bool> isConnected, Func<Task<bool>> connectAsync)
            {
                try
                {
                    if (isConnected())
                    {
                        return;
                    }

                    var connected = await connectAsync();
                    if (connected)
                    {
                        reconnected.Add(equipmentName);
                    }
                    else
                    {
                        failed.Add(equipmentName);
                    }
                }
                catch
                {
                    failed.Add(equipmentName);
                }
            }

            if (reconnectAll)
            {
                await TryReconnectAsync("Camera", () => _cameraMediator.GetInfo().Connected, () => _cameraMediator.Connect());
                await TryReconnectAsync("Mount", () => _telescopeMediator.GetInfo().Connected, () => _telescopeMediator.Connect());
                await TryReconnectAsync("Guider", () => _guiderMediator.GetInfo().Connected, () => _guiderMediator.Connect());
                await TryReconnectAsync("Focuser", () => _focuserMediator.GetInfo().Connected, () => _focuserMediator.Connect());
                await TryReconnectAsync("FilterWheel", () => _filterWheelMediator.GetInfo().Connected, () => _filterWheelMediator.Connect());
                await TryReconnectAsync("Rotator", () => _rotatorMediator.GetInfo().Connected, () => _rotatorMediator.Connect());
                await TryReconnectAsync("SafetyMonitor", () => _safetyMonitorMediator.GetInfo().Connected, () => _safetyMonitorMediator.Connect());
                await TryReconnectAsync("Weather", () => _weatherDataMediator.GetInfo().Connected, () => _weatherDataMediator.Connect());
                await TryReconnectAsync("Dome", () => _domeMediator.GetInfo().Connected, () => _domeMediator.Connect());
            }
            else if (reconnectComponent.HasValue && reconnectComponent.Value != RuntimeSafetyReconnectComponent.CriticalImaging)
            {
                switch (reconnectComponent.Value)
                {
                    case RuntimeSafetyReconnectComponent.Camera:
                        await TryReconnectAsync("Camera", () => _cameraMediator.GetInfo().Connected, () => _cameraMediator.Connect());
                        break;
                    case RuntimeSafetyReconnectComponent.Mount:
                        await TryReconnectAsync("Mount", () => _telescopeMediator.GetInfo().Connected, () => _telescopeMediator.Connect());
                        break;
                    case RuntimeSafetyReconnectComponent.Guider:
                        await TryReconnectAsync("Guider", () => _guiderMediator.GetInfo().Connected, () => _guiderMediator.Connect());
                        break;
                    case RuntimeSafetyReconnectComponent.Focuser:
                        await TryReconnectAsync("Focuser", () => _focuserMediator.GetInfo().Connected, () => _focuserMediator.Connect());
                        break;
                    case RuntimeSafetyReconnectComponent.FilterWheel:
                        await TryReconnectAsync("FilterWheel", () => _filterWheelMediator.GetInfo().Connected, () => _filterWheelMediator.Connect());
                        break;
                    case RuntimeSafetyReconnectComponent.Rotator:
                        await TryReconnectAsync("Rotator", () => _rotatorMediator.GetInfo().Connected, () => _rotatorMediator.Connect());
                        break;
                    case RuntimeSafetyReconnectComponent.SafetyMonitor:
                        await TryReconnectAsync("SafetyMonitor", () => _safetyMonitorMediator.GetInfo().Connected, () => _safetyMonitorMediator.Connect());
                        break;
                    case RuntimeSafetyReconnectComponent.Weather:
                        await TryReconnectAsync("Weather", () => _weatherDataMediator.GetInfo().Connected, () => _weatherDataMediator.Connect());
                        break;
                    case RuntimeSafetyReconnectComponent.Dome:
                        await TryReconnectAsync("Dome", () => _domeMediator.GetInfo().Connected, () => _domeMediator.Connect());
                        break;
                    default:
                        await TryReconnectAsync("Camera", () => _cameraMediator.GetInfo().Connected, () => _cameraMediator.Connect());
                        await TryReconnectAsync("Mount", () => _telescopeMediator.GetInfo().Connected, () => _telescopeMediator.Connect());
                        await TryReconnectAsync("Guider", () => _guiderMediator.GetInfo().Connected, () => _guiderMediator.Connect());
                        break;
                }
            }
            else
            {
                await TryReconnectAsync("Camera", () => _cameraMediator.GetInfo().Connected, () => _cameraMediator.Connect());
                await TryReconnectAsync("Mount", () => _telescopeMediator.GetInfo().Connected, () => _telescopeMediator.Connect());
                await TryReconnectAsync("Guider", () => _guiderMediator.GetInfo().Connected, () => _guiderMediator.Connect());
            }

            if (reconnected.Count == 0 && failed.Count == 0)
            {
                return "all targeted equipment was already connected";
            }

            if (failed.Count == 0)
            {
                return $"reconnected: {string.Join(", ", reconnected)}";
            }

            if (reconnected.Count == 0)
            {
                return $"failed: {string.Join(", ", failed)}";
            }

            return $"reconnected: {string.Join(", ", reconnected)} | failed: {string.Join(", ", failed)}";
        }

        private void InitializeSessionNightEndTwilight()
        {
            _sessionNightEndTwilightLocal = CalculateSessionNightEndTwilight(DateTime.Now);

            if (_sessionNightEndTwilightLocal.HasValue)
            {
                AddLogEntry($"Session twilight boundary: {_sessionNightEndTwilightLocal.Value:yyyy-MM-dd HH:mm}", SchedulerLogLevel.Info);
            }
            else
            {
                Logger.Warning("[TWILIGHT-CHECK] Could not determine session twilight boundary at start; fallback to lazy calculation");
            }
        }

        private DateTime? CalculateSessionNightEndTwilight(DateTime sessionStartLocal)
        {
            var calculator = new NighttimeCalculator(_profileService);

            DateTime? nextDawn = null;
            var candidateDates = new[]
            {
                sessionStartLocal.Date.AddDays(-1),
                sessionStartLocal.Date,
                sessionStartLocal.Date.AddDays(1),
                sessionStartLocal.Date.AddDays(2)
            };

            foreach (var candidateDate in candidateDates)
            {
                var referenceDate = NighttimeCalculator.GetReferenceDate(candidateDate);
                var nighttimeData = calculator.Calculate(referenceDate);
                var dawn = nighttimeData?.NauticalTwilightRiseAndSet?.Rise;

                if (!dawn.HasValue || dawn.Value <= sessionStartLocal)
                {
                    continue;
                }

                if (!nextDawn.HasValue || dawn.Value < nextDawn.Value)
                {
                    nextDawn = dawn.Value;
                }
            }

            if (nextDawn.HasValue)
            {
                Logger.Info($"[TWILIGHT-CHECK] Session start {sessionStartLocal:yyyy-MM-dd HH:mm}, boundary twilight {nextDawn.Value:yyyy-MM-dd HH:mm}");
            }

            return nextDawn;
        }

        private async Task TryRefreshObservatoryCacheAsync()
        {
            try
            {
                var observatory = await _apiClient.GetLicenseObservatoryAsync();
                if (observatory != null)
                {
                    _cachedObservatory = observatory;
                    Logger.Info($"[OFFLINE] Cached observatory '{observatory.Name}' ({observatory.Latitude:F4}, {observatory.Longitude:F4}) for offline fallback");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[OFFLINE] Could not refresh observatory cache: {ex.Message}");
            }
        }

        private async Task<(double Latitude, double Longitude, string Source)?> ResolveOfflineCoordinatesAsync()
        {
            if (_cachedObservatory != null)
            {
                return (_cachedObservatory.Latitude, _cachedObservatory.Longitude, "cached observatory");
            }

            await TryRefreshObservatoryCacheAsync();
            if (_cachedObservatory != null)
            {
                return (_cachedObservatory.Latitude, _cachedObservatory.Longitude, "API observatory");
            }

            var profile = _profileService.ActiveProfile;
            if (profile?.AstrometrySettings != null)
            {
                var latitude = profile.AstrometrySettings.Latitude;
                var longitude = profile.AstrometrySettings.Longitude;
                Logger.Warning($"[OFFLINE] Observatory API unavailable - using NINA profile coordinates ({latitude:F4}, {longitude:F4})");
                return (latitude, longitude, "NINA profile");
            }

            return null;
        }

        private void TryResetGuiderRmsGraphBeforeExposure()
        {
            try
            {
                static bool TryInvokeResetCandidate(object target, out string methodName)
                {
                    methodName = string.Empty;
                    var candidates = new[]
                    {
                        "ResetRmsHistory",
                        "ResetRms",
                        "ResetGuidingRms",
                        "ResetStatistics",
                        "ResetStats",
                        "ClearRmsGraph",
                        "ClearGuideGraph",
                        "ClearHistory",
                        "Clear",
                        "Reset"
                    };

                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var targetType = target.GetType();

                    foreach (var candidate in candidates)
                    {
                        var method = targetType.GetMethod(candidate, flags, null, Type.EmptyTypes, null);
                        if (method == null)
                        {
                            continue;
                        }

                        method.Invoke(target, null);
                        methodName = candidate;
                        return true;
                    }

                    return false;
                }

                if (TryInvokeResetCandidate(_guiderMediator, out var mediatorMethod))
                {
                    Logger.Debug($"[GUIDER-RMS] Reset guider graph/statistics via mediator method '{mediatorMethod}' before exposure.");
                    return;
                }

                var guiderInfo = _guiderMediator.GetInfo();
                if (guiderInfo != null && TryInvokeResetCandidate(guiderInfo, out var infoMethod))
                {
                    Logger.Debug($"[GUIDER-RMS] Reset guider graph/statistics via guider info method '{infoMethod}' before exposure.");
                    return;
                }

                var rmsProperty = guiderInfo?.GetType().GetProperty("RMSError", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?? guiderInfo?.GetType().GetProperty("RmsError", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var rmsObject = rmsProperty?.GetValue(guiderInfo);
                if (rmsObject != null && TryInvokeResetCandidate(rmsObject, out var rmsMethod))
                {
                    Logger.Debug($"[GUIDER-RMS] Reset guider RMS object via method '{rmsMethod}' before exposure.");
                    return;
                }

                Logger.Debug("[GUIDER-RMS] Active guider API does not expose a supported RMS reset method.");
            }
            catch (Exception ex)
            {
                Logger.Debug($"[GUIDER-RMS] Failed to reset guider RMS graph before exposure: {ex.Message}");
            }
        }

        private static double? TryReadDoubleProperty(object source, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property == null)
                {
                    continue;
                }

                var value = property.GetValue(source);
                if (value == null)
                {
                    continue;
                }

                try
                {
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    if (double.TryParse(value.ToString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var invariantParsed))
                    {
                        return invariantParsed;
                    }

                    if (double.TryParse(value.ToString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var currentParsed))
                    {
                        return currentParsed;
                    }
                }
            }

            return null;
        }

        private static int? TryReadIntProperty(object source, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property == null)
                {
                    continue;
                }

                var value = property.GetValue(source);
                if (value == null)
                {
                    continue;
                }

                try
                {
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var invariantParsed))
                    {
                        return invariantParsed;
                    }

                    if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var currentParsed))
                    {
                        return currentParsed;
                    }
                }
            }

            return null;
        }

        private static DateTime? TryReadDateTimeProperty(object source, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property == null)
                {
                    continue;
                }

                var value = property.GetValue(source);
                if (value == null)
                {
                    continue;
                }

                if (value is DateTime directDateTime)
                {
                    return directDateTime;
                }

                if (DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedInvariant))
                {
                    return parsedInvariant;
                }

                if (DateTime.TryParse(value.ToString(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsedCurrent))
                {
                    return parsedCurrent;
                }
            }

            return null;
        }
        
        /// <summary>
        /// Reset state for a new run
        /// </summary>
        public void ResetSession()
        {
            _sessionStarted = false;
            _sessionNightEndTwilightLocal = null;
            _shouldContinue = true;
            _receivedStopSignal = false; // Clear stop signal on explicit reset
            Logger.Info("AstroManager Scheduler: ResetSession - cleared _receivedStopSignal");
            _trackingStoppedForNoSlot = false;
            _currentTargetId = null;
            _currentPanelId = null;
            _currentFilter = null;
            _completedTargetEventsRaised.Clear();
            SessionExposuresTaken = 0;
            _exposuresSinceLastDither = 0;
            _exposureContainer?.ResetExposureCounter();
            // Reset slew tracking - new session should slew to first target
            _lastSuccessfulSlewTargetId = null;
            _lastSuccessfulSlewPanelId = null;
            // Note: _errorRetryCount is NOT reset here - it's static and only resets on target change or explicit skip
            StatusMessage = "Ready";
            ClearLog();
            AddLogEntry("Session reset - ready for new run");
        }
        
        /// <summary>
        /// Called by NINA when the sequence is reset/restarted.
        /// IMPORTANT: Do NOT call ResetSession() here - NINA calls ResetProgress after each slot execution,
        /// which would reset trigger counters and break AF After # Exposures, etc.
        /// Only reset the minimal state needed for NINA to allow re-execution.
        /// </summary>
        public override void ResetProgress()
        {
            // Only reset the flags that allow the scheduler to run again
            // Do NOT reset session counters, trigger state, or clear logs here!
            _shouldContinue = true;
            base.ResetProgress();
        }
        
        /// <summary>
        /// Called when the sequence block is initialized.
        /// IMPORTANT: Reset _shouldContinue here because NINA checks loop conditions
        /// BEFORE calling ResetProgress() on child items.
        /// </summary>
        public override void SequenceBlockInitialize()
        {
            Logger.Debug($"AstroManager Scheduler: SequenceBlockInitialize (receivedStopSignal={_receivedStopSignal}, shouldContinue={_shouldContinue})");
            
            // Only reset _shouldContinue if we haven't received a Stop signal from the API.
            // This prevents the flag from being reset after NIGHT ENDED or ALL TARGETS COMPLETE.
            // The _receivedStopSignal flag is only cleared by explicit user action (ResetSession).
            if (!_receivedStopSignal)
            {
                _shouldContinue = true;
            }
            else
            {
                Logger.Info("AstroManager Scheduler: NOT resetting _shouldContinue because Stop signal was received");
            }
            
            // Clear current target state so next slot request will trigger a fresh slew
            // This is important after sequence cancel/restart to ensure proper re-centering
            _currentTargetId = null;
            _currentPanelId = null;
            _currentFilter = null;
            _completedTargetEventsRaised.Clear();
            Logger.Debug("AstroManager Scheduler: Cleared current target state for fresh slew on restart");
            
            base.SequenceBlockInitialize();
        }
        
        /// <summary>
        /// Called when the sequence block starts executing
        /// </summary>
        public override void SequenceBlockStarted()
        {
            Logger.Debug("AstroManager Scheduler: SequenceBlockStarted");
            base.SequenceBlockStarted();
        }
        
        /// <summary>
        /// Called when the sequence block finishes executing
        /// </summary>
        public override void SequenceBlockFinished()
        {
            Logger.Debug("AstroManager Scheduler: SequenceBlockFinished");
            ClearTarget();
            base.SequenceBlockFinished();
        }
        
        /// <summary>
        /// Called when the sequence block is torn down
        /// </summary>
        public override void SequenceBlockTeardown()
        {
            Logger.Debug("AstroManager Scheduler: SequenceBlockTeardown");
            ClearTarget();
            SharedSchedulerState.Instance.Clear(); // Clear scheduler state so manual captures don't associate with old target
            _exposureContainer = null;
            base.SequenceBlockTeardown();
        }
        
        private async Task CleanupSessionAsync()
        {
            if (_sessionStarted)
            {
                AddLogEntry($"Session ending. Total exposures: {SessionExposuresTaken}");
                await _apiClient.StopSessionAsync();
                _sessionStarted = false;
                _sessionNightEndTwilightLocal = null;
                _heartbeatService.ClearCurrentState();
                SharedSchedulerState.Instance.Clear(); // Clear scheduler state so manual captures don't associate with old target
                _exposureContainer?.ResetExposureCounter();
                StatusMessage = $"Complete ({SessionExposuresTaken} exposures)";
            }
        }

        /// <summary>
        /// Execute a single exposure slot
        /// </summary>
        private async Task ExecuteExposureSlotAsync(
            NextSlotDto slot,
            RuntimeStopSafetyPolicyDto? runtimePolicy,
            IProgress<ApplicationStatus> progress,
            CancellationToken token)
        {
            // Build target display name with panel indicator for mosaics
            var targetDisplayName = slot.TargetName ?? "Unknown";
            if (!string.IsNullOrEmpty(slot.PanelName))
            {
                var panelIndicator = slot.PanelName.Replace("Panel ", "P").Replace("Panel_", "P");
                targetDisplayName = $"{targetDisplayName} ({panelIndicator})";
            }
            CurrentTargetName = targetDisplayName;
            CurrentPositionAngle = slot.PositionAngle;

            var isNewTargetContext = !_currentTargetId.HasValue
                || _currentTargetId != slot.TargetId
                || _currentPanelId != slot.PanelId;
            
            // IMPORTANT: Update current target IMMEDIATELY so error handling knows which target failed
            // This must happen BEFORE any operations that could throw errors
            _currentTargetId = slot.TargetId;
            _currentPanelId = slot.PanelId;
            _currentQueueItemId = slot.QueueItemId;
            // Set target for IDeepSkyObjectContainer - enables NINA triggers to find coordinates
            SetTarget(slot);
            
            // ALWAYS update heartbeat with current target at slot start (even if no slew needed)
            // Await this to ensure AM receives the update immediately
            _heartbeatService.SetCurrentState("Starting Slot", slot.TargetName, slot.Filter, slot.CompletedExposures, slot.TotalGoalExposures,
                targetId: slot.TargetId, imagingGoalId: slot.ImagingGoalId, panelId: slot.PanelId,
                panelName: slot.PanelName, exposureTimeSeconds: slot.ExposureTimeSeconds);
            await _heartbeatService.ForceStatusUpdateAsync();
            
            Logger.Info($"AstroManager Scheduler: Exposure slot - {slot.TargetName} {slot.PanelName ?? ""} {slot.Filter} ({slot.CompletedExposures + 1}/{slot.TotalGoalExposures})");

            // 1. SLEW if needed - also force re-slew if previous slew for this target/panel failed
            var needsSlew = slot.RequiresSlew;
            var slewFailedPreviously = _lastSuccessfulSlewTargetId != slot.TargetId || _lastSuccessfulSlewPanelId != slot.PanelId;
            if (!needsSlew && slewFailedPreviously)
            {
                Logger.Warning($"[SLEW-RETRY] Forcing re-slew: API said RequiresSlew=false but last successful slew was for different target/panel. " +
                    $"LastSuccessfulSlew: TargetId={_lastSuccessfulSlewTargetId}, PanelId={_lastSuccessfulSlewPanelId}. " +
                    $"Current: TargetId={slot.TargetId}, PanelId={slot.PanelId}");
                needsSlew = true;
            }
            
            if (needsSlew)
            {
                AddLogEntry($"Slew+Center: {slot.TargetName}{(slot.PanelName != null ? $" ({slot.PanelName})" : "")}", SchedulerLogLevel.Info);
                _heartbeatService.SetCurrentState("Slewing", slot.TargetName, slot.Filter, slot.CompletedExposures, slot.TotalGoalExposures,
                    targetId: slot.TargetId, imagingGoalId: slot.ImagingGoalId, panelId: slot.PanelId,
                    panelName: slot.PanelName, exposureTimeSeconds: slot.ExposureTimeSeconds);
                _ = _heartbeatService.ForceStatusUpdateAsync();
                await SlewToCoordinatesAsync(slot, progress, token);
                
                // Mark this target/panel as successfully slewed - future retries won't need to re-slew
                _lastSuccessfulSlewTargetId = slot.TargetId;
                _lastSuccessfulSlewPanelId = slot.PanelId;
                Logger.Debug($"[SLEW-RETRY] Marked successful slew for TargetId={slot.TargetId}, PanelId={slot.PanelId}");
                
                // Reset dither counter after slew (new target or re-center)
                _exposuresSinceLastDither = 0;
                
                // Inject coordinates into CenterAfterDrift triggers in parent containers
                // This fixes the "No Target Set" warning by telling CenterAfterDrift where we're pointing
                InjectCoordinatesIntoCenterAfterDriftTriggers(slot);
            }

            // 2. SWITCH FILTER for mono cameras (idempotent if already on same filter)
            var filterToSwitch = !string.IsNullOrEmpty(slot.NinaFilterName) ? slot.NinaFilterName : slot.Filter;
            if (slot.ShouldAutomateFilterChanges && slot.IsMono && !string.IsNullOrEmpty(filterToSwitch))
            {
                Logger.Info($"[FILTER-SWITCH] Slot.Filter='{slot.Filter}', Slot.NinaFilterName='{slot.NinaFilterName ?? "(null)"}' -> Using: '{filterToSwitch}'");
                if (!slot.RequiresFilterChange)
                {
                    Logger.Debug($"[FILTER-SWITCH] RequiresFilterChange=false, forcing idempotent filter validation switch to '{filterToSwitch}'");
                }
                _heartbeatService.SetCurrentState("Switching Filter", slot.TargetName, slot.Filter, slot.CompletedExposures, slot.TotalGoalExposures,
                    targetId: slot.TargetId, imagingGoalId: slot.ImagingGoalId, panelId: slot.PanelId,
                    panelName: slot.PanelName, exposureTimeSeconds: slot.ExposureTimeSeconds);
                _ = _heartbeatService.ForceStatusUpdateAsync();
                await SwitchFilterAsync(filterToSwitch, progress, token);
                _currentFilter = filterToSwitch;
            }
            else if (!slot.ShouldAutomateFilterChanges)
            {
                Logger.Info("AstroManager Scheduler: Manual filter mode active - skipping NINA filter automation");
                if (slot.PreferSchedulerFilterForCaptureAttribution)
                {
                    _currentFilter = slot.Filter;
                    Logger.Info($"AstroManager Scheduler: Direct AstroManager manual filter mode active - using slot filter '{slot.Filter}' as current filter context");
                }
                else
                {
                    var ninaReportedFilter = GetCurrentNinaFilterName();
                    if (!string.IsNullOrWhiteSpace(ninaReportedFilter))
                    {
                        _currentFilter = ninaReportedFilter;
                    }
                }
            }
            else if (!slot.IsMono)
            {
                Logger.Info($"AstroManager Scheduler: Skipping filter switch for OSC camera (IsMono=false)");
                _currentFilter = GetCurrentNinaFilterName() ?? slot.NinaFilterName ?? slot.Filter;
            }
            else
            {
                // Mono camera but no usable filter was provided - keep state aligned with wheel.
                _currentFilter = GetCurrentNinaFilterName() ?? slot.NinaFilterName ?? slot.Filter;
            }

            if (isNewTargetContext)
            {
                await ExecuteEventContainerAsync(BeforeTargetContainer, progress, token);
            }

            // 3. START GUIDING if not already guiding and guider connected
            if (needsSlew && _guiderMediator.GetInfo().Connected)
            {
                _heartbeatService.SetCurrentState("Starting Guiding", slot.TargetName, slot.Filter, slot.CompletedExposures, slot.TotalGoalExposures,
                    targetId: slot.TargetId, imagingGoalId: slot.ImagingGoalId, panelId: slot.PanelId,
                    panelName: slot.PanelName, exposureTimeSeconds: slot.ExposureTimeSeconds);
                _ = _heartbeatService.ForceStatusUpdateAsync();
                StatusMessage = "Starting guiding";
                progress.Report(new ApplicationStatus { Status = "Starting guider..." });
                var startGuideItem = new StartGuiding(_guiderMediator);
                await startGuideItem.Execute(progress, token);
            }

            // 4. DITHER if needed (before exposure) - using local session-based counter
            // Dither logic: dither every N exposures since last slew/dither
            var guiderConnected = _guiderMediator.GetInfo().Connected;
            var ditherEveryX = slot.DitherEveryX > 0 ? slot.DitherEveryX : 0;
            var shouldDither = ditherEveryX > 0 && _exposuresSinceLastDither > 0 && (_exposuresSinceLastDither % ditherEveryX == 0);
            
            if (shouldDither && guiderConnected)
            {
                AddLogEntry($"Dithering (after {_exposuresSinceLastDither} exposures)", SchedulerLogLevel.Info);
                _heartbeatService.SetCurrentState("Dithering", slot.TargetName, slot.Filter, slot.CompletedExposures, slot.TotalGoalExposures,
                    targetId: slot.TargetId, imagingGoalId: slot.ImagingGoalId, panelId: slot.PanelId,
                    panelName: slot.PanelName, exposureTimeSeconds: slot.ExposureTimeSeconds);
                _ = _heartbeatService.ForceStatusUpdateAsync();
                StatusMessage = "Dithering...";
                progress.Report(new ApplicationStatus { Status = "Dithering..." });
                var ditherItem = new Dither(_guiderMediator, _profileService);
                await ditherItem.Execute(progress, token);
                _exposuresSinceLastDither = 0; // Reset counter after dither
                AddLogEntry("✅ Dither completed", SchedulerLogLevel.Success);
            }

            // Note: NINA handles triggers automatically via container execution flow
            // Triggers on parent containers will fire based on NINA's standard infrastructure

            // 5.5. Reset guider RMS graph/snapshot before each exposure when supported by the active guider provider.
            // This keeps per-exposure RMS values from being biased by older samples.
            if (_guiderMediator.GetInfo().Connected)
            {
                TryResetGuiderRmsGraphBeforeExposure();
            }

            // 5. TAKE EXPOSURE - await the status update to ensure AM receives it before exposure starts
            _heartbeatService.SetCurrentState("Exposing", slot.TargetName, slot.Filter, slot.CompletedExposures, slot.TotalGoalExposures,
                targetId: slot.TargetId, imagingGoalId: slot.ImagingGoalId, panelId: slot.PanelId,
                panelName: slot.PanelName, exposureTimeSeconds: slot.ExposureTimeSeconds);
            await _heartbeatService.ForceStatusUpdateAsync();
            StatusMessage = $"{slot.TargetName} - {slot.Filter} ({slot.CompletedExposures + 1}/{slot.TotalGoalExposures})";
            progress.Report(new ApplicationStatus { Status = $"Exposing {slot.Filter} - {slot.ExposureTimeSeconds}s" });
            
            // Generate unique capture ID before exposure (used for FITS header AM_UID)
            var captureId = SharedSchedulerState.Instance.GenerateNewCaptureId();
            
            // Create ExposureContainer (like TargetScheduler's PlanContainer)
            // This properly integrates with NINA's trigger system via base.Execute()
            _exposureContainer ??= new ExposureContainer(
                this,
                _profileService,
                _cameraMediator,
                _imagingMediator,
                _imageSaveMediator,
                _imageHistoryVM);

            _exposureContainer.ConfigureExposure(slot);
            
            // Execute via container - this calls base.Execute() which handles triggers properly
            AddLogEntry($"📷 Exposure started: {slot.Filter} {slot.ExposureTimeSeconds}s", SchedulerLogLevel.Info);
            var exposureTask = _exposureContainer.Execute(progress, token);

            if (runtimePolicy != null
                && slot.ExposureTimeSeconds > ExposureSafetyCheckInterval.TotalSeconds
                && ShouldRunDuringExposureSafetyChecks(runtimePolicy))
            {
                while (!exposureTask.IsCompleted)
                {
                    var completed = await Task.WhenAny(exposureTask, Task.Delay(ExposureSafetyCheckInterval, token));
                    if (completed == exposureTask)
                    {
                        break;
                    }

                    var exposureHandled = await EvaluateRuntimeStopChecksAsync(
                        runtimePolicy,
                        slot,
                        RuntimeSafetyEvaluationPhase.DuringExposure,
                        progress,
                        token);

                    if (exposureHandled)
                    {
                        try
                        {
                            await exposureTask;
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"[SAFETY-CHECK] Exposure finished with error after in-exposure safety action: {ex.Message}");
                        }

                        return;
                    }
                }
            }

            await exposureTask;
            
            // Mark exposure as complete - image has been saved, OnImageSaved has fired
            SharedSchedulerState.Instance.MarkExposureComplete();

            await WaitForCaptureMetricsIfRequiredAsync(runtimePolicy, captureId, token);
            
            SessionExposuresTaken++;
            TotalExposuresTaken++;
            _exposuresSinceLastDither++; // Increment dither counter
            
            // Execute AfterEachExposureContainer (like TargetScheduler does)
            // This runs user-configured instructions like HFR logging, custom processing
            await ExecuteEventContainerAsync(AfterEachExposureContainer, progress, token);

            // 6. REPORT COMPLETION to API
            var targetCompleted = await ReportSlotCompletedAsync(slot, true);
            
            // 7. FORCE STATUS UPDATE to immediately reflect new progress in Remote Control UI
            // Keep target info but clear imaging goal ID to indicate we're between exposures
            _heartbeatService.SetCurrentState("Exposure Complete", slot.TargetName, slot.Filter, slot.CompletedExposures + 1, slot.TotalGoalExposures,
                targetId: slot.TargetId, imagingGoalId: null, panelId: slot.PanelId,
                panelName: slot.PanelName, exposureTimeSeconds: null);
            await _heartbeatService.ForceStatusUpdateAsync();

            await ExecuteEventContainerAsync(AfterEachTargetContainer, progress, token);

            if (targetCompleted)
            {
                await ExecuteEventContainerAsync(AfterTargetCompleteContainer, progress, token);
            }
            
            // Note: _currentTargetId, _currentPanelId, etc. already set at slot START for error handling
        }

        /// <summary>
        /// Slew and center on coordinates
        /// </summary>
        private async Task SlewToCoordinatesAsync(NextSlotDto slot, IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            var raAngle = Angle.ByHours(slot.RightAscensionHours);
            var decAngle = Angle.ByDegree(slot.DeclinationDegrees);
            var coordinates = new Coordinates(raAngle, decAngle, Epoch.J2000);
            var inputCoordinates = new InputCoordinates(coordinates);

            await WaitForMountIdleAsync(token);

            // Stop guiding before slew
            if (_guiderMediator.GetInfo().Connected)
            {
                var stopGuideItem = new StopGuiding(_guiderMediator);
                await stopGuideItem.Execute(progress, token);
            }

            // Slew - set state BEFORE slew starts
            StatusMessage = $"Slewing to {slot.TargetName}";
            progress.Report(new ApplicationStatus { Status = $"Slewing to {slot.TargetName}..." });
            
            _heartbeatService.SetCurrentState("Slewing", slot.TargetName, slot.Filter, slot.CompletedExposures, slot.TotalGoalExposures,
                targetId: slot.TargetId, imagingGoalId: slot.ImagingGoalId, panelId: slot.PanelId,
                panelName: slot.PanelName, exposureTimeSeconds: slot.ExposureTimeSeconds);
            await _heartbeatService.ForceStatusUpdateAsync();
            
            var slewItem = new SlewScopeToRaDec(_telescopeMediator, _guiderMediator);
            slewItem.Coordinates = inputCoordinates;
            await slewItem.Execute(progress, token);
            
            token.ThrowIfCancellationRequested();

            // Center with plate solve - update state to show centering
            StatusMessage = $"Centering on {slot.TargetName}";
            progress.Report(new ApplicationStatus { Status = $"Centering on {slot.TargetName}..." });
            
            // Update heartbeat to show centering state (preserve target context)
            _heartbeatService.SetCurrentState("Centering", slot.TargetName, slot.Filter, slot.CompletedExposures, slot.TotalGoalExposures,
                targetId: slot.TargetId, imagingGoalId: slot.ImagingGoalId, panelId: slot.PanelId,
                panelName: slot.PanelName, exposureTimeSeconds: slot.ExposureTimeSeconds);
            await _heartbeatService.ForceStatusUpdateAsync();
            
            // If rotator and PA specified, use CenterAndRotate
            if (slot.PositionAngle.HasValue && _rotatorMediator.GetInfo().Connected)
            {
                var centerRotateItem = new CenterAndRotate(
                    _profileService, _telescopeMediator, _imagingMediator, _rotatorMediator,
                    _filterWheelMediator, _guiderMediator, _domeMediator, _domeFollower, 
                    _plateSolverFactory, _windowServiceFactory);
                centerRotateItem.Coordinates = inputCoordinates;
                centerRotateItem.PositionAngle = slot.PositionAngle.Value;
                await centerRotateItem.Execute(progress, token);
            }
            else
            {
                var centerItem = new Center(
                    _profileService, _telescopeMediator, _imagingMediator, _filterWheelMediator, 
                    _guiderMediator, _domeMediator, _domeFollower, _plateSolverFactory, _windowServiceFactory);
                centerItem.Coordinates = inputCoordinates;
                await centerItem.Execute(progress, token);
            }
            
            // Update state after centering complete
            _heartbeatService.SetCurrentState("Centered", slot.TargetName, slot.Filter, slot.CompletedExposures, slot.TotalGoalExposures,
                targetId: slot.TargetId, imagingGoalId: slot.ImagingGoalId, panelId: slot.PanelId,
                panelName: slot.PanelName, exposureTimeSeconds: slot.ExposureTimeSeconds);
            _ = _heartbeatService.ForceStatusUpdateAsync();
        }

        private async Task WaitForMountIdleAsync(CancellationToken token)
        {
            try
            {
                const int maxWaitSeconds = 120;
                const int pollMs = 500;
                var waitedMs = 0;

                while (!token.IsCancellationRequested)
                {
                    var telescopeInfo = _telescopeMediator.GetInfo();
                    if (telescopeInfo == null || !telescopeInfo.Connected || !telescopeInfo.Slewing)
                    {
                        return;
                    }

                    if (waitedMs == 0)
                    {
                        Logger.Warning("[SLEW-GUARD] Mount still reports slewing before new slew command; waiting for mount to become idle");
                    }

                    if (waitedMs >= maxWaitSeconds * 1000)
                    {
                        Logger.Warning($"[SLEW-GUARD] Mount still slewing after {maxWaitSeconds}s; proceeding with slew command");
                        return;
                    }

                    await Task.Delay(pollMs, token);
                    waitedMs += pollMs;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[SLEW-GUARD] Failed while waiting for mount idle: {ex.Message}");
            }
        }

        /// <summary>
        /// Switch to specified filter
        /// </summary>
        private async Task SwitchFilterAsync(string filterName, IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            StatusMessage = $"Switching to {filterName}";
            progress.Report(new ApplicationStatus { Status = $"Switching to filter {filterName}..." });
            
            var switchFilterItem = new SwitchFilter(_profileService, _filterWheelMediator);
            var availableFilters = _profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
            if (availableFilters != null && availableFilters.Any())
            {
                var availableFilterNames = string.Join(", ", availableFilters.Select(f => f.Name));
                Logger.Debug($"[FILTER-SWITCH] Looking for filter '{filterName}' in NINA profile. Available: [{availableFilterNames}]");
                
                var matchingFilter = availableFilters.FirstOrDefault(f => f.Name.Equals(filterName, StringComparison.OrdinalIgnoreCase));
                if (matchingFilter != null)
                {
                    Logger.Info($"[FILTER-SWITCH] Found matching filter: '{matchingFilter.Name}'");
                    switchFilterItem.Filter = matchingFilter;
                }
                else
                {
                    // Filter not found - log error with available filters to help diagnose mapping issues
                    Logger.Error($"[FILTER-SWITCH] Filter '{filterName}' NOT FOUND in NINA profile! Available filters: [{availableFilterNames}]. " +
                        "Check your filter name mappings in AstroManager Equipment settings.");
                    AddLogEntry($"⚠️ Filter '{filterName}' not found in NINA. Available: {availableFilterNames}", SchedulerLogLevel.Warning);
                    throw new InvalidOperationException($"Filter '{filterName}' not found in NINA profile. Available filters: [{availableFilterNames}]. " +
                        "Please configure filter name mappings in AstroManager Equipment settings.");
                }
            }
            else
            {
                Logger.Warning("[FILTER-SWITCH] No filters configured in NINA profile");
                throw new InvalidOperationException("No filters configured in NINA profile");
            }
            
            await switchFilterItem.Execute(progress, token);
        }
        
        /// <summary>
        /// Execute guider calibration (user-triggered mid-session)
        /// </summary>
        private async Task ExecuteGuiderCalibrationAsync(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            Logger.Info("AstroManager Scheduler: Executing guider calibration");
            StatusMessage = "Calibrating guider...";
            progress.Report(new ApplicationStatus { Status = "Calibrating guider..." });
            
            var info = _guiderMediator.GetInfo();
            if (!info.Connected)
            {
                Logger.Warning("Guider calibration skipped - guider not connected");
                AddLogEntry("⚠️ Guider calibration skipped - not connected", SchedulerLogLevel.Warning);
                return;
            }
            
            // Stop guiding first if active
            await _guiderMediator.StopGuiding(token);
            
            // Start guiding with forceCalibration=true to force a new calibration
            await _guiderMediator.StartGuiding(true, progress, token);
            
            Logger.Info("AstroManager Scheduler: Guider calibration complete");
        }
        
        /// <summary>
        /// Execute autofocus (user-triggered mid-session)
        /// </summary>
        private async Task ExecuteAutofocusAsync(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            Logger.Info("AstroManager Scheduler: Executing autofocus");
            StatusMessage = "Running autofocus...";
            progress.Report(new ApplicationStatus { Status = "Running autofocus..." });
            
            // Stop guiding before autofocus
            if (_guiderMediator.GetInfo().Connected)
            {
                await _guiderMediator.StopGuiding(token);
            }
            
            // Create AutoFocusVM and run autofocus (same approach as AstroManagerPlugin)
            var autofocusVM = _autoFocusVMFactory.Create();
            
            // Get current filter for autofocus
            var currentFilter = _filterWheelMediator.GetInfo()?.SelectedFilter;
            
            // Show AF window and run autofocus
            var windowService = _windowServiceFactory.Create();
            windowService.Show(autofocusVM, "AutoFocus", System.Windows.ResizeMode.CanResize, System.Windows.WindowStyle.ToolWindow);
            
            var afResult = await autofocusVM.StartAutoFocus(currentFilter, token, progress);
            
            if (afResult != null)
            {
                Logger.Info($"AstroManager Scheduler: Autofocus complete - Position: {afResult.CalculatedFocusPoint?.Position}, Temperature: {afResult.Temperature:F1}C");
            }
            else
            {
                Logger.Warning("AstroManager Scheduler: Autofocus returned null result");
            }
            
            // Restart guiding after autofocus
            if (_guiderMediator.GetInfo().Connected)
            {
                await _guiderMediator.StartGuiding(false, progress, token);
            }
            
            Logger.Info("AstroManager Scheduler: Autofocus complete");
        }

        /// <summary>
        /// Report slot completion - increments goal progress immediately after shot
        /// Thumbnail upload happens separately via OnImageSaved, matching via ImagingGoalId
        /// </summary>
        private async Task<bool> ReportSlotCompletedAsync(NextSlotDto slot, bool success)
        {
            try
            {
                Logger.Info($"[EXPOSURE-COMPLETE] {slot.TargetName}/{slot.Filter} - Success={success}, Goal={slot.ImagingGoalId}");
                var targetCompleted = false;
                
                // Check if we're in offline mode - if so, queue capture for later sync
                var isOffline = _offlineCalculator.ShouldUseOfflineMode();
                
                if (isOffline)
                {
                    Logger.Info($"[OFFLINE] Queuing capture for {slot.TargetName}/{slot.Filter}");
                    
                    await _apiClient.QueueCaptureAsync(new OfflineCaptureDto
                    {
                        Id = Guid.NewGuid(),
                        CapturedAt = DateTime.UtcNow,
                        TargetId = slot.TargetId,
                        ImagingGoalId = slot.ImagingGoalId,
                        PanelId = slot.PanelId,
                        Filter = slot.Filter ?? "",
                        ExposureTimeSeconds = slot.ExposureTimeSeconds,
                        Success = success
                    });
                    
                    // Record progress locally for offline slot calculation
                    if (slot.TargetId.HasValue && slot.ImagingGoalId.HasValue)
                    {
                        _offlineCalculator.RecordOfflineExposure(slot.TargetId.Value, slot.ImagingGoalId.Value);
                        targetCompleted = UpdateLocalTargetStoreProgressAndCheckCompletion(
                            slot.TargetId.Value,
                            slot.PanelId,
                            slot.ImagingGoalId.Value,
                            slot.CompletedExposures + 1);
                    }
                    
                    AddLogEntry($"[OFFLINE] Capture queued: {slot.TargetName}/{slot.Filter}", SchedulerLogLevel.Info);
                }
                else
                {
                    // Online mode - increment goal progress immediately after shot
                    // Thumbnail will be uploaded separately via OnImageSaved, matched by ImagingGoalId
                    if (success && slot.TargetId.HasValue && slot.ImagingGoalId.HasValue)
                    {
                        var request = new ExposureCompleteDto
                        {
                            TargetId = slot.TargetId.Value,
                            ImagingGoalId = slot.ImagingGoalId.Value,
                            PanelId = slot.PanelId,
                            Filter = slot.Filter ?? "",
                            ExposureTimeSeconds = slot.ExposureTimeSeconds,
                            Success = true
                        };
                        
                        var response = await _apiClient.ReportExposureCompleteAsync(request);
                        if (response?.Acknowledged == true)
                        {
                            Logger.Info($"[EXPOSURE-COMPLETE] Progress updated: {response.NewCompletedCount}/{response.TotalGoalCount} ({response.CompletionPercentage}%)");
                            targetCompleted = UpdateLocalTargetStoreProgressAndCheckCompletion(
                                slot.TargetId.Value,
                                slot.PanelId,
                                slot.ImagingGoalId.Value,
                                response.NewCompletedCount);
                        }
                        else
                        {
                            Logger.Warning($"[EXPOSURE-COMPLETE] Failed to update progress: {response?.Message}");
                        }
                    }
                    
                    // Also try to sync any queued offline captures
                    _ = TrySyncOfflineCapturesAsync();
                }

                return targetCompleted;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager Scheduler: Failed to report exposure: {ex.Message}");
                return false;
            }
        }

        private bool UpdateLocalTargetStoreProgressAndCheckCompletion(
            Guid targetId,
            Guid? panelId,
            Guid imagingGoalId,
            int completedExposures)
        {
            try
            {
                var target = _targetStore.GetTarget(targetId);
                if (target == null)
                {
                    return false;
                }

                var updated = false;

                if (panelId.HasValue)
                {
                    var panel = target.Panels.FirstOrDefault(p => p.Id == panelId.Value);
                    var panelGoal = panel?.ImagingGoals.FirstOrDefault(g => g.Id == imagingGoalId);
                    if (panelGoal != null)
                    {
                        panelGoal.CompletedExposures = completedExposures;
                        updated = true;
                    }

                    if (panel != null)
                    {
                        panel.IsCompleted = panel.ImagingGoals
                            .Where(g => g.IsEnabled)
                            .All(g => g.IsCompleted);
                        panel.CompletionPercentage = panel.ImagingGoals
                            .Where(g => g.IsEnabled)
                            .DefaultIfEmpty()
                            .Average(g => g == null ? 100 : g.CompletionPercentage);
                    }
                }
                else
                {
                    var goal = target.ImagingGoals.FirstOrDefault(g => g.Id == imagingGoalId);
                    if (goal != null)
                    {
                        goal.CompletedExposures = completedExposures;
                        updated = true;
                    }
                }

                if (!updated)
                {
                    return false;
                }

                _targetStore.UpdateTarget(target);

                var targetCompleted = IsTargetCompleted(target);
                if (targetCompleted && !_completedTargetEventsRaised.Contains(targetId))
                {
                    _completedTargetEventsRaised.Add(targetId);
                    AddLogEntry($"Target complete: {target.Name}", SchedulerLogLevel.Success);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager Scheduler: Failed to update local target completion state: {ex.Message}");
                return false;
            }
        }

        private static bool IsTargetCompleted(ScheduledTargetDto target)
        {
            if (target.IsMosaic && target.HasPanels)
            {
                return target.Panels
                    .Where(p => p.IsEnabled)
                    .All(p => p.ImagingGoals.Where(g => g.IsEnabled).All(g => g.IsCompleted));
            }

            return target.ImagingGoals
                .Where(g => g.IsEnabled)
                .All(g => g.IsCompleted);
        }

        /// <summary>
        /// Try to reconnect to API when in offline mode.
        /// If successful, syncs offline captures and returns true.
        /// </summary>
        private async Task<bool> TryReconnectAsync(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            try
            {
                progress?.Report(new ApplicationStatus { Status = "Attempting to reconnect to API..." });
                
                // Try a simple API call to test connectivity
                var currentFilterForApi = GetCurrentNinaFilterName();
                var slot = await _apiClient.GetNextSlotAsync(
                    GetEffectiveConfigurationId(),
                    _currentTargetId,
                    _currentPanelId,
                    currentFilterForApi);
                
                if (slot != null)
                {
                    // Connection restored! Report success to offline calculator
                    _offlineCalculator.ReportApiResult(true);
                    
                    Logger.Info("[RECONNECT] API connection restored!");
                    
                    // Sync any offline captures we accumulated
                    await TrySyncOfflineCapturesAsync();
                    
                    // Refresh target store with latest data
                    try
                    {
                        var result = await _apiClient.SyncScheduledTargetsAsync();
                        if (result.Success && result.Targets?.Any() == true)
                        {
                            _targetStore.UpdateTargets(result.Targets);
                            Logger.Info($"[RECONNECT] Refreshed target store with {result.Targets.Count} targets");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[RECONNECT] Failed to refresh targets: {ex.Message}");
                        // Continue anyway - we're back online
                    }
                    
                    return true;
                }
                else
                {
                    // API returned null - still having issues
                    _offlineCalculator.ReportApiResult(false);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[RECONNECT] Reconnection attempt failed: {ex.Message}");
                _offlineCalculator.ReportApiResult(false);
                return false;
            }
        }
        
        private async Task TrySyncOfflineCapturesAsync()
        {
            try
            {
                var unsyncedCaptures = await _apiClient.GetUnsyncedCapturesAsync();
                if (!unsyncedCaptures.Any())
                    return;
                
                // Log details about what we're syncing for debugging
                Logger.Info($"[OFFLINE-SYNC] Found {unsyncedCaptures.Count} captures to sync:");
                foreach (var cap in unsyncedCaptures.Take(10)) // Log first 10
                {
                    Logger.Info($"[OFFLINE-SYNC]   - CapturedAt={cap.CapturedAt:HH:mm:ss}, Target={cap.TargetId?.ToString()?.Substring(0,8) ?? "null"}, Goal={cap.ImagingGoalId?.ToString()?.Substring(0,8) ?? "null"}, Filter={cap.Filter}");
                }
                AddLogEntry($"Syncing {unsyncedCaptures.Count} offline captures...", SchedulerLogLevel.Info);
                
                var syncedIds = new List<Guid>();
                
                foreach (var capture in unsyncedCaptures)
                {
                    try
                    {
                        var request = new ExposureCompleteDto
                        {
                            TargetId = capture.TargetId ?? Guid.Empty,
                            ImagingGoalId = capture.ImagingGoalId ?? Guid.Empty,
                            PanelId = capture.PanelId,
                            Filter = capture.Filter ?? "",
                            ExposureTimeSeconds = (int)(capture.ExposureTimeSeconds ?? 0),
                            ImagePath = capture.FileName,
                            ImageMetadata = new ImageMetadataDto
                            {
                                HFR = capture.HFR,
                                StarCount = capture.DetectedStars
                            },
                            Success = capture.Success
                        };
                        
                        var response = await _apiClient.ReportExposureCompleteAsync(request);
                        if (response?.Acknowledged == true)
                        {
                            syncedIds.Add(capture.Id);
                            Logger.Debug($"[OFFLINE-SYNC] Synced capture {capture.Id}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[OFFLINE-SYNC] Failed to sync capture {capture.Id}: {ex.Message}");
                        break; // Stop if API is failing again
                    }
                }
                
                if (syncedIds.Any())
                {
                    await _apiClient.MarkCapturesSyncedAsync(syncedIds);
                    Logger.Info($"[OFFLINE-SYNC] Successfully synced {syncedIds.Count} captures");
                    AddLogEntry($"Synced {syncedIds.Count} offline captures", SchedulerLogLevel.Success);
                    
                    // Clear offline progress tracking since we've synced
                    _offlineCalculator.ClearOfflineProgress();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[OFFLINE-SYNC] Error during sync: {ex.Message}");
            }
        }

        /// <summary>
        /// Execute a custom event container (like TargetScheduler's pattern).
        /// This allows users to add custom instructions that run at specific times.
        /// </summary>
        private async Task ExecuteEventContainerAsync(EventInstructionContainer container, IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            if (container == null || container.Items?.Count == 0)
            {
                return;
            }
            
            try
            {
                // Build list of instruction names for logging
                var instructionNames = container.Items
                    .Select(item => item.Name ?? item.GetType().Name)
                    .ToList();
                var instructionList = string.Join(", ", instructionNames);
                
                Logger.Info($"Executing event container '{container.Name}' with {container.Items.Count} items: [{instructionList}]");
                AddLogEntry($"Running {container.Name}: {instructionList}", SchedulerLogLevel.Info);
                
                // Reset parent to ensure items can find the DSO Target
                container.ResetParent(this);
                InjectCoordinatesIntoContainer(container);
                
                await container.Execute(progress, token);
                
                Logger.Info($"Event container '{container.Name}' completed successfully");
                AddLogEntry($"Completed {container.Name} ({container.Items.Count} instructions)", SchedulerLogLevel.Info);
                
                // Reset for next execution
                container.ResetAll();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error executing event container '{container.Name}': {ex.Message}");
                AddLogEntry($"{container.Name} error: {ex.Message}", SchedulerLogLevel.Warning);
                // Don't fail the sequence due to event container errors
            }
        }

        private void InjectCoordinatesIntoContainer(ISequenceContainer container)
        {
            if (Target?.InputCoordinates == null || container?.Items == null || container.Items.Count == 0)
            {
                return;
            }

            foreach (var item in container.Items)
            {
                if (item is SlewScopeToRaDec slewScopeToRaDec)
                {
                    slewScopeToRaDec.Coordinates = Target.InputCoordinates;
                    slewScopeToRaDec.Inherited = true;
                    slewScopeToRaDec.SequenceBlockInitialize();
                }

                if (item is Center center)
                {
                    center.Coordinates = Target.InputCoordinates;
                    center.Inherited = true;
                    center.SequenceBlockInitialize();
                }

                if (item is CenterAndRotate centerAndRotate)
                {
                    centerAndRotate.Coordinates = Target.InputCoordinates;
                    centerAndRotate.Inherited = true;
                    centerAndRotate.SequenceBlockInitialize();
                }

                if (item is ISequenceContainer subContainer)
                {
                    InjectCoordinatesIntoContainer(subContainer);
                }
            }
        }

        /// <summary>
        /// Inject target coordinates into CenterAfterDrift triggers in parent containers.
        /// This is required because CenterAfterDrift needs to know the original target coordinates
        /// to calculate drift. Target Scheduler does this same coordinate injection.
        /// </summary>
        private void InjectCoordinatesIntoCenterAfterDriftTriggers(NextSlotDto slot)
        {
            try
            {
                if (slot.RightAscensionHours == 0 && slot.DeclinationDegrees == 0)
                {
                    Logger.Debug("[CAD-INJECT] Cannot inject coordinates - slot has no RA/Dec");
                    return;
                }
                
                var targetCoords = new Coordinates(
                    Angle.ByHours(slot.RightAscensionHours),
                    Angle.ByDegree(slot.DeclinationDegrees),
                    Epoch.J2000);
                
                Logger.Debug($"[CAD-INJECT] Injecting coordinates RA={slot.RightAscensionHours:F5}h Dec={slot.DeclinationDegrees:F4}° into CenterAfterDrift triggers");
                
                // Walk up the parent hierarchy to find CenterAfterDrift triggers
                var parent = this.Parent;
                int injectedCount = 0;
                
                while (parent != null)
                {
                    if (parent is ITriggerable triggerable)
                    {
                        foreach (var trigger in triggerable.GetTriggersSnapshot())
                        {
                            var typeName = trigger.GetType().Name;
                            
                            // Check if this is a CenterAfterDrift trigger
                            if (typeName.Contains("CenterAfterDrift") || typeName.Contains("CenterAfterDriftTrigger"))
                            {
                                try
                                {
                                    // CenterAfterDriftTrigger has:
                                    // - Coordinates property of type InputCoordinates
                                    // - InputCoordinates has a Coordinates property of type Coordinates
                                    // - Inherited property (bool) - must be true to avoid "No Target Set" error
                                    
                                    var inputCoordsProperty = trigger.GetType().GetProperty("Coordinates");
                                    if (inputCoordsProperty != null)
                                    {
                                        var inputCoords = inputCoordsProperty.GetValue(trigger);
                                        if (inputCoords != null)
                                        {
                                            // Set the inner Coordinates property on InputCoordinates
                                            var innerCoordsProperty = inputCoords.GetType().GetProperty("Coordinates");
                                            if (innerCoordsProperty != null && innerCoordsProperty.CanWrite)
                                            {
                                                innerCoordsProperty.SetValue(inputCoords, targetCoords);
                                                Logger.Debug($"[CAD-INJECT] Set InputCoordinates.Coordinates = RA={slot.RightAscensionHours:F5}h Dec={slot.DeclinationDegrees:F4}°");
                                                
                                                // Set Inherited = true to prevent "No Target Set" validation error
                                                var inheritedProperty = trigger.GetType().GetProperty("Inherited");
                                                if (inheritedProperty != null && inheritedProperty.CanWrite)
                                                {
                                                    inheritedProperty.SetValue(trigger, true);
                                                    Logger.Debug($"[CAD-INJECT] Set Inherited = true");
                                                }
                                                
                                                injectedCount++;
                                                Logger.Debug($"[CAD-INJECT] Successfully injected coordinates into {typeName}");
                                            }
                                            else
                                            {
                                                Logger.Debug($"[CAD-INJECT] InputCoordinates.Coordinates property not writable");
                                            }
                                        }
                                        else
                                        {
                                            Logger.Debug($"[CAD-INJECT] {typeName}.Coordinates (InputCoordinates) is null");
                                        }
                                    }
                                    else
                                    {
                                        Logger.Debug($"[CAD-INJECT] Could not find Coordinates property on {typeName}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Debug($"[CAD-INJECT] Failed to inject coordinates into {typeName}: {ex.Message}");
                                }
                            }
                        }
                    }
                    
                    parent = parent.Parent;
                }
                
                if (injectedCount > 0)
                {
                    Logger.Debug($"[CAD-INJECT] Injected coordinates into {injectedCount} CenterAfterDrift trigger(s)");
                }
                else
                {
                    Logger.Debug("[CAD-INJECT] No CenterAfterDrift triggers found in parent hierarchy");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[CAD-INJECT] Error during coordinate injection: {ex.Message}");
            }
        }

        /// <summary>
        /// Evaluate configured runtime safety/stop conditions between slots.
        /// Returns true when the scheduler already handled the violation action for this cycle.
        /// </summary>
        private Dictionary<string, object?> BuildRuntimeSafetyMetricValues(NextSlotDto slot, RuntimeSafetyEvaluationPhase evaluationPhase)
        {
            var metrics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            var safetyInfo = _safetyMonitorMediator.GetInfo();
            var guiderInfo = _guiderMediator.GetInfo();
            var weatherInfo = _weatherDataMediator.GetInfo();
            var telescopeInfo = _telescopeMediator.GetInfo();
            var cameraInfo = _cameraMediator.GetInfo();

            var guidingRms = guiderInfo?.Connected == true
                ? (double?)guiderInfo.RMSError?.Total?.Arcseconds
                : null;
            var rawGuiderGuiding = TryGetGuiderGuidingState(guiderInfo);
            var rawGuiderSettling = TryGetGuiderSettlingState(guiderInfo);

            bool? guiderGuiding;
            bool? guiderSettling;
            if (evaluationPhase == RuntimeSafetyEvaluationPhase.DuringExposure)
            {
                guiderGuiding = GetStableGuiderState(
                    rawGuiderGuiding,
                    ref _lastGuiderGuidingSample,
                    ref _guiderGuidingSampleCount,
                    ref _stableGuiderGuidingState,
                    GuiderStateConfirmationSamples);

                guiderSettling = GetStableGuiderState(
                    rawGuiderSettling,
                    ref _lastGuiderSettlingSample,
                    ref _guiderSettlingSampleCount,
                    ref _stableGuiderSettlingState,
                    GuiderStateConfirmationSamples);
            }
            else
            {
                ResetGuiderStateStabilization();
                guiderGuiding = rawGuiderGuiding;
                guiderSettling = rawGuiderSettling;
            }

            _heartbeatService.TryGetLastCaptureMetrics(out var lastCaptureHfr, out var lastCaptureStarCount, out var lastCaptureAtUtc);
            var hasFreshLastCapture = lastCaptureAtUtc.HasValue
                && (DateTime.UtcNow - lastCaptureAtUtc.Value) <= LastCaptureMetricMaxAge;

            var needsLastCaptureFallback = !hasFreshLastCapture
                || !lastCaptureHfr.HasValue
                || !lastCaptureStarCount.HasValue;

            if (needsLastCaptureFallback)
            {
                if (TryGetLastCaptureMetricsFromImageHistoryVm(out var fallbackHfr, out var fallbackStarCount, out var fallbackCapturedAtUtc))
                {
                    var fallbackFresh = !fallbackCapturedAtUtc.HasValue
                        || (DateTime.UtcNow - fallbackCapturedAtUtc.Value) <= LastCaptureMetricMaxAge;

                    if (fallbackFresh)
                    {
                        if (!hasFreshLastCapture || !lastCaptureHfr.HasValue)
                        {
                            lastCaptureHfr = fallbackHfr;
                        }

                        if (!hasFreshLastCapture || !lastCaptureStarCount.HasValue)
                        {
                            lastCaptureStarCount = fallbackStarCount;
                        }

                        if (!lastCaptureAtUtc.HasValue)
                        {
                            lastCaptureAtUtc = fallbackCapturedAtUtc;
                        }
                    }
                    else
                    {
                        lastCaptureHfr = null;
                        lastCaptureStarCount = null;
                    }
                }
                else
                {
                    lastCaptureHfr = null;
                    lastCaptureStarCount = null;
                }
            }

            var weatherConnected = weatherInfo?.Connected == true;
            var cloudCoverPercent = weatherConnected
                ? SanitizeFiniteDouble(TryReadDoubleProperty(weatherInfo!, "CloudCover", "CloudCoverPercent", "Cloudiness"))
                : null;
            var rainRate = weatherConnected
                ? SanitizeFiniteDouble(TryReadDoubleProperty(weatherInfo!, "RainRate", "Rain", "PrecipitationRate"))
                : null;
            var weatherSkyQuality = weatherConnected
                ? SanitizeFiniteDouble(TryReadDoubleProperty(weatherInfo!, "SkyQuality", "SQM", "SkyBrightness"))
                : null;

            metrics["safetyMonitorSafe"] = safetyInfo?.Connected == true ? (bool?)safetyInfo.IsSafe : null;
            metrics["guidingRmsArcSec"] = guidingRms;
            metrics["lastCaptureHfr"] = lastCaptureHfr;
            metrics["lastCaptureStarCount"] = lastCaptureStarCount;
            metrics["cloudCoverPercent"] = cloudCoverPercent;
            metrics["cloudCover"] = cloudCoverPercent;
            metrics["weatherCloudCover"] = cloudCoverPercent;
            metrics["rainRate"] = rainRate;
            metrics["weatherRainRate"] = rainRate;
            metrics["weatherSkyQuality"] = weatherSkyQuality;
            metrics["skyQuality"] = weatherSkyQuality;
            metrics["sqm"] = weatherSkyQuality;
            metrics["mountAltitudeDegrees"] = telescopeInfo?.Connected == true ? telescopeInfo.Altitude : null;
            metrics["coolerPowerPercent"] = cameraInfo?.Connected == true ? cameraInfo.CoolerPower : null;

            metrics["guiderConnected"] = guiderInfo?.Connected;
            metrics["guiderGuiding"] = guiderGuiding;
            metrics["guiderSettling"] = guiderSettling;
            metrics["cameraConnected"] = cameraInfo?.Connected;
            metrics["mountConnected"] = telescopeInfo?.Connected;
            metrics["safetyMonitorConnected"] = safetyInfo?.Connected;
            metrics["weatherConnected"] = weatherInfo?.Connected;

            return metrics;
        }

        private bool TryGetLastCaptureMetricsFromImageHistoryVm(out double? hfr, out int? starCount, out DateTime? capturedAtUtc)
        {
            hfr = null;
            starCount = null;
            capturedAtUtc = null;

            try
            {
                var history = _imageHistoryVM?.ImageHistory;
                if (history == null)
                {
                    return false;
                }

                object? bestEntry = null;
                DateTime? bestCapturedAtUtc = null;
                double? bestHfr = null;
                int? bestStarCount = null;

                foreach (var entry in history)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    var capturedAt = TryReadDateTimeProperty(entry, "CapturedAt", "Date", "DateTime", "Timestamp", "CreatedAt");
                    var entryCapturedAtUtc = capturedAt.HasValue
                        ? capturedAt.Value.Kind switch
                        {
                            DateTimeKind.Utc => capturedAt.Value,
                            DateTimeKind.Local => capturedAt.Value.ToUniversalTime(),
                            _ => DateTime.SpecifyKind(capturedAt.Value, DateTimeKind.Local).ToUniversalTime()
                        }
                        : (DateTime?)null;

                    var entryHfr = TryReadDoubleProperty(entry, "HFR", "Hfr", "HFRMean", "HfrMean");
                    var entryStarCount = TryReadIntProperty(entry, "DetectedStars", "StarCount", "Stars", "StarNumber");
                    if (!entryHfr.HasValue && !entryStarCount.HasValue)
                    {
                        continue;
                    }

                    if (bestEntry == null)
                    {
                        bestEntry = entry;
                        bestCapturedAtUtc = entryCapturedAtUtc;
                        bestHfr = entryHfr;
                        bestStarCount = entryStarCount;
                        continue;
                    }

                    if (entryCapturedAtUtc.HasValue
                        && (!bestCapturedAtUtc.HasValue || entryCapturedAtUtc.Value > bestCapturedAtUtc.Value))
                    {
                        bestEntry = entry;
                        bestCapturedAtUtc = entryCapturedAtUtc;
                        bestHfr = entryHfr;
                        bestStarCount = entryStarCount;
                    }
                }

                if (bestEntry == null)
                {
                    return false;
                }

                hfr = bestHfr;
                starCount = bestStarCount;
                capturedAtUtc = bestCapturedAtUtc;

                return hfr.HasValue || starCount.HasValue;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[SAFETY-CHECK] Could not read last capture metrics from image history VM: {ex.Message}");
                return false;
            }
        }

        private static double? SanitizeFiniteDouble(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                return null;
            }

            return value;
        }

        private static bool? GetStableGuiderState(
            bool? rawValue,
            ref bool? lastSample,
            ref int sampleCount,
            ref bool? stableState,
            int requiredSamples)
        {
            if (!rawValue.HasValue)
            {
                lastSample = null;
                sampleCount = 0;
                return stableState;
            }

            if (lastSample == rawValue)
            {
                sampleCount++;
            }
            else
            {
                lastSample = rawValue;
                sampleCount = 1;
            }

            if (sampleCount >= requiredSamples)
            {
                stableState = rawValue;
            }

            return stableState;
        }

        private void ResetGuiderStateStabilization()
        {
            _lastGuiderGuidingSample = null;
            _guiderGuidingSampleCount = 0;
            _stableGuiderGuidingState = null;
            _lastGuiderSettlingSample = null;
            _guiderSettlingSampleCount = 0;
            _stableGuiderSettlingState = null;
        }

        private static bool? TryGetGuiderGuidingState(object? guiderInfo)
        {
            if (guiderInfo == null)
            {
                return null;
            }

            var direct = TryReadBoolProperty(guiderInfo, "IsGuiding", "Guiding", "GuideActive");
            if (direct.HasValue)
            {
                return direct;
            }

            var state = TryReadStringProperty(guiderInfo, "State", "GuidingState", "Status");
            if (string.IsNullOrWhiteSpace(state))
            {
                return null;
            }

            var normalized = state.ToLowerInvariant();
            if (normalized.Contains("guid"))
            {
                return true;
            }

            if (normalized.Contains("idle")
                || normalized.Contains("stop")
                || normalized.Contains("disconnect")
                || normalized.Contains("notguid"))
            {
                return false;
            }

            return null;
        }

        private static bool? TryGetGuiderSettlingState(object? guiderInfo)
        {
            if (guiderInfo == null)
            {
                return null;
            }

            var direct = TryReadBoolProperty(guiderInfo, "IsSettling", "Settling");
            if (direct.HasValue)
            {
                return direct;
            }

            var state = TryReadStringProperty(guiderInfo, "State", "GuidingState", "Status");
            if (string.IsNullOrWhiteSpace(state))
            {
                return null;
            }

            var normalized = state.ToLowerInvariant();
            if (normalized.Contains("settl"))
            {
                return true;
            }

            if (normalized.Contains("guid")
                || normalized.Contains("idle")
                || normalized.Contains("stop"))
            {
                return false;
            }

            return null;
        }

        private static bool? TryReadBoolProperty(object source, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property == null)
                {
                    continue;
                }

                var value = property.GetValue(source);
                if (value is bool boolValue)
                {
                    return boolValue;
                }

                if (value != null && bool.TryParse(value.ToString(), out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        private static string? TryReadStringProperty(object source, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property == null)
                {
                    continue;
                }

                var value = property.GetValue(source)?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static List<RuntimeSafetyMetricConditionDto> GetRuleConditions(RuntimeSafetyRuleDto rule)
        {
            var conditions = rule.Conditions?
                .Where(c => !string.IsNullOrWhiteSpace(c.MetricKey))
                .ToList() ?? new List<RuntimeSafetyMetricConditionDto>();

            if (conditions.Count > 0)
            {
                return conditions;
            }

            if (!string.IsNullOrWhiteSpace(rule.MetricKey))
            {
                conditions.Add(new RuntimeSafetyMetricConditionDto
                {
                    MetricKey = rule.MetricKey,
                    ConditionOperator = rule.ConditionOperator,
                    NumericValue = rule.NumericValue,
                    BoolValue = rule.BoolValue
                });
            }

            return conditions;
        }

        private static bool EvaluateRuntimeMetricCondition(RuntimeSafetyMetricConditionDto condition, Dictionary<string, object?> metrics, out string detail)
        {
            detail = string.Empty;
            if (string.IsNullOrWhiteSpace(condition.MetricKey))
            {
                detail = "metric key is empty";
                return false;
            }

            if (!metrics.TryGetValue(condition.MetricKey, out var rawValue))
            {
                detail = $"{condition.MetricKey} missing in runtime metric snapshot";
                return false;
            }

            if (rawValue is null)
            {
                detail = $"{condition.MetricKey} is null";
                return false;
            }

            if (rawValue is bool boolMetric && condition.BoolValue.HasValue)
            {
                var threshold = condition.BoolValue.Value;
                var matched = condition.ConditionOperator switch
                {
                    RuntimeSafetyConditionOperator.Equals => boolMetric == threshold,
                    RuntimeSafetyConditionOperator.NotEquals => boolMetric != threshold,
                    _ => false
                };

                detail = $"{condition.MetricKey}={boolMetric} {condition.ConditionOperator} {threshold}";
                return matched;
            }

            if (condition.NumericValue.HasValue)
            {
                if (!double.TryParse(rawValue.ToString(), out var metricNumber)
                    || double.IsNaN(metricNumber)
                    || double.IsInfinity(metricNumber))
                {
                    detail = $"{condition.MetricKey} value '{rawValue}' is not a finite number";
                    return false;
                }

                var threshold = condition.NumericValue.Value;
                var matched = condition.ConditionOperator switch
                {
                    RuntimeSafetyConditionOperator.GreaterThan => metricNumber > threshold,
                    RuntimeSafetyConditionOperator.GreaterThanOrEqual => metricNumber >= threshold,
                    RuntimeSafetyConditionOperator.LessThan => metricNumber < threshold,
                    RuntimeSafetyConditionOperator.LessThanOrEqual => metricNumber <= threshold,
                    RuntimeSafetyConditionOperator.Equals => Math.Abs(metricNumber - threshold) < 0.0001,
                    RuntimeSafetyConditionOperator.NotEquals => Math.Abs(metricNumber - threshold) >= 0.0001,
                    _ => false
                };

                detail = $"{condition.MetricKey}={metricNumber:F3} {condition.ConditionOperator} {threshold:F3}";
                return matched;
            }

            detail = $"{condition.MetricKey} has unsupported condition payload (expected bool or numeric value)";
            return false;
        }

        private static bool EvaluateRuntimeRule(
            RuntimeSafetyRuleDto rule,
            Dictionary<string, object?> metrics,
            RuntimeSafetyEvaluationPhase evaluationPhase,
            out string details,
            out List<RuntimeSafetyMetricConditionDto> matchedConditions)
        {
            details = string.Empty;
            matchedConditions = new List<RuntimeSafetyMetricConditionDto>();
            var conditions = GetRuleConditions(rule);
            if (evaluationPhase == RuntimeSafetyEvaluationPhase.DuringExposure)
            {
                conditions = conditions
                    .Where(c => IsDuringExposureMetric(c.MetricKey))
                    .ToList();
            }
            else
            {
                // During-exposure-only metrics (e.g., guider guiding/settling) are not reliable outside exposure.
                conditions = conditions
                    .Where(c => !IsDuringExposureMetric(c.MetricKey))
                    .ToList();
            }

            if (conditions.Count == 0)
            {
                details = $"No eligible conditions for phase {evaluationPhase}";
                return false;
            }

            var matchedDetails = new List<string>();
            var evaluatedDetails = new List<string>();
            foreach (var condition in conditions)
            {
                var matched = EvaluateRuntimeMetricCondition(condition, metrics, out var conditionDetail);
                evaluatedDetails.Add($"{condition.MetricKey}: {(matched ? "MATCH" : "MISS")} ({conditionDetail})");
                if (matched)
                {
                    matchedDetails.Add(conditionDetail);
                    matchedConditions.Add(condition);
                }
            }

            if (matchedDetails.Count == 0)
            {
                details = string.Join("; ", evaluatedDetails);
                return false;
            }

            details = string.Join(" OR ", matchedDetails);
            return true;
        }

        private static string BuildFriendlySafetyViolationSummary(
            IEnumerable<RuntimeSafetyMetricConditionDto> matchedConditions,
            Dictionary<string, object?> metrics)
        {
            var messages = matchedConditions
                .Select(condition => BuildFriendlyConditionMessage(condition, metrics))
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList();

            return messages.Count == 0
                ? "Safety limit exceeded"
                : string.Join(" | ", messages);
        }

        private static string BuildFriendlyConditionMessage(RuntimeSafetyMetricConditionDto condition, Dictionary<string, object?> metrics)
        {
            var key = condition.MetricKey ?? string.Empty;
            var label = GetSafetyMetricLabel(key);

            metrics.TryGetValue(key, out var rawValue);

            if (condition.BoolValue.HasValue && rawValue is bool boolValue)
            {
                var expected = condition.BoolValue.Value;
                var op = condition.ConditionOperator == RuntimeSafetyConditionOperator.NotEquals ? "is not" : "is";
                return $"{label} {op} {FormatBoolean(expected)} (current: {FormatBoolean(boolValue)})";
            }

            if (condition.NumericValue.HasValue
                && rawValue != null
                && double.TryParse(rawValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var metricNumber)
                && !double.IsNaN(metricNumber)
                && !double.IsInfinity(metricNumber))
            {
                var current = FormatMetricValue(key, metricNumber);
                var threshold = FormatMetricValue(key, condition.NumericValue.Value);
                var op = GetOperatorSymbol(condition.ConditionOperator);
                return $"{label} ({current}) {op} {threshold} safety limit";
            }

            return $"{label} reached configured safety threshold";
        }

        private static string GetSafetyMetricLabel(string? metricKey)
        {
            return metricKey?.ToLowerInvariant() switch
            {
                "cloudcoverpercent" or "cloudcover" or "weathercloudcover" => "Clouds",
                "rainrate" or "weatherrainrate" => "Rain rate",
                "lastcapturestarcount" => "Stars in last capture",
                "lastcapturehfr" => "Last capture HFR",
                "guidingrmsarcsec" => "Guiding RMS",
                "guiderguiding" => "Guider guiding",
                "guidersettling" => "Guider settling",
                "safetymonitorsafe" => "Safety monitor",
                "safetymonitorconnected" => "Safety monitor connection",
                "weatherconnected" => "Weather sensor connection",
                "mountconnected" => "Mount connection",
                "cameraconnected" => "Camera connection",
                "guiderconnected" => "Guider connection",
                "mountaltitudedegrees" => "Mount altitude",
                "weatherskyquality" or "skyquality" or "sqm" => "Sky quality",
                "coolerpowerpercent" => "Cooler power",
                _ => string.IsNullOrWhiteSpace(metricKey) ? "Metric" : metricKey
            };
        }

        private static string FormatMetricValue(string metricKey, double value)
        {
            return metricKey.ToLowerInvariant() switch
            {
                "cloudcoverpercent" or "cloudcover" or "weathercloudcover" => $"{value:0.#}%",
                "coolerpowerpercent" => $"{value:0.#}%",
                "mountaltitudedegrees" => $"{value:0.#}°",
                "guidingrmsarcsec" => $"{value:0.##}\"",
                "lastcapturestarcount" => $"{Math.Round(value):0}",
                "lastcapturehfr" => $"{value:0.##}",
                _ => $"{value:0.###}"
            };
        }

        private static string GetOperatorSymbol(RuntimeSafetyConditionOperator op)
        {
            return op switch
            {
                RuntimeSafetyConditionOperator.GreaterThan => ">",
                RuntimeSafetyConditionOperator.GreaterThanOrEqual => ">=",
                RuntimeSafetyConditionOperator.LessThan => "<",
                RuntimeSafetyConditionOperator.LessThanOrEqual => "<=",
                RuntimeSafetyConditionOperator.Equals => "=",
                RuntimeSafetyConditionOperator.NotEquals => "!=",
                _ => op.ToString()
            };
        }

        private static string FormatBoolean(bool value)
        {
            return value ? "ON" : "OFF";
        }

        private static bool IsPreSlotHardSafetyMetric(string? metricKey)
        {
            return !string.IsNullOrWhiteSpace(metricKey)
                && PreSlotHardSafetyMetricKeys.Contains(metricKey);
        }

        private static bool IsDuringExposureMetric(string? metricKey)
        {
            return !string.IsNullOrWhiteSpace(metricKey)
                && DuringExposureMetricKeys.Contains(metricKey);
        }

        private static bool ShouldRunDuringExposureSafetyChecks(RuntimeStopSafetyPolicyDto config)
        {
            if (config.Rules == null || config.Rules.Count == 0)
            {
                return false;
            }

            return config.Rules
                .Where(r => r.IsEnabled)
                .SelectMany(GetRuleConditions)
                .Any(c => IsDuringExposureMetric(c.MetricKey));
        }

        private static bool RequiresLastCaptureMetrics(RuntimeStopSafetyPolicyDto? config)
        {
            if (config?.Rules == null || config.Rules.Count == 0)
            {
                return false;
            }

            return config.Rules
                .Where(r => r.IsEnabled)
                .SelectMany(GetRuleConditions)
                .Any(c => !string.IsNullOrWhiteSpace(c.MetricKey) && LastCaptureMetricKeys.Contains(c.MetricKey));
        }

        private async Task WaitForCaptureMetricsIfRequiredAsync(
            RuntimeStopSafetyPolicyDto? runtimePolicy,
            Guid captureId,
            CancellationToken token)
        {
            if (!RequiresLastCaptureMetrics(runtimePolicy))
            {
                return;
            }

            CaptureMetricsCompletion? completion;
            try
            {
                completion = await SharedSchedulerState.Instance.WaitForCaptureMetricsCompletionAsync(captureId, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[SAFETY-CHECK] Failed while waiting for capture metrics completion (CaptureId={captureId}): {ex.Message}");
                return;
            }

            if (completion == null)
            {
                Logger.Debug($"[SAFETY-CHECK] No pending capture metric completion found for CaptureId={captureId}");
                return;
            }

            if (completion.Hfr.HasValue || completion.StarCount.HasValue)
            {
                _heartbeatService.SetLastCaptureMetrics(completion.Hfr, completion.StarCount, completion.CapturedAtUtc);
            }

            if (!completion.IsSuccess)
            {
                Logger.Warning($"[SAFETY-CHECK] Capture metric completion reported non-success for CaptureId={captureId}: {completion.Reason ?? "unknown reason"}");
            }
        }

        private static bool IsBlockingSafetyAction(SchedulerViolationAction action)
        {
            return action is SchedulerViolationAction.StopScheduler
                or SchedulerViolationAction.StopAmScheduler
                or SchedulerViolationAction.ParkAndRetry
                or SchedulerViolationAction.StopTrackingAndRetry;
        }

        private static bool IsRetryAction(SchedulerViolationAction action)
        {
            return action is SchedulerViolationAction.ParkAndRetry
                or SchedulerViolationAction.StopTrackingAndRetry;
        }

        private static bool IsAlertSafetyAction(SchedulerViolationAction action)
        {
            return action is SchedulerViolationAction.SendEmail
                or SchedulerViolationAction.CreateNotification;
        }

        private async Task<bool> EvaluateRuntimeStopRulesV2Async(
            RuntimeStopSafetyPolicyDto config,
            NextSlotDto slot,
            RuntimeSafetyEvaluationPhase evaluationPhase,
            IProgress<ApplicationStatus> progress,
            CancellationToken token)
        {
            if (config.Rules == null || config.Rules.Count == 0)
            {
                Logger.Debug($"AstroManager Scheduler: Safety check ({evaluationPhase}) skipped - policy '{config.Name}' has no rules");
                return false;
            }

            var metrics = BuildRuntimeSafetyMetricValues(slot, evaluationPhase);
            var enabledRules = config.Rules.Where(r => r.IsEnabled).ToList();
            if (enabledRules.Count == 0)
            {
                Logger.Debug($"AstroManager Scheduler: Safety check ({evaluationPhase}) skipped - policy '{config.Name}' has 0 enabled rules");
                return false;
            }

            var watchedMetricKeys = enabledRules
                .SelectMany(GetRuleConditions)
                .Select(c => c.MetricKey)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var metricSnapshot = string.Join(", ",
                watchedMetricKeys.Select(key =>
                {
                    if (!metrics.TryGetValue(key, out var value))
                    {
                        return $"{key}=(missing)";
                    }

                    return $"{key}={(value?.ToString() ?? "null")}";
                }));

            Logger.Debug(
                $"AstroManager Scheduler: Safety check ({evaluationPhase}) policy '{config.Name}': {enabledRules.Count} enabled rule(s). Metrics: {metricSnapshot}");

            foreach (var rule in enabledRules)
            {
                Logger.Debug($"AstroManager Scheduler: Safety rule evaluating ({evaluationPhase}): '{rule.Name}'");

                if (!EvaluateRuntimeRule(rule, metrics, evaluationPhase, out var detail, out var matchedConditions))
                {
                    Logger.Debug($"AstroManager Scheduler: Safety rule not matched ({evaluationPhase}): '{rule.Name}' -> {detail}");
                    continue;
                }

                var friendlyViolation = BuildFriendlySafetyViolationSummary(matchedConditions, metrics);
                AddLogEntry($"Safety violation: {friendlyViolation}", SchedulerLogLevel.Warning);
                Logger.Warning($"[SAFETY-CHECK] Safety violation ({evaluationPhase}, rule {rule.Id}): {friendlyViolation}");

                var actions = rule.Actions?.Where(a => Enum.IsDefined(typeof(SchedulerViolationAction), a)).ToList()
                              ?? new List<SchedulerViolationAction>();

                if (actions.Count == 0)
                {
                    actions.Add(SchedulerViolationAction.StopScheduler);
                }

                var executedActions = new HashSet<SchedulerViolationAction>();
                foreach (var action in actions)
                {
                    if (executedActions.Contains(action))
                    {
                        continue;
                    }

                    var actionPayload = rule.ActionConfigs?.FirstOrDefault(c => c.Action == action);
                    var waitMinutes = actionPayload?.WaitMinutes;

                    if (IsRetryAction(action) && (!waitMinutes.HasValue || waitMinutes.Value < 1))
                    {
                        AddLogEntry($"Safety rule action '{action}' skipped: wait minutes are required.", SchedulerLogLevel.Warning);
                        continue;
                    }

                    var handled = await ExecuteSafetyActionAsync(
                        friendlyViolation,
                        action,
                        actionPayload,
                        waitMinutes,
                        rule,
                        slot,
                        evaluationPhase,
                        progress,
                        token);
                    executedActions.Add(action);
                    if (handled || IsBlockingSafetyAction(action))
                    {
                        return true;
                    }
                }

                // Only execute first matched rule per cycle
                return false;
            }

            return false;
        }

        private async Task<bool> EvaluateRuntimeStopChecksAsync(
            RuntimeStopSafetyPolicyDto config,
            NextSlotDto slot,
            RuntimeSafetyEvaluationPhase evaluationPhase,
            IProgress<ApplicationStatus> progress,
            CancellationToken token)
        {
            return await EvaluateRuntimeStopRulesV2Async(config, slot, evaluationPhase, progress, token);
        }

        private void SetSafetyOperationStatus(string operation, string status)
        {
            _heartbeatService.SetOperationStatus(operation, status);
            _ = _heartbeatService.ForceStatusUpdateAsync();
        }

        private async Task<bool> ExecuteSafetyActionAsync(
            string violation,
            SchedulerViolationAction action,
            RuntimeSafetyActionConfigDto? actionConfig,
            int? waitMinutes,
            RuntimeSafetyRuleDto triggeringRule,
            NextSlotDto slot,
            RuntimeSafetyEvaluationPhase evaluationPhase,
            IProgress<ApplicationStatus> progress,
            CancellationToken token)
        {
            switch (action)
            {
                case SchedulerViolationAction.ParkAndRetry:
                {
                    var wait = Math.Max(1, waitMinutes ?? 1);
                    SetSafetyOperationStatus($"Safety pause ({wait}m): Parking mount", "SafetyPause");
                    try
                    {
                        var telescopeInfo = _telescopeMediator.GetInfo();
                        if (telescopeInfo.Connected && telescopeInfo.CanPark && !telescopeInfo.AtPark)
                        {
                            AddLogEntry($"Safety action: park mount and wait {wait} minute(s) to retry ({violation})", SchedulerLogLevel.Warning);
                            await _telescopeMediator.ParkTelescope(null, token);
                        }
                        else if (telescopeInfo.Connected && telescopeInfo.CanSetTrackingEnabled && telescopeInfo.TrackingEnabled)
                        {
                            AddLogEntry($"Safety action: stop mount tracking and wait {wait} minute(s) to retry ({violation})", SchedulerLogLevel.Warning);
                            _telescopeMediator.SetTrackingEnabled(false);
                            _trackingStoppedForNoSlot = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[SAFETY-CHECK] Failed to park/stop mount: {ex.Message}");
                    }

                    StatusMessage = $"Safety pause {wait} min: {violation}";
                    progress.Report(new ApplicationStatus { Status = StatusMessage });
                    SetSafetyOperationStatus($"Safety pause ({wait}m): {violation}", "SafetyPause");

                    var resumedEarly = await WaitForSafetyPauseCompletionAsync(
                        wait,
                        actionConfig,
                        triggeringRule,
                        slot,
                        evaluationPhase,
                        token);

                    if (resumedEarly)
                    {
                        AddLogEntry("Safety action: safety recovered, ending pause early and resuming sequence", SchedulerLogLevel.Success);
                    }

                    try
                    {
                        var telescopeInfo = _telescopeMediator.GetInfo();
                        if (telescopeInfo.Connected && telescopeInfo.CanPark && telescopeInfo.AtPark)
                        {
                            AddLogEntry("Safety action: attempting unpark after safety pause", SchedulerLogLevel.Info);
                            await _telescopeMediator.UnparkTelescope(null, token);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[SAFETY-CHECK] Failed to unpark after safety pause: {ex.Message}");
                    }

                    SetSafetyOperationStatus("Safety pause complete", "Imaging");

                    return true;
                }

                case SchedulerViolationAction.StopTrackingAndRetry:
                {
                    var wait = Math.Max(1, waitMinutes ?? 1);
                    SetSafetyOperationStatus($"Safety pause ({wait}m): Stopping tracking", "SafetyPause");
                    try
                    {
                        var telescopeInfo = _telescopeMediator.GetInfo();
                        if (telescopeInfo.Connected && telescopeInfo.CanSetTrackingEnabled && telescopeInfo.TrackingEnabled)
                        {
                            AddLogEntry($"Safety action: stop tracking and wait {wait} minute(s) to retry ({violation})", SchedulerLogLevel.Warning);
                            _telescopeMediator.SetTrackingEnabled(false);
                            _trackingStoppedForNoSlot = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[SAFETY-CHECK] Failed to stop tracking: {ex.Message}");
                    }

                    StatusMessage = $"Safety pause {wait} min: {violation}";
                    progress.Report(new ApplicationStatus { Status = StatusMessage });
                    SetSafetyOperationStatus($"Safety pause ({wait}m): {violation}", "SafetyPause");

                    var resumedEarly = await WaitForSafetyPauseCompletionAsync(
                        wait,
                        actionConfig,
                        triggeringRule,
                        slot,
                        evaluationPhase,
                        token);

                    if (resumedEarly)
                    {
                        AddLogEntry("Safety action: safety recovered, ending pause early and resuming sequence", SchedulerLogLevel.Success);
                    }

                    SetSafetyOperationStatus("Safety pause complete", "Imaging");
                    return true;
                }

                case SchedulerViolationAction.StopAmScheduler:
                    AddLogEntry($"Safety action: stop AM scheduler (NINA sequence continues) ({violation})", SchedulerLogLevel.Error);
                    StatusMessage = $"AM scheduler stopped: {violation}";
                    _shouldContinue = false;
                    _receivedStopSignal = true;
                    SharedSchedulerState.Instance.Clear();
                    _heartbeatService.SetCurrentState("AM Scheduler Stopped", null, null, 0, 0, status: "Idle");
                    _ = _heartbeatService.ForceStatusUpdateAsync();
                    return true;

                case SchedulerViolationAction.CalibrateGuider:
                    AddLogEntry($"Safety action: calibrating guider ({violation})", SchedulerLogLevel.Warning);
                    SetSafetyOperationStatus("Safety action: calibrating guider", "SafetyAction");
                    await ExecuteGuiderCalibrationAsync(progress, token);
                    SetSafetyOperationStatus("Safety action complete", "Imaging");
                    return false;

                case SchedulerViolationAction.RunAutofocus:
                    AddLogEntry($"Safety action: running autofocus ({violation})", SchedulerLogLevel.Warning);
                    SetSafetyOperationStatus("Safety action: running autofocus", "SafetyAction");
                    await ExecuteAutofocusAsync(progress, token);
                    SetSafetyOperationStatus("Safety action complete", "Imaging");
                    return false;

                case SchedulerViolationAction.ReconnectEquipment:
                {
                    var reconnectTarget = actionConfig?.ReconnectComponent ?? RuntimeSafetyReconnectComponent.CriticalImaging;
                    SetSafetyOperationStatus($"Safety action: reconnecting {reconnectTarget}", "SafetyAction");
                    var reconnectResult = await ReconnectEquipmentForSafetyAsync(reconnectAll: false, reconnectTarget, token);
                    AddLogEntry($"Safety action: reconnect equipment ({reconnectResult})", SchedulerLogLevel.Warning);
                    SetSafetyOperationStatus("Safety action complete", "Imaging");
                    return false;
                }

                case SchedulerViolationAction.ReconnectAllEquipment:
                {
                    SetSafetyOperationStatus("Safety action: reconnecting all equipment", "SafetyAction");
                    var reconnectResult = await ReconnectEquipmentForSafetyAsync(reconnectAll: true, reconnectComponent: null, token);
                    AddLogEntry($"Safety action: reconnect all equipment ({reconnectResult})", SchedulerLogLevel.Warning);
                    SetSafetyOperationStatus("Safety action complete", "Imaging");
                    return false;
                }

                case SchedulerViolationAction.SendEmail:
                {
                    var emailConfig = actionConfig?.Email;
                    var emailRequest = new RuntimeSafetyEmailAlertDto
                    {
                        Violation = violation,
                        Subject = emailConfig?.SubjectTemplate,
                        Body = emailConfig?.BodyTemplate,
                        AdditionalRecipients = emailConfig?.AdditionalRecipients?.ToList() ?? new List<string>()
                    };

                    var sent = await _apiClient.SendRuntimeSafetyEmailAsync(emailRequest);
                    if (!sent)
                    {
                        Logger.Warning($"[SAFETY-CHECK] Failed to send safety email alert ({violation})");
                    }
                    return false;
                }

                case SchedulerViolationAction.CreateNotification:
                {
                    var notificationConfig = actionConfig?.Notification;
                    var notificationRequest = new RuntimeSafetyNotificationAlertDto
                    {
                        Violation = violation,
                        Title = notificationConfig?.TitleTemplate,
                        Text = notificationConfig?.ContentTemplate
                    };

                    var created = await _apiClient.CreateRuntimeSafetyNotificationAsync(notificationRequest);
                    if (!created)
                    {
                        Logger.Warning($"[SAFETY-CHECK] Failed to create safety notification ({violation})");
                    }
                    return false;
                }

                case SchedulerViolationAction.AdjustCoolerTemperatureDelta:
                {
                    var delta = actionConfig?.CoolerDeltaC;
                    if (!delta.HasValue || Math.Abs(delta.Value) < 0.0001)
                    {
                        AddLogEntry("Safety action skipped: cooler delta is not configured.", SchedulerLogLevel.Warning);
                        return false;
                    }

                    var cameraInfo = _cameraMediator.GetInfo();
                    if (!cameraInfo.Connected)
                    {
                        AddLogEntry("Safety action skipped: camera is not connected.", SchedulerLogLevel.Warning);
                        return false;
                    }

                    if (!cameraInfo.CanSetTemperature)
                    {
                        AddLogEntry("Safety action skipped: camera does not support temperature setpoint control.", SchedulerLogLevel.Warning);
                        return false;
                    }

                    var cameraDevice = _cameraMediator.GetDevice() as NINA.Equipment.Interfaces.ICamera;
                    if (cameraDevice == null)
                    {
                        AddLogEntry("Safety action skipped: unable to access camera device for cooler adjustment.", SchedulerLogLevel.Warning);
                        return false;
                    }

                    try
                    {
                        var currentSetPoint = cameraDevice.TemperatureSetPoint;
                        var nextSetPoint = currentSetPoint + delta.Value;

                        cameraDevice.CoolerOn = true;
                        cameraDevice.TemperatureSetPoint = nextSetPoint;

                        AddLogEntry(
                            $"Safety action: adjusted cooler setpoint by {delta.Value:+0.0;-0.0;0.0}°C ({currentSetPoint:0.0}°C -> {nextSetPoint:0.0}°C)",
                            SchedulerLogLevel.Warning);
                    }
                    catch (Exception ex)
                    {
                        AddLogEntry($"Safety action failed: cooler delta adjustment error ({ex.Message})", SchedulerLogLevel.Error);
                        Logger.Warning($"[SAFETY-CHECK] Failed to adjust cooler setpoint: {ex.Message}");
                    }

                    return false;
                }

                case SchedulerViolationAction.StopScheduler:
                default:
                    AddLogEntry($"Safety action: stop full NINA sequence ({violation})", SchedulerLogLevel.Error);
                    StatusMessage = $"Sequence stopped: {violation}";
                    _shouldContinue = false;
                    _receivedStopSignal = true;
                    SharedSchedulerState.Instance.Clear();
                    _heartbeatService.SetCurrentState("Sequence Stopped (Safety)", null, null, 0, 0, status: "Idle");
                    _ = _heartbeatService.ForceStatusUpdateAsync();

                    try
                    {
                        _sequenceMediator.CancelAdvancedSequence();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[SAFETY-CHECK] Failed to cancel NINA sequence: {ex.Message}");
                    }

                    return true;
            }
        }

        private async Task<bool> WaitForSafetyPauseCompletionAsync(
            int waitMinutes,
            RuntimeSafetyActionConfigDto? actionConfig,
            RuntimeSafetyRuleDto triggeringRule,
            NextSlotDto slot,
            RuntimeSafetyEvaluationPhase evaluationPhase,
            CancellationToken token)
        {
            var waitUntilUtc = DateTime.UtcNow.AddMinutes(waitMinutes);

            if (actionConfig?.RetryWhenRuleClears != true)
            {
                while (true)
                {
                    var remaining = waitUntilUtc - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        return false;
                    }

                    var keepAliveDelay = remaining < TimeSpan.FromMinutes(1)
                        ? remaining
                        : TimeSpan.FromMinutes(1);

                    await Task.Delay(keepAliveDelay, token);
                    await _heartbeatService.ForceStatusUpdateAsync();
                }
            }

            AddLogEntry(
                $"Safety action: will re-check safety every {SafetyRetryRecheckInterval.TotalMinutes:F0} minute(s) and resume early when recovered",
                SchedulerLogLevel.Info);

            while (true)
            {
                var remaining = waitUntilUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                var nextDelay = remaining < SafetyRetryRecheckInterval
                    ? remaining
                    : SafetyRetryRecheckInterval;

                await Task.Delay(nextDelay, token);
                await _heartbeatService.ForceStatusUpdateAsync();

                var metrics = BuildRuntimeSafetyMetricValues(slot, evaluationPhase);
                var stillViolated = EvaluateRuntimeRule(
                    triggeringRule,
                    metrics,
                    evaluationPhase,
                    out _,
                    out _);

                if (!stillViolated)
                {
                    return true;
                }
            }
        }

        private double CalculateTargetAltitude(double raHours, double decDegrees)
        {
            var latitude = _profileService.ActiveProfile.AstrometrySettings.Latitude;
            var longitude = _profileService.ActiveProfile.AstrometrySettings.Longitude;
            var lst = CalculateLST(DateTime.UtcNow, longitude);
            return CalculateAltitude(raHours, decDegrees, latitude, lst);
        }

        private static double CalculateLST(DateTime utc, double longitude)
        {
            var jd = utc.ToOADate() + 2415018.5;
            var t = (jd - 2451545.0) / 36525.0;
            var gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0) + 0.000387933 * t * t - t * t * t / 38710000.0;
            gmst %= 360;
            if (gmst < 0) gmst += 360;

            var lst = gmst + longitude;
            lst %= 360;
            if (lst < 0) lst += 360;

            return lst / 15.0;
        }

        private static double CalculateAltitude(double raHours, double decDegrees, double latitudeDegrees, double lstHours)
        {
            var hourAngleDegrees = (lstHours - raHours) * 15.0;
            var decRad = decDegrees * Math.PI / 180.0;
            var latRad = latitudeDegrees * Math.PI / 180.0;
            var haRad = hourAngleDegrees * Math.PI / 180.0;

            var sinAlt = Math.Sin(decRad) * Math.Sin(latRad) + Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
            return Math.Asin(sinAlt) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Handle errors and get instructions from API
        /// </summary>
        private async Task HandleErrorAsync(Exception ex, IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            // CRITICAL: Reset dither counter on error - don't count failed attempts towards dither
            // This prevents dithering on retry when the previous attempt failed before exposure
            _exposuresSinceLastDither = 0;
            
            // Check if we switched to a different target - reset counter if so
            if (_errorTargetId != _currentTargetId)
            {
                _errorRetryCount = 0;
                _errorTargetId = _currentTargetId;
            }
            
            _errorRetryCount++;
            var normalizedErrorMessage = ExtractCoreErrorMessage(ex);
            var errorType = ClassifyError(ex);

            if (errorType == ErrorType.PlateSolveFailed)
            {
                var failedPlateSolveReport = new PlateSolveReportDto
                {
                    CompletedAt = DateTime.UtcNow,
                    Success = false,
                    FailureReason = normalizedErrorMessage
                };
                _heartbeatService.SetPlateSolveReport(failedPlateSolveReport);
                _heartbeatService.SetOperationStatus("Plate Solve Failed", "Imaging");
                _ = _heartbeatService.ForceStatusUpdateAsync();
            }
            
            var errorReport = new ErrorReportDto
            {
                TargetId = _currentTargetId,
                PanelId = _currentPanelId,
                ErrorType = errorType,
                ErrorMessage = normalizedErrorMessage,
                ErrorDetails = ex.StackTrace,
                OccurredAtUtc = DateTime.UtcNow,
                RetryCount = _errorRetryCount
            };
            
            var response = await _apiClient.ReportErrorAsync(errorReport);
            
            switch (response?.Instruction ?? ErrorInstruction.Stop)
            {
                case ErrorInstruction.Retry:
                    Logger.Info($"AstroManager Scheduler: Retrying after error ({_errorRetryCount})");
                    StatusMessage = $"Retrying... ({_errorRetryCount})";
                    await Task.Delay(2000, token);
                    break;
                    
                case ErrorInstruction.Wait:
                    Logger.Info($"AstroManager Scheduler: Waiting {response?.WaitMinutes ?? 5} minutes after error");
                    StatusMessage = $"Waiting after error...";
                    await Task.Delay(TimeSpan.FromMinutes(response?.WaitMinutes ?? 5), token);
                    _errorRetryCount = 0;
                    break;
                    
                case ErrorInstruction.SkipTarget:
                    Logger.Info("AstroManager Scheduler: Skipping target after error");
                    AddLogEntry($"Skipping target due to error: {ex.Message}", SchedulerLogLevel.Warning);
                    StatusMessage = "Skipping target...";
                    
                    // Update queue item status to Skipped if we have a queue item
                    if (_currentQueueItemId.HasValue)
                    {
                        await _apiClient.UpdateQueueItemStatusAsync(_currentQueueItemId.Value, QueueItemStatus.Skipped);
                        Logger.Info($"AstroManager Scheduler: Updated queue item {_currentQueueItemId} to Skipped");
                    }
                    
                    _currentTargetId = null;
                    _currentPanelId = null;
                    _currentQueueItemId = null;
                    _errorRetryCount = 0;
                    break;
                    
                case ErrorInstruction.Stop:
                default:
                    Logger.Error($"AstroManager Scheduler: Stopping due to error - {ex.Message}");
                    AddLogEntry($"Stopping due to error: {ex.Message}", SchedulerLogLevel.Error);
                    StatusMessage = $"Error: {ex.Message}";
                    
                    // Update queue item status to Failed if we have a queue item
                    if (_currentQueueItemId.HasValue)
                    {
                        await _apiClient.UpdateQueueItemStatusAsync(_currentQueueItemId.Value, QueueItemStatus.Failed);
                        Logger.Info($"AstroManager Scheduler: Updated queue item {_currentQueueItemId} to Failed");
                    }
                    
                    throw new OperationCanceledException($"Stopping due to error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Classify exception into error type
        /// </summary>
        private ErrorType ClassifyError(Exception ex)
        {
            var msg = ExtractCoreErrorMessage(ex).ToLowerInvariant();
            if (msg.Contains("platesolv") || msg.Contains("plate solv")) return ErrorType.PlateSolveFailed;
            if (msg.Contains("guid")) return ErrorType.GuidingFailed;
            if (msg.Contains("filter")) return ErrorType.FilterWheelError;
            if (msg.Contains("focus")) return ErrorType.FocuserError;
            if (msg.Contains("rotat")) return ErrorType.RotatorError;
            if (msg.Contains("camera")) return ErrorType.CameraError;
            if (msg.Contains("telescope") || msg.Contains("mount")) return ErrorType.TelescopeError;
            if (msg.Contains("dome")) return ErrorType.DomeError;
            if (msg.Contains("weather")) return ErrorType.WeatherAlert;
            if (msg.Contains("safe")) return ErrorType.SafetyEvent;
            if (msg.Contains("slot execution")) return ErrorType.Unknown;
            return ErrorType.Unknown;
        }

        private static string ExtractCoreErrorMessage(Exception ex)
        {
            var message = ex.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                return ex.InnerException?.Message ?? "Unknown error";
            }

            const string slotExecutionPrefix = "Error in slot execution:";
            if (message.StartsWith(slotExecutionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = message[slotExecutionPrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return trimmed;
                }
            }

            if (ex.InnerException != null && !string.IsNullOrWhiteSpace(ex.InnerException.Message))
            {
                return ex.InnerException.Message;
            }

            return message;
        }

    }
}
