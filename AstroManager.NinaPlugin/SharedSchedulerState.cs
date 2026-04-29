using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Context for a pending capture, stored until OnImageSaved fires
    /// </summary>
    public class PendingCaptureContext
    {
        public Guid? ScheduledTargetId { get; set; }
        public string? TargetName { get; set; }
        public Guid? ImagingGoalId { get; set; }
        public Guid? PanelId { get; set; }
        public int? PanelNumber { get; set; }
        public string? Filter { get; set; }
        public bool PreferSchedulerFilterForCaptureAttribution { get; set; }
        public double? ExposureTime { get; set; }
        public Guid? CaptureId { get; set; }
        public DateTime CapturedAt { get; set; }
    }

    public class CaptureMetricsCompletion
    {
        public Guid CaptureId { get; set; }
        public bool IsSuccess { get; set; }
        public double? Hfr { get; set; }
        public int? StarCount { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public string? Source { get; set; }
        public string? Reason { get; set; }
    }
    
    /// <summary>
    /// Singleton to share current scheduler state between AstroManagerTargetScheduler and AstroManagerPlugin.
    /// This allows the image upload handler to know which ImagingGoal/Target/Panel is currently being captured.
    /// </summary>
    public class SharedSchedulerState
    {
        private static readonly Lazy<SharedSchedulerState> _instance = new(() => new SharedSchedulerState());
        public static SharedSchedulerState Instance => _instance.Value;
        
        /// <summary>
        /// Queue of pending capture contexts waiting for OnImageSaved to fire.
        /// Since image saving is asynchronous and happens AFTER exposureContainer.Execute() returns,
        /// we queue the capture context here so OnImageSaved can retrieve it later.
        /// </summary>
        private readonly ConcurrentQueue<PendingCaptureContext> _pendingCaptures = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<CaptureMetricsCompletion>> _captureMetricCompletions = new();
        
        private SharedSchedulerState() { }
        
        /// <summary>
        /// Current scheduled target ID being imaged
        /// </summary>
        public Guid? CurrentScheduledTargetId { get; private set; }
        
        /// <summary>
        /// Current target name being imaged
        /// </summary>
        public string? CurrentTargetName { get; private set; }
        
        /// <summary>
        /// Current imaging goal ID being captured
        /// </summary>
        public Guid? CurrentImagingGoalId { get; private set; }
        
        /// <summary>
        /// Current panel ID (for mosaic targets)
        /// </summary>
        public Guid? CurrentPanelId { get; private set; }
        
        /// <summary>
        /// Current panel number (1-based) for mosaic targets
        /// </summary>
        public int? CurrentPanelNumber { get; private set; }
        
        /// <summary>
        /// Current filter being used
        /// </summary>
        public string? CurrentFilter { get; private set; }

        /// <summary>
        /// Whether AM/runtime slot filter should win over N.I.N.A. image metadata for capture attribution.
        /// </summary>
        public bool CurrentPreferSchedulerFilterForCaptureAttribution { get; private set; }
        
        /// <summary>
        /// Current exposure time in seconds
        /// </summary>
        public double? CurrentExposureTime { get; private set; }
        
        /// <summary>
        /// Pre-generated unique ID for the current capture (used for FITS header and database PK)
        /// </summary>
        public Guid? CurrentCaptureId { get; private set; }
        
        /// <summary>
        /// Flag indicating an AM-scheduled exposure is actively in progress.
        /// Only images captured while this is true should count towards target goals.
        /// </summary>
        public bool IsActiveExposure { get; private set; }
        
        /// <summary>
        /// Flag indicating the scheduler session is actively running (between start and stop).
        /// Used to determine if commands should be deferred to the scheduler.
        /// </summary>
        public bool IsSchedulerRunning { get; private set; }
        
        /// <summary>
        /// Flag indicating a pending autofocus request (user-triggered mid-session).
        /// Scheduler checks this after each exposure and runs AF if set.
        /// </summary>
        public bool PendingAutofocus { get; private set; }
        
        /// <summary>
        /// Flag indicating a pending guider calibration request (user-triggered mid-session).
        /// Scheduler checks this after each exposure and runs calibration if set.
        /// </summary>
        public bool PendingGuiderCalibration { get; private set; }

        /// <summary>
        /// Flag indicating a pending request to stop only the AM scheduler instruction.
        /// Scheduler checks this between slots and exits its loop gracefully.
        /// </summary>
        public bool PendingStopScheduler { get; private set; }
        
        /// <summary>
        /// Update the current slot state when scheduler starts a new exposure
        /// </summary>
        public void SetCurrentSlot(
            Guid? targetId,
            string? targetName,
            Guid? goalId,
            Guid? panelId,
            int? panelNumber,
            string? filter,
            bool preferSchedulerFilterForCaptureAttribution,
            double? exposureTime)
        {
            CurrentScheduledTargetId = targetId;
            CurrentTargetName = targetName;
            CurrentImagingGoalId = goalId;
            CurrentPanelId = panelId;
            CurrentPanelNumber = panelNumber;
            CurrentFilter = filter;
            CurrentPreferSchedulerFilterForCaptureAttribution = preferSchedulerFilterForCaptureAttribution;
            CurrentExposureTime = exposureTime;
        }
        
        /// <summary>
        /// Generate a new capture ID, mark exposure as active, and queue the capture context.
        /// The context is queued because OnImageSaved fires AFTER exposureContainer.Execute() returns,
        /// so we need to preserve the slot data for when the async image save completes.
        /// </summary>
        public Guid GenerateNewCaptureId()
        {
            CurrentCaptureId = Guid.NewGuid();
            IsActiveExposure = true;

            _captureMetricCompletions[CurrentCaptureId.Value] =
                new TaskCompletionSource<CaptureMetricsCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
            
            // Queue the capture context for OnImageSaved to retrieve later
            // This is critical because image saving is async and happens after MarkExposureComplete
            var context = new PendingCaptureContext
            {
                ScheduledTargetId = CurrentScheduledTargetId,
                TargetName = CurrentTargetName,
                ImagingGoalId = CurrentImagingGoalId,
                PanelId = CurrentPanelId,
                PanelNumber = CurrentPanelNumber,
                Filter = CurrentFilter,
                PreferSchedulerFilterForCaptureAttribution = CurrentPreferSchedulerFilterForCaptureAttribution,
                ExposureTime = CurrentExposureTime,
                CaptureId = CurrentCaptureId,
                CapturedAt = DateTime.UtcNow
            };
            _pendingCaptures.Enqueue(context);
            
            return CurrentCaptureId.Value;
        }

        public async Task<CaptureMetricsCompletion?> WaitForCaptureMetricsCompletionAsync(Guid captureId, CancellationToken token)
        {
            if (!_captureMetricCompletions.TryGetValue(captureId, out var completionSource))
            {
                return null;
            }

            var cancelTask = Task.Delay(Timeout.Infinite, token);
            var completedTask = await Task.WhenAny(completionSource.Task, cancelTask);
            if (completedTask == completionSource.Task)
            {
                return await completionSource.Task;
            }

            throw new OperationCanceledException(token);
        }

        public bool CompleteCaptureMetrics(
            Guid captureId,
            bool isSuccess,
            double? hfr,
            int? starCount,
            DateTime capturedAtUtc,
            string? source,
            string? reason = null)
        {
            if (!_captureMetricCompletions.TryRemove(captureId, out var completionSource))
            {
                return false;
            }

            return completionSource.TrySetResult(new CaptureMetricsCompletion
            {
                CaptureId = captureId,
                IsSuccess = isSuccess,
                Hfr = hfr,
                StarCount = starCount,
                CapturedAtUtc = capturedAtUtc,
                Source = source,
                Reason = reason
            });
        }

        public bool FailCaptureMetrics(Guid captureId, string reason)
        {
            return CompleteCaptureMetrics(
                captureId,
                isSuccess: false,
                hfr: null,
                starCount: null,
                capturedAtUtc: DateTime.UtcNow,
                source: "SchedulerState",
                reason: reason);
        }
        
        /// <summary>
        /// Try to get the next pending capture context for OnImageSaved.
        /// Returns null if no pending captures (e.g., manual capture, not AM-scheduled).
        /// </summary>
        public PendingCaptureContext? TryGetPendingCaptureContext()
        {
            if (_pendingCaptures.TryDequeue(out var context))
            {
                return context;
            }
            return null;
        }
        
        /// <summary>
        /// Get count of pending captures (for debugging)
        /// </summary>
        public int PendingCaptureCount => _pendingCaptures.Count;
        
        /// <summary>
        /// Mark the current exposure as complete (called after exposure finishes)
        /// Note: The capture context is already queued by GenerateNewCaptureId for OnImageSaved
        /// </summary>
        public void MarkExposureComplete()
        {
            IsActiveExposure = false;
            CurrentCaptureId = null;
        }
        
        /// <summary>
        /// Set scheduler running state (called when session starts/stops)
        /// </summary>
        public void SetSchedulerRunning(bool running)
        {
            IsSchedulerRunning = running;
            if (!running)
            {
                // Clear pending commands when scheduler stops
                PendingAutofocus = false;
                PendingGuiderCalibration = false;
                PendingStopScheduler = false;
                CompleteAllPendingCaptureMetrics("Scheduler stopped before image metrics were finalized");
            }
        }
        
        /// <summary>
        /// Queue an autofocus to run after the current exposure completes.
        /// Returns true if queued successfully, false if scheduler is not running.
        /// </summary>
        public bool QueueAutofocus()
        {
            if (!IsSchedulerRunning) return false;
            PendingAutofocus = true;
            return true;
        }
        
        /// <summary>
        /// Queue a guider calibration to run after the current exposure completes.
        /// Returns true if queued successfully, false if scheduler is not running.
        /// </summary>
        public bool QueueGuiderCalibration()
        {
            if (!IsSchedulerRunning) return false;
            PendingGuiderCalibration = true;
            return true;
        }

        /// <summary>
        /// Queue a request to stop the AM scheduler instruction (without stopping the full NINA sequence).
        /// Returns true if queued successfully, false if scheduler is not running.
        /// </summary>
        public bool QueueStopScheduler()
        {
            if (!IsSchedulerRunning) return false;
            PendingStopScheduler = true;
            return true;
        }
        
        /// <summary>
        /// Check and clear pending autofocus flag.
        /// Returns true if AF was pending (and clears it).
        /// </summary>
        public bool ConsumeAutofocusRequest()
        {
            if (!PendingAutofocus) return false;
            PendingAutofocus = false;
            return true;
        }
        
        /// <summary>
        /// Check and clear pending guider calibration flag.
        /// Returns true if calibration was pending (and clears it).
        /// </summary>
        public bool ConsumeGuiderCalibrationRequest()
        {
            if (!PendingGuiderCalibration) return false;
            PendingGuiderCalibration = false;
            return true;
        }

        /// <summary>
        /// Check and clear pending AM scheduler stop request.
        /// Returns true if stop was requested (and clears it).
        /// </summary>
        public bool ConsumeStopSchedulerRequest()
        {
            if (!PendingStopScheduler) return false;
            PendingStopScheduler = false;
            return true;
        }
        
        /// <summary>
        /// Clear the current slot state when scheduler finishes or stops
        /// </summary>
        public void Clear()
        {
            CurrentScheduledTargetId = null;
            CurrentTargetName = null;
            CurrentImagingGoalId = null;
            CurrentPanelId = null;
            CurrentPanelNumber = null;
            CurrentFilter = null;
            CurrentPreferSchedulerFilterForCaptureAttribution = false;
            CurrentExposureTime = null;
            CurrentCaptureId = null;
            IsActiveExposure = false;
            IsSchedulerRunning = false;
            PendingAutofocus = false;
            PendingGuiderCalibration = false;
            PendingStopScheduler = false;
            
            // Clear pending captures queue (don't process leftover contexts after session ends)
            while (_pendingCaptures.TryDequeue(out _)) { }

            CompleteAllPendingCaptureMetrics("Scheduler state cleared before image metrics were finalized");
        }

        private void CompleteAllPendingCaptureMetrics(string reason)
        {
            foreach (var pair in _captureMetricCompletions)
            {
                if (_captureMetricCompletions.TryRemove(pair.Key, out var completionSource))
                {
                    completionSource.TrySetResult(new CaptureMetricsCompletion
                    {
                        CaptureId = pair.Key,
                        IsSuccess = false,
                        Hfr = null,
                        StarCount = null,
                        CapturedAtUtc = DateTime.UtcNow,
                        Source = "SchedulerState",
                        Reason = reason
                    });
                }
            }
        }
    }
}
