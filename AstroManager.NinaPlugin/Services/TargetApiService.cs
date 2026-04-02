using Shared.Model.DTO.Client;
using Shared.Model.DTO.Scheduler;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace AstroManager.NinaPlugin.Services
{
    /// <summary>
    /// Service for scheduled target API operations.
    /// Handles target CRUD, imaging goals, and panels.
    /// </summary>
    public class TargetApiService
    {
        private readonly Func<Task<bool>> _ensureAuthenticatedAsync;
        private readonly Func<string?> _getJwtToken;
        private readonly Func<string> _getBaseUrl;
        private readonly Func<Guid?> _getObservatoryId;
        private readonly Func<Guid?> _getEquipmentId;
        private readonly HttpClient _httpClient;
        private readonly AstroManagerSettings _settings;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public TargetApiService(
            HttpClient httpClient,
            AstroManagerSettings settings,
            Func<Task<bool>> ensureAuthenticatedAsync,
            Func<string?> getJwtToken,
            Func<string> getBaseUrl,
            Func<Guid?> getObservatoryId,
            Func<Guid?> getEquipmentId)
        {
            _httpClient = httpClient;
            _settings = settings;
            _ensureAuthenticatedAsync = ensureAuthenticatedAsync;
            _getJwtToken = getJwtToken;
            _getBaseUrl = getBaseUrl;
            _getObservatoryId = getObservatoryId;
            _getEquipmentId = getEquipmentId;
        }

        #region Target CRUD

        /// <summary>
        /// Sync scheduled targets from API
        /// </summary>
        public async Task<(bool Success, string Message, List<ScheduledTargetDto>? Targets)> SyncScheduledTargetsAsync()
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                {
                    return (false, "Authentication failed", null);
                }

                // Get scheduled targets by equipment (if configured)
                string url;
                if (_settings.EquipmentId.HasValue && _settings.EquipmentId != Guid.Empty)
                {
                    url = $"{_getBaseUrl()}/api/scheduler/targets/by-equipment/{_settings.EquipmentId}";
                }
                else if (_settings.ObservatoryId.HasValue && _settings.ObservatoryId != Guid.Empty)
                {
                    url = $"{_getBaseUrl()}/api/scheduler/targets/by-observatory/{_settings.ObservatoryId}";
                }
                else
                {
                    url = $"{_getBaseUrl()}/api/scheduler/targets";
                }

                Logger.Info($"TargetApiService: Syncing targets from {url}");

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"TargetApiService: Sync failed: {response.StatusCode} - {error}");
                    return (false, $"Failed to get targets: {response.StatusCode}", null);
                }

                var targets = await response.Content.ReadFromJsonAsync<List<ScheduledTargetDto>>(_jsonOptions);
                _settings.LastSyncTime = DateTime.UtcNow;

                var mosaicTargets = targets?.Where(t => t.IsMosaic).ToList() ?? new List<ScheduledTargetDto>();
                foreach (var mt in mosaicTargets)
                {
                    Logger.Info($"TargetApiService: Mosaic target '{mt.Name}' has {mt.Panels?.Count ?? 0} panels");
                }

                Logger.Info($"TargetApiService: Synced {targets?.Count ?? 0} targets ({mosaicTargets.Count} mosaic)");
                return (true, $"Synced {targets?.Count ?? 0} targets", targets ?? new List<ScheduledTargetDto>());
            }
            catch (HttpRequestException ex)
            {
                Logger.Error($"TargetApiService: Sync failed: {ex.Message}");
                return (false, $"Network error: {ex.Message}", null);
            }
            catch (TaskCanceledException)
            {
                return (false, "Connection timeout", null);
            }
            catch (Exception ex)
            {
                Logger.Error($"TargetApiService: Sync failed: {ex.Message}");
                return (false, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Get a single target by ID from the API (includes panels)
        /// </summary>
        public async Task<ScheduledTargetDto?> GetTargetByIdAsync(Guid targetId)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                {
                    return null;
                }

                var url = $"{_getBaseUrl()}/api/scheduler/targets/{targetId}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"TargetApiService: Get target failed: {response.StatusCode} - {error}");
                    return null;
                }

                var target = await response.Content.ReadFromJsonAsync<ScheduledTargetDto>(_jsonOptions);
                Logger.Info($"TargetApiService: GetTargetById - IsMosaic={target?.IsMosaic}, Panels={target?.Panels?.Count ?? 0}");
                return target;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: Get target failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Update a scheduled target on the server
        /// </summary>
        public async Task<bool> UpdateTargetAsync(ScheduledTargetDto target)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                {
                    return false;
                }

                var updateDto = new UpdateScheduledTargetDto
                {
                    Id = target.Id,
                    Name = target.Name,
                    Description = target.Description,
                    Priority = target.Priority,
                    RepeatCount = target.RepeatCount,
                    Status = target.Status,
                    Notes = target.Notes,
                    RightAscension = target.RightAscension,
                    Declination = target.Declination,
                    PA = target.PA,
                    ObjectType = target.ObjectType,
                    MagnitudeV = target.MagnitudeV,
                    Distance = target.Distance,
                    SizeMinArcmin = target.SizeMinArcmin,
                    SizeMaxArcmin = target.SizeMaxArcmin,
                    RelevantFilters = target.RelevantFilters,
                    UserTags = target.UserTags,
                    IsMosaic = target.IsMosaic,
                    MosaicPanelsX = target.MosaicPanelsX,
                    MosaicPanelsY = target.MosaicPanelsY,
                    MosaicOverlapPercent = target.MosaicOverlapPercent,
                    MosaicUseRotator = target.MosaicUseRotator,
                    UseCustomPanelGoals = target.UseCustomPanelGoals,
                    MosaicShootingStrategy = target.MosaicShootingStrategy,
                    MosaicPanelOrderingMethod = target.MosaicPanelOrderingMethod,
                    GoalOrderingMethod = target.GoalOrderingMethod,
                    ShowImage = target.ShowImage,
                    AstroBinImageId = target.AstroBinImageId,
                    AstroBinImageUrl = target.AstroBinImageUrl,
                    SchedulerTargetTemplateId = target.SchedulerTargetTemplateId
                };

                var url = $"{_getBaseUrl()}/api/scheduler/targets/{target.Id}";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = JsonContent.Create(updateDto);

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"TargetApiService: Update target failed: {response.StatusCode} - {error}");
                }

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: Update target failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Delete a target from the API
        /// </summary>
        public async Task<bool> DeleteTargetAsync(Guid targetId)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                {
                    return false;
                }

                var url = $"{_getBaseUrl()}/api/scheduler/targets/{targetId}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"TargetApiService: Delete target failed: {response.StatusCode} - {error}");
                }

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: Delete target failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get next target for imaging
        /// </summary>
        public async Task<NextTargetDto?> GetNextTargetAsync(Guid? configurationId = null)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                {
                    return null;
                }

                var obsId = _settings.ObservatoryId ?? Guid.Empty;
                var eqId = _settings.EquipmentId ?? Guid.Empty;
                var url = $"{_getBaseUrl()}/api/client/sessions/current?observatoryId={obsId}&equipmentId={eqId}";

                if (configurationId.HasValue)
                {
                    url += $"&configurationId={configurationId.Value}";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<NextTargetDto>(_jsonOptions);
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: GetNextTarget failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Imaging Goals

        /// <summary>
        /// Add an imaging goal to a target
        /// </summary>
        public async Task<ImagingGoalDto?> AddImagingGoalAsync(Guid targetId, ImagingGoalDto goal)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return null;

                var url = $"{_getBaseUrl()}/api/scheduler/targets/{targetId}/imaging-goals";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = JsonContent.Create(goal);

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ImagingGoalDto>();
                    Logger.Info($"TargetApiService: AddImagingGoal succeeded for target {targetId}");
                    return result;
                }

                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"TargetApiService: AddImagingGoal failed: {response.StatusCode} - {error}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: AddImagingGoal failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Update an imaging goal
        /// </summary>
        public async Task<ImagingGoalDto?> UpdateImagingGoalAsync(Guid targetId, ImagingGoalDto goal)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return null;

                var url = $"{_getBaseUrl()}/api/scheduler/targets/{targetId}/imaging-goals/{goal.Id}";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = JsonContent.Create(goal);

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<ImagingGoalDto>();
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: UpdateImagingGoal failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Delete an imaging goal
        /// </summary>
        public async Task<bool> DeleteImagingGoalAsync(Guid targetId, Guid goalId)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return false;

                var url = $"{_getBaseUrl()}/api/scheduler/targets/{targetId}/imaging-goals/{goalId}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: DeleteImagingGoal failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Panels

        /// <summary>
        /// Update custom goals for a panel
        /// </summary>
        public async Task<bool> UpdatePanelCustomGoalsAsync(Guid panelId, List<PanelImagingGoalDto> customGoals)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return false;

                var url = $"{_getBaseUrl()}/api/scheduler/targets/panels/{panelId}/custom-goals";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = JsonContent.Create(customGoals);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    Logger.Info($"TargetApiService: Updated {customGoals.Count} custom goals for panel {panelId}");
                    return true;
                }

                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"TargetApiService: UpdatePanelCustomGoals failed: {response.StatusCode} - {error}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: UpdatePanelCustomGoals failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Update a panel (e.g., enable/disable)
        /// </summary>
        public async Task<bool> UpdatePanelAsync(ScheduledTargetPanelDto panel)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return false;

                var url = $"{_getBaseUrl()}/api/scheduler/targets/panels/{panel.Id}";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = JsonContent.Create(panel);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    Logger.Info($"TargetApiService: Updated panel {panel.PanelNumber}");
                    return true;
                }

                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"TargetApiService: UpdatePanel failed: {response.StatusCode} - {error}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: UpdatePanel failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Progress Reporting

        /// <summary>
        /// Report imaging progress back to API
        /// </summary>
        public async Task<bool> ReportProgressAsync(Guid targetId, Guid goalId, int completedExposures)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                {
                    return false;
                }

                var url = $"{_getBaseUrl()}/api/scheduler/progress";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = JsonContent.Create(new { targetId, goalId, completedExposures });

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: ReportProgress failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Report completed exposure back to AstroManager
        /// </summary>
        public async Task ReportExposureCompletedAsync(Guid scheduledTargetId, Guid imagingGoalId, int exposureCount, int exposureTimeSeconds)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return;

                var url = $"{_getBaseUrl()}/api/client/sessions/exposure-completed";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = JsonContent.Create(new
                {
                    ScheduledTargetId = scheduledTargetId,
                    ImagingGoalId = imagingGoalId,
                    ExposureCount = exposureCount,
                    ExposureTimeSeconds = exposureTimeSeconds
                });

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"TargetApiService: ReportExposureCompleted failed: {response.StatusCode} - {error}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"TargetApiService: ReportExposureCompleted failed: {ex.Message}");
            }
        }

        #endregion
    }
}
