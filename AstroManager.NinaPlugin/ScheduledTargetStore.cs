using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Text.Json;
using NINA.Core.Utility;
using Shared.Model.DTO.Scheduler;
using Shared.Model.Enums;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Local storage for scheduled targets - enables offline operation
    /// </summary>
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class ScheduledTargetStore
    {
        private const string TargetsFileName = "scheduled_targets.json";
        private readonly AstroManagerSettings _settings;
        private readonly string _targetsPath;
        private List<ScheduledTargetDto> _targets = new();
        private readonly object _lock = new();

        [ImportingConstructor]
        public ScheduledTargetStore(AstroManagerSettings settings)
        {
            _settings = settings;
            _targetsPath = Path.Combine(settings.PluginDataFolder, TargetsFileName);
            Load();
        }

        public IReadOnlyList<ScheduledTargetDto> Targets
        {
            get
            {
                lock (_lock)
                {
                    return _targets.ToList();
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _targets.Count;
                }
            }
        }

        /// <summary>
        /// Get active targets for the configured observatory and equipment
        /// </summary>
        public IReadOnlyList<ScheduledTargetDto> GetActiveTargets()
        {
            lock (_lock)
            {
                return _targets
                    .Where(t => t.Status == ScheduledTargetStatus.Active)
                    .Where(t => !_settings.ObservatoryId.HasValue || t.ObservatoryId == _settings.ObservatoryId.Value)
                    .Where(t => !_settings.EquipmentId.HasValue || t.EquipmentId == _settings.EquipmentId.Value)
                    .OrderBy(t => t.Priority)
                    .ToList();
            }
        }

        /// <summary>
        /// Get a specific target by ID
        /// </summary>
        public ScheduledTargetDto? GetTarget(Guid id)
        {
            lock (_lock)
            {
                return _targets.FirstOrDefault(t => t.Id == id);
            }
        }

        /// <summary>
        /// Get all targets
        /// </summary>
        public List<ScheduledTargetDto> GetAllTargets()
        {
            lock (_lock)
            {
                return _targets.ToList();
            }
        }

        /// <summary>
        /// Update a single target
        /// </summary>
        public void UpdateTarget(ScheduledTargetDto target)
        {
            lock (_lock)
            {
                var index = _targets.FindIndex(t => t.Id == target.Id);
                if (index >= 0)
                {
                    _targets[index] = target;
                }
                else
                {
                    _targets.Add(target);
                }
                Save();
            }
            Logger.Info($"ScheduledTargetStore: Updated target {target.Name}");
        }

        /// <summary>
        /// Update targets from API sync
        /// </summary>
        public void UpdateTargets(IEnumerable<ScheduledTargetDto> targets)
        {
            lock (_lock)
            {
                _targets = targets.ToList();
                Save();
            }
            Logger.Info($"ScheduledTargetStore: Updated {_targets.Count} targets");
        }

        /// <summary>
        /// Update a single target's progress (e.g., after imaging)
        /// </summary>
        public void UpdateTargetProgress(Guid targetId, Guid goalId, int completedExposures)
        {
            lock (_lock)
            {
                var target = _targets.FirstOrDefault(t => t.Id == targetId);
                if (target != null)
                {
                    var goal = target.ImagingGoals.FirstOrDefault(g => g.Id == goalId);
                    if (goal != null)
                    {
                        goal.CompletedExposures = completedExposures;
                        Save();
                        Logger.Info($"ScheduledTargetStore: Updated progress for target '{target.Name}', goal {goal.Filter}: {completedExposures} exposures");
                    }
                }
            }
        }

        /// <summary>
        /// Remove a target by ID
        /// </summary>
        public void RemoveTarget(Guid id)
        {
            lock (_lock)
            {
                var target = _targets.FirstOrDefault(t => t.Id == id);
                if (target != null)
                {
                    _targets.Remove(target);
                    Save();
                    Logger.Info($"ScheduledTargetStore: Removed target {target.Name}");
                }
            }
        }

        /// <summary>
        /// Clear all cached targets
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _targets.Clear();
                Save();
            }
            Logger.Info("ScheduledTargetStore: Cleared all targets");
        }

        /// <summary>
        /// Export targets to a file
        /// </summary>
        public bool ExportToFile(string path)
        {
            try
            {
                lock (_lock)
                {
                    var exportData = new ExportData
                    {
                        ExportedAt = DateTime.UtcNow,
                        ObservatoryId = _settings.ObservatoryId,
                        ObservatoryName = _settings.ObservatoryName,
                        EquipmentId = _settings.EquipmentId,
                        EquipmentName = _settings.EquipmentName,
                        Targets = _targets
                    };

                    var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions 
                    { 
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    
                    File.WriteAllText(path, json);
                    Logger.Info($"ScheduledTargetStore: Exported {_targets.Count} targets to {path}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"ScheduledTargetStore: Failed to export targets: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Import targets from a file
        /// </summary>
        public bool ImportFromFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Logger.Warning($"ScheduledTargetStore: Import file not found: {path}");
                    return false;
                }

                var json = File.ReadAllText(path);
                var exportData = JsonSerializer.Deserialize<ExportData>(json, new JsonSerializerOptions 
                { 
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                if (exportData?.Targets != null)
                {
                    lock (_lock)
                    {
                        _targets = exportData.Targets;
                        Save();
                    }
                    
                    // Update settings with imported observatory/equipment info
                    if (exportData.ObservatoryId.HasValue)
                    {
                        _settings.ObservatoryId = exportData.ObservatoryId;
                        _settings.ObservatoryName = exportData.ObservatoryName;
                    }
                    if (exportData.EquipmentId.HasValue)
                    {
                        _settings.EquipmentId = exportData.EquipmentId;
                        _settings.EquipmentName = exportData.EquipmentName;
                    }
                    
                    Logger.Info($"ScheduledTargetStore: Imported {_targets.Count} targets from {path}");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"ScheduledTargetStore: Failed to import targets: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get default export file path
        /// </summary>
        public string GetDefaultExportPath()
        {
            var basePath = string.IsNullOrEmpty(_settings.ExportImportPath) 
                ? _settings.PluginDataFolder 
                : _settings.ExportImportPath;
            
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(basePath, $"astromanager_targets_{timestamp}.json");
        }

        /// <summary>
        /// Get latest export file in the configured path
        /// </summary>
        public string? GetLatestExportFile()
        {
            var basePath = string.IsNullOrEmpty(_settings.ExportImportPath) 
                ? _settings.PluginDataFolder 
                : _settings.ExportImportPath;

            if (!Directory.Exists(basePath))
                return null;

            return Directory.GetFiles(basePath, "astromanager_targets_*.json")
                .OrderByDescending(f => f)
                .FirstOrDefault();
        }

        private void Load()
        {
            if (File.Exists(_targetsPath))
            {
                try
                {
                    var json = File.ReadAllText(_targetsPath);
                    _targets = JsonSerializer.Deserialize<List<ScheduledTargetDto>>(json, new JsonSerializerOptions 
                    { 
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }) ?? new List<ScheduledTargetDto>();
                    
                    Logger.Info($"ScheduledTargetStore: Loaded {_targets.Count} targets from cache");
                }
                catch (Exception ex)
                {
                    Logger.Error($"ScheduledTargetStore: Failed to load targets: {ex.Message}");
                    _targets = new List<ScheduledTargetDto>();
                }
            }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_targets, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(_targetsPath, json);
            }
            catch (Exception ex)
            {
                Logger.Error($"ScheduledTargetStore: Failed to save targets: {ex.Message}");
            }
        }

        public class ExportData
        {
            public DateTime ExportedAt { get; set; }
            public Guid? ObservatoryId { get; set; }
            public string? ObservatoryName { get; set; }
            public Guid? EquipmentId { get; set; }
            public string? EquipmentName { get; set; }
            public List<ScheduledTargetDto> Targets { get; set; } = new();
        }
    }
}
