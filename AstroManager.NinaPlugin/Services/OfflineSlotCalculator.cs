using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Core.Utility;
using Shared.Model.DTO.Client;
using Shared.Model.DTO.Scheduler;
using Shared.Model.Enums;

namespace AstroManager.NinaPlugin.Services
{
    /// <summary>
    /// Calculates next slot offline using cached targets when API is unavailable.
    /// Simple priority-based selection with altitude filtering.
    /// </summary>
    public class OfflineSlotCalculator
    {
        private readonly ScheduledTargetStore _targetStore;
        private readonly AstroManagerSettings _settings;
        
        // Track offline progress (will be synced when back online)
        private readonly Dictionary<Guid, int> _offlineExposureProgress = new();
        
        public bool IsOfflineMode { get; private set; }
        public int ConsecutiveApiFailures { get; private set; }
        public DateTime? LastApiSuccess { get; private set; }
        
        private const int API_FAILURES_BEFORE_OFFLINE = 3;
        
        public OfflineSlotCalculator(ScheduledTargetStore targetStore, AstroManagerSettings settings)
        {
            _targetStore = targetStore;
            _settings = settings;
        }
        
        /// <summary>
        /// Report API call result to track connection status
        /// </summary>
        public void ReportApiResult(bool success)
        {
            if (success)
            {
                ConsecutiveApiFailures = 0;
                LastApiSuccess = DateTime.UtcNow;
                
                if (IsOfflineMode)
                {
                    Logger.Info("OfflineSlotCalculator: API connection restored - switching back to online mode");
                    IsOfflineMode = false;
                }
            }
            else
            {
                ConsecutiveApiFailures++;
                
                if (!IsOfflineMode && ConsecutiveApiFailures >= API_FAILURES_BEFORE_OFFLINE)
                {
                    Logger.Warning($"OfflineSlotCalculator: {ConsecutiveApiFailures} consecutive API failures - switching to OFFLINE mode");
                    IsOfflineMode = true;
                }
            }
        }
        
        /// <summary>
        /// Check if we should use offline mode
        /// </summary>
        public bool ShouldUseOfflineMode()
        {
            return IsOfflineMode || ConsecutiveApiFailures >= API_FAILURES_BEFORE_OFFLINE;
        }
        
