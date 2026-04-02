using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using Shared.Model.DTO.Client;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Manages offline storage and retry of failed capture uploads.
    /// Persists queued captures to disk and retries with exponential backoff.
    /// </summary>
    public class OfflineCaptureQueue
    {
        private readonly string _queueFilePath;
        private readonly object _lock = new();
        private List<QueuedCapture> _queue = new();
        private System.Threading.Timer? _retryTimer;
        private bool _isRetrying;
        private int _consecutiveFailures;
        
        // Retry settings
        private const int InitialRetryDelaySeconds = 30;
        private const int MaxRetryDelaySeconds = 600; // 10 minutes max
        private const int MaxQueueSize = 100; // Don't queue more than 100 captures
        
        public event Func<UploadImageThumbnailDto, Task<bool>>? OnRetryUpload;
        
        public int QueueCount
        {
            get { lock (_lock) return _queue.Count; }
        }
        
        public OfflineCaptureQueue()
        {
            // Store in NINA's plugin data folder
            var pluginFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins", "AstroManager");
            
            Directory.CreateDirectory(pluginFolder);
            _queueFilePath = Path.Combine(pluginFolder, "offline_captures.json");
            
            LoadQueue();
            StartRetryTimer();
        }
        
        /// <summary>
        /// Queue a failed capture for later retry
        /// </summary>
        public void Enqueue(UploadImageThumbnailDto dto, string? errorMessage = null)
        {
            lock (_lock)
            {
                // Don't exceed max queue size - drop oldest if needed
                while (_queue.Count >= MaxQueueSize)
                {
                    _queue.RemoveAt(0);
                    Logger.Warning($"OfflineCaptureQueue: Dropped oldest capture (queue full, max={MaxQueueSize})");
                }
                
                var queued = new QueuedCapture
                {
                    Id = Guid.NewGuid(),
                    Dto = dto,
                    QueuedAt = DateTime.UtcNow,
                    RetryCount = 0,
                    LastError = errorMessage
                };
                
                _queue.Add(queued);
                SaveQueue();
                
                Logger.Info($"OfflineCaptureQueue: Queued capture {dto.FileName} for retry (queue size: {_queue.Count})");
            }
        }
        
        /// <summary>
        /// Process the retry queue
        /// </summary>
        public async Task ProcessQueueAsync()
        {
            if (_isRetrying || OnRetryUpload == null)
                return;
            
            List<QueuedCapture> toProcess;
            lock (_lock)
            {
                if (_queue.Count == 0)
                    return;
                
                toProcess = new List<QueuedCapture>(_queue);
            }
            
            _isRetrying = true;
            var successCount = 0;
            var failCount = 0;
            
            try
            {
                foreach (var capture in toProcess)
                {
                    try
                    {
                        Logger.Debug($"OfflineCaptureQueue: Retrying upload for {capture.Dto.FileName} (attempt {capture.RetryCount + 1})");
                        
                        var success = await OnRetryUpload(capture.Dto);
                        
                        if (success)
                        {
                            successCount++;
                            lock (_lock)
                            {
                                _queue.RemoveAll(q => q.Id == capture.Id);
                                SaveQueue();
                            }
                            Logger.Info($"OfflineCaptureQueue: Successfully uploaded {capture.Dto.FileName}");
                        }
                        else
                        {
                            failCount++;
                            capture.RetryCount++;
                            capture.LastRetryAt = DateTime.UtcNow;
                            capture.LastError = "Upload returned false";
                        }
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        capture.RetryCount++;
                        capture.LastRetryAt = DateTime.UtcNow;
                        capture.LastError = ex.Message;
                        Logger.Warning($"OfflineCaptureQueue: Retry failed for {capture.Dto.FileName}: {ex.Message}");
                    }
                    
                    // Small delay between retries to avoid hammering the server
                    await Task.Delay(500);
                }
                
                // Update consecutive failure tracking for backoff
                if (failCount > 0 && successCount == 0)
                {
                    _consecutiveFailures++;
                }
                else if (successCount > 0)
                {
                    _consecutiveFailures = 0;
                }
                
                // Save updated retry counts
                lock (_lock)
                {
                    SaveQueue();
                }
                
                if (successCount > 0 || failCount > 0)
                {
                    Logger.Info($"OfflineCaptureQueue: Processed {successCount} successful, {failCount} failed, {QueueCount} remaining");
                }
            }
            finally
            {
                _isRetrying = false;
            }
        }
        
        private void StartRetryTimer()
        {
            _retryTimer = new System.Threading.Timer(async _ =>
            {
                await ProcessQueueAsync();
                
                // Update timer interval based on backoff (consecutive failures)
                _retryTimer?.Change(GetRetryInterval(), Timeout.InfiniteTimeSpan);
            }, null, TimeSpan.FromSeconds(InitialRetryDelaySeconds), Timeout.InfiniteTimeSpan);
        }
        
        private TimeSpan GetRetryInterval()
        {
            // Exponential backoff based on consecutive failures
            var delaySeconds = InitialRetryDelaySeconds * Math.Pow(2, Math.Min(_consecutiveFailures, 5));
            delaySeconds = Math.Min(delaySeconds, MaxRetryDelaySeconds);
            return TimeSpan.FromSeconds(delaySeconds);
        }
        
        private void LoadQueue()
        {
            try
            {
                if (File.Exists(_queueFilePath))
                {
                    var json = File.ReadAllText(_queueFilePath);
                    _queue = JsonSerializer.Deserialize<List<QueuedCapture>>(json) ?? new List<QueuedCapture>();
                    
                    if (_queue.Count > 0)
                    {
                        Logger.Info($"OfflineCaptureQueue: Loaded {_queue.Count} queued captures from disk");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"OfflineCaptureQueue: Failed to load queue: {ex.Message}");
                _queue = new List<QueuedCapture>();
            }
        }
        
        private void SaveQueue()
        {
            try
            {
                var json = JsonSerializer.Serialize(_queue, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_queueFilePath, json);
            }
            catch (Exception ex)
            {
                Logger.Warning($"OfflineCaptureQueue: Failed to save queue: {ex.Message}");
            }
        }
        
        public void Dispose()
        {
            _retryTimer?.Dispose();
        }
    }
    
    /// <summary>
    /// A capture queued for retry
    /// </summary>
    public class QueuedCapture
    {
        public Guid Id { get; set; }
        public UploadImageThumbnailDto Dto { get; set; } = new();
        public DateTime QueuedAt { get; set; }
        public DateTime? LastRetryAt { get; set; }
        public int RetryCount { get; set; }
        public string? LastError { get; set; }
    }
}
