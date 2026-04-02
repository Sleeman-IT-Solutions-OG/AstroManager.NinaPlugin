using Shared.Model.DTO.Scheduler;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace AstroManager.NinaPlugin.Services
{
    /// <summary>
    /// Service for scheduler configuration API operations.
    /// Handles configuration CRUD.
    /// </summary>
    public class ConfigurationApiService
    {
        private readonly Func<Task<bool>> _ensureAuthenticatedAsync;
        private readonly Func<string?> _getJwtToken;
        private readonly Func<string> _getBaseUrl;
        private readonly HttpClient _httpClient;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public ConfigurationApiService(
            HttpClient httpClient,
            Func<Task<bool>> ensureAuthenticatedAsync,
            Func<string?> getJwtToken,
            Func<string> getBaseUrl)
        {
            _httpClient = httpClient;
            _ensureAuthenticatedAsync = ensureAuthenticatedAsync;
            _getJwtToken = getJwtToken;
            _getBaseUrl = getBaseUrl;
        }

        /// <summary>
        /// Fetch scheduler configurations from API
        /// </summary>
        public async Task<List<SchedulerConfigurationDto>?> GetSchedulerConfigurationsAsync()
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return null;

                var url = $"{_getBaseUrl()}/api/scheduler/configurations";
                Logger.Debug($"ConfigurationApiService: Fetching scheduler configurations from {url}");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var configs = await response.Content.ReadFromJsonAsync<List<SchedulerConfigurationDto>>(_jsonOptions);
                    Logger.Info($"ConfigurationApiService: Loaded {configs?.Count ?? 0} scheduler configurations");
                    return configs;
                }

                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"ConfigurationApiService: GetSchedulerConfigurations failed: {response.StatusCode} - {error}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConfigurationApiService: GetSchedulerConfigurations failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create a new scheduler configuration
        /// </summary>
        public async Task<SchedulerConfigurationDto?> CreateSchedulerConfigurationAsync(SchedulerConfigurationDto dto)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return null;

                var url = $"{_getBaseUrl()}/api/scheduler/configurations";

                // Serialize with camelCase (API expects integers for enums)
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var jsonContent = JsonSerializer.Serialize(dto, jsonOptions);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<SchedulerConfigurationDto>(_jsonOptions);
                }

                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.Warning($"ConfigurationApiService: CreateSchedulerConfiguration failed: {response.StatusCode} - {errorContent}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConfigurationApiService: CreateSchedulerConfiguration failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Update a scheduler configuration
        /// </summary>
        public async Task<SchedulerConfigurationDto?> UpdateSchedulerConfigurationAsync(SchedulerConfigurationDto dto)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return null;

                var url = $"{_getBaseUrl()}/api/scheduler/configurations/{dto.Id}";
                Logger.Info($"ConfigurationApiService: UpdateSchedulerConfiguration URL: {url}");
                Logger.Info($"ConfigurationApiService: UpdateSchedulerConfiguration DTO: Id={dto.Id}, Name={dto.Name}, StartDate={dto.StartDate}, EndDate={dto.EndDate}");

                // Serialize with camelCase (API expects integers for enums)
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                };
                var jsonContent = JsonSerializer.Serialize(dto, jsonOptions);
                Logger.Info($"ConfigurationApiService: UpdateSchedulerConfiguration JSON length: {jsonContent.Length}");

                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                Logger.Info($"ConfigurationApiService: Sending PUT request to {url}");
                var response = await _httpClient.SendAsync(request);
                Logger.Info($"ConfigurationApiService: Response status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    Logger.Info("ConfigurationApiService: UpdateSchedulerConfiguration succeeded");
                    return await response.Content.ReadFromJsonAsync<SchedulerConfigurationDto>(_jsonOptions);
                }

                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.Error($"ConfigurationApiService: UpdateSchedulerConfiguration failed: {response.StatusCode}");
                Logger.Error($"ConfigurationApiService: Error response: {errorContent}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"ConfigurationApiService: UpdateSchedulerConfiguration exception: {ex.Message}");
                Logger.Error($"ConfigurationApiService: Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Delete a scheduler configuration
        /// </summary>
        public async Task<bool> DeleteSchedulerConfigurationAsync(Guid id)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return false;

                var url = $"{_getBaseUrl()}/api/scheduler/configurations/{id}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConfigurationApiService: DeleteSchedulerConfiguration failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set a scheduler configuration as default
        /// </summary>
        public async Task<bool> SetDefaultSchedulerConfigurationAsync(Guid id)
        {
            try
            {
                if (!await _ensureAuthenticatedAsync())
                    return false;

                var url = $"{_getBaseUrl()}/api/scheduler/configurations/{id}/set-default";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _getJwtToken());

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConfigurationApiService: SetDefaultSchedulerConfiguration failed: {ex.Message}");
                return false;
            }
        }
    }
}