        /// <summary>
        /// Calculate next slot offline using cached targets
        /// </summary>
        public NextSlotDto? CalculateNextSlotOffline(
            Guid? currentTargetId, 
            Guid? currentPanelId, 
            string? currentFilter,
            double observatoryLatitude,
            double observatoryLongitude)
        {
            try
            {
                Logger.Info("[OFFLINE] Calculating next slot from cached targets...");
                
                var activeTargets = _targetStore.GetActiveTargets();
                if (!activeTargets.Any())
                {
                    Logger.Warning("[OFFLINE] No cached active targets available");
                    return new NextSlotDto
                    {
                        SlotType = SlotType.Wait,
                        WaitMinutes = 5,
                        Message = "Offline mode: No cached targets available"
                    };
                }
                
                Logger.Info($"[OFFLINE] Found {activeTargets.Count} cached active targets");
                
                var now = DateTime.UtcNow;
                var lst = CalculateLST(now, observatoryLongitude);
                
                // Score targets by priority, altitude, and incomplete goals
                var scoredTargets = new List<(ScheduledTargetDto Target, ImagingGoalDto Goal, double Score, double Altitude)>();
                
                foreach (var target in activeTargets)
                {
                    // Calculate current altitude
                    var altitude = CalculateAltitude(target.RightAscension, target.Declination, observatoryLatitude, lst);
                    
                    // Skip if below minimum altitude (default 30°)
                    var minAlt = target.SchedulerTargetTemplate?.MinAltitude ?? 30;
                    if (altitude < minAlt)
                    {
                        Logger.Debug($"[OFFLINE] {target.Name}: Alt={altitude:F1}° < MinAlt={minAlt}° - skipping");
                        continue;
                    }
                    
                    // Find incomplete goals - check panel goals for mosaic targets
                    ImagingGoalDto? goal = null;
                    Guid? selectedPanelId = null;
                    
                    if (target.IsMosaic && target.HasPanels && target.Panels?.Any() == true)
                    {
                        // For mosaic targets, ALWAYS use panel goals because:
                        // - If UseCustomPanelGoals=true: panel has custom goals with their own progress
                        // - If UseCustomPanelGoals=false: panel has synced base goals with per-panel progress
                        // Find first panel with incomplete goals
                        foreach (var panel in target.Panels.Where(p => p.IsEnabled).OrderBy(p => p.ShootingOrder ?? p.PanelNumber))
                        {
                            var panelGoal = panel.ImagingGoals?
                                .Where(g => g.IsEnabled)
                                .Where(g => g.CompletedExposures < g.GoalExposureCount)
                                .OrderBy(g => g.FilterPriority)
                                .FirstOrDefault();
                            
                            if (panelGoal != null)
                            {
                                // Convert panel goal to ImagingGoalDto for compatibility
                                goal = new ImagingGoalDto
                                {
                                    Id = panelGoal.Id,
                                    GoalExposureCount = panelGoal.GoalExposureCount,
                                    CompletedExposures = panelGoal.CompletedExposures,
                                    IsEnabled = panelGoal.IsEnabled,
                                    ExposureTemplate = panelGoal.ExposureTemplate
                                };
                                selectedPanelId = panel.Id;
                                Logger.Debug($"[OFFLINE] {target.Name}: Using panel {panel.PanelNumber} goal ({panelGoal.Filter})");
                                break;
                            }
                        }
                        
                        // No panel goals found
                        if (goal == null)
                        {
                            Logger.Debug($"[OFFLINE] {target.Name}: No incomplete panel goals - skipping");
                            continue;
                        }
                    }
                    else
                    {
                        // Non-mosaic target - use base goals
                        goal = target.ImagingGoals
                            .Where(g => g.IsEnabled)
                            .Where(g => GetEffectiveCompleted(target.Id, g) < g.GoalExposureCount)
                            .OrderBy(g => g.FilterPriority)
                            .FirstOrDefault();
                    }
                    
                    if (goal == null)
                    {
                        Logger.Debug($"[OFFLINE] {target.Name}: No incomplete goals - skipping");
                        continue;
                    }
                    
                    // Score: lower priority number = better, higher altitude = better
                    // Priority ranges 1-99, altitude 0-90
                    var priorityScore = 100 - target.Priority; // Invert so lower priority = higher score
                    var altitudeScore = altitude;
                    var score = priorityScore * 2 + altitudeScore;
                    
                    scoredTargets.Add((target, goal, score, altitude));
                    Logger.Debug($"[OFFLINE] {target.Name}: Alt={altitude:F1}°, Priority={target.Priority}, Score={score:F1}");
                }
                
                if (!scoredTargets.Any())
                {
                    Logger.Info("[OFFLINE] No observable targets with incomplete goals");
                    return new NextSlotDto
                    {
                        SlotType = SlotType.Wait,
                        WaitMinutes = 10,
                        Message = "Offline mode: No observable targets at this time"
                    };
                }
                
                // Select best target
                var best = scoredTargets.OrderByDescending(s => s.Score).First();
                var selectedTarget = best.Target;
                var selectedGoal = best.Goal;
                
                var completed = GetEffectiveCompleted(selectedTarget.Id, selectedGoal);
                var total = selectedGoal.GoalExposureCount;
                var requiresSlew = currentTargetId != selectedTarget.Id;
                var requiresFilterChange = currentFilter != selectedGoal.Filter.ToString();
                
                Logger.Info($"[OFFLINE] Selected: {selectedTarget.Name} - {selectedGoal.Filter} (Alt={best.Altitude:F1}°, Priority={selectedTarget.Priority}, Progress={completed}/{total})");
                
                // Get exposure template settings
                var template = selectedGoal.ExposureTemplate;
                
                return new NextSlotDto
                {
                    SlotType = SlotType.Exposure,
                    TargetId = selectedTarget.Id,
                    TargetName = selectedTarget.Name,
                    ImagingGoalId = selectedGoal.Id,
                    RightAscensionHours = selectedTarget.RightAscension,
                    DeclinationDegrees = selectedTarget.Declination,
                    PositionAngle = selectedTarget.PA,
                    Filter = selectedGoal.Filter.ToString(),
                    ExposureTimeSeconds = selectedGoal.ExposureTimeSeconds,
                    Gain = template?.Gain ?? -1,
                    Offset = template?.Offset ?? -1,
                    Binning = template?.Binning > 0 ? $"{template.Binning}x{template.Binning}" : "1x1",
                    RequiresSlew = requiresSlew,
                    RequiresFilterChange = requiresFilterChange,
                    CompletedExposures = completed,
                    TotalGoalExposures = total,
                    DitherEveryX = template?.DitherEveryX ?? selectedTarget.SchedulerTargetTemplate?.DitherEveryX ?? 0,
                    Message = $"[OFFLINE] {selectedTarget.Name} - {selectedGoal.Filter}"
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"[OFFLINE] Error calculating slot: {ex.Message}");
                return new NextSlotDto
                {
                    SlotType = SlotType.Wait,
                    WaitMinutes = 5,
                    Message = $"Offline mode error: {ex.Message}"
                };
            }
        }
        
