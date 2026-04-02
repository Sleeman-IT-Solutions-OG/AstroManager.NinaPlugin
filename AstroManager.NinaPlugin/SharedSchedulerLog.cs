using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using Shared.Model.DTO.Client;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Shared singleton for scheduler log entries accessible from both
    /// the AstroManagerTargetScheduler and AstroManagerDockVM.
    /// Also sends logs to AstroManager API for viewing in the web UI.
    /// </summary>
    public class SharedSchedulerLog
    {
        private static readonly Lazy<SharedSchedulerLog> _instance = new(() => new SharedSchedulerLog());
        public static SharedSchedulerLog Instance => _instance.Value;
        
        private const int MaxLogEntries = 100;
        private const int BatchSize = 20;
        private const int FlushIntervalSeconds = 30;
        
        public ObservableCollection<SchedulerLogEntry> LogEntries { get; } = new();
        
        // Queue for pending log entries to send to API
        private readonly ConcurrentQueue<SequencerLogEntryDto> _pendingLogs = new();
        private AstroManagerApiClient? _apiClient;
        private CancellationTokenSource? _flushCts;
        private Task? _flushTask;
        private string? _currentTargetName;
        private string? _currentFilter;
        
        private SharedSchedulerLog() { }
        
        /// <summary>
        /// Initialize the API client for log submission
        /// </summary>
        public void Initialize(AstroManagerApiClient apiClient)
        {
            _apiClient = apiClient;
            StartFlushTask();
        }
        
        /// <summary>
        /// Set current target context for log entries
        /// </summary>
        public void SetContext(string? targetName, string? filter = null)
        {
            _currentTargetName = targetName;
            _currentFilter = filter;
        }
        
        /// <summary>
        /// Clear current context
        /// </summary>
        public void ClearContext()
        {
            _currentTargetName = null;
            _currentFilter = null;
        }
        
        /// <summary>
        /// Add a log entry with timestamp
        /// </summary>
        public void AddEntry(string message, SchedulerLogLevel level = SchedulerLogLevel.Info, string? category = null)
        {
            var timestamp = DateTime.UtcNow;
            var entry = new SchedulerLogEntry
            {
                Timestamp = DateTime.Now, // Local time for UI display
                Message = message,
                Level = level
            };
            
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                LogEntries.Insert(0, entry); // Add at top (newest first)
                
                // Keep log size manageable
                while (LogEntries.Count > MaxLogEntries)
                {
                    LogEntries.RemoveAt(LogEntries.Count - 1);
                }
            });
            
            // Queue for API submission
            var apiEntry = new SequencerLogEntryDto
            {
                Timestamp = timestamp,
                Message = message,
                Level = MapLevel(level),
                TargetName = _currentTargetName,
                Filter = _currentFilter,
                Category = category
            };
            _pendingLogs.Enqueue(apiEntry);
            
            // Also log to NINA logger
            switch (level)
            {
                case SchedulerLogLevel.Error:
                    Logger.Error($"AstroManager Scheduler: {message}");
                    break;
                case SchedulerLogLevel.Warning:
                    Logger.Warning($"AstroManager Scheduler: {message}");
                    break;
                default:
                    Logger.Info($"AstroManager Scheduler: {message}");
                    break;
            }
        }
        
        private SequencerLogLevel MapLevel(SchedulerLogLevel level)
        {
            return level switch
            {
                SchedulerLogLevel.Error => SequencerLogLevel.Error,
                SchedulerLogLevel.Warning => SequencerLogLevel.Warning,
                SchedulerLogLevel.Success => SequencerLogLevel.Success,
                _ => SequencerLogLevel.Info
            };
        }
        
        /// <summary>
        /// Start the background task to flush logs to API
        /// </summary>
        private void StartFlushTask()
        {
            _flushCts?.Cancel();
            _flushCts = new CancellationTokenSource();
            var token = _flushCts.Token;
            
            _flushTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(FlushIntervalSeconds), token);
                        await FlushLogsAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"SharedSchedulerLog: Flush error: {ex.Message}");
                    }
                }
            }, token);
        }
        
        /// <summary>
        /// Flush pending logs to the API
        /// </summary>
        public async Task FlushLogsAsync()
        {
            if (_apiClient == null || _pendingLogs.IsEmpty) return;
            
            var batch = new List<SequencerLogEntryDto>();
            while (batch.Count < BatchSize && _pendingLogs.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }
            
            if (batch.Count > 0)
            {
                var success = await _apiClient.SubmitLogsAsync(batch);
                if (!success)
                {
                    // Re-queue failed entries (at the front)
                    foreach (var entry in batch.AsEnumerable().Reverse())
                    {
                        // Note: ConcurrentQueue doesn't support prepend, so these go to end
                        // This is acceptable for logging - order within a batch may shift slightly
                        _pendingLogs.Enqueue(entry);
                    }
                }
            }
        }
        
        /// <summary>
        /// Clear all log entries
        /// </summary>
        public void Clear()
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                LogEntries.Clear();
            });
        }
        
        /// <summary>
        /// Stop the flush task and flush remaining logs
        /// </summary>
        public async Task ShutdownAsync()
        {
            _flushCts?.Cancel();
            
            // Flush remaining logs
            while (!_pendingLogs.IsEmpty)
            {
                await FlushLogsAsync();
            }
        }
    }
}
