using System;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Text.Json;
using NINA.Core.Utility;

namespace AstroManager.NinaPlugin
{
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class AstroManagerSettings
    {
        private const string SettingsFileName = "astromanager_settings.json";
        private readonly string _settingsPath;
        private readonly string _pluginDataFolder;
        private SettingsData _data = new SettingsData();

        public string PluginDataFolder => _pluginDataFolder;

        public AstroManagerSettings()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _pluginDataFolder = Path.Combine(appData, "NINA", "Plugins", "AstroManager");
            
            if (!Directory.Exists(_pluginDataFolder))
            {
                Directory.CreateDirectory(_pluginDataFolder);
            }

            _settingsPath = Path.Combine(_pluginDataFolder, SettingsFileName);
            Load();
        }

        public string LicenseKey
        {
            get => _data.LicenseKey;
            set { _data.LicenseKey = value; Save(); }
        }

        // ApiUrl is now hardcoded - no longer user-configurable
        public string ApiUrl => "https://api.astro.sleeman.at";

        public bool UseCachedTargetsOnConnectionLoss
        {
            get => _data.UseCachedTargetsOnConnectionLoss;
            set { _data.UseCachedTargetsOnConnectionLoss = value; Save(); }
        }

        public string ExportImportPath
        {
            get => _data.ExportImportPath;
            set { _data.ExportImportPath = value; Save(); }
        }

        // AutoSyncOnStartup is now always true - no longer user-configurable
        public bool AutoSyncOnStartup => true;

        public bool AutoExportAfterSync
        {
            get => _data.AutoExportAfterSync;
            set { _data.AutoExportAfterSync = value; Save(); }
        }

        public DateTime? LastSyncTime
        {
            get => _data.LastSyncTime;
            set { _data.LastSyncTime = value; Save(); }
        }

        public Guid? ObservatoryId
        {
            get => _data.ObservatoryId;
            set { _data.ObservatoryId = value; Save(); }
        }

        public string? ObservatoryName
        {
            get => _data.ObservatoryName;
            set { _data.ObservatoryName = value; Save(); }
        }

        public Guid? EquipmentId
        {
            get => _data.EquipmentId;
            set { _data.EquipmentId = value; Save(); }
        }

        public string? EquipmentName
        {
            get => _data.EquipmentName;
            set { _data.EquipmentName = value; Save(); }
        }

        // EnableHeartbeat is now always true - no longer user-configurable
        public bool EnableHeartbeat => true;

        public int HeartbeatIntervalSeconds
        {
            get => Math.Max(60, _data.HeartbeatIntervalSeconds); // Enforce minimum 60 seconds
            set { _data.HeartbeatIntervalSeconds = Math.Max(60, value); Save(); }
        }

        // AutoRefreshIntervalMinutes removed - sync interval is now used instead
        public int AutoRefreshIntervalMinutes => 0;

        // AutoConnectOnStartup is now always true - no longer user-configurable
        public bool AutoConnectOnStartup => true;
        
        // EnableRealTimeConnection is now always true - no longer user-configurable
        public bool EnableRealTimeConnection => true;
        
        // EnableImageUpload is now always true - thumbnails are essential for progress tracking
        public bool EnableImageUpload => true;

        // Server-side default scheduler configuration
        public Guid? DefaultSchedulerConfigurationId
        {
            get => _data.DefaultSchedulerConfigurationId;
            set { _data.DefaultSchedulerConfigurationId = value; Save(); }
        }
        
        public string? DefaultSchedulerConfigurationName
        {
            get => _data.DefaultSchedulerConfigurationName;
            set { _data.DefaultSchedulerConfigurationName = value; Save(); }
        }

        public Guid? RuntimeStopSafetyPolicyId
        {
            get => _data.RuntimeStopSafetyPolicyId;
            set { _data.RuntimeStopSafetyPolicyId = value; Save(); }
        }

        public string? RuntimeStopSafetyPolicyName
        {
            get => _data.RuntimeStopSafetyPolicyName;
            set { _data.RuntimeStopSafetyPolicyName = value; Save(); }
        }

        // Runtime status (not persisted)
        public string? CurrentImagingStatus { get; set; } = "Idle";
        public Guid? CurrentTargetId { get; set; }
        public string? CurrentTargetName { get; set; }

        private void Load()
        {
            if (File.Exists(_settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsPath);
                    _data = JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to load AstroManager settings: {ex.Message}");
                }
            }
            
            _data = new SettingsData();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save AstroManager settings: {ex.Message}");
            }
        }

        public class SettingsData
        {
            public string LicenseKey { get; set; } = string.Empty;
            public string ApiUrl { get; set; } = "https://api.astro.sleeman.at";
            public bool UseCachedTargetsOnConnectionLoss { get; set; } = false;
            public string ExportImportPath { get; set; } = string.Empty;
            public bool AutoSyncOnStartup { get; set; } = true;
            public bool AutoExportAfterSync { get; set; } = false;
            public DateTime? LastSyncTime { get; set; }
            public Guid? ObservatoryId { get; set; }
            public string? ObservatoryName { get; set; }
            public Guid? EquipmentId { get; set; }
            public string? EquipmentName { get; set; }
            
            // Server-side default scheduler configuration (set by AstroManager web)
            public Guid? DefaultSchedulerConfigurationId { get; set; }
            public string? DefaultSchedulerConfigurationName { get; set; }
            public Guid? RuntimeStopSafetyPolicyId { get; set; }
            public string? RuntimeStopSafetyPolicyName { get; set; }
            
            // Heartbeat settings
            public bool EnableHeartbeat { get; set; } = true;
            public int HeartbeatIntervalSeconds { get; set; } = 60;
            public int AutoRefreshIntervalMinutes { get; set; } = 5; // 0 = disabled, default 5 minutes
            public bool AutoConnectOnStartup { get; set; } = true;
            
            // Real-time connection (SignalR) - enables instant command delivery
            public bool EnableRealTimeConnection { get; set; } = true;
            
            // EnableImageUpload removed - now always enabled
        }
    }
}
