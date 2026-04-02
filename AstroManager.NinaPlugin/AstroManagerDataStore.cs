using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text.Json;
using NINA.Core.Utility;
using Shared.Model.DTO.Scheduler;
using Shared.Model.DTO.Settings;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Local storage for AstroManager configuration data - enables offline operation
    /// Caches: Observatory, Exposure Templates, Scheduler Configurations, Moon Avoidance Profiles, Target Templates
    /// </summary>
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class AstroManagerDataStore
    {
        private const string DataFileName = "astromanager_data.json";
        private readonly AstroManagerSettings _settings;
        private readonly string _dataPath;
        private CachedData _data = new();
        private readonly object _lock = new();

        [ImportingConstructor]
        public AstroManagerDataStore(AstroManagerSettings settings)
        {
            _settings = settings;
            _dataPath = Path.Combine(settings.PluginDataFolder, DataFileName);
            Load();
        }

        #region Properties

        public ObservatoryDto? Observatory
        {
            get { lock (_lock) { return _data.Observatory; } }
        }

        public IReadOnlyList<ExposureTemplateDto> ExposureTemplates
        {
            get { lock (_lock) { return _data.ExposureTemplates.ToList(); } }
        }

        public IReadOnlyList<SchedulerConfigurationDto> SchedulerConfigurations
        {
            get { lock (_lock) { return _data.SchedulerConfigurations.ToList(); } }
        }

        public IReadOnlyList<MoonAvoidanceProfileDto> MoonAvoidanceProfiles
        {
            get { lock (_lock) { return _data.MoonAvoidanceProfiles.ToList(); } }
        }

        public IReadOnlyList<SchedulerTargetTemplateDto> SchedulerTemplates
        {
            get { lock (_lock) { return _data.SchedulerTemplates.ToList(); } }
        }

        public DateTime? LastSyncUtc
        {
            get { lock (_lock) { return _data.LastSyncUtc; } }
        }

        public bool HasCachedData
        {
            get { lock (_lock) { return _data.Observatory != null || _data.ExposureTemplates.Count > 0; } }
        }

        #endregion

        #region Update Methods

        public void UpdateObservatory(ObservatoryDto? observatory)
        {
            lock (_lock)
            {
                _data.Observatory = observatory;
                _data.LastSyncUtc = DateTime.UtcNow;
                Save();
            }
            Logger.Info($"AstroManagerDataStore: Updated observatory: {observatory?.Name ?? "null"}");
        }

        public void UpdateExposureTemplates(IEnumerable<ExposureTemplateDto> templates)
        {
            lock (_lock)
            {
                _data.ExposureTemplates = templates.ToList();
                _data.LastSyncUtc = DateTime.UtcNow;
                Save();
            }
            Logger.Info($"AstroManagerDataStore: Updated {_data.ExposureTemplates.Count} exposure templates");
        }

        public void UpdateSchedulerConfigurations(IEnumerable<SchedulerConfigurationDto> configs)
        {
            lock (_lock)
            {
                _data.SchedulerConfigurations = configs.ToList();
                _data.LastSyncUtc = DateTime.UtcNow;
                Save();
            }
            Logger.Info($"AstroManagerDataStore: Updated {_data.SchedulerConfigurations.Count} scheduler configurations");
        }

        public void UpdateMoonAvoidanceProfiles(IEnumerable<MoonAvoidanceProfileDto> profiles)
        {
            lock (_lock)
            {
                _data.MoonAvoidanceProfiles = profiles.ToList();
                _data.LastSyncUtc = DateTime.UtcNow;
                Save();
            }
            Logger.Info($"AstroManagerDataStore: Updated {_data.MoonAvoidanceProfiles.Count} moon avoidance profiles");
        }

        public void UpdateSchedulerTemplates(IEnumerable<SchedulerTargetTemplateDto> templates)
        {
            lock (_lock)
            {
                _data.SchedulerTemplates = templates.ToList();
                _data.LastSyncUtc = DateTime.UtcNow;
                Save();
            }
            Logger.Info($"AstroManagerDataStore: Updated {_data.SchedulerTemplates.Count} scheduler templates");
        }

        /// <summary>
        /// Update all data at once (more efficient for full sync)
        /// </summary>
        public void UpdateAll(
            ObservatoryDto? observatory,
            IEnumerable<ExposureTemplateDto>? exposureTemplates,
            IEnumerable<SchedulerConfigurationDto>? schedulerConfigs,
            IEnumerable<MoonAvoidanceProfileDto>? moonAvoidanceProfiles,
            IEnumerable<SchedulerTargetTemplateDto>? schedulerTemplates)
        {
            lock (_lock)
            {
                if (observatory != null)
                    _data.Observatory = observatory;
                if (exposureTemplates != null)
                    _data.ExposureTemplates = exposureTemplates.ToList();
                if (schedulerConfigs != null)
                    _data.SchedulerConfigurations = schedulerConfigs.ToList();
                if (moonAvoidanceProfiles != null)
                    _data.MoonAvoidanceProfiles = moonAvoidanceProfiles.ToList();
                if (schedulerTemplates != null)
                    _data.SchedulerTemplates = schedulerTemplates.ToList();
                
                _data.LastSyncUtc = DateTime.UtcNow;
                Save();
            }
            
            Logger.Info($"AstroManagerDataStore: Full sync - Observatory={_data.Observatory?.Name ?? "null"}, " +
                $"ExpTemplates={_data.ExposureTemplates.Count}, SchedConfigs={_data.SchedulerConfigurations.Count}, " +
                $"MoonProfiles={_data.MoonAvoidanceProfiles.Count}, SchedTemplates={_data.SchedulerTemplates.Count}");
        }

        #endregion

        #region Lookup Methods

        public ExposureTemplateDto? GetExposureTemplate(Guid id)
        {
            lock (_lock)
            {
                return _data.ExposureTemplates.FirstOrDefault(t => t.Id == id);
            }
        }

        public SchedulerConfigurationDto? GetSchedulerConfiguration(Guid id)
        {
            lock (_lock)
            {
                return _data.SchedulerConfigurations.FirstOrDefault(c => c.Id == id);
            }
        }

        public SchedulerConfigurationDto? GetDefaultSchedulerConfiguration()
        {
            lock (_lock)
            {
                return _data.SchedulerConfigurations.FirstOrDefault(c => c.IsDefault) 
                    ?? _data.SchedulerConfigurations.FirstOrDefault();
            }
        }

        public MoonAvoidanceProfileDto? GetMoonAvoidanceProfile(Guid id)
        {
            lock (_lock)
            {
                return _data.MoonAvoidanceProfiles.FirstOrDefault(p => p.Id == id);
            }
        }

        public SchedulerTargetTemplateDto? GetSchedulerTemplate(Guid id)
        {
            lock (_lock)
            {
                return _data.SchedulerTemplates.FirstOrDefault(t => t.Id == id);
            }
        }

        #endregion

        #region Clear/Load/Save

        public void Clear()
        {
            lock (_lock)
            {
                _data = new CachedData();
                Save();
            }
            Logger.Info("AstroManagerDataStore: Cleared all cached data");
        }

        private void Load()
        {
            if (File.Exists(_dataPath))
            {
                try
                {
                    var json = File.ReadAllText(_dataPath);
                    _data = JsonSerializer.Deserialize<CachedData>(json, new JsonSerializerOptions 
                    { 
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }) ?? new CachedData();
                    
                    Logger.Info($"AstroManagerDataStore: Loaded from cache - Observatory={_data.Observatory?.Name ?? "null"}, " +
                        $"ExpTemplates={_data.ExposureTemplates.Count}, SchedConfigs={_data.SchedulerConfigurations.Count}, " +
                        $"MoonProfiles={_data.MoonAvoidanceProfiles.Count}, SchedTemplates={_data.SchedulerTemplates.Count}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"AstroManagerDataStore: Failed to load cached data: {ex.Message}");
                    _data = new CachedData();
                }
            }
        }

        private void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(_dataPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(_dataPath, json);
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManagerDataStore: Failed to save cached data: {ex.Message}");
            }
        }

        #endregion

        #region Data Classes

        public class CachedData
        {
            public DateTime? LastSyncUtc { get; set; }
            public ObservatoryDto? Observatory { get; set; }
            public List<ExposureTemplateDto> ExposureTemplates { get; set; } = new();
            public List<SchedulerConfigurationDto> SchedulerConfigurations { get; set; } = new();
            public List<MoonAvoidanceProfileDto> MoonAvoidanceProfiles { get; set; } = new();
            public List<SchedulerTargetTemplateDto> SchedulerTemplates { get; set; } = new();
        }

        #endregion
    }
}
