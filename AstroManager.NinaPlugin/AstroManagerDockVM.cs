using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using Shared.Model.DTO.Scheduler;
using Shared.Model.Enums;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections.Generic;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Dockable panel for AstroManager in NINA's Imaging tab
    /// Shows current target with imaging goal status and next probable targets
    /// </summary>
    [Export(typeof(IDockableVM))]
    public class AstroManagerDockVM : DockableVM
    {
        private readonly AstroManagerSettings _settings;
        private readonly AstroManagerApiClient _apiClient;
        private readonly ScheduledTargetStore _targetStore;
        private readonly ITelescopeMediator _telescopeMediator;

        [ImportingConstructor]
        public AstroManagerDockVM(
            IProfileService profileService,
            AstroManagerSettings settings,
            AstroManagerApiClient apiClient,
            ScheduledTargetStore targetStore,
            ITelescopeMediator telescopeMediator) : base(profileService)
        {
            _settings = settings;
            _apiClient = apiClient;
            _targetStore = targetStore;
            _telescopeMediator = telescopeMediator;

            // Set panel title and icon
            Title = "AstroManager";
            
            // Custom AstroManager logo: telescope with star
            ImageGeometry = CreateAstroManagerLogo();
            ImageGeometry.Freeze();

            // Initialize collections
            CurrentTargetGoals = new ObservableCollection<ImagingGoalStatus>();
            GroupedTargets = new ObservableCollection<TargetGroup>();

            // Initialize commands
            RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
            SyncCommand = new RelayCommand(async _ => await SyncTargetsAsync());

            // Initial refresh
            Task.Run(async () => await RefreshAsync());
        }

        // IsTool = true means it appears on the right side (tools), false = left side (info)
        public override bool IsTool => true;

        #region Commands

        public ICommand RefreshCommand { get; }
        public ICommand SyncCommand { get; }

        #endregion

        #region Properties

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; RaisePropertyChanged(); }
        }

        private string _connectionStatus = "Checking...";
        public string ConnectionStatus
        {
            get => _connectionStatus;
            set { _connectionStatus = value; RaisePropertyChanged(); }
        }

        private string _runtimeSafetyPolicyName = "Not assigned";
        public string RuntimeSafetyPolicyName
        {
            get => _runtimeSafetyPolicyName;
            set { _runtimeSafetyPolicyName = value; RaisePropertyChanged(); }
        }

        // Current Target
        private string? _currentTargetName;
        public string? CurrentTargetName
        {
            get => _currentTargetName;
            set { _currentTargetName = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasCurrentTarget)); }
        }

        public bool HasCurrentTarget => !string.IsNullOrEmpty(_currentTargetName);

        private double _currentTargetProgress;
        public double CurrentTargetProgress
        {
            get => _currentTargetProgress;
            set { _currentTargetProgress = value; RaisePropertyChanged(); }
        }

        private string? _currentTargetType;
        public string? CurrentTargetType
        {
            get => _currentTargetType;
            set { _currentTargetType = value; RaisePropertyChanged(); }
        }

        private int _currentTargetPriority;
        public int CurrentTargetPriority
        {
            get => _currentTargetPriority;
            set { _currentTargetPriority = value; RaisePropertyChanged(); }
        }

        public ObservableCollection<ImagingGoalStatus> CurrentTargetGoals { get; }

        // Next Targets (grouped)
        public ObservableCollection<TargetGroup> GroupedTargets { get; }
        public bool HasNextTargets => GroupedTargets.Count > 0;

        // Session Stats
        private int _totalTargets;
        public int TotalTargets
        {
            get => _totalTargets;
            set { _totalTargets = value; RaisePropertyChanged(); }
        }

        private int _completedGoals;
        public int CompletedGoals
        {
            get => _completedGoals;
            set { _completedGoals = value; RaisePropertyChanged(); }
        }

        private int _totalGoals;
        public int TotalGoals
        {
            get => _totalGoals;
            set { _totalGoals = value; RaisePropertyChanged(); }
        }

        private string _lastSyncTime = "Never";
        public string LastSyncTime
        {
            get => _lastSyncTime;
            set { _lastSyncTime = value; RaisePropertyChanged(); }
        }

        // Scheduler Log - shared static collection accessible from both scheduler and dock
        public ObservableCollection<SchedulerLogEntry> SchedulerLog => SharedSchedulerLog.Instance.LogEntries;
        
        private string _schedulerStatus = "Idle";
        public string SchedulerStatus
        {
            get => _schedulerStatus;
            set { _schedulerStatus = value; RaisePropertyChanged(); }
        }

        // Altitude chart properties
        private Coordinates? _currentTargetCoordinates;
        public Coordinates? CurrentTargetCoordinates
        {
            get => _currentTargetCoordinates;
            set { _currentTargetCoordinates = value; RaisePropertyChanged(); }
        }

        private double _observerLatitude;
        public double ObserverLatitude
        {
            get => _observerLatitude;
            set { _observerLatitude = value; RaisePropertyChanged(); }
        }

        private double _observerLongitude;
        public double ObserverLongitude
        {
            get => _observerLongitude;
            set { _observerLongitude = value; RaisePropertyChanged(); }
        }

        private double _minAltitude = 30.0;
        public double MinAltitude
        {
            get => _minAltitude;
            set { _minAltitude = value; RaisePropertyChanged(); }
        }

        private DateTime? _astronomicalDusk;
        public DateTime? AstronomicalDusk
        {
            get => _astronomicalDusk;
            set { _astronomicalDusk = value; RaisePropertyChanged(); }
        }

        private DateTime? _astronomicalDawn;
        public DateTime? AstronomicalDawn
        {
            get => _astronomicalDawn;
            set { _astronomicalDawn = value; RaisePropertyChanged(); }
        }

        #endregion

        #region Methods

        private async Task SyncTargetsAsync()
        {
            try
            {
                ConnectionStatus = "Syncing...";
                var (success, message, targets) = await _apiClient.SyncScheduledTargetsAsync();
                if (success && targets != null && targets.Any())
                {
                    _targetStore.UpdateTargets(targets);
                    LastSyncTime = DateTime.Now.ToString("HH:mm:ss");
                    ConnectionStatus = "Connected";
                    IsConnected = true;
                    await RefreshAsync();
                }
                else
                {
                    ConnectionStatus = success ? "No targets" : "Sync failed";
                    IsConnected = success;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerDock: Sync failed: {ex.Message}");
                ConnectionStatus = "Sync failed";
                IsConnected = false;
            }
        }

        private async Task RefreshAsync()
        {
            try
            {
                // Check connection
                if (!string.IsNullOrEmpty(_settings.LicenseKey))
                {
                    var (success, _, _) = await _apiClient.TestConnectionAsync();
                    IsConnected = success;
                    ConnectionStatus = success ? "Connected" : "Offline";
                    RuntimeSafetyPolicyName = string.IsNullOrWhiteSpace(_settings.RuntimeStopSafetyPolicyName)
                        ? "Not assigned"
                        : _settings.RuntimeStopSafetyPolicyName;
                }
                else
                {
                    ConnectionStatus = "No license";
                    IsConnected = false;
                    RuntimeSafetyPolicyName = "Not assigned";
                }

                var targets = _targetStore.GetAllTargets()?.ToList();
                if (targets == null || !targets.Any())
                {
                    CurrentTargetName = null;
                    TotalTargets = 0;
                    return;
                }

                TotalTargets = targets.Count;

                // First check if scheduler has an active target (most accurate source)
                var schedulerTargetId = SharedSchedulerState.Instance.CurrentScheduledTargetId;
                var schedulerTargetName = SharedSchedulerState.Instance.CurrentTargetName;
                
                ScheduledTargetDto? activeTarget = null;
                
                if (schedulerTargetId.HasValue)
                {
                    // Scheduler is running - use the target it's currently imaging
                    activeTarget = targets.FirstOrDefault(t => t.Id == schedulerTargetId.Value);
                }
                
                if (activeTarget == null && !string.IsNullOrEmpty(schedulerTargetName))
                {
                    // Fallback: match by name if ID not found
                    activeTarget = targets.FirstOrDefault(t => t.Name == schedulerTargetName);
                }
                
                if (activeTarget == null)
                {
                    // No scheduler running - find the current/active target (Active status, highest priority)
                    activeTarget = targets
                        .Where(t => t.Status == ScheduledTargetStatus.Active)
                        .OrderBy(t => t.Priority)
                        .FirstOrDefault();
                }

                if (activeTarget == null)
                {
                    // No active target, show the next pending one
                    activeTarget = targets
                        .Where(t => t.Status != ScheduledTargetStatus.Completed && t.Status != ScheduledTargetStatus.Paused)
                        .OrderBy(t => t.Priority)
                        .FirstOrDefault();
                }

                if (activeTarget != null)
                {
                    CurrentTargetName = activeTarget.Name;
                    CurrentTargetType = activeTarget.ObjectType;
                    CurrentTargetPriority = activeTarget.Priority;
                    UpdateTargetProgress(activeTarget);
                    UpdateImagingGoals(activeTarget);
                    
                    // Update altitude chart coordinates
                    if (activeTarget.RightAscension != 0 || activeTarget.Declination != 0)
                    {
                        CurrentTargetCoordinates = new Coordinates(
                            Angle.ByHours(activeTarget.RightAscension),
                            Angle.ByDegree(activeTarget.Declination),
                            Epoch.J2000);
                    }
                    else
                    {
                        CurrentTargetCoordinates = null;
                    }
                }
                else
                {
                    CurrentTargetName = null;
                    CurrentTargetCoordinates = null;
                }

                // Update observer location and twilight times for altitude chart
                UpdateAltitudeChartData();

                // Calculate total goals stats
                CalculateGoalStats(targets);
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerDock: Refresh failed: {ex.Message}");
            }
        }

        private void UpdateTargetProgress(ScheduledTargetDto target)
        {
            if (target.ImagingGoals == null || !target.ImagingGoals.Any())
            {
                CurrentTargetProgress = 0;
                return;
            }

            // Apply RepeatCount multiplier to goal totals
            var repeatCount = Math.Max(1, target.RepeatCount);
            var totalPlanned = target.ImagingGoals.Sum(g => g.GoalExposureCount) * repeatCount;
            var totalCompleted = target.ImagingGoals.Sum(g => g.CompletedExposures);

            CurrentTargetProgress = totalPlanned > 0
                ? (totalCompleted * 100.0 / totalPlanned)
                : 0;
        }

        private void UpdateImagingGoals(ScheduledTargetDto target)
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                CurrentTargetGoals.Clear();

                if (target.ImagingGoals == null) return;

                // Apply RepeatCount multiplier to goal totals
                var repeatCount = Math.Max(1, target.RepeatCount);
                
                foreach (var goal in target.ImagingGoals.OrderBy(g => g.Filter))
                {
                    CurrentTargetGoals.Add(new ImagingGoalStatus
                    {
                        FilterName = goal.Filter.ToString(),
                        PlannedExposures = goal.GoalExposureCount * repeatCount,
                        CompletedExposures = goal.CompletedExposures,
                        ExposureTime = goal.ExposureTimeSeconds
                    });
                }
            });
        }

        private void UpdateNextTargets(System.Collections.Generic.List<ScheduledTargetDto> targets, Guid? currentTargetId)
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                GroupedTargets.Clear();

                var upcoming = targets
                    .Where(t => t.Id != currentTargetId && t.Status != ScheduledTargetStatus.Completed)
                    .OrderBy(t => t.Priority);

                // Group targets by ObjectType
                var groups = upcoming
                    .GroupBy(t => t.ObjectType ?? "Unknown")
                    .OrderBy(g => g.Key);

                foreach (var group in groups)
                {
                    var targetGroup = new TargetGroup
                    {
                        GroupName = group.Key,
                        TargetCount = group.Count(),
                        IsExpanded = false // Collapsed by default
                    };

                    foreach (var target in group.OrderBy(t => t.Priority))
                    {
                        var goalCount = target.ImagingGoals?.Count ?? 0;
                        var totalPlanned = target.ImagingGoals?.Sum(g => g.GoalExposureCount) ?? 0;
                        var totalCompleted = target.ImagingGoals?.Sum(g => g.CompletedExposures) ?? 0;
                        var completionPercent = totalPlanned > 0 ? (totalCompleted * 100.0 / totalPlanned) : 0;

                        targetGroup.Targets.Add(new NextTargetInfo
                        {
                            Name = target.Name ?? "Unknown",
                            Priority = target.Priority,
                            GoalCount = goalCount,
                            CompletionPercent = completionPercent,
                            Status = target.Status.ToString()
                        });
                    }

                    GroupedTargets.Add(targetGroup);
                }

                RaisePropertyChanged(nameof(HasNextTargets));
            });
        }

        private void CalculateGoalStats(System.Collections.Generic.List<ScheduledTargetDto> targets)
        {
            TotalGoals = targets.Sum(t => t.ImagingGoals?.Count ?? 0);
            CompletedGoals = targets.Sum(t => 
                t.ImagingGoals?.Count(g => g.CompletedExposures >= g.GoalExposureCount) ?? 0);
        }

        private void UpdateAltitudeChartData()
        {
            try
            {
                // Get observer location from NINA profile
                var latitude = profileService.ActiveProfile.AstrometrySettings.Latitude;
                var longitude = profileService.ActiveProfile.AstrometrySettings.Longitude;
                
                ObserverLatitude = latitude;
                ObserverLongitude = longitude;
                
                // Use default min altitude
                MinAltitude = 30.0;
                
                // Calculate twilight times using RiseAndSetEvent
                var now = DateTime.UtcNow;
                var tonight = now.Hour < 12 ? now.Date.AddDays(-1) : now.Date;
                
                // Get astronomical twilight events
                var duskEvent = AstroUtil.GetSunRiseAndSet(tonight, latitude, longitude);
                var dawnEvent = AstroUtil.GetSunRiseAndSet(tonight.AddDays(1), latitude, longitude);
                
                // Use sunset + 1.5 hours as approximate astronomical dusk
                // Use sunrise - 1.5 hours as approximate astronomical dawn
                DateTime dusk = duskEvent?.Set?.AddHours(1.5) ?? tonight.AddHours(19);
                DateTime dawn = dawnEvent?.Rise?.AddHours(-1.5) ?? tonight.AddDays(1).AddHours(5);
                
                // If we're past dawn, calculate for next night
                if (now > dawn)
                {
                    tonight = tonight.AddDays(1);
                    duskEvent = AstroUtil.GetSunRiseAndSet(tonight, latitude, longitude);
                    dawnEvent = AstroUtil.GetSunRiseAndSet(tonight.AddDays(1), latitude, longitude);
                    dusk = duskEvent?.Set?.AddHours(1.5) ?? tonight.AddHours(19);
                    dawn = dawnEvent?.Rise?.AddHours(-1.5) ?? tonight.AddDays(1).AddHours(5);
                }
                
                AstronomicalDusk = dusk;
                AstronomicalDawn = dawn;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerDock: Failed to update altitude chart data: {ex.Message}");
            }
        }

        #endregion
        
        /// <summary>
        /// Creates the AstroManager logo geometry: telescope with star
        /// </summary>
        private static GeometryGroup CreateAstroManagerLogo()
        {
            var group = new GeometryGroup();
            
            // 5-point star (top right)
            var starGeometry = Geometry.Parse("M 19,4 L 19.588,5.81 L 21.9,5.81 L 20.376,6.92 L 21.012,9 L 19,7.6 L 16.988,9 L 17.624,6.92 L 16.1,5.81 L 18.412,5.81 Z");
            group.Children.Add(starGeometry);
            
            // Telescope barrel (rotated rectangle approximated as path)
            var barrelGeometry = Geometry.Parse("M 4.5,11.5 L 13.5,8.5 L 14.2,11 L 5.2,14 Z");
            group.Children.Add(barrelGeometry);
            
            // Mount joint (circle)
            var mountGeometry = new EllipseGeometry(new System.Windows.Point(9, 16), 1.4, 1.4);
            group.Children.Add(mountGeometry);
            
            // Tripod legs
            var leg1 = new LineGeometry(new System.Windows.Point(8, 17), new System.Windows.Point(6, 22));
            var leg2 = new LineGeometry(new System.Windows.Point(10, 17), new System.Windows.Point(12, 22));
            group.Children.Add(leg1);
            group.Children.Add(leg2);
            
            return group;
        }
    }

    /// <summary>
    /// Status info for an imaging goal
    /// </summary>
    public class ImagingGoalStatus
    {
        public string FilterName { get; set; } = string.Empty;
        public int PlannedExposures { get; set; }
        public int CompletedExposures { get; set; }
        public double ExposureTime { get; set; }
        public double ProgressPercent => PlannedExposures > 0 ? (CompletedExposures * 100.0 / PlannedExposures) : 0;
    }

    /// <summary>
    /// Info for a next target in the queue
    /// </summary>
    public class NextTargetInfo
    {
        public string Name { get; set; } = string.Empty;
        public int Priority { get; set; }
        public int GoalCount { get; set; }
        public double CompletionPercent { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// Group of targets by object type
    /// </summary>
    public class TargetGroup : NINA.Core.Utility.BaseINPC
    {
        public string GroupName { get; set; } = string.Empty;
        public int TargetCount { get; set; }
        public ObservableCollection<NextTargetInfo> Targets { get; } = new ObservableCollection<NextTargetInfo>();

        private bool _isExpanded = false;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; RaisePropertyChanged(); }
        }
    }
}