        /// <summary>
        /// Record offline exposure progress
        /// </summary>
        public void RecordOfflineExposure(Guid targetId, Guid goalId)
        {
            var key = goalId;
            if (!_offlineExposureProgress.ContainsKey(key))
                _offlineExposureProgress[key] = 0;
            
            _offlineExposureProgress[key]++;
            
            // Also update local target store
            var target = _targetStore.GetTarget(targetId);
            var goal = target?.ImagingGoals.FirstOrDefault(g => g.Id == goalId);
            if (goal != null)
            {
                _targetStore.UpdateTargetProgress(targetId, goalId, goal.CompletedExposures + 1);
            }
            
            Logger.Info($"[OFFLINE] Recorded exposure for goal {goalId}, offline count: {_offlineExposureProgress[key]}");
        }
        
        /// <summary>
        /// Get offline progress to sync
        /// </summary>
        public Dictionary<Guid, int> GetOfflineProgress()
        {
            return new Dictionary<Guid, int>(_offlineExposureProgress);
        }
        
        /// <summary>
        /// Clear offline progress after sync
        /// </summary>
        public void ClearOfflineProgress()
        {
            _offlineExposureProgress.Clear();
            Logger.Info("[OFFLINE] Cleared offline progress after sync");
        }
        
        private int GetEffectiveCompleted(Guid targetId, ImagingGoalDto goal)
        {
            var baseCompleted = goal.CompletedExposures;
            if (_offlineExposureProgress.TryGetValue(goal.Id, out var offlineCount))
            {
                return baseCompleted + offlineCount;
            }
            return baseCompleted;
        }
        
        /// <summary>
        /// Calculate Local Sidereal Time
        /// </summary>
        private double CalculateLST(DateTime utc, double longitude)
        {
            // Julian Date
            var jd = utc.ToOADate() + 2415018.5;
            var t = (jd - 2451545.0) / 36525.0;
            
            // Greenwich Mean Sidereal Time
            var gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0) + 0.000387933 * t * t - t * t * t / 38710000.0;
            gmst = gmst % 360;
            if (gmst < 0) gmst += 360;
            
            // Local Sidereal Time
            var lst = gmst + longitude;
            lst = lst % 360;
            if (lst < 0) lst += 360;
            
            return lst / 15.0; // Convert to hours
        }
        
        /// <summary>
        /// Calculate altitude of a target
        /// </summary>
        private double CalculateAltitude(double ra, double dec, double latitude, double lst)
        {
            // Hour angle in hours, then convert to degrees
            var ha = (lst - ra) * 15.0;
            
            // Convert to radians
            var decRad = dec * Math.PI / 180.0;
            var latRad = latitude * Math.PI / 180.0;
            var haRad = ha * Math.PI / 180.0;
            
            // Calculate altitude
            var sinAlt = Math.Sin(decRad) * Math.Sin(latRad) + Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
            var altitude = Math.Asin(sinAlt) * 180.0 / Math.PI;
            
            return altitude;
        }
    }
}
