using Shared.Model.DTO.Client;
using Shared.Model.Enums;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace AstroManager.NinaPlugin.Services
{
    /// <summary>
    /// Service for slot-based scheduling API operations.
    /// Handles real-time exposure slot management.
    /// </summary>
    public class SlotApiService
    {
        private readonly Func<Task<bool>> _ensureAuthenticatedAsync;
        private readonly Func<string?> _getJwtToken;
        private readonly Func<string> _getBaseUrl;
        private readonly Func<Guid?> _getObservatoryId;
        private readonly Func<Guid?> _getEquipmentId;
        private readonly HttpClient _httpClient;

        public SlotApiService(
            HttpClient httpClient,
            Func<Task<bool>> ensureAuthenticatedAsync,
            Func<string?> getJwtToken,
            Func<string> getBaseUrl,
            Func<Guid?> getObservatoryId,
            Func<Guid?> getEquipmentId)
        {
            _httpClient = httpClient;
            _ensureAuthenticatedAsync = ensureAuthenticatedAsync;
            _getJwtToken = getJwtToken;
            _getBaseUrl = getBaseUrl;
            _getObservatoryId = getObservatoryId;
            _getEquipmentId = getEquipmentId;
        }

        /// <summary>
        /// Get the next exposure slot to execute
        /// </summary>
        public async Task<NextSlotDto?> GetNextSlotAsync(Guid? configurationId = null, Guid? currentTargetId = null, Guid? currentPanelId = null, string? currentFilter = null)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return null;

                var obsId = _getObservatoryId() ?? Guid.Empty;
                var eqId = _getEquipmentId() ?? Guid.Empty;

                var url = $"{_getBaseUrl()}/api/client/slot/next?observatoryId={obsId}&equipmentId={eqId}";
                if (configurationId.HasValue) url += $"&configurationId={configurationId.Value}";
                if (currentTargetId.HasValue) url += $"&currentTargetId={currentTargetId.Value}";
                if (currentPanelId.HasValue) url += $"&currentPanelId={currentPanelId.Value}";
                if (!string.IsNullOrEmpty(currentFilter)) url += $"&currentFilter={Uri.EscapeDataString(currentFilter)}";

                Logger.Info($"[API-CALL] GetNextSlot REQUEST: ConfigId={configurationId}, CurrentTargetId={currentTargetId}, CurrentPanelId={currentPanelId}, CurrentFilter={currentFilter}");

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var slot = await response.Content.ReadFromJsonAsync<NextSlotDto>();
                    if (slot != null)
                    {
                        Logger.Info($"[API-CALL] GetNextSlot RESPONSE: SlotType={slot.SlotType}, Target={slot.TargetName}, Panel={slot.PanelName}, Filter={slot.Filter}, GoalId={slot.ImagingGoalId}, Progress={slot.CompletedExposures}/{slot.TotalGoalExposures}, RequiresSlew={slot.RequiresSlew}, Message={slot.Message}");
                    }
                    else
                    {
                        Logger.Info($"[API-CALL] GetNextSlot RESPONSE: null (no slot)");
                    }
                    return slot;
                }

                Logger.Warning($"[API-CALL] GetNextSlot FAILED: {response.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[API-CALL] GetNextSlot ERROR: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Report a completed exposure with image metadata
        /// </summary>
        public async Task<ExposureCompleteResponseDto?> ReportExposureCompleteAsync(ExposureCompleteDto request)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return null;

                Logger.Info($"[API-CALL] ReportExposureComplete REQUEST: TargetId={request.TargetId}, GoalId={request.ImagingGoalId}, PanelId={request.PanelId}, Filter={request.Filter}, Success={request.Success}");

                var url = $"{_getBaseUrl()}/api/client/slot/exposure-complete";
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                httpRequest.Content = JsonContent.Create(request);

                var response = await _httpClient.SendAsync(httpRequest);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ExposureCompleteResponseDto>();
                    Logger.Info($"[API-CALL] ReportExposureComplete RESPONSE: Acknowledged={result?.Acknowledged}, NewCompleted={result?.NewCompletedCount}, TotalGoal={result?.TotalGoalCount}, Message={result?.Message}");
                    return result;
                }

                Logger.Warning($"[API-CALL] ReportExposureComplete FAILED: {response.StatusCode}");
                return new ExposureCompleteResponseDto { Acknowledged = false };
            }
            catch (Exception ex)
            {
                Logger.Warning($"[API-CALL] ReportExposureComplete ERROR: {ex.Message}");
                return new ExposureCompleteResponseDto { Acknowledged = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// Report an error and get handling instructions
        /// </summary>
        public async Task<ErrorResponseDto?> ReportErrorAsync(ErrorReportDto request)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return new ErrorResponseDto { Instruction = ErrorInstruction.Stop };

                var url = $"{_getBaseUrl()}/api/client/slot/error";
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                httpRequest.Content = JsonContent.Create(request);

                var response = await _httpClient.SendAsync(httpRequest);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
                }

                return new ErrorResponseDto { Instruction = ErrorInstruction.Stop };
            }
            catch (Exception ex)
            {
                Logger.Warning($"SlotApiService: ReportError failed: {ex.Message}");
                return new ErrorResponseDto { Instruction = ErrorInstruction.Stop, Message = ex.Message };
            }
        }

        /// <summary>
        /// Start a new imaging session
        /// </summary>
        public async Task<bool> StartSessionAsync(Guid? configurationId = null)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return false;

                var obsId = _getObservatoryId() ?? Guid.Empty;
                var eqId = _getEquipmentId() ?? Guid.Empty;

                var url = $"{_getBaseUrl()}/api/client/slot/start?observatoryId={obsId}&equipmentId={eqId}";
                if (configurationId.HasValue) url += $"&configurationId={configurationId.Value}";

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"SlotApiService: StartSession failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stop the current imaging session
        /// </summary>
        public async Task<bool> StopSessionAsync()
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return false;

                var url = $"{_getBaseUrl()}/api/client/slot/stop";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"SlotApiService: StopSession failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Update session status
        /// </summary>
        public async Task<bool> UpdateSessionStatusAsync(UpdateSessionStatusDto status)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return false;

                // Sanitize double values to prevent JSON serialization errors with Infinity/NaN
                SanitizeStatusDto(status);

                var url = $"{_getBaseUrl()}/api/client/slot/status";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = JsonContent.Create(status);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"SlotApiService: UpdateSessionStatus failed: {ex.Message}");
                return false;
            }
        }

        private static void SanitizeStatusDto(UpdateSessionStatusDto status)
        {
            // Mount data
            status.MountRightAscension = SanitizeDouble(status.MountRightAscension);
            status.MountDeclination = SanitizeDouble(status.MountDeclination);
            status.MountAltitude = SanitizeDouble(status.MountAltitude);
            status.MountAzimuth = SanitizeDouble(status.MountAzimuth);
            
            // Equipment data
            status.FocuserTemperature = SanitizeDouble(status.FocuserTemperature);
            status.RotatorAngle = SanitizeDouble(status.RotatorAngle);
            status.GuidingRaRms = SanitizeDouble(status.GuidingRaRms);
            status.GuidingDecRms = SanitizeDouble(status.GuidingDecRms);
            status.CameraTemperature = SanitizeDouble(status.CameraTemperature);
            status.CameraTargetTemperature = SanitizeDouble(status.CameraTargetTemperature);
            status.CoolerPower = SanitizeDouble(status.CoolerPower);
            status.ExposureDurationSeconds = SanitizeDouble(status.ExposureDurationSeconds);
            status.ExposureElapsedSeconds = SanitizeDouble(status.ExposureElapsedSeconds);
            
            // Weather data
            status.WeatherTemperature = SanitizeDouble(status.WeatherTemperature);
            status.WeatherHumidity = SanitizeDouble(status.WeatherHumidity);
            status.WeatherDewPoint = SanitizeDouble(status.WeatherDewPoint);
            status.WeatherPressure = SanitizeDouble(status.WeatherPressure);
            status.WeatherCloudCover = SanitizeDouble(status.WeatherCloudCover);
            status.WeatherRainRate = SanitizeDouble(status.WeatherRainRate);
            status.WeatherWindSpeed = SanitizeDouble(status.WeatherWindSpeed);
            status.WeatherWindDirection = SanitizeDouble(status.WeatherWindDirection);
            status.WeatherSkyQuality = SanitizeDouble(status.WeatherSkyQuality);
            status.WeatherSkyTemperature = SanitizeDouble(status.WeatherSkyTemperature);
            status.WeatherStarFWHM = SanitizeDouble(status.WeatherStarFWHM);
            
            // Nested WeatherDataPoint
            if (status.WeatherDataPoint != null)
            {
                SanitizeWeatherDataPoint(status.WeatherDataPoint);
            }
            
            // Autofocus reports
            if (status.CurrentAutofocusReport != null)
                SanitizeAutofocusReport(status.CurrentAutofocusReport);
            if (status.LastAutofocusReport != null)
                SanitizeAutofocusReport(status.LastAutofocusReport);
            if (status.AutofocusHistory != null)
            {
                foreach (var report in status.AutofocusHistory)
                    SanitizeAutofocusReport(report);
            }
            
            // Plate solve reports
            if (status.LastPlateSolveReport != null)
                SanitizePlateSolveReport(status.LastPlateSolveReport);
            if (status.PlateSolveHistory != null)
            {
                foreach (var report in status.PlateSolveHistory)
                    SanitizePlateSolveReport(report);
            }
            
            // Image history
            if (status.ImageHistory != null)
            {
                foreach (var item in status.ImageHistory)
                    SanitizeImageHistoryItem(item);
            }
        }
        
        private static void SanitizeWeatherDataPoint(WeatherDataPointDto wp)
        {
            wp.Temperature = SanitizeDouble(wp.Temperature);
            wp.Humidity = SanitizeDouble(wp.Humidity);
            wp.DewPoint = SanitizeDouble(wp.DewPoint);
            wp.Pressure = SanitizeDouble(wp.Pressure);
            wp.CloudCover = SanitizeDouble(wp.CloudCover);
            wp.RainRate = SanitizeDouble(wp.RainRate);
            wp.WindSpeed = SanitizeDouble(wp.WindSpeed);
            wp.WindDirection = SanitizeDouble(wp.WindDirection);
            wp.WindGust = SanitizeDouble(wp.WindGust);
            wp.SkyQuality = SanitizeDouble(wp.SkyQuality);
            wp.SkyTemperature = SanitizeDouble(wp.SkyTemperature);
            wp.StarFWHM = SanitizeDouble(wp.StarFWHM);
        }
        
        private static void SanitizeAutofocusReport(AutofocusReportDto af)
        {
            af.FinalHfr = SanitizeDoubleRequired(af.FinalHfr);
            af.Temperature = SanitizeDoubleRequired(af.Temperature);
            af.RSquaredHyperbolic = SanitizeDouble(af.RSquaredHyperbolic);
            af.RSquaredParabolic = SanitizeDouble(af.RSquaredParabolic);
            af.RSquared = SanitizeDouble(af.RSquared);
            if (af.DataPoints != null)
            {
                foreach (var dp in af.DataPoints)
                    dp.Hfr = SanitizeDoubleRequired(dp.Hfr);
            }
        }
        
        private static void SanitizePlateSolveReport(PlateSolveReportDto ps)
        {
            ps.SolvedRa = SanitizeDouble(ps.SolvedRa);
            ps.SolvedDec = SanitizeDouble(ps.SolvedDec);
            ps.Rotation = SanitizeDouble(ps.Rotation);
            ps.PixelScale = SanitizeDouble(ps.PixelScale);
            ps.SolveDurationSeconds = SanitizeDouble(ps.SolveDurationSeconds);
            ps.SeparationArcsec = SanitizeDouble(ps.SeparationArcsec);
            ps.RaSeparationArcsec = SanitizeDouble(ps.RaSeparationArcsec);
            ps.DecSeparationArcsec = SanitizeDouble(ps.DecSeparationArcsec);
        }
        
        private static void SanitizeImageHistoryItem(ImageHistoryItemDto img)
        {
            img.ExposureTime = SanitizeDouble(img.ExposureTime);
            img.HFR = SanitizeDouble(img.HFR);
        }

        private static double? SanitizeDouble(double? value)
        {
            if (!value.HasValue) return null;
            if (double.IsInfinity(value.Value) || double.IsNaN(value.Value)) return null;
            return value;
        }
        
        private static double SanitizeDoubleRequired(double value)
        {
            if (double.IsInfinity(value) || double.IsNaN(value)) return 0;
            return value;
        }
    }
}
