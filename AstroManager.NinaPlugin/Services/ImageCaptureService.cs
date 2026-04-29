using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using Shared.Model.DTO.Client;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AstroManager.NinaPlugin.Services
{
    /// <summary>
    /// Service responsible for handling image capture events, thumbnail generation,
    /// FITS header injection, and image upload to AstroManager API.
    /// </summary>
    public class ImageCaptureService
    {
        private readonly AstroManagerApiClient _apiClient;
        private readonly HeartbeatService _heartbeatService;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly IRotatorMediator _rotatorMediator;
        private readonly IFocuserMediator _focuserMediator;
        private readonly IGuiderMediator _guiderMediator;
        private readonly IWeatherDataMediator _weatherDataMediator;
        private readonly OfflineCaptureQueue _offlineCaptureQueue;
        private readonly AstroManagerSettings _settings;
        private readonly Action<string> _raisePropertyChanged;
        private readonly Action<string> _setLastCapturedImage;

        // Cached plate solve data
        private double? _cachedPlateSolvePA;
        private DateTime? _cachedPlateSolveTime;
        private double? _cachedPixelScale;

        public ImageCaptureService(
            AstroManagerApiClient apiClient,
            HeartbeatService heartbeatService,
            ITelescopeMediator telescopeMediator,
            IRotatorMediator rotatorMediator,
            IFocuserMediator focuserMediator,
            IGuiderMediator guiderMediator,
            IWeatherDataMediator weatherDataMediator,
            OfflineCaptureQueue offlineCaptureQueue,
            AstroManagerSettings settings,
            Action<string> raisePropertyChanged,
            Action<string> setLastCapturedImage)
        {
            _apiClient = apiClient;
            _heartbeatService = heartbeatService;
            _telescopeMediator = telescopeMediator;
            _rotatorMediator = rotatorMediator;
            _focuserMediator = focuserMediator;
            _guiderMediator = guiderMediator;
            _weatherDataMediator = weatherDataMediator;
            _offlineCaptureQueue = offlineCaptureQueue;
            _settings = settings;
            _raisePropertyChanged = raisePropertyChanged;
            _setLastCapturedImage = setLastCapturedImage;
        }

        /// <summary>
        /// Handle AutoFocusPoints collection changes - captures ALL AF runs
        /// </summary>
        public void OnAutoFocusPointsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add || e.NewItems == null)
                return;

            try
            {
                Logger.Info($"AstroManager: AutoFocusPoints collection changed - {e.NewItems.Count} new item(s) added");
                
                foreach (var item in e.NewItems)
                {
                    if (item == null) continue;
                    
                    var itemType = item.GetType();
                    Logger.Info($"AstroManager: Processing AF point of type: {itemType.FullName}");
                    
                    var dataPoints = new List<AutofocusDataPointDto>();
                    int finalPosition = 0;
                    double finalHfr = 0;
                    double temperature = 0;
                    string? filter = null;
                    string? fittingMethod = null;
                    
                    // Extract MeasurePoints
                    var measurePointsProp = itemType.GetProperty("MeasurePoints");
                    var measurePoints = measurePointsProp?.GetValue(item) as System.Collections.IEnumerable;
                    if (measurePoints != null)
                    {
                        foreach (var mp in measurePoints)
                        {
                            var mpType = mp.GetType();
                            var posVal = mpType.GetProperty("Position")?.GetValue(mp);
                            var hfrVal = mpType.GetProperty("Value")?.GetValue(mp);
                            
                            double pos = posVal != null ? Convert.ToDouble(posVal) : 0;
                            double hfr = hfrVal != null ? Convert.ToDouble(hfrVal) : 0;
                            
                            if (pos > 0 && hfr > 0)
                            {
                                dataPoints.Add(new AutofocusDataPointDto
                                {
                                    Position = (int)pos,
                                    Hfr = hfr,
                                    StarCount = 0
                                });
                            }
                        }
                    }
                    
                    // Get calculated focus point
                    var calcPointProp = itemType.GetProperty("CalculatedFocusPoint");
                    var calcPoint = calcPointProp?.GetValue(item);
                    if (calcPoint != null)
                    {
                        var cpType = calcPoint.GetType();
                        var cpPos = cpType.GetProperty("Position")?.GetValue(calcPoint);
                        var cpHfr = cpType.GetProperty("Value")?.GetValue(calcPoint);
                        if (cpPos != null) finalPosition = Convert.ToInt32(cpPos);
                        if (cpHfr != null) finalHfr = Convert.ToDouble(cpHfr);
                    }
                    
                    // Get temperature
                    var tempProp = itemType.GetProperty("Temperature");
                    var tempVal = tempProp?.GetValue(item);
                    if (tempVal != null) temperature = Convert.ToDouble(tempVal);
                    
                    // Get filter
                    var filterProp = itemType.GetProperty("Filter");
                    filter = filterProp?.GetValue(item) as string;
                    
                    // Get fitting method
                    var fittingsProp = itemType.GetProperty("Fittings");
                    var fittingsVal = fittingsProp?.GetValue(item);
                    if (fittingsVal != null) fittingMethod = fittingsVal.ToString();
                    
                    // Get R² values
                    double? rSquared = null;
                    double? rSquaredHyperbolic = null;
                    double? rSquaredParabolic = null;
                    var rSquaresProp = itemType.GetProperty("RSquares");
                    var rSquaresVal = rSquaresProp?.GetValue(item);
                    if (rSquaresVal != null)
                    {
                        var rsType = rSquaresVal.GetType();
                        var hypR2Prop = rsType.GetProperty("Hyperbolic");
                        var paraR2Prop = rsType.GetProperty("Parabolic");
                        
                        if (hypR2Prop != null)
                        {
                            var hypR2Val = hypR2Prop.GetValue(rSquaresVal);
                            if (hypR2Val != null) rSquaredHyperbolic = Convert.ToDouble(hypR2Val);
                        }
                        if (paraR2Prop != null)
                        {
                            var paraR2Val = paraR2Prop.GetValue(rSquaresVal);
                            if (paraR2Val != null) rSquaredParabolic = Convert.ToDouble(paraR2Val);
                        }
                        
                        var fm = fittingMethod ?? "Hyperbolic";
                        if (fm.Contains("Hyperbolic", StringComparison.OrdinalIgnoreCase) && rSquaredHyperbolic.HasValue)
                            rSquared = rSquaredHyperbolic;
                        else if (fm.Contains("Parabolic", StringComparison.OrdinalIgnoreCase) && rSquaredParabolic.HasValue)
                            rSquared = rSquaredParabolic;
                        else
                            rSquared = rSquaredHyperbolic ?? rSquaredParabolic;
                    }
                    
                    // Get Succeeded property
                    bool? ninaSucceeded = null;
                    var succeededProp = itemType.GetProperty("Succeeded");
                    if (succeededProp != null)
                    {
                        var succeededVal = succeededProp.GetValue(item);
                        if (succeededVal != null) ninaSucceeded = Convert.ToBoolean(succeededVal);
                    }
                    
                    if (dataPoints.Count > 0 || finalPosition > 0 || ninaSucceeded == true)
                    {
                        var afSuccess = ninaSucceeded ?? (finalPosition > 0 && finalHfr > 0);
                        
                        var afReport = new AutofocusReportDto
                        {
                            CompletedAt = DateTime.UtcNow,
                            Success = afSuccess,
                            FinalPosition = finalPosition,
                            FinalHfr = finalHfr,
                            Temperature = temperature,
                            Filter = filter,
                            FittingMethod = fittingMethod ?? "Hyperbolic",
                            DataPoints = dataPoints,
                            FailureReason = null,
                            RSquared = rSquared,
                            RSquaredHyperbolic = rSquaredHyperbolic,
                            RSquaredParabolic = rSquaredParabolic
                        };
                        
                        Logger.Info($"AstroManager: Captured sequence AF run - Success: {afSuccess}, Position: {finalPosition}, HFR: {finalHfr:F2}, Points: {dataPoints.Count}, R²: {rSquared:F4}");
                        
                        var afLogMsg = afSuccess 
                            ? $"AF Complete: Pos={finalPosition}, HFR={finalHfr:F2}{(filter != null ? $", Filter={filter}" : "")}"
                            : $"AF Failed{(filter != null ? $" (Filter={filter})" : "")}";
                        SharedSchedulerLog.Instance.AddEntry(afLogMsg, afSuccess ? SchedulerLogLevel.Success : SchedulerLogLevel.Warning);
                        
                        _heartbeatService.SetAutofocusReport(afReport);
                        _ = _heartbeatService.ForceStatusUpdateAsync();
                    }
                    else
                    {
                        Logger.Debug($"AstroManager: AF point had no extractable data - skipping");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Error in OnAutoFocusPointsCollectionChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle ImageHistory collection changes - captures plate solve PA when images are added
        /// </summary>
        public void OnImageHistoryCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add || e.NewItems == null)
                return;
            
            try
            {
                foreach (var newItem in e.NewItems)
                {
                    if (newItem == null) continue;
                    
                    var imageType = newItem.GetType();
                    
                    try
                    {
                        var psResultProp = imageType.GetProperty("PlateSolveResult") 
                            ?? imageType.GetProperty("PlateSolveInfo")
                            ?? imageType.GetProperty("PlateSolve");
                        
                        if (psResultProp != null)
                        {
                            var plateSolveResult = psResultProp.GetValue(newItem);
                            if (plateSolveResult != null)
                            {
                                var psType = plateSolveResult.GetType();
                                
                                var paProp = psType.GetProperty("PositionAngle") 
                                    ?? psType.GetProperty("Rotation")
                                    ?? psType.GetProperty("Orientation");
                                var scaleProp = psType.GetProperty("PixelScale") 
                                    ?? psType.GetProperty("Pixscale");
                                
                                if (paProp != null)
                                {
                                    var pa = paProp.GetValue(plateSolveResult);
                                    if (pa != null)
                                    {
                                        _cachedPlateSolvePA = Convert.ToDouble(pa);
                                        _cachedPlateSolveTime = DateTime.UtcNow;
                                        Logger.Info($"AstroManager: [PS-CACHE] Captured plate solve PA={_cachedPlateSolvePA:F2}° from ImageHistory");
                                    }
                                }
                                if (scaleProp != null)
                                {
                                    var scale = scaleProp.GetValue(plateSolveResult);
                                    if (scale != null)
                                    {
                                        _cachedPixelScale = Convert.ToDouble(scale);
                                        Logger.Debug($"AstroManager: [PS-CACHE] Captured pixel scale={_cachedPixelScale:F2}\"/px from ImageHistory");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception propEx)
                    {
                        Logger.Debug($"AstroManager: Could not extract plate solve info from ImageHistory item: {propEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Error in OnImageHistoryCollectionChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle image saved events - upload thumbnail and metadata to API
        /// </summary>
        public void OnImageSavedAsync(object? sender, ImageSavedEventArgs e)
        {
            _ = ProcessImageSavedAsync(sender, e);
        }

        private async Task ProcessImageSavedAsync(object? sender, ImageSavedEventArgs e)
        {
            var pendingContext = SharedSchedulerState.Instance.TryGetPendingCaptureContext();
            var isAmScheduledCapture = pendingContext != null;
            
            var scheduledTargetId = pendingContext?.ScheduledTargetId;
            var imagingGoalId = pendingContext?.ImagingGoalId;
            var panelId = pendingContext?.PanelId;
            var panelNumber = pendingContext?.PanelNumber;
            var captureId = pendingContext?.CaptureId;
            var schedulerFilter = pendingContext?.Filter;
            var preferSchedulerFilterForCaptureAttribution = pendingContext?.PreferSchedulerFilterForCaptureAttribution == true;
            var capturedAtUtc = DateTime.UtcNow;
            var captureHfr = SanitizeDouble(e.StarDetectionAnalysis?.HFR);
            var captureStarCount = e.StarDetectionAnalysis?.DetectedStars;
            var handlerSucceeded = false;
            string? completionReason = null;
            UploadImageThumbnailDto? dto = null;
            string? fileName = null;

            if (captureId.HasValue && (captureHfr.HasValue || captureStarCount.HasValue))
            {
                _heartbeatService.SetLastCaptureMetrics(captureHfr, captureStarCount, capturedAtUtc);
            }
            
            Logger.Debug($"AstroManager: Image saved - IsAmScheduled={isAmScheduledCapture}, TargetId={scheduledTargetId}, GoalId={imagingGoalId}, PanelId={panelId}, PanelNum={panelNumber}, CaptureId={captureId}, SchedulerFilter={schedulerFilter}, PendingQueue={SharedSchedulerState.Instance.PendingCaptureCount}");

            try
            {
                if (e.PathToImage?.LocalPath != null)
                {
                    await InjectFitsHeadersAsync(e.PathToImage.LocalPath, imagingGoalId, captureId);
                }

                if (string.IsNullOrEmpty(_settings.LicenseKey) || !_settings.EnableImageUpload)
                {
                    handlerSucceeded = true;
                    return;
                }

                Logger.Debug("AstroManager: Image saved event received");
                
                var (thumbnailBase64, microThumbnailBase64) = await GenerateThumbnailsAsync(e);
                if (string.IsNullOrEmpty(thumbnailBase64))
                {
                    Logger.Warning("AstroManager: Failed to generate thumbnail");
                    completionReason = "Failed to generate thumbnail";
                    return;
                }

                var stats = BuildImageStats(e);
                
                fileName = e.PathToImage?.LocalPath != null 
                    ? Path.GetFileName(e.PathToImage.LocalPath) 
                    : null;

                var imageReportedFilter = e.Filter?.Trim();
                var scheduledFilter = schedulerFilter?.Trim();

                if (!string.IsNullOrWhiteSpace(imageReportedFilter)
                    && !string.IsNullOrWhiteSpace(scheduledFilter)
                    && !string.Equals(imageReportedFilter, scheduledFilter, StringComparison.OrdinalIgnoreCase))
                {
                    if (preferSchedulerFilterForCaptureAttribution)
                    {
                        Logger.Info($"AstroManager: AstroManager/runtime filter '{scheduledFilter}' overrides N.I.N.A. image filter '{imageReportedFilter}' for capture attribution");
                    }
                    else
                    {
                        Logger.Info($"AstroManager: Image filter '{imageReportedFilter}' reported by N.I.N.A. overrides scheduler filter '{scheduledFilter}' for capture attribution");
                    }
                }

                var filterToUse = preferSchedulerFilterForCaptureAttribution
                    ? (!string.IsNullOrWhiteSpace(scheduledFilter) ? scheduledFilter : imageReportedFilter)
                    : (!string.IsNullOrWhiteSpace(imageReportedFilter) ? imageReportedFilter : scheduledFilter);
                
                dto = new UploadImageThumbnailDto
                {
                    ThumbnailBase64 = thumbnailBase64,
                    MicroThumbnailBase64 = microThumbnailBase64,
                    FileName = fileName,
                    Stats = stats,
                    Filter = filterToUse,
                    ExposureTime = SanitizeDouble(e.MetaData?.Image?.ExposureTime),
                    HFR = captureHfr,
                    DetectedStars = captureStarCount,
                    TargetName = e.MetaData?.Target?.Name,
                    Gain = e.MetaData?.Camera?.Gain,
                    Offset = e.MetaData?.Camera?.Offset,
                    CameraTemp = SanitizeDouble(e.MetaData?.Camera?.Temperature),
                    Binning = e.MetaData?.Camera?.BinX,
                    
                    Mean = SanitizeDouble(e.Statistics?.Mean),
                    Median = SanitizeDouble(e.Statistics?.Median),
                    StdDev = SanitizeDouble(e.Statistics?.StDev),
                    Min = SanitizeDouble(e.Statistics?.Min),
                    Max = SanitizeDouble(e.Statistics?.Max),
                    
                    RightAscension = SanitizeDouble(e.MetaData?.Target?.Coordinates?.RA),
                    Declination = SanitizeDouble(e.MetaData?.Target?.Coordinates?.Dec),
                    Altitude = SanitizeDouble(_telescopeMediator?.GetInfo()?.Altitude),
                    Azimuth = SanitizeDouble(_telescopeMediator?.GetInfo()?.Azimuth),
                    
                    CameraName = e.MetaData?.Camera?.Name,
                    TelescopeName = e.MetaData?.Telescope?.Name,
                    MountName = _telescopeMediator?.GetInfo()?.Name,
                    FocalLength = SanitizeDouble(e.MetaData?.Telescope?.FocalLength),
                    Aperture = SanitizeDouble(e.MetaData?.Telescope?.FocalRatio > 0 && e.MetaData?.Telescope?.FocalLength > 0 
                        ? e.MetaData.Telescope.FocalLength / e.MetaData.Telescope.FocalRatio : null),
                    PixelScale = SanitizeDouble(e.MetaData?.Image?.RecordedRMS?.Scale),
                    PixelSizeX = SanitizeDouble(e.MetaData?.Camera?.PixelSize),
                    PixelSizeY = SanitizeDouble(e.MetaData?.Camera?.PixelSize),
                    RotatorAngle = SanitizeDouble(_rotatorMediator?.GetInfo()?.Position),
                    FocuserPosition = _focuserMediator?.GetInfo()?.Position,
                    
                    SiteLatitude = SanitizeDouble(e.MetaData?.Observer?.Latitude),
                    SiteLongitude = SanitizeDouble(e.MetaData?.Observer?.Longitude),
                    SiteElevation = SanitizeDouble(e.MetaData?.Observer?.Elevation),
                    
                    GuidingRmsRA = SanitizeDouble(e.MetaData?.Image?.RecordedRMS?.RA) 
                        ?? SanitizeDouble(_guiderMediator?.GetInfo()?.RMSError?.RA?.Arcseconds),
                    GuidingRmsDec = SanitizeDouble(e.MetaData?.Image?.RecordedRMS?.Dec)
                        ?? SanitizeDouble(_guiderMediator?.GetInfo()?.RMSError?.Dec?.Arcseconds),
                    GuidingRmsTotal = SanitizeDouble(e.MetaData?.Image?.RecordedRMS?.Total)
                        ?? SanitizeDouble(_guiderMediator?.GetInfo()?.RMSError?.Total?.Arcseconds),
                    
                    ImageWidth = e.Image?.PixelWidth,
                    ImageHeight = e.Image?.PixelHeight,
                    
                    Software = "N.I.N.A.",
                    CapturedAt = capturedAtUtc,
                    
                    ScheduledTargetId = scheduledTargetId,
                    ImagingGoalId = imagingGoalId,
                    PanelId = panelId,
                    PanelNumber = panelNumber,
                    
                    WeatherTemperature = SanitizeDouble(_weatherDataMediator?.GetInfo()?.Temperature),
                    WeatherHumidity = SanitizeDouble(_weatherDataMediator?.GetInfo()?.Humidity),
                    WeatherDewPoint = SanitizeDouble(_weatherDataMediator?.GetInfo()?.DewPoint),
                    WeatherPressure = SanitizeDouble(_weatherDataMediator?.GetInfo()?.Pressure),
                    WeatherCloudCover = SanitizeDouble(_weatherDataMediator?.GetInfo()?.CloudCover),
                    WeatherWindSpeed = SanitizeDouble(_weatherDataMediator?.GetInfo()?.WindSpeed),
                    WeatherSkyQuality = SanitizeDouble(_weatherDataMediator?.GetInfo()?.SkyQuality)
                };

                var success = await _apiClient.UploadImageThumbnailAsync(dto);
                
                if (success)
                {
                    Logger.Info($"AstroManager: Thumbnail uploaded for {fileName}");
                    _setLastCapturedImage(fileName ?? "Unknown");
                    _raisePropertyChanged("LastCapturedImage");
                    handlerSucceeded = true;
                }
                else
                {
                    Logger.Warning($"AstroManager: Failed to upload thumbnail for {fileName} - queuing for retry");
                    _offlineCaptureQueue.Enqueue(dto, "Upload returned false");
                    completionReason = "Upload returned false (queued for retry)";
                }
            }
            catch (Exception ex)
            {
                completionReason = ex.Message;
                Logger.Error($"AstroManager: Error uploading thumbnail for {fileName ?? e.PathToImage?.LocalPath}: {ex.Message}");
                
                if (dto != null)
                {
                    Logger.Warning($"AstroManager: Queuing {fileName ?? "Unknown"} for retry due to error");
                    _offlineCaptureQueue.Enqueue(dto, ex.Message);
                }
                else
                {
                    Logger.Warning($"AstroManager: Cannot queue {fileName ?? "Unknown"} - DTO was not built before error");
                }
            }
            finally
            {
                if (captureId.HasValue)
                {
                    var completionSet = SharedSchedulerState.Instance.CompleteCaptureMetrics(
                        captureId.Value,
                        isSuccess: handlerSucceeded,
                        hfr: captureHfr,
                        starCount: captureStarCount,
                        capturedAtUtc: capturedAtUtc,
                        source: nameof(ProcessImageSavedAsync),
                        reason: completionReason);

                    if (!completionSet)
                    {
                        Logger.Debug($"AstroManager: Capture metrics completion was not pending for CaptureId={captureId}");
                    }
                }
            }
        }

        /// <summary>
        /// Inject AstroManager custom FITS headers into a saved FITS file
        /// </summary>
        private async Task InjectFitsHeadersAsync(string filePath, Guid? goalId, Guid? captureId)
        {
            if (!filePath.EndsWith(".fits", StringComparison.OrdinalIgnoreCase) &&
                !filePath.EndsWith(".fit", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Debug($"AstroManager: Skipping FITS header injection for non-FITS file: {filePath}");
                return;
            }
            
            if (!goalId.HasValue && !captureId.HasValue)
            {
                Logger.Debug("AstroManager: No GoalId or CaptureId to inject into FITS header");
                return;
            }
            
            const int maxRetries = 3;
            const int retryDelayMs = 500;
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await Task.Run(() => InjectFitsHeadersCore(filePath, goalId, captureId));
                    return;
                }
                catch (IOException ex) when (attempt < maxRetries)
                {
                    Logger.Warning($"AstroManager: FITS header injection attempt {attempt}/{maxRetries} failed (file locked), retrying in {retryDelayMs}ms: {ex.Message}");
                    await Task.Delay(retryDelayMs * attempt);
                }
                catch (Exception ex)
                {
                    Logger.Error($"AstroManager: Error injecting FITS headers (attempt {attempt}/{maxRetries}): {ex.Message}");
                    return;
                }
            }
            
            Logger.Error($"AstroManager: Failed to inject FITS headers after {maxRetries} attempts: {filePath}");
        }
        
        private void InjectFitsHeadersCore(string filePath, Guid? goalId, Guid? captureId)
        {
            const int BLOCK_SIZE = 2880;
            const int CARD_SIZE = 80;
            
            var fileBytes = File.ReadAllBytes(filePath);
            
            int endPosition = -1;
            for (int i = 0; i < fileBytes.Length - 3; i++)
            {
                if (fileBytes[i] == 'E' && fileBytes[i + 1] == 'N' && fileBytes[i + 2] == 'D' &&
                    (i + 3 >= fileBytes.Length || fileBytes[i + 3] == ' ' || fileBytes[i + 3] == 0))
                {
                    if (i % CARD_SIZE == 0 || (i > 0 && i % CARD_SIZE == 0))
                    {
                        endPosition = i;
                        break;
                    }
                }
            }
            
            if (endPosition < 0)
            {
                Logger.Warning($"AstroManager: Could not find END keyword in FITS header: {filePath}");
                return;
            }
            
            var newCards = new List<string>();
            
            if (goalId.HasValue)
            {
                var card = $"AM_GOALID= '{goalId.Value,-36}' / AstroManager ImagingGoal ID";
                newCards.Add(card.PadRight(CARD_SIZE));
            }
            
            if (captureId.HasValue)
            {
                var card = $"AM_UID  = '{captureId.Value,-36}' / AstroManager Capture UID";
                newCards.Add(card.PadRight(CARD_SIZE));
            }
            
            if (newCards.Count == 0) return;
            
            int headerEnd = ((endPosition / BLOCK_SIZE) + 1) * BLOCK_SIZE;
            int newCardsSize = newCards.Count * CARD_SIZE;
            
            int spaceBeforeEnd = 0;
            for (int i = endPosition - CARD_SIZE; i >= 0 && i >= endPosition - (BLOCK_SIZE - CARD_SIZE); i -= CARD_SIZE)
            {
                bool isEmpty = true;
                for (int j = 0; j < CARD_SIZE; j++)
                {
                    if (fileBytes[i + j] != ' ' && fileBytes[i + j] != 0)
                    {
                        isEmpty = false;
                        break;
                    }
                }
                if (!isEmpty) break;
                spaceBeforeEnd += CARD_SIZE;
            }
            
            int spaceAfterEnd = headerEnd - endPosition - CARD_SIZE;
            int totalSpace = spaceBeforeEnd + spaceAfterEnd;
            
            if (totalSpace >= newCardsSize)
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.Position = endPosition;
                
                foreach (var card in newCards)
                {
                    var cardBytes = System.Text.Encoding.ASCII.GetBytes(card);
                    stream.Write(cardBytes, 0, CARD_SIZE);
                }
                
                var endCard = "END".PadRight(CARD_SIZE);
                var endBytes = System.Text.Encoding.ASCII.GetBytes(endCard);
                stream.Write(endBytes, 0, CARD_SIZE);
                
                int remaining = headerEnd - (int)stream.Position;
                if (remaining > 0)
                {
                    var blanks = new byte[remaining];
                    for (int i = 0; i < remaining; i++) blanks[i] = (byte)' ';
                    stream.Write(blanks, 0, remaining);
                }
                
                Logger.Info($"AstroManager: Injected FITS headers - AM_GOALID={goalId}, AM_UID={captureId} into {Path.GetFileName(filePath)}");
            }
            else
            {
                Logger.Warning($"AstroManager: Not enough space in FITS header to inject custom keywords: {filePath}");
            }
        }

        private async Task<(string? thumbnail, string? microThumbnail)> GenerateThumbnailsAsync(ImageSavedEventArgs e)
        {
            try
            {
                var bitmapSource = e.Image;
                if (bitmapSource == null)
                {
                    Logger.Debug("AstroManager: No image data available");
                    return (null, null);
                }

                return await Task.Run(() =>
                {
                    try
                    {
                        if (!bitmapSource.IsFrozen)
                        {
                            bitmapSource = bitmapSource.Clone();
                            bitmapSource.Freeze();
                        }
                        
                        int width = bitmapSource.PixelWidth;
                        int height = bitmapSource.PixelHeight;
                        
                        const int maxWidth = 1080;
                        const int maxHeight = 810;
                        double scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
                        
                        string? thumbnailBase64 = null;
                        using (var ms = new MemoryStream())
                        {
                            var encoder = new JpegBitmapEncoder();
                            encoder.QualityLevel = 90;
                            var scaledBitmap = new TransformedBitmap(
                                bitmapSource,
                                new System.Windows.Media.ScaleTransform(scale, scale));
                            encoder.Frames.Add(BitmapFrame.Create(scaledBitmap));
                            encoder.Save(ms);
                            thumbnailBase64 = Convert.ToBase64String(ms.ToArray());
                        }
                        
                        const int microMaxWidth = 50;
                        const int microMaxHeight = 50;
                        double microScale = Math.Min((double)microMaxWidth / width, (double)microMaxHeight / height);
                        
                        string? microThumbnailBase64 = null;
                        using (var ms = new MemoryStream())
                        {
                            var encoder = new JpegBitmapEncoder();
                            encoder.QualityLevel = 70;
                            var scaledBitmap = new TransformedBitmap(
                                bitmapSource,
                                new System.Windows.Media.ScaleTransform(microScale, microScale));
                            encoder.Frames.Add(BitmapFrame.Create(scaledBitmap));
                            encoder.Save(ms);
                            microThumbnailBase64 = Convert.ToBase64String(ms.ToArray());
                        }
                        
                        Logger.Debug($"AstroManager: Generated thumbnails - regular: {thumbnailBase64?.Length ?? 0} chars, micro: {microThumbnailBase64?.Length ?? 0} chars");
                        
                        return (thumbnailBase64, microThumbnailBase64);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"AstroManager: Thumbnail generation error: {ex.Message}");
                        return (null, null);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: GenerateThumbnailsAsync error: {ex.Message}");
                return (null, null);
            }
        }

        private string BuildImageStats(ImageSavedEventArgs e)
        {
            var parts = new List<string>();
            
            if (e.StarDetectionAnalysis?.HFR > 0)
            {
                parts.Add($"HFR: {e.StarDetectionAnalysis.HFR:F2}");
            }
            
            if (e.StarDetectionAnalysis?.DetectedStars > 0)
            {
                parts.Add($"Stars: {e.StarDetectionAnalysis.DetectedStars}");
            }
            
            if (!string.IsNullOrEmpty(e.Filter))
            {
                parts.Add($"Filter: {e.Filter}");
            }
            
            if (e.MetaData?.Image?.ExposureTime > 0)
            {
                parts.Add($"Exp: {e.MetaData.Image.ExposureTime}s");
            }
            
            return parts.Count > 0 ? string.Join(" | ", parts) : "No stats available";
        }

        private static double? SanitizeDouble(double? value)
        {
            if (!value.HasValue) return null;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return null;
            return value.Value;
        }
    }
}
