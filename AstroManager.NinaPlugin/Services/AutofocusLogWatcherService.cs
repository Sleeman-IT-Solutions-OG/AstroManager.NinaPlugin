using NINA.Core.Utility;
using Shared.Model.DTO.Client;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services
{
    /// <summary>
    /// Service that watches NINA's AutoFocus log folder for new JSON files
    /// and processes them to send AF reports to AstroManager API.
    /// </summary>
    public class AutofocusLogWatcherService : IDisposable
    {
        private readonly HeartbeatService _heartbeatService;
        private readonly Func<bool> _isShuttingDown;
        
        private FileSystemWatcher? _afLogWatcher;
        private readonly HashSet<string> _processedAfLogFiles = new();
        private DateTime _lastAfLogProcessed = DateTime.MinValue;
        private bool _disposed;

        public AutofocusLogWatcherService(
            HeartbeatService heartbeatService,
            Func<bool> isShuttingDown)
        {
            _heartbeatService = heartbeatService;
            _isShuttingDown = isShuttingDown;
        }

        /// <summary>
        /// Start watching NINA's AutoFocus log folder for new JSON files.
        /// This detects AF runs triggered by sequences, triggers, or other plugins.
        /// </summary>
        public void Start()
        {
            try
            {
                var afLogFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NINA", "AutoFocus");
                
                if (!Directory.Exists(afLogFolder))
                {
                    Logger.Info($"AstroManager: AF log folder does not exist yet: {afLogFolder}");
                    Directory.CreateDirectory(afLogFolder);
                }
                
                _afLogWatcher = new FileSystemWatcher(afLogFolder)
                {
                    Filter = "*.json",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                
                _afLogWatcher.Created += OnAfLogFileCreated;
                
                // Mark existing files as processed so we don't re-process old logs
                foreach (var file in Directory.GetFiles(afLogFolder, "*.json"))
                {
                    _processedAfLogFiles.Add(Path.GetFileName(file));
                }
                
                Logger.Info($"AstroManager: Started AF log watcher on {afLogFolder} ({_processedAfLogFiles.Count} existing files ignored)");
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Failed to start AF log watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop watching for AF log files.
        /// </summary>
        public void Stop()
        {
            if (_afLogWatcher != null)
            {
                _afLogWatcher.EnableRaisingEvents = false;
                _afLogWatcher.Created -= OnAfLogFileCreated;
                _afLogWatcher.Dispose();
                _afLogWatcher = null;
                Logger.Debug("AstroManager: Stopped AF log watcher");
            }
        }

        private async void OnAfLogFileCreated(object sender, FileSystemEventArgs e)
        {
            if (_isShuttingDown()) return;
            
            try
            {
                var fileName = Path.GetFileName(e.FullPath);
                
                // Skip if already processed
                if (_processedAfLogFiles.Contains(fileName))
                {
                    return;
                }
                
                // Debounce - don't process multiple files within 2 seconds
                if ((DateTime.UtcNow - _lastAfLogProcessed).TotalSeconds < 2)
                {
                    Logger.Debug($"AstroManager: Skipping AF log {fileName} - too soon after last");
                    _processedAfLogFiles.Add(fileName);
                    return;
                }
                
                // Wait a moment for file to be fully written
                await Task.Delay(500);
                
                Logger.Info($"AstroManager: New AF log file detected: {fileName}");
                _processedAfLogFiles.Add(fileName);
                _lastAfLogProcessed = DateTime.UtcNow;
                
                // Parse the AF JSON log and send report
                await ProcessAfLogFileAsync(e.FullPath);
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Error processing AF log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Parse NINA's AF JSON log file and send report to AstroManager.
        /// </summary>
        private async Task ProcessAfLogFileAsync(string filePath)
        {
            try
            {
                // Read the JSON file
                var json = await File.ReadAllTextAsync(filePath);
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                // Extract AF data from NINA's JSON format
                var dataPoints = new List<AutofocusDataPointDto>();
                int finalPosition = 0;
                double finalHfr = 0;
                double temperature = 0;
                string? filter = null;
                string? fittingMethod = null;
                double? rSquared = null;
                bool success = false;
                
                // Get basic info
                if (root.TryGetProperty("Filter", out var filterProp))
                    filter = filterProp.GetString();
                if (root.TryGetProperty("Temperature", out var tempProp))
                    temperature = tempProp.GetDouble();
                if (root.TryGetProperty("Method", out var methodProp))
                    fittingMethod = methodProp.GetString();
                if (root.TryGetProperty("Succeeded", out var successProp))
                    success = successProp.GetBoolean();
                    
                // Get calculated best position
                if (root.TryGetProperty("CalculatedFocusPoint", out var calcPoint))
                {
                    if (calcPoint.TryGetProperty("Position", out var posProp))
                        finalPosition = (int)posProp.GetDouble();
                    if (calcPoint.TryGetProperty("Value", out var valProp))
                        finalHfr = valProp.GetDouble();
                }
                
                // Get R² value
                if (root.TryGetProperty("RSquares", out var rSquares))
                {
                    if (fittingMethod?.Contains("Hyperbolic", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (rSquares.TryGetProperty("Hyperbolic", out var hypR2))
                            rSquared = hypR2.GetDouble();
                    }
                    else if (rSquares.TryGetProperty("Parabolic", out var paraR2))
                    {
                        rSquared = paraR2.GetDouble();
                    }
                }
                
                // Get measurement points
                if (root.TryGetProperty("MeasurePoints", out var measurePoints) && measurePoints.ValueKind == JsonValueKind.Array)
                {
                    foreach (var point in measurePoints.EnumerateArray())
                    {
                        double pos = 0, hfr = 0;
                        if (point.TryGetProperty("Position", out var pPos))
                            pos = pPos.GetDouble();
                        if (point.TryGetProperty("Value", out var pVal))
                            hfr = pVal.GetDouble();
                        
                        if (pos > 0 && hfr > 0)
                        {
                            dataPoints.Add(new AutofocusDataPointDto
                            {
                                Position = (int)pos,
                                Hfr = hfr
                            });
                        }
                    }
                }
                
                // Create and send AF report
                var afReport = new AutofocusReportDto
                {
                    CompletedAt = DateTime.UtcNow,
                    Success = success,
                    FinalPosition = finalPosition,
                    FinalHfr = finalHfr,
                    Temperature = temperature,
                    Filter = filter,
                    FittingMethod = fittingMethod ?? "Unknown",
                    DataPoints = dataPoints,
                    RSquared = rSquared
                };
                
                Logger.Info($"AstroManager: Parsed AF log - Success: {success}, Position: {finalPosition}, HFR: {finalHfr:F2}, Points: {dataPoints.Count}, Filter: {filter}, R²: {rSquared:F4}");
                
                // Send to heartbeat service
                _heartbeatService.SetAutofocusReport(afReport);
                await _heartbeatService.ForceStatusUpdateAsync();
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Failed to parse AF log file: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
        }
    }
}
