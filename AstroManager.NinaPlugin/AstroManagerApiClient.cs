using AstroManager.NinaPlugin.Services;
using Shared.Model.DTO.Client;
using Shared.Model.DTO.Scheduler;
using Shared.Model.DTO.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace AstroManager.NinaPlugin
{
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class AstroManagerApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly AstroManagerSettings _settings;
        private readonly OfflineDataStore _offlineStore;
        
        // Extracted services for cleaner architecture
        private readonly SlotApiService _slotService;
        private readonly TargetApiService _targetService;
        private readonly ConfigurationApiService _configurationService;

        private const string ClientNameHeader = "X-AstroManager-Client";
        private const string ClientVersionHeader = "X-AstroManager-Client-Version";
        private const string ClientPlatformHeader = "X-AstroManager-Client-Platform";
        private const string ApiVersionHeader = "X-AstroManager-Api-Version";
        private const string CurrentApiVersion = "1";
        private static readonly string PluginVersion = ResolvePluginVersion();
        
        // Shared JSON options with enum string converter for API deserialization
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        
        // JWT token management
        private string? _jwtToken;
        private string? _refreshToken;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private Guid _userId;
        private Guid _clientLicenseId;
        private Guid? _observatoryId;
        private Guid? _equipmentId;
        private ClientConfigurationDto? _currentClientConfiguration;

        // Captured image summary cache (short-lived to avoid repetitive UI fetches)
        private static readonly TimeSpan CapturedImageSummaryCacheDuration = TimeSpan.FromMinutes(1);
        private readonly object _capturedImageSummaryCacheLock = new();
        private readonly Dictionary<Guid, (CapturedImageSummaryDto Summary, DateTime CachedAtUtc)> _capturedImageSummaryCache = new();
        
        // Offline mode
        private OfflineTokenDto? _offlineToken;
        private bool _isOfflineMode;
        private DateTime _lastSyncAttempt = DateTime.MinValue;

        [ImportingConstructor]
        public AstroManagerApiClient(AstroManagerSettings settings)
        {
            _settings = settings;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            ConfigureClientMetadataHeaders();
            _offlineStore = new OfflineDataStore();
            
            // Initialize extracted services with dependency injection via lambdas
            _slotService = new SlotApiService(
                _httpClient,
                EnsureAuthenticatedAsync,
                () => _jwtToken,
                () => BaseUrl,
                () => _settings.ObservatoryId ?? _observatoryId,
                () => _settings.EquipmentId ?? _equipmentId);
                
            _targetService = new TargetApiService(
                _httpClient,
                _settings,
                EnsureAuthenticatedAsync,
                () => _jwtToken,
                () => BaseUrl,
                () => _settings.ObservatoryId ?? _observatoryId,
                () => _settings.EquipmentId ?? _equipmentId);
                
            _configurationService = new ConfigurationApiService(
                _httpClient,
                EnsureAuthenticatedAsync,
                () => _jwtToken,
                () => BaseUrl);
            
            // Initialize offline store async
            Task.Run(async () => await InitializeOfflineStoreAsync());
        }

        /// <summary>
        /// Refresh client configuration snapshot used by scheduler/runtime policy checks.
        /// Uses a single lightweight API call and updates cached settings/policy in-memory.
        /// </summary>
        public async Task<bool> RefreshClientConfigurationAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_settings.LicenseKey))
                {
                    return false;
                }

                if (!await EnsureAuthenticatedAsync())
                {
                    return false;
                }

                SetBearerToken();
                var configUrl = $"{BaseUrl}/api/client-configurations/by-license/{Uri.EscapeDataString(_settings.LicenseKey)}";
                var response = await _httpClient.GetAsync(configUrl);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                var configDto = await response.Content.ReadFromJsonAsync<ClientConfigurationDto>(_jsonOptions);
                if (configDto == null)
                {
                    return false;
                }

                _currentClientConfiguration = configDto;
                _settings.ObservatoryId = configDto.ObservatoryId;
                _settings.ObservatoryName = configDto.ObservatoryName;
                _settings.EquipmentId = configDto.EquipmentId;
                _settings.EquipmentName = configDto.EquipmentName;
                _settings.DefaultSchedulerConfigurationId = configDto.DefaultSchedulerConfigurationId;
                _settings.DefaultSchedulerConfigurationName = configDto.DefaultSchedulerConfigurationName;
                _settings.RuntimeStopSafetyPolicyId = configDto.RuntimeStopSafetyPolicyId;
                _settings.RuntimeStopSafetyPolicyName = configDto.RuntimeStopSafetyPolicyName;

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: RefreshClientConfigurationAsync failed: {ex.Message}");
                return false;
            }
        }
        
        private async Task InitializeOfflineStoreAsync()
        {
            try
            {
                await _offlineStore.InitializeAsync();
                _offlineToken = await _offlineStore.LoadOfflineTokenAsync();
                if (_offlineToken != null && !_offlineToken.IsExpired)
                {
                    Logger.Info($"AstroManagerApiClient: Loaded offline token, valid until {_offlineToken.ExpiresAt}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: Failed to initialize offline store: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Whether we're currently operating in offline mode
        /// </summary>
        public bool IsOfflineMode => _isOfflineMode;
        
        /// <summary>
        /// How long the offline token is valid for (if any)
        /// </summary>
        public TimeSpan? OfflineTimeRemaining => _offlineToken?.IsExpired == false ? _offlineToken.TimeRemaining : null;

        private string BaseUrl => _settings.ApiUrl?.TrimEnd('/') ?? "https://api.astro.sleeman.at";
        
        /// <summary>
        /// Check if a license key is configured
        /// </summary>
        public bool HasLicenseKey => !string.IsNullOrEmpty(_settings.LicenseKey);
        
        /// <summary>
        /// Get the settings instance
        /// </summary>
        public AstroManagerSettings GetSettings() => _settings;
        public ClientConfigurationDto? CurrentClientConfiguration => _currentClientConfiguration;
        
        /// <summary>
        /// Settings property for direct access
        /// </summary>
        public AstroManagerSettings Settings => _settings;

        private void ConfigureClientMetadataHeaders()
        {
            SetDefaultHeader(ClientNameHeader, "NINA Plugin");
            SetDefaultHeader(ClientVersionHeader, PluginVersion);
            SetDefaultHeader(ClientPlatformHeader, "NINA");
            SetDefaultHeader(ApiVersionHeader, CurrentApiVersion);
        }

        private void SetDefaultHeader(string name, string value)
        {
            _httpClient.DefaultRequestHeaders.Remove(name);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }

        private static string ResolvePluginVersion()
        {
            var assembly = typeof(AstroManagerApiClient).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
        }

        private void SetBearerToken()
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
            if (!string.IsNullOrEmpty(_jwtToken))
            {
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new AuthenticationHeaderValue("Bearer", _jwtToken);
            }
        }

        /// <summary>
        /// Authenticate with license key to get JWT token
        /// </summary>
        private async Task<(bool Success, string Message)> AuthenticateAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_settings.LicenseKey))
                {
                    return (false, "No license key configured");
                }

                var url = $"{BaseUrl}/api/client-auth/validate";
                Logger.Info($"AstroManagerApiClient: Authenticating at {url}");

                var payload = new
                {
                    LicenseKey = _settings.LicenseKey,
                    MachineId = GetMachineId(),
                    ClientVersion = PluginVersion
                };

                var response = await _httpClient.PostAsJsonAsync(url, payload);
                
                if (response.IsSuccessStatusCode)
                {
                    var authResponse = await response.Content.ReadFromJsonAsync<ClientAuthResponse>();
                    if (authResponse != null)
                    {
                        _jwtToken = authResponse.Token;
                        _refreshToken = authResponse.RefreshToken;
                        _tokenExpiry = DateTime.UtcNow.AddSeconds(authResponse.ExpiresIn - 60); // Refresh 1 min early
                        _userId = authResponse.UserId;
                        _clientLicenseId = authResponse.ClientLicenseId;
                        _observatoryId = authResponse.ObservatoryId;
                        _equipmentId = authResponse.EquipmentId;
                        _isOfflineMode = false;
                        
                        // Save offline token for future offline operation
                        if (authResponse.OfflineToken != null)
                        {
                            _offlineToken = authResponse.OfflineToken;
                            await _offlineStore.SaveOfflineTokenAsync(authResponse.OfflineToken);
                            Logger.Info($"AstroManagerApiClient: Offline token saved, valid until {authResponse.OfflineToken.ExpiresAt}");
                        }
                        
                        // Sync any queued offline data
                        _ = Task.Run(async () => await SyncOfflineDataAsync());
                        
                        Logger.Info($"AstroManagerApiClient: Authenticated as {authResponse.ClientName} (Observatory: {authResponse.ObservatoryName}, Equipment: {authResponse.EquipmentName})");
                        return (true, $"Authenticated as {authResponse.ClientName}");
                    }
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: Authentication failed: {errorContent}");
                    
                    // Try to parse specific error message from API
                    try
                    {
                        var errorObj = System.Text.Json.JsonDocument.Parse(errorContent);
                        if (errorObj.RootElement.TryGetProperty("message", out var messageElement))
                        {
                            var errorMessage = messageElement.GetString();
                            return (false, errorMessage ?? "Authentication failed");
                        }
                    }
                    catch { /* Ignore JSON parsing errors */ }
                    
                    return (false, "Invalid license key or machine ID");
                }
                
                return (false, $"Authentication failed: {response.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                // Network error - try offline mode
                Logger.Warning($"AstroManagerApiClient: Network error during auth: {ex.Message}");
                return await TryOfflineModeAsync(ex.Message);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || ex.CancellationToken.IsCancellationRequested == false)
            {
                // Timeout - try offline mode
                Logger.Warning($"AstroManagerApiClient: Timeout during auth");
                return await TryOfflineModeAsync("Connection timeout");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManagerApiClient: Authentication error: {ex.Message}");
                return await TryOfflineModeAsync(ex.Message);
            }
        }
        
        /// <summary>
        /// Try to use offline mode with cached token
        /// </summary>
        private async Task<(bool Success, string Message)> TryOfflineModeAsync(string errorMessage)
        {
            // Check if we have a valid offline token
            if (_offlineToken == null)
            {
                _offlineToken = await _offlineStore.LoadOfflineTokenAsync();
            }
            
            if (_offlineToken != null && !_offlineToken.IsExpired)
            {
                // Validate machine fingerprint
                var currentFingerprint = GetMachineId();
                if (_offlineToken.MachineFingerprint == currentFingerprint)
                {
                    _isOfflineMode = true;
                    _clientLicenseId = _offlineToken.LicenseId;
                    _userId = _offlineToken.UserId;
                    
                    var remaining = _offlineToken.TimeRemaining;
                    Logger.Info($"AstroManagerApiClient: OFFLINE MODE - Token valid for {remaining.Hours}h {remaining.Minutes}m");
                    return (true, $"Offline mode ({remaining.Hours}h {remaining.Minutes}m remaining)");
                }
                else
                {
                    Logger.Warning("AstroManagerApiClient: Offline token machine fingerprint mismatch");
                }
            }
            else if (_offlineToken?.IsExpired == true)
            {
                Logger.Warning("AstroManagerApiClient: Offline token expired");
            }
            
            return (false, $"Server unreachable and no valid offline token: {errorMessage}");
        }
        
        #region Offline Data Queue
        
        /// <summary>
        /// Queue a capture for later sync (used when offline)
        /// </summary>
        public async Task QueueCaptureAsync(OfflineCaptureDto capture)
        {
            await _offlineStore.QueueCaptureAsync(capture);
        }
        
        /// <summary>
        /// Queue a capture with parameters (convenience method)
        /// </summary>
        public async Task QueueCaptureAsync(Guid? targetId, Guid? imagingGoalId, Guid? panelId, 
            string? filter, double? exposureTime, bool success, string? fileName,
            double? hfr = null, int? detectedStars = null, double? cameraTemp = null, int? gain = null)
        {
            var capture = new OfflineCaptureDto
            {
                Id = Guid.NewGuid(),
                CapturedAt = DateTime.UtcNow,
                TargetId = targetId,
                ImagingGoalId = imagingGoalId,
                PanelId = panelId,
                Filter = filter,
                ExposureTimeSeconds = exposureTime,
                Success = success,
                FileName = fileName,
                HFR = hfr,
                DetectedStars = detectedStars,
                CameraTemp = cameraTemp,
                Gain = gain
            };
            
            await _offlineStore.QueueCaptureAsync(capture);
            Logger.Debug($"AstroManagerApiClient: Queued capture for offline sync - Target={targetId}, Filter={filter}");
        }
        
        /// <summary>
        /// Get count of unsynced captures
        /// </summary>
        public async Task<int> GetUnsyncedCaptureCountAsync()
        {
            return await _offlineStore.GetUnsyncedCountAsync();
        }
        
        /// <summary>
        /// Get unsynced captures for manual sync
        /// </summary>
        public async Task<List<OfflineCaptureDto>> GetUnsyncedCapturesAsync(int maxCount = 50)
        {
            return await _offlineStore.GetUnsyncedCapturesAsync(maxCount);
        }
        
        /// <summary>
        /// Mark captures as synced
        /// </summary>
        public async Task MarkCapturesSyncedAsync(IEnumerable<Guid> captureIds)
        {
            await _offlineStore.MarkCapturesSyncedAsync(captureIds);
        }
        
        /// <summary>
        /// Sync queued offline data to server
        /// </summary>
        public async Task<(int Synced, int Failed)> SyncOfflineDataAsync()
        {
            if (_isOfflineMode)
            {
                Logger.Debug("AstroManagerApiClient: Skipping sync - still in offline mode");
                return (0, 0);
            }
            
            // Rate limit sync attempts
            if ((DateTime.UtcNow - _lastSyncAttempt).TotalSeconds < 30)
            {
                return (0, 0);
            }
            _lastSyncAttempt = DateTime.UtcNow;
            
            var captures = await _offlineStore.GetUnsyncedCapturesAsync(50);
            if (!captures.Any())
            {
                return (0, 0);
            }
            
            Logger.Info($"AstroManagerApiClient: Syncing {captures.Count} offline captures...");
            
            int synced = 0;
            int failed = 0;
            
            try
            {
                SetBearerToken();
                
                var request = new OfflineSyncRequestDto { Captures = captures };
                var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/api/client-slot/sync-offline", request);
                
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<OfflineSyncResponseDto>();
                    if (result?.Success == true && result.SyncedIds.Any())
                    {
                        await _offlineStore.MarkCapturesSyncedAsync(result.SyncedIds);
                        synced = result.SyncedCount;
                        Logger.Info($"AstroManagerApiClient: Successfully synced {synced} captures");
                    }
                }
                else
                {
                    failed = captures.Count;
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: Sync failed: {response.StatusCode} - {error}");
                    
                    // Update attempt counts
                    foreach (var capture in captures)
                    {
                        await _offlineStore.UpdateSyncAttemptAsync(capture.Id, error);
                    }
                }
            }
            catch (Exception ex)
            {
                failed = captures.Count;
                Logger.Error($"AstroManagerApiClient: Sync error: {ex.Message}");
            }
            
            // Cleanup old synced data
            await _offlineStore.CleanupOldSyncedCapturesAsync();
            
            return (synced, failed);
        }
        
        #endregion

        /// <summary>
        /// Get the current JWT token for SignalR authentication
        /// </summary>
        public async Task<string?> GetJwtTokenAsync()
        {
            if (await EnsureAuthenticatedAsync())
            {
                return _jwtToken;
            }
            return null;
        }
        
        /// <summary>
        /// Ensure we have a valid token, refreshing if needed
        /// </summary>
        private async Task<bool> EnsureAuthenticatedAsync()
        {
            if (!string.IsNullOrEmpty(_jwtToken) && DateTime.UtcNow < _tokenExpiry)
            {
                return true; // Token still valid
            }

            // Try to refresh if we have a refresh token
            if (!string.IsNullOrEmpty(_refreshToken))
            {
                try
                {
                    var response = await _httpClient.PostAsJsonAsync(
                        $"{BaseUrl}/api/client-auth/refresh",
                        new { RefreshToken = _refreshToken });
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var authResponse = await response.Content.ReadFromJsonAsync<ClientAuthResponse>();
                        if (authResponse != null)
                        {
                            _jwtToken = authResponse.Token;
                            _refreshToken = authResponse.RefreshToken;
                            _tokenExpiry = DateTime.UtcNow.AddSeconds(authResponse.ExpiresIn - 60);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"AstroManagerApiClient: Token refresh failed: {ex.Message}");
                }
            }

            // Need to re-authenticate
            var (success, _) = await AuthenticateAsync();
            return success;
        }

        private string GetMachineId()
        {
            // Generate a stable machine ID based on machine name and username
            var machineInfo = $"{Environment.MachineName}-{Environment.UserName}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(machineInfo));
            return Convert.ToBase64String(hash).Substring(0, 16);
        }

        /// <summary>
        /// Test connection and validate license key
        /// </summary>
        public async Task<(bool Success, string Message, ClientConfigResponse? Config)> TestConnectionAsync()
        {
            try
            {
                // Step 1: Authenticate with license key
                var (authSuccess, authMessage) = await AuthenticateAsync();
                if (!authSuccess)
                {
                    return (false, authMessage, null);
                }

                // Step 2: Get configuration
                SetBearerToken();
                var configUrl = $"{BaseUrl}/api/client-configurations/by-license/{Uri.EscapeDataString(_settings.LicenseKey)}";
                var response = await _httpClient.GetAsync(configUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    var configDto = await response.Content.ReadFromJsonAsync<ClientConfigurationDto>(_jsonOptions);
                    if (configDto != null)
                    {
                        _currentClientConfiguration = configDto;

                        // Update settings with config info (IDs and names)
                        _settings.ObservatoryId = configDto.ObservatoryId;
                        _settings.ObservatoryName = configDto.ObservatoryName;
                        _settings.EquipmentId = configDto.EquipmentId;
                        _settings.EquipmentName = configDto.EquipmentName;
                        _settings.RuntimeStopSafetyPolicyId = configDto.RuntimeStopSafetyPolicyId;
                        _settings.RuntimeStopSafetyPolicyName = configDto.RuntimeStopSafetyPolicyName;
                        
                        var config = new ClientConfigResponse
                        {
                            ObservatoryId = configDto.ObservatoryId,
                            ObservatoryName = configDto.ObservatoryName,
                            EquipmentId = configDto.EquipmentId,
                            EquipmentName = configDto.EquipmentName,
                            ImagingSoftware = configDto.ImagingSoftware,
                            DefaultSchedulerConfigurationId = configDto.DefaultSchedulerConfigurationId,
                            DefaultSchedulerConfigurationName = configDto.DefaultSchedulerConfigurationName,
                            RuntimeStopSafetyPolicyId = configDto.RuntimeStopSafetyPolicyId,
                            RuntimeStopSafetyPolicyName = configDto.RuntimeStopSafetyPolicyName
                        };
                        
                        // Store the default scheduler config for use when "Use Server Default" is selected
                        _settings.DefaultSchedulerConfigurationId = configDto.DefaultSchedulerConfigurationId;
                        _settings.DefaultSchedulerConfigurationName = configDto.DefaultSchedulerConfigurationName;
                        
                        return (true, "Connected successfully!", config);
                    }
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // License is valid but no Observatory/Equipment configured yet in AstroManager web
                    return (true, "Connected! Configure Observatory/Equipment in AstroManager web.", null);
                }
                
                return (false, $"Config fetch failed: {response.StatusCode}", null);
            }
            catch (HttpRequestException ex)
            {
                Logger.Error($"AstroManagerApiClient: Connection test failed: {ex.Message}");
                return (false, $"Network error: {ex.Message}", null);
            }
            catch (TaskCanceledException)
            {
                return (false, "Connection timeout", null);
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManagerApiClient: Connection test failed: {ex.Message}");
                return (false, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Update meridian flip settings on server from NINA profile
        /// </summary>
        public async Task<bool> UpdateMeridianFlipSettingsAsync(bool enabled, double minutesAfterMeridian, double pauseTimeBeforeFlip, double maxMinutesToMeridian)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    Logger.Warning("AstroManagerApiClient: Cannot update meridian flip settings - not authenticated");
                    return false;
                }

                var url = $"{BaseUrl}/api/client-configurations/by-license/{Uri.EscapeDataString(_settings.LicenseKey)}/meridian-flip";
                
                var dto = new
                {
                    Enabled = enabled,
                    MinutesAfterMeridian = minutesAfterMeridian,
                    PauseTimeBeforeFlipMinutes = pauseTimeBeforeFlip,
                    MaxMinutesToMeridian = maxMinutesToMeridian
                };

                var jsonContent = JsonSerializer.Serialize(dto);
                using var request = new HttpRequestMessage(HttpMethod.Patch, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    Logger.Info($"AstroManagerApiClient: Updated meridian flip settings: Enabled={enabled}, After={minutesAfterMeridian}min, Pause={pauseTimeBeforeFlip}min, Max={maxMinutesToMeridian}min");
                    return true;
                }

                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: Failed to update meridian flip settings: {response.StatusCode} - {error}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: Error updating meridian flip settings: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sync scheduled targets from API
        /// </summary>
        public async Task<(bool Success, string Message, List<ScheduledTargetDto>? Targets)> SyncScheduledTargetsAsync()
        {
            try
            {
                // Ensure we're authenticated
                if (!await EnsureAuthenticatedAsync())
                {
                    return (false, "Authentication failed", null);
                }

                // Get scheduled targets by equipment (if configured)
                string url;
                if (_settings.EquipmentId.HasValue && _settings.EquipmentId != Guid.Empty)
                {
                    url = $"{BaseUrl}/api/scheduler/targets/by-equipment/{_settings.EquipmentId}";
                }
                else if (_settings.ObservatoryId.HasValue && _settings.ObservatoryId != Guid.Empty)
                {
                    url = $"{BaseUrl}/api/scheduler/targets/by-observatory/{_settings.ObservatoryId}";
                }
                else
                {
                    // Get all targets for user
                    url = $"{BaseUrl}/api/scheduler/targets";
                }
                
                Logger.Info($"AstroManagerApiClient: Syncing targets from {url}");
                
                // Use HttpRequestMessage to ensure Bearer token is applied
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var targetsResponse = await _httpClient.SendAsync(request);
                
                if (!targetsResponse.IsSuccessStatusCode)
                {
                    var error = await targetsResponse.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: Sync failed: {targetsResponse.StatusCode} - {error}");
                    return (false, $"Failed to get targets: {targetsResponse.StatusCode}", null);
                }

                var targets = await targetsResponse.Content.ReadFromJsonAsync<List<ScheduledTargetDto>>(_jsonOptions);
                _settings.LastSyncTime = DateTime.UtcNow;
                
                // Log mosaic targets and their panels
                var mosaicTargets = targets?.Where(t => t.IsMosaic).ToList() ?? new List<ScheduledTargetDto>();
                foreach (var mt in mosaicTargets)
                {
                    Logger.Info($"AstroManagerApiClient: Mosaic target '{mt.Name}' has {mt.Panels?.Count ?? 0} panels");
                }
                
                Logger.Info($"AstroManagerApiClient: Synced {targets?.Count ?? 0} targets ({mosaicTargets.Count} mosaic)");
                return (true, $"Synced {targets?.Count ?? 0} targets", targets ?? new List<ScheduledTargetDto>());
            }
            catch (HttpRequestException ex)
            {
                Logger.Error($"AstroManagerApiClient: Sync failed: {ex.Message}");
                return (false, $"Network error: {ex.Message}", null);
            }
            catch (TaskCanceledException)
            {
                return (false, "Connection timeout", null);
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManagerApiClient: Sync failed: {ex.Message}");
                return (false, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Get next target for imaging
        /// </summary>
        public async Task<NextTargetDto?> GetNextTargetAsync(Guid? configurationId = null)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    return null;
                }
                
                var obsId = _settings.ObservatoryId ?? Guid.Empty;
                var eqId = _settings.EquipmentId ?? Guid.Empty;
                var url = $"{BaseUrl}/api/client/sessions/current?observatoryId={obsId}&equipmentId={eqId}";
                
                // Add configuration ID if specified
                if (configurationId.HasValue)
                {
                    url += $"&configurationId={configurationId.Value}";
                }
                
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<NextTargetDto>(_jsonOptions);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetNextTarget failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Report imaging progress back to API
        /// </summary>
        public async Task<bool> ReportProgressAsync(Guid targetId, Guid goalId, int completedExposures)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    return false;
                }
                
                var url = $"{BaseUrl}/api/scheduler/progress";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(new { targetId, goalId, completedExposures });
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: ReportProgress failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send heartbeat to API
        /// </summary>
        public async Task<bool> SendHeartbeatAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    return false;
                }
                
                var url = $"{BaseUrl}/api/client-auth/heartbeat";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: Heartbeat failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Update a scheduled target on the server
        /// </summary>
        public async Task<bool> UpdateTargetAsync(ScheduledTargetDto target)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    return false;
                }
                
                // Convert to UpdateScheduledTargetDto for API
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
                    // Mosaic properties
                    IsMosaic = target.IsMosaic,
                    MosaicPanelsX = target.MosaicPanelsX,
                    MosaicPanelsY = target.MosaicPanelsY,
                    MosaicOverlapPercent = target.MosaicOverlapPercent,
                    MosaicUseRotator = target.MosaicUseRotator,
                    UseCustomPanelGoals = target.UseCustomPanelGoals,
                    MosaicShootingStrategy = target.MosaicShootingStrategy,
                    MosaicPanelOrderingMethod = target.MosaicPanelOrderingMethod,
                    GoalOrderingMethod = target.GoalOrderingMethod,
                    // Image properties
                    ShowImage = target.ShowImage,
                    AstroBinImageId = target.AstroBinImageId,
                    AstroBinImageUrl = target.AstroBinImageUrl,
                    SchedulerTargetTemplateId = target.SchedulerTargetTemplateId,
                    UpdateSchedulerTargetTemplate = true
                };
                
                var url = $"{BaseUrl}/api/scheduler/targets/{target.Id}";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(updateDto);
                
                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: Update target failed: {response.StatusCode} - {error}");
                }
                
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: Update target failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get a single target by ID from the API (includes panels)
        /// </summary>
        public async Task<ScheduledTargetDto?> GetTargetByIdAsync(Guid targetId)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    return null;
                }
                
                var url = $"{BaseUrl}/api/scheduler/targets/{targetId}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: Get target failed: {response.StatusCode} - {error}");
                    return null;
                }
                
                var target = await response.Content.ReadFromJsonAsync<ScheduledTargetDto>(_jsonOptions);
                Logger.Info($"AstroManagerApiClient: GetTargetById - IsMosaic={target?.IsMosaic}, Panels={target?.Panels?.Count ?? 0}");
                return target;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: Get target failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Delete a target from the API
        /// </summary>
        public async Task<bool> DeleteTargetAsync(Guid targetId)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    return false;
                }
                
                var url = $"{BaseUrl}/api/scheduler/targets/{targetId}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: Delete target failed: {response.StatusCode} - {error}");
                }
                
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: Delete target failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fetch exposure templates from API, filtered by license-linked Observatory+Equipment
        /// </summary>
        public async Task<List<ExposureTemplateDto>?> GetExposureTemplatesAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                // Build URL with Observatory+Equipment filter from license
                var url = $"{BaseUrl}/api/exposure-templates";
                var queryParams = new List<string>();
                
                if (_observatoryId.HasValue)
                    queryParams.Add($"observatoryId={_observatoryId.Value}");
                if (_equipmentId.HasValue)
                    queryParams.Add($"equipmentId={_equipmentId.Value}");
                
                if (queryParams.Any())
                    url += "?" + string.Join("&", queryParams);
                
                Logger.Debug($"AstroManagerApiClient: Fetching exposure templates from {url}");
                
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var templates = await response.Content.ReadFromJsonAsync<List<ExposureTemplateDto>>(_jsonOptions);
                    Logger.Info($"AstroManagerApiClient: Loaded {templates?.Count ?? 0} exposure templates for Observatory+Equipment");
                    return templates;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetExposureTemplates failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetch scheduler configurations from API
        /// </summary>
        public Task<List<SchedulerConfigurationDto>?> GetSchedulerConfigurationsAsync()
            => _configurationService.GetSchedulerConfigurationsAsync();

        /// <summary>
        /// Get scheduler preview for a specific date
        /// </summary>
        public async Task<SchedulerPreviewDto?> GetPreviewAsync(Guid configurationId, DateTime previewDate, TimeSpan? startTime = null)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                if (!_observatoryId.HasValue || !_equipmentId.HasValue)
                {
                    Logger.Warning($"AstroManagerApiClient: Observatory or Equipment not set for preview. ObservatoryId={_observatoryId}, EquipmentId={_equipmentId}");
                    return null;
                }
                
                var url = $"{BaseUrl}/api/scheduler/preview/tonight?observatoryId={_observatoryId}&equipmentId={_equipmentId}&configurationId={configurationId}&date={previewDate:yyyy-MM-dd}";
                if (startTime.HasValue)
                {
                    url += $"&startTime={startTime.Value:hh\\:mm}";
                }
                Logger.Debug($"AstroManagerApiClient: Fetching tonight's preview from {url}");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var preview = await response.Content.ReadFromJsonAsync<SchedulerPreviewDto>(_jsonOptions);
                    Logger.Info($"AstroManagerApiClient: Got preview with {preview?.Sessions?.Count ?? 0} sessions");
                    return preview;
                }
                
                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: GetTonightPreview failed: {response.StatusCode} - {error}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetTonightPreview failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get observatory data from license (cached from auth response)
        /// </summary>
        public async Task<ObservatoryDto?> GetLicenseObservatoryAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                if (!_observatoryId.HasValue)
                {
                    Logger.Warning("AstroManagerApiClient: No observatory linked to license");
                    return null;
                }
                
                // Fetch observatory details from API
                var url = $"{BaseUrl}/api/observatories/{_observatoryId.Value}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var observatory = await response.Content.ReadFromJsonAsync<ObservatoryDto>(_jsonOptions);
                    Logger.Info($"AstroManagerApiClient: Got observatory '{observatory?.Name}' at ({observatory?.Latitude}, {observatory?.Longitude})");
                    return observatory;
                }
                
                Logger.Warning($"AstroManagerApiClient: GetLicenseObservatory failed: {response.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetLicenseObservatory failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Generate scheduler preview for a specific date
        /// </summary>
        public async Task<SchedulerPreviewDto?> GeneratePreviewAsync(SchedulerPreviewRequestDto request)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/scheduler/preview";
                Logger.Debug($"AstroManagerApiClient: Generating preview for {request.PreviewDate}");
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                httpRequest.Content = JsonContent.Create(request);
                
                var response = await _httpClient.SendAsync(httpRequest);
                
                if (response.IsSuccessStatusCode)
                {
                    var preview = await response.Content.ReadFromJsonAsync<SchedulerPreviewDto>(_jsonOptions);
                    Logger.Info($"AstroManagerApiClient: Generated preview with {preview?.Sessions?.Count ?? 0} sessions");
                    return preview;
                }
                
                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: GeneratePreview failed: {response.StatusCode} - {error}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GeneratePreview failed: {ex.Message}");
                return null;
            }
        }

        #region Progress Reporting

        /// <summary>
        /// Report completed exposure back to AstroManager
        /// </summary>
        public async Task ReportExposureCompletedAsync(Guid scheduledTargetId, Guid imagingGoalId, int exposureCount, int exposureTimeSeconds)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return;
                
                var url = $"{BaseUrl}/api/client/sessions/exposure-completed";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
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
                    Logger.Warning($"AstroManagerApiClient: ReportExposureCompleted failed: {response.StatusCode} - {error}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: ReportExposureCompleted failed: {ex.Message}");
            }
        }

        #endregion

        #region Exposure Template CRUD

        /// <summary>
        /// Create a new exposure template - automatically assigns license-linked Observatory+Equipment
        /// </summary>
        public async Task<ExposureTemplateDto?> CreateExposureTemplateAsync(CreateExposureTemplateDto dto)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                // Automatically assign the license-linked Observatory+Equipment
                dto.ObservatoryId = _observatoryId;
                dto.EquipmentId = _equipmentId;
                
                Logger.Debug($"AstroManagerApiClient: Creating exposure template '{dto.Name}' for Observatory={_observatoryId}, Equipment={_equipmentId}");
                
                var url = $"{BaseUrl}/api/exposure-templates";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var created = await response.Content.ReadFromJsonAsync<ExposureTemplateDto>(_jsonOptions);
                    Logger.Info($"AstroManagerApiClient: Created exposure template '{created?.Name}' ({created?.Id})");
                    return created;
                }
                
                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: CreateExposureTemplate failed: {response.StatusCode} - {error}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: CreateExposureTemplate failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Update an exposure template
        /// </summary>
        public async Task<ExposureTemplateDto?> UpdateExposureTemplateAsync(Guid id, UpdateExposureTemplateDto dto)
        {
            var (result, _) = await UpdateExposureTemplateWithConflictCheckAsync(id, dto);
            return result;
        }
        
        /// <summary>
        /// Update an exposure template with conflict detection
        /// Returns (result, conflictDetected) tuple
        /// </summary>
        public async Task<(ExposureTemplateDto? Result, bool ConflictDetected)> UpdateExposureTemplateWithConflictCheckAsync(Guid id, UpdateExposureTemplateDto dto)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return (null, false);
                
                var url = $"{BaseUrl}/api/exposure-templates/{id}";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ExposureTemplateDto>(_jsonOptions);
                    return (result, false);
                }
                
                // Check for conflict (HTTP 409)
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    Logger.Warning("AstroManagerApiClient: UpdateExposureTemplate conflict detected - record was modified");
                    return (null, true);
                }
                
                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: UpdateExposureTemplate failed: {response.StatusCode} - {error}");
                return (null, false);
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: UpdateExposureTemplate failed: {ex.Message}");
                return (null, false);
            }
        }

        /// <summary>
        /// Delete an exposure template
        /// Returns (success, inUseCount) - if inUseCount > 0 and success is false, template is in use
        /// </summary>
        public async Task<(bool Success, int InUseCount)> DeleteExposureTemplateAsync(Guid id, bool force = false)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return (false, 0);
                
                var url = $"{BaseUrl}/api/exposure-templates/{id}" + (force ? "?force=true" : "");
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                    return (true, 0);
                
                // Check for conflict (template in use)
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    // Parse usage count from response
                    try
                    {
                        var json = System.Text.Json.JsonDocument.Parse(content);
                        var imagingGoalCount = json.RootElement.TryGetProperty("imagingGoalCount", out var igc) ? igc.GetInt32() : 0;
                        var panelGoalCount = json.RootElement.TryGetProperty("panelGoalCount", out var pgc) ? pgc.GetInt32() : 0;
                        var templateItemCount = json.RootElement.TryGetProperty("templateItemCount", out var tic) ? tic.GetInt32() : 0;
                        return (false, imagingGoalCount + panelGoalCount + templateItemCount);
                    }
                    catch
                    {
                        return (false, 1); // At least 1 if we got conflict
                    }
                }
                
                return (false, 0);
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: DeleteExposureTemplate failed: {ex.Message}");
                return (false, 0);
            }
        }

        #endregion

        #region Scheduler Configuration CRUD (delegated to ConfigurationApiService)

        /// <summary>
        /// Create a new scheduler configuration
        /// </summary>
        public Task<SchedulerConfigurationDto?> CreateSchedulerConfigurationAsync(SchedulerConfigurationDto dto)
            => _configurationService.CreateSchedulerConfigurationAsync(dto);

        /// <summary>
        /// Update a scheduler configuration
        /// </summary>
        public Task<SchedulerConfigurationDto?> UpdateSchedulerConfigurationAsync(SchedulerConfigurationDto dto)
            => _configurationService.UpdateSchedulerConfigurationAsync(dto);

        /// <summary>
        /// Delete a scheduler configuration
        /// </summary>
        public Task<bool> DeleteSchedulerConfigurationAsync(Guid id)
            => _configurationService.DeleteSchedulerConfigurationAsync(id);

        /// <summary>
        /// Set a scheduler configuration as default
        /// </summary>
        public Task<bool> SetDefaultSchedulerConfigurationAsync(Guid id)
            => _configurationService.SetDefaultSchedulerConfigurationAsync(id);

        #endregion

        #region Moon Avoidance Profiles

        /// <summary>
        /// Fetch user's moon avoidance profiles from API (includes system defaults)
        /// </summary>
        public async Task<List<MoonAvoidanceProfileDto>?> GetMoonAvoidanceProfilesAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var allProfiles = new List<MoonAvoidanceProfileDto>();
                
                // Get system defaults first
                var systemDefaultsUrl = $"{BaseUrl}/api/moonavoidanceprofiles/system-defaults";
                Logger.Debug($"AstroManagerApiClient: Fetching system default moon avoidance profiles from {systemDefaultsUrl}");
                using var systemRequest = new HttpRequestMessage(HttpMethod.Get, systemDefaultsUrl);
                systemRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                var systemResponse = await _httpClient.SendAsync(systemRequest);
                if (systemResponse.IsSuccessStatusCode)
                {
                    var systemProfiles = await systemResponse.Content.ReadFromJsonAsync<List<MoonAvoidanceProfileDto>>(_jsonOptions);
                    if (systemProfiles != null)
                    {
                        foreach (var p in systemProfiles)
                        {
                            p.IsSystemDefault = true; // Ensure marked as system default
                        }
                        allProfiles.AddRange(systemProfiles);
                    }
                }
                
                // Get user profiles
                var url = $"{BaseUrl}/api/moonavoidanceprofiles";
                Logger.Debug($"AstroManagerApiClient: Fetching user moon avoidance profiles from {url}");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var userProfiles = await response.Content.ReadFromJsonAsync<List<MoonAvoidanceProfileDto>>(_jsonOptions);
                    if (userProfiles != null)
                    {
                        allProfiles.AddRange(userProfiles.Where(p => !p.IsSystemDefault));
                    }
                }
                
                Logger.Info($"AstroManagerApiClient: Loaded {allProfiles.Count} moon avoidance profiles (including system defaults)");
                return allProfiles;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetMoonAvoidanceProfiles failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create a new moon avoidance profile
        /// </summary>
        public async Task<MoonAvoidanceProfileDto?> CreateMoonAvoidanceProfileAsync(MoonAvoidanceProfileDto dto)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/moonavoidanceprofiles";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<MoonAvoidanceProfileDto>(_jsonOptions);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: CreateMoonAvoidanceProfile failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Update a moon avoidance profile
        /// </summary>
        public async Task<MoonAvoidanceProfileDto?> UpdateMoonAvoidanceProfileAsync(MoonAvoidanceProfileDto dto)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/moonavoidanceprofiles/{dto.Id}";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<MoonAvoidanceProfileDto>(_jsonOptions);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: UpdateMoonAvoidanceProfile failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Delete a moon avoidance profile
        /// </summary>
        public async Task<bool> DeleteMoonAvoidanceProfileAsync(Guid id)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return false;
                
                var url = $"{BaseUrl}/api/moonavoidanceprofiles/{id}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: DeleteMoonAvoidanceProfile failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Remote Commands

        /// <summary>
        /// Poll for pending remote commands
        /// </summary>
        public async Task<List<RemoteCommandDto>?> PollCommandsAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/client/commands/poll";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<List<RemoteCommandDto>>(_jsonOptions);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: PollCommands failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Acknowledge receipt of a command
        /// </summary>
        public async Task<bool> AcknowledgeCommandAsync(Guid commandId)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return false;
                
                var url = $"{BaseUrl}/api/client/commands/{commandId}/acknowledge";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: AcknowledgeCommand failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Update command status
        /// </summary>
        public async Task<bool> UpdateCommandStatusAsync(Guid commandId, RemoteCommandStatus status, string? resultMessage = null)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    Logger.Warning($"AstroManagerApiClient: UpdateCommandStatus failed - not authenticated");
                    return false;
                }
                
                var url = $"{BaseUrl}/api/client/commands/update-status";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var messageLength = resultMessage?.Length ?? 0;
                Logger.Debug($"AstroManagerApiClient: UpdateCommandStatus - CommandId={commandId}, Status={status}, MessageLength={messageLength}");
                
                request.Content = JsonContent.Create(new UpdateRemoteCommandStatusDto
                {
                    CommandId = commandId,
                    Status = status,
                    ResultMessage = resultMessage
                });
                
                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: UpdateCommandStatus failed - HTTP {(int)response.StatusCode}: {errorBody}");
                    return false;
                }
                
                Logger.Debug($"AstroManagerApiClient: UpdateCommandStatus success - CommandId={commandId}, Status={status}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: UpdateCommandStatus failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Upload last captured image thumbnail
        /// </summary>
        public async Task<bool> UploadImageThumbnailAsync(UploadImageThumbnailDto dto)
        {
            // Debug: Log request details before sending
            var thumbnailSize = dto?.ThumbnailBase64?.Length ?? 0;
            var microSize = dto?.MicroThumbnailBase64?.Length ?? 0;
            var totalPayloadEstimate = thumbnailSize + microSize + (dto?.RawThumbnailBase64?.Length ?? 0);
            
            Logger.Debug($"AstroManagerApiClient: UploadImageThumbnail START - File={dto?.FileName}, Filter={dto?.Filter}, " +
                $"Target={dto?.TargetName}, ThumbnailSize={thumbnailSize}, MicroSize={microSize}, TotalPayload~={totalPayloadEstimate}, " +
                $"TargetId={dto?.ScheduledTargetId}, GoalId={dto?.ImagingGoalId}");
            
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    Logger.Warning("AstroManagerApiClient: UploadImageThumbnail failed - not authenticated");
                    return false;
                }
                
                var url = $"{BaseUrl}/api/client/commands/upload-image";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);
                
                // Log serialized size
                var serializedContent = await request.Content.ReadAsStringAsync();
                Logger.Debug($"AstroManagerApiClient: Serialized JSON size = {serializedContent.Length} bytes");
                
                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: UploadImageThumbnail FAILED - HTTP {(int)response.StatusCode} {response.StatusCode}: {errorBody}");
                    Logger.Warning($"AstroManagerApiClient: Failed request details - File={dto?.FileName}, PayloadSize={serializedContent.Length}");
                }
                else
                {
                    Logger.Debug($"AstroManagerApiClient: UploadImageThumbnail SUCCESS - File={dto?.FileName}");
                }
                
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: UploadImageThumbnail EXCEPTION: {ex.Message}");
                Logger.Warning($"AstroManagerApiClient: Exception details - File={dto?.FileName}, Type={ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Logger.Warning($"AstroManagerApiClient: Inner exception: {ex.InnerException.Message}");
                }
                return false;
            }
        }

        #endregion

        #region Scheduler Preview

        /// <summary>
        /// Get scheduled sessions for a specific date
        /// </summary>
        public async Task<List<ScheduledSessionDto>?> GetSessionsByDateAsync(DateTime date)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/scheduler/sessions/by-date/{date:yyyy-MM-dd}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<List<ScheduledSessionDto>>(_jsonOptions);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetSessionsByDate failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get scheduled sessions within a date range
        /// </summary>
        public async Task<List<ScheduledSessionDto>?> GetSessionsAsync(DateTime startDate, DateTime endDate)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/scheduler/sessions?startDate={startDate:yyyy-MM-dd}&endDate={endDate:yyyy-MM-dd}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<List<ScheduledSessionDto>>(_jsonOptions);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetSessions failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Captured Images

        /// <summary>
        /// Get captured images summary for a target
        /// </summary>
        public async Task<CapturedImageSummaryDto?> GetCapturedImageSummaryAsync(Guid targetId)
        {
            try
            {
                lock (_capturedImageSummaryCacheLock)
                {
                    if (_capturedImageSummaryCache.TryGetValue(targetId, out var cached)
                        && (DateTime.UtcNow - cached.CachedAtUtc) < CapturedImageSummaryCacheDuration)
                    {
                        return cached.Summary;
                    }
                }

                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/scheduler/captured-images/target/{targetId}/summary";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var summary = await response.Content.ReadFromJsonAsync<CapturedImageSummaryDto>();
                    if (summary != null)
                    {
                        lock (_capturedImageSummaryCacheLock)
                        {
                            _capturedImageSummaryCache[targetId] = (summary, DateTime.UtcNow);

                            // best-effort bounded cleanup
                            if (_capturedImageSummaryCache.Count > 256)
                            {
                                var staleKeys = _capturedImageSummaryCache
                                    .Where(kvp => (DateTime.UtcNow - kvp.Value.CachedAtUtc) > CapturedImageSummaryCacheDuration)
                                    .Select(kvp => kvp.Key)
                                    .Take(64)
                                    .ToList();

                                foreach (var staleKey in staleKeys)
                                {
                                    _capturedImageSummaryCache.Remove(staleKey);
                                }
                            }
                        }
                    }

                    return summary;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetCapturedImageSummary failed: {ex.Message}");

                lock (_capturedImageSummaryCacheLock)
                {
                    if (_capturedImageSummaryCache.TryGetValue(targetId, out var cached))
                    {
                        return cached.Summary;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Get captured images for a target
        /// </summary>
        public async Task<List<CapturedImageDto>?> GetCapturedImagesAsync(Guid targetId, int skip = 0, int take = 50)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/scheduler/captured-images/target/{targetId}?skip={skip}&take={take}&includeThumbnails=false";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<List<CapturedImageDto>>();
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetCapturedImages failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Add a captured image
        /// </summary>
        public async Task<CapturedImageDto?> AddCapturedImageAsync(CapturedImageDto image)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/scheduler/captured-images";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(image);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<CapturedImageDto>();
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: AddCapturedImage failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Imaging Goal CRUD

        /// <summary>
        /// Add an imaging goal to a target
        /// </summary>
        public async Task<ImagingGoalDto?> AddImagingGoalAsync(Guid targetId, ImagingGoalDto goal)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/scheduler/targets/{targetId}/imaging-goals";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(goal);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ImagingGoalDto>();
                    Logger.Info($"AstroManagerApiClient: AddImagingGoal succeeded for target {targetId}");
                    return result;
                }
                
                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: AddImagingGoal failed: {response.StatusCode} - {error}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: AddImagingGoal failed: {ex.Message}");
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
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/scheduler/targets/{targetId}/imaging-goals/{goal.Id}";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
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
                Logger.Warning($"AstroManagerApiClient: UpdateImagingGoal failed: {ex.Message}");
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
                if (!await EnsureAuthenticatedAsync())
                    return false;
                
                var url = $"{BaseUrl}/api/scheduler/targets/{targetId}/imaging-goals/{goalId}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: DeleteImagingGoal failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Update custom goals for a panel
        /// </summary>
        public async Task<bool> UpdatePanelCustomGoalsAsync(Guid panelId, List<PanelImagingGoalDto> customGoals)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return false;
                
                var url = $"{BaseUrl}/api/scheduler/targets/panels/{panelId}/custom-goals";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(customGoals);
                
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    Logger.Info($"AstroManagerApiClient: Updated {customGoals.Count} custom goals for panel {panelId}");
                    return true;
                }
                
                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: UpdatePanelCustomGoals failed: {response.StatusCode} - {error}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: UpdatePanelCustomGoals failed: {ex.Message}");
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
                if (!await EnsureAuthenticatedAsync())
                    return false;
                
                var url = $"{BaseUrl}/api/scheduler/targets/panels/{panel.Id}";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(panel);
                
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    Logger.Info($"AstroManagerApiClient: Updated panel {panel.PanelNumber}");
                    return true;
                }
                
                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: UpdatePanel failed: {response.StatusCode} - {error}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: UpdatePanel failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Scheduler Target Template CRUD

        /// <summary>
        /// Fetch scheduler target templates from API
        /// </summary>
        public async Task<List<SchedulerTargetTemplateDto>?> GetSchedulerTargetTemplatesAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    Logger.Warning("AstroManagerApiClient: GetSchedulerTargetTemplates - Not authenticated");
                    return null;
                }
                
                var url = $"{BaseUrl}/api/SchedulerTargetTemplate";
                Logger.Info($"AstroManagerApiClient: Fetching scheduler target templates from {url}");
                
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var templates = await response.Content.ReadFromJsonAsync<List<SchedulerTargetTemplateDto>>();
                    Logger.Info($"AstroManagerApiClient: Loaded {templates?.Count ?? 0} scheduler target templates");
                    return templates;
                }
                
                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: GetSchedulerTargetTemplates failed with status {response.StatusCode}: {errorContent}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManagerApiClient: GetSchedulerTargetTemplates exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create a new scheduler target template
        /// </summary>
        public async Task<SchedulerTargetTemplateDto?> CreateSchedulerTargetTemplateAsync(CreateSchedulerTargetTemplateDto dto)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/schedulertargettemplate";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<SchedulerTargetTemplateDto>();
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: CreateSchedulerTargetTemplate failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Update a scheduler target template
        /// </summary>
        public async Task<SchedulerTargetTemplateDto?> UpdateSchedulerTargetTemplateAsync(Guid id, UpdateSchedulerTargetTemplateDto dto)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;
                
                var url = $"{BaseUrl}/api/schedulertargettemplate/{id}";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<SchedulerTargetTemplateDto>();
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: UpdateSchedulerTargetTemplate failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Delete a scheduler target template
        /// </summary>
        public async Task<bool> DeleteSchedulerTargetTemplateAsync(Guid id)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return false;
                
                var url = $"{BaseUrl}/api/schedulertargettemplate/{id}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: DeleteSchedulerTargetTemplate failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Slot-Based Scheduling API (delegated to SlotApiService)

        /// <summary>
        /// Get the next exposure slot to execute
        /// </summary>
        public Task<NextSlotDto?> GetNextSlotAsync(Guid? configurationId = null, Guid? currentTargetId = null, Guid? currentPanelId = null, string? currentFilter = null)
            => _slotService.GetNextSlotAsync(configurationId, currentTargetId, currentPanelId, currentFilter);

        /// <summary>
        /// Report a completed exposure with image metadata
        /// </summary>
        public Task<ExposureCompleteResponseDto?> ReportExposureCompleteAsync(ExposureCompleteDto request)
            => _slotService.ReportExposureCompleteAsync(request);

        /// <summary>
        /// Report an error and get handling instructions
        /// </summary>
        public Task<ErrorResponseDto?> ReportErrorAsync(ErrorReportDto request)
            => _slotService.ReportErrorAsync(request);

        /// <summary>
        /// Start a new imaging session
        /// </summary>
        public Task<bool> StartSessionAsync(Guid? configurationId = null)
            => _slotService.StartSessionAsync(configurationId);

        /// <summary>
        /// Stop the current imaging session
        /// </summary>
        public Task<bool> StopSessionAsync()
            => _slotService.StopSessionAsync();

        /// <summary>
        /// Update session status
        /// </summary>
        public Task<bool> UpdateSessionStatusAsync(UpdateSessionStatusDto status)
            => _slotService.UpdateSessionStatusAsync(status);

        /// <summary>
        /// Send runtime safety email alert via server-side SMTP configuration.
        /// </summary>
        public async Task<bool> SendRuntimeSafetyEmailAsync(RuntimeSafetyEmailAlertDto request)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    return false;
                }

                var url = $"{BaseUrl}/api/client/slot/safety-alert/email";
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                httpRequest.Content = JsonContent.Create(request);

                var response = await _httpClient.SendAsync(httpRequest);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: SendRuntimeSafetyEmailAsync failed - HTTP {(int)response.StatusCode}: {error}");
                    return false;
                }

                var payload = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return true;
                }

                try
                {
                    using var document = JsonDocument.Parse(payload);
                    if (document.RootElement.TryGetProperty("sent", out var sentElement)
                        && sentElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        var sent = sentElement.GetBoolean();
                        if (!sent)
                        {
                            var reason = document.RootElement.TryGetProperty("reason", out var reasonElement)
                                ? reasonElement.GetString()
                                : null;
                            Logger.Warning($"AstroManagerApiClient: SendRuntimeSafetyEmailAsync skipped by server{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})")}");
                        }

                        return sent;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"AstroManagerApiClient: SendRuntimeSafetyEmailAsync could not parse response payload: {ex.Message}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: SendRuntimeSafetyEmailAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Create runtime safety notification for the owning user.
        /// </summary>
        public async Task<bool> CreateRuntimeSafetyNotificationAsync(RuntimeSafetyNotificationAlertDto request)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    return false;
                }

                var url = $"{BaseUrl}/api/client/slot/safety-alert/notification";
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                httpRequest.Content = JsonContent.Create(request);

                var response = await _httpClient.SendAsync(httpRequest);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: CreateRuntimeSafetyNotificationAsync failed - HTTP {(int)response.StatusCode}: {error}");
                }

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: CreateRuntimeSafetyNotificationAsync failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Target Queue Management

        /// <summary>
        /// Get the scheduler mode for this client (Auto or Manual)
        /// </summary>
        public async Task<SchedulerMode> GetSchedulerModeAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return SchedulerMode.Auto;

                var url = $"{BaseUrl}/api/client/queue/{_clientLicenseId}/mode";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<SchedulerMode>(_jsonOptions);
                }

                return SchedulerMode.Auto;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetSchedulerMode failed: {ex.Message}");
                return SchedulerMode.Auto;
            }
        }

        /// <summary>
        /// Set the scheduler mode for this client
        /// </summary>
        public async Task<bool> SetSchedulerModeAsync(SchedulerMode mode)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    Logger.Warning("AstroManagerApiClient: SetSchedulerMode - not authenticated");
                    return false;
                }

                var url = $"{BaseUrl}/api/client/queue/mode";
                var dto = new { ClientLicenseId = _clientLicenseId, Mode = (int)mode };
                Logger.Debug($"AstroManagerApiClient: SetSchedulerMode - URL: {url}, ClientLicenseId: {_clientLicenseId}, Mode: {mode} ({(int)mode})");
                
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: SetSchedulerMode failed - Status: {response.StatusCode}, Error: {error}");
                }
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: SetSchedulerMode failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get the target queue for this client
        /// </summary>
        public async Task<List<ClientTargetQueueDto>?> GetTargetQueueAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;

                var url = $"{BaseUrl}/api/client/queue/{_clientLicenseId}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<List<ClientTargetQueueDto>>();
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetTargetQueue failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get the next pending target from the queue
        /// </summary>
        public async Task<ClientTargetQueueDto?> GetNextQueuedTargetAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return null;

                var url = $"{BaseUrl}/api/client/queue/{_clientLicenseId}/next";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<ClientTargetQueueDto>();
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: GetNextQueuedTarget failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Update the status of a queue item
        /// </summary>
        public async Task<bool> UpdateQueueItemStatusAsync(Guid queueItemId, QueueItemStatus status)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return false;

                var url = $"{BaseUrl}/api/client/queue/{queueItemId}/status";
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(status);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: UpdateQueueItemStatus failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Add a target to the queue
        /// </summary>
        public async Task<ClientTargetQueueDto?> AddToQueueAsync(Guid scheduledTargetId)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    Logger.Warning("AstroManagerApiClient: AddToQueue - not authenticated");
                    return null;
                }

                var url = $"{BaseUrl}/api/client/queue";
                var dto = new { ClientLicenseId = _clientLicenseId, ScheduledTargetId = scheduledTargetId };
                Logger.Debug($"AstroManagerApiClient: AddToQueue - URL: {url}, ClientLicenseId: {_clientLicenseId}, ScheduledTargetId: {scheduledTargetId}");
                
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ClientTargetQueueDto>();
                    Logger.Info($"AstroManagerApiClient: AddToQueue succeeded - QueueItemId: {result?.Id}");
                    return result;
                }

                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"AstroManagerApiClient: AddToQueue failed - Status: {response.StatusCode}, Error: {error}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: AddToQueue failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Remove a target from the queue
        /// </summary>
        public async Task<bool> RemoveFromQueueAsync(Guid queueItemId)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return false;

                var url = $"{BaseUrl}/api/client/queue/{queueItemId}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: RemoveFromQueue failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reorder queue items
        /// </summary>
        public async Task<bool> ReorderQueueAsync(List<Guid> queueItemIds)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return false;

                var url = $"{BaseUrl}/api/client/queue/reorder";
                var dto = new { ClientLicenseId = _clientLicenseId, QueueItemIds = queueItemIds };
                Logger.Debug($"AstroManagerApiClient: ReorderQueue - reordering {queueItemIds.Count} items");
                
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
                request.Content = JsonContent.Create(dto);

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Warning($"AstroManagerApiClient: ReorderQueue failed - Status: {response.StatusCode}, Error: {error}");
                }
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: ReorderQueue failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clear all items from the queue
        /// </summary>
        public async Task<bool> ClearQueueAsync()
        {
            try
            {
                if (!await EnsureAuthenticatedAsync())
                    return false;

                var url = $"{BaseUrl}/api/client/queue/{_clientLicenseId}/clear";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: ClearQueue failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Sequencer Logs

        /// <summary>
        /// Submit a batch of sequencer log entries to the server
        /// </summary>
        public async Task<bool> SubmitLogsAsync(List<SequencerLogEntryDto> entries)
        {
            if (entries == null || entries.Count == 0) return true;

            try
            {
                if (!await EnsureAuthenticatedAsync()) return false;
                SetBearerToken();

                var url = $"{BaseUrl}/api/client/logs/batch";
                var payload = new { Entries = entries };

                var response = await _httpClient.PostAsJsonAsync(url, payload);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: SubmitLogs failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Submit a single log entry
        /// </summary>
        public async Task<bool> SubmitLogAsync(SequencerLogEntryDto entry)
        {
            try
            {
                if (!await EnsureAuthenticatedAsync()) return false;
                SetBearerToken();

                var url = $"{BaseUrl}/api/client/logs";
                var response = await _httpClient.PostAsJsonAsync(url, entry);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManagerApiClient: SubmitLog failed: {ex.Message}");
                return false;
            }
        }

        #endregion
    }

    /// <summary>
    /// Response from client auth endpoint
    /// </summary>
    public class ClientAuthResponse
    {
        public string Token { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public int ExpiresIn { get; set; }
        public Guid UserId { get; set; }
        public Guid ClientLicenseId { get; set; }
        public string? ClientName { get; set; }
        
        /// <summary>
        /// Observatory this license is linked to (for scoping exposure templates)
        /// </summary>
        public Guid? ObservatoryId { get; set; }
        public string? ObservatoryName { get; set; }
        
        /// <summary>
        /// Equipment profile this license is linked to (for scoping exposure templates)
        /// </summary>
        public Guid? EquipmentId { get; set; }
        public string? EquipmentName { get; set; }
        
        /// <summary>
        /// Offline token for operating without server connection
        /// </summary>
        public OfflineTokenDto? OfflineToken { get; set; }
    }

    /// <summary>
    /// Response from client config endpoint
    /// </summary>
    public class ClientConfigResponse
    {
        public Guid ObservatoryId { get; set; }
        public string? ObservatoryName { get; set; }
        public Guid EquipmentId { get; set; }
        public string? EquipmentName { get; set; }
        public string? ImagingSoftware { get; set; }
        public Guid? DefaultSchedulerConfigurationId { get; set; }
        public string? DefaultSchedulerConfigurationName { get; set; }
        public Guid? RuntimeStopSafetyPolicyId { get; set; }
        public string? RuntimeStopSafetyPolicyName { get; set; }
    }

    /// <summary>
    /// Scheduler control mode for a client
    /// </summary>
    public enum SchedulerMode
    {
        Auto,
        Manual
    }

    /// <summary>
    /// Status of a queued target
    /// </summary>
    public enum QueueItemStatus
    {
        Pending,
        Active,
        Completed,
        Skipped,
        Failed
    }

    /// <summary>
    /// DTO for a queued target in manual scheduler mode
    /// </summary>
    public class ClientTargetQueueDto
    {
        public Guid Id { get; set; }
        public Guid ClientLicenseId { get; set; }
        public Guid ScheduledTargetId { get; set; }
        public int QueueOrder { get; set; }
        public QueueItemStatus Status { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? Notes { get; set; }
        public string? TargetName { get; set; }
        public double? RightAscension { get; set; }
        public double? Declination { get; set; }
    }

    /// <summary>
    /// Log level for sequencer entries
    /// </summary>
    public enum SequencerLogLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2,
        Success = 3
    }

    /// <summary>
    /// DTO for a sequencer log entry to send to the server
    /// </summary>
    public class SequencerLogEntryDto
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; } = string.Empty;
        public SequencerLogLevel Level { get; set; }
        public string? TargetName { get; set; }
        public string? Filter { get; set; }
        public string? Category { get; set; }
    }
}
