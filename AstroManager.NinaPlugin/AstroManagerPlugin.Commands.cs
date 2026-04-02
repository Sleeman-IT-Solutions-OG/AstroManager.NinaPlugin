using NINA.Core.Utility;
using NINA.Astrometry;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using Shared.Model.DTO.Scheduler;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WpfBrushes = System.Windows.Media.Brushes;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Partial class containing command implementations:
    /// - Heartbeat start/stop
    /// - Connection testing
    /// - Target sync/save/delete
    /// - Settings import/export
    /// - Equipment status updates
    /// </summary>
    public partial class AstroManagerPlugin
    {
        private bool? _lastObservedSequenceRunning;

        #region Error Handling
        
        private void SetError(string message)
        {
            HasError = true;
            ErrorMessage = message;
        }

        private void ClearError()
        {
            HasError = false;
            ErrorMessage = string.Empty;
        }
        
        #endregion

        #region Sequence Name Extraction
        
        /// <summary>
        /// Get the sequence name via reflection from NINA's SequenceMediator.
        /// Must be called on the WPF dispatcher thread.
        /// </summary>
        private string? GetSequenceNameViaReflection()
        {
            try
            {
                var mediatorType = _sequenceMediator.GetType();
                
                var navField = mediatorType.GetField("sequenceNavigation", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?? mediatorType.GetField("_sequenceNavigation", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (navField == null)
                {
                    return null;
                }
                
                var nav = navField.GetValue(_sequenceMediator);
                if (nav == null)
                {
                    return null;
                }
                
                var navType = nav.GetType();
                var sequence2VMProp = navType.GetProperty("Sequence2VM");
                if (sequence2VMProp == null)
                {
                    return null;
                }
                
                var sequence2VM = sequence2VMProp.GetValue(nav);
                if (sequence2VM == null)
                {
                    return null;
                }
                
                var seq2Type = sequence2VM.GetType();
                // Try multiple property names - NINA uses different names in different versions
                var seqProp = seq2Type.GetProperty("Sequencer") 
                    ?? seq2Type.GetProperty("Sequence")
                    ?? seq2Type.GetProperty("RootContainer")
                    ?? seq2Type.GetProperty("MainContainer");
                    
                if (seqProp == null)
                {
                    return null;
                }
                
                var seq = seqProp.GetValue(sequence2VM);
                if (seq == null)
                {
                    return null;
                }
                
                var nameProp = seq.GetType().GetProperty("Name");
                if (nameProp != null)
                {
                    return nameProp.GetValue(seq) as string;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }
        
        #endregion

        #region Coordinate Formatting and Parsing
        
        private string FormatRA(double raHours)
        {
            var hours = (int)raHours;
            var minutes = (int)((raHours - hours) * 60);
            var seconds = ((raHours - hours) * 60 - minutes) * 60;
            return $"{hours:00}h {minutes:00}m {seconds:00.0}s";
        }

        private string FormatDec(double decDegrees)
        {
            var sign = decDegrees >= 0 ? "+" : "-";
            var absDec = Math.Abs(decDegrees);
            var degrees = (int)absDec;
            var arcmin = (int)((absDec - degrees) * 60);
            var arcsec = ((absDec - degrees) * 60 - arcmin) * 60;
            return $"{sign}{degrees:00}° {arcmin:00}' {arcsec:00.0}\"";
        }

        private bool TryParseRA(string input, out double raHours)
        {
            raHours = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;
            
            try
            {
                input = input.Trim().ToLowerInvariant()
                    .Replace("h", " ").Replace("m", " ").Replace("s", " ")
                    .Replace(":", " ").Replace("°", " ").Replace("'", " ").Replace("\"", " ");
                
                var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && double.TryParse(parts[0], out var h))
                {
                    raHours = h;
                    if (parts.Length >= 2 && double.TryParse(parts[1], out var m))
                        raHours += m / 60.0;
                    if (parts.Length >= 3 && double.TryParse(parts[2], out var s))
                        raHours += s / 3600.0;
                    
                    return raHours >= 0 && raHours < 24;
                }
            }
            catch { }
            return false;
        }

        private bool TryParseDec(string input, out double decDegrees)
        {
            decDegrees = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;
            
            try
            {
                input = input.Trim();
                var sign = 1.0;
                if (input.StartsWith("-")) { sign = -1; input = input.Substring(1); }
                else if (input.StartsWith("+")) { input = input.Substring(1); }
                
                input = input.ToLowerInvariant()
                    .Replace("°", " ").Replace("'", " ").Replace("\"", " ")
                    .Replace("d", " ").Replace("m", " ").Replace("s", " ")
                    .Replace(":", " ");
                
                var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && double.TryParse(parts[0], out var d))
                {
                    decDegrees = d;
                    if (parts.Length >= 2 && double.TryParse(parts[1], out var m))
                        decDegrees += m / 60.0;
                    if (parts.Length >= 3 && double.TryParse(parts[2], out var s))
                        decDegrees += s / 3600.0;
                    
                    decDegrees *= sign;
                    return decDegrees >= -90 && decDegrees <= 90;
                }
            }
            catch { }
            return false;
        }
        
        // Editable RA in HMS format (J2000)
        public string EditableRA
        {
            get => _selectedTarget != null ? FormatRA(_selectedTarget.RightAscension) : string.Empty;
            set
            {
                if (_selectedTarget != null && TryParseRA(value, out double raHours))
                {
                    _selectedTarget.RightAscension = raHours;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedTargetRA));
                }
            }
        }

        // Separate RA fields for h/m/s input
        public int EditableRAHours
        {
            get => _selectedTarget != null ? (int)Math.Floor(_selectedTarget.RightAscension) : 0;
            set
            {
                if (_selectedTarget != null)
                {
                    var minutes = (_selectedTarget.RightAscension - Math.Floor(_selectedTarget.RightAscension)) * 60;
                    _selectedTarget.RightAscension = Math.Clamp(value, 0, 23) + minutes / 60.0;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(EditableRA));
                    RaisePropertyChanged(nameof(SelectedTargetRA));
                }
            }
        }

        public int EditableRAMinutes
        {
            get
            {
                if (_selectedTarget == null) return 0;
                var totalMinutes = (_selectedTarget.RightAscension - Math.Floor(_selectedTarget.RightAscension)) * 60;
                return (int)Math.Floor(totalMinutes);
            }
            set
            {
                if (_selectedTarget != null)
                {
                    var hours = Math.Floor(_selectedTarget.RightAscension);
                    var totalMinutes = (_selectedTarget.RightAscension - hours) * 60;
                    var seconds = (totalMinutes - Math.Floor(totalMinutes)) * 60;
                    _selectedTarget.RightAscension = hours + Math.Clamp(value, 0, 59) / 60.0 + seconds / 3600.0;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(EditableRA));
                    RaisePropertyChanged(nameof(SelectedTargetRA));
                }
            }
        }

        public double EditableRASeconds
        {
            get
            {
                if (_selectedTarget == null) return 0;
                var totalMinutes = (_selectedTarget.RightAscension - Math.Floor(_selectedTarget.RightAscension)) * 60;
                var seconds = (totalMinutes - Math.Floor(totalMinutes)) * 60;
                return Math.Round(seconds, 2);
            }
            set
            {
                if (_selectedTarget != null)
                {
                    var hours = Math.Floor(_selectedTarget.RightAscension);
                    var totalMinutes = (_selectedTarget.RightAscension - hours) * 60;
                    var minutes = Math.Floor(totalMinutes);
                    _selectedTarget.RightAscension = hours + minutes / 60.0 + Math.Clamp(value, 0, 59.99) / 3600.0;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(EditableRA));
                    RaisePropertyChanged(nameof(SelectedTargetRA));
                }
            }
        }

        // Editable Dec in DMS format (J2000)
        public string EditableDec
        {
            get => _selectedTarget != null ? FormatDec(_selectedTarget.Declination) : string.Empty;
            set
            {
                if (_selectedTarget != null && TryParseDec(value, out double decDegrees))
                {
                    _selectedTarget.Declination = decDegrees;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(SelectedTargetDec));
                }
            }
        }

        // Separate DEC fields for d/m/s input
        public string EditableDecDegrees
        {
            get
            {
                if (_selectedTarget == null) return "0";
                var sign = _selectedTarget.Declination >= 0 ? "+" : "";
                return sign + ((int)Math.Truncate(_selectedTarget.Declination)).ToString();
            }
            set
            {
                if (_selectedTarget != null && int.TryParse(value, out int degrees))
                {
                    var absOld = Math.Abs(_selectedTarget.Declination);
                    var oldMinutes = (absOld - Math.Floor(absOld)) * 60;
                    var sign = degrees >= 0 ? 1 : -1;
                    _selectedTarget.Declination = sign * (Math.Abs(Math.Clamp(degrees, -90, 90)) + oldMinutes / 60.0);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(EditableDec));
                    RaisePropertyChanged(nameof(SelectedTargetDec));
                }
            }
        }

        public int EditableDecMinutes
        {
            get
            {
                if (_selectedTarget == null) return 0;
                var absDec = Math.Abs(_selectedTarget.Declination);
                var totalMinutes = (absDec - Math.Floor(absDec)) * 60;
                return (int)Math.Floor(totalMinutes);
            }
            set
            {
                if (_selectedTarget != null)
                {
                    var sign = _selectedTarget.Declination >= 0 ? 1 : -1;
                    var absDec = Math.Abs(_selectedTarget.Declination);
                    var degrees = Math.Floor(absDec);
                    var totalMinutes = (absDec - degrees) * 60;
                    var seconds = (totalMinutes - Math.Floor(totalMinutes)) * 60;
                    _selectedTarget.Declination = sign * (degrees + Math.Clamp(value, 0, 59) / 60.0 + seconds / 3600.0);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(EditableDec));
                    RaisePropertyChanged(nameof(SelectedTargetDec));
                }
            }
        }

        public double EditableDecSeconds
        {
            get
            {
                if (_selectedTarget == null) return 0;
                var absDec = Math.Abs(_selectedTarget.Declination);
                var totalMinutes = (absDec - Math.Floor(absDec)) * 60;
                var seconds = (totalMinutes - Math.Floor(totalMinutes)) * 60;
                return Math.Round(seconds, 2);
            }
            set
            {
                if (_selectedTarget != null)
                {
                    var sign = _selectedTarget.Declination >= 0 ? 1 : -1;
                    var absDec = Math.Abs(_selectedTarget.Declination);
                    var degrees = Math.Floor(absDec);
                    var totalMinutes = (absDec - degrees) * 60;
                    var minutes = Math.Floor(totalMinutes);
                    _selectedTarget.Declination = sign * (degrees + minutes / 60.0 + Math.Clamp(value, 0, 59.99) / 3600.0);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(EditableDec));
                    RaisePropertyChanged(nameof(SelectedTargetDec));
                }
            }
        }

        #endregion

        #region Static Enum Arrays
        
        // Filter values for exposure template dropdown
        public static Shared.Model.DTO.Settings.ECameraFilter[] FilterValues => 
            Enum.GetValues(typeof(Shared.Model.DTO.Settings.ECameraFilter)).Cast<Shared.Model.DTO.Settings.ECameraFilter>().ToArray();
        
        // Twilight values for exposure template dropdown
        public static Shared.Model.Enums.ETwilightType[] TwilightValues => 
            Enum.GetValues(typeof(Shared.Model.Enums.ETwilightType)).Cast<Shared.Model.Enums.ETwilightType>().ToArray();
        
        // No Target Behavior display values for scheduler configuration
        public static KeyValuePair<Shared.Model.Enums.NoTargetBehavior, string>[] NoTargetBehaviorValues =>
        new KeyValuePair<Shared.Model.Enums.NoTargetBehavior, string>[]
        {
            new KeyValuePair<Shared.Model.Enums.NoTargetBehavior, string>(Shared.Model.Enums.NoTargetBehavior.StopSequence, "Stop Sequence"),
            new KeyValuePair<Shared.Model.Enums.NoTargetBehavior, string>(Shared.Model.Enums.NoTargetBehavior.WaitAndRetry, "Wait + Retry"),
            new KeyValuePair<Shared.Model.Enums.NoTargetBehavior, string>(Shared.Model.Enums.NoTargetBehavior.ShootCompletedTargets, "Shoot Completed Targets")
        };

        // Error Behavior display values for scheduler configuration
        public static KeyValuePair<Shared.Model.Enums.ErrorBehavior, string>[] ErrorBehaviorValues =>
        new KeyValuePair<Shared.Model.Enums.ErrorBehavior, string>[]
        {
            new KeyValuePair<Shared.Model.Enums.ErrorBehavior, string>(Shared.Model.Enums.ErrorBehavior.StopSequence, "Stop Sequence"),
            new KeyValuePair<Shared.Model.Enums.ErrorBehavior, string>(Shared.Model.Enums.ErrorBehavior.WaitAndRetry, "Wait + Retry"),
            new KeyValuePair<Shared.Model.Enums.ErrorBehavior, string>(Shared.Model.Enums.ErrorBehavior.SkipTarget, "Skip Target"),
            new KeyValuePair<Shared.Model.Enums.ErrorBehavior, string>(Shared.Model.Enums.ErrorBehavior.SkipTargetTemporarily, "Skip Target (Temporarily)")
        };
        
        #endregion

        #region NINA Filter Wheel Integration
        
        public bool IsFilterWheelConnected => _filterWheelMediator?.GetInfo()?.Connected ?? false;
        
        public string[] NinaFilters
        {
            get
            {
                try
                {
                    var info = _filterWheelMediator?.GetInfo();
                    if (info?.Connected == true && info.SelectedFilter != null)
                    {
                        return Array.Empty<string>();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"AstroManager: Could not get NINA filter info: {ex.Message}");
                }
                return Array.Empty<string>();
            }
        }
        
        public bool HasFilterMismatch
        {
            get
            {
                if (!IsFilterWheelConnected || ExposureTemplates == null || !ExposureTemplates.Any())
                    return false;
                
                var ninaFilters = NinaFilters;
                if (ninaFilters.Length == 0)
                    return false;
                
                var ninaFilterNames = ninaFilters.Select(f => f.ToUpperInvariant()).ToHashSet();
                return ExposureTemplates.Any(t => !ninaFilterNames.Contains(t.Filter.ToString().ToUpperInvariant()));
            }
        }
        
        public string FilterMismatchWarning
        {
            get
            {
                if (!HasFilterMismatch)
                    return string.Empty;
                
                var ninaFilters = NinaFilters;
                var ninaFilterNames = ninaFilters.Select(f => f.ToUpperInvariant()).ToHashSet();
                var missingFilters = ExposureTemplates
                    .Where(t => !ninaFilterNames.Contains(t.Filter.ToString().ToUpperInvariant()))
                    .Select(t => t.Filter.ToString())
                    .Distinct()
                    .ToList();
                
                if (missingFilters.Any())
                {
                    return $"⚠️ Filters not in NINA: {string.Join(", ", missingFilters)}";
                }
                return string.Empty;
            }
        }
        
        #endregion

        #region Helper Methods
        
        private static double? SanitizeDouble(double? value)
        {
            if (!value.HasValue) return null;
            if (double.IsInfinity(value.Value) || double.IsNaN(value.Value)) return null;
            return value.Value;
        }
        
        private bool? GetRotatorReverse()
        {
            try
            {
                var rotatorInfo = _rotatorMediator?.GetInfo();
                if (rotatorInfo == null) return null;
                var prop = rotatorInfo.GetType().GetProperty("Reverse");
                return prop?.GetValue(rotatorInfo) as bool?;
            }
            catch { return null; }
        }
        
        private bool? GetRotatorCanReverse()
        {
            try
            {
                var rotatorInfo = _rotatorMediator?.GetInfo();
                if (rotatorInfo == null) return null;
                var prop = rotatorInfo.GetType().GetProperty("CanReverse");
                return prop?.GetValue(rotatorInfo) as bool?;
            }
            catch { return null; }
        }
        
        private double? GetExposureDuration(object cameraInfo)
        {
            try
            {
                var prop = cameraInfo.GetType().GetProperty("ExposureTime") 
                    ?? cameraInfo.GetType().GetProperty("LastExposureDuration");
                var value = prop?.GetValue(cameraInfo);
                if (value is double d) return d;
                if (value is TimeSpan ts) return ts.TotalSeconds;
                return null;
            }
            catch { return null; }
        }
        
        private double? GetExposureElapsed(object cameraInfo)
        {
            try
            {
                var prop = cameraInfo.GetType().GetProperty("ExposureEndTime");
                if (prop != null)
                {
                    var endTime = prop.GetValue(cameraInfo);
                    if (endTime is DateTime dt && dt > DateTime.MinValue)
                    {
                        var remaining = (dt - DateTime.Now).TotalSeconds;
                        var durationProp = cameraInfo.GetType().GetProperty("ExposureTime");
                        if (durationProp?.GetValue(cameraInfo) is double duration)
                        {
                            return duration - remaining;
                        }
                    }
                }
                return null;
            }
            catch { return null; }
        }
        
        #endregion

        #region Command Implementations

        private void StartHeartbeat()
        {
            UpdateEquipmentStatus();
            _heartbeatService.Start();
            _isNinaReady = true;
        }

        private void StopHeartbeat()
        {
            _heartbeatService.Stop();
        }
        
        public override Task Teardown()
        {
            if (_isShuttingDown) return Task.CompletedTask;
            Logger.Info("AstroManager: Teardown called by NINA, sending offline notification...");
            _isShuttingDown = true;
            StopFocuserPolling();
            StopRotatorPolling();
            _afLogWatcherService.Stop();
            _imageSaveMediator.ImageSaved -= _imageCaptureService.OnImageSavedAsync;
            _heartbeatService.MarkShuttingDown();
            _heartbeatService.Stop();
            return Task.CompletedTask;
        }
        
        private void OnApplicationExit(object sender, System.Windows.ExitEventArgs e)
        {
            if (_isShuttingDown) return;
            Logger.Info("AstroManager: Application.Exit detected, sending offline notification...");
            _isShuttingDown = true;
            _heartbeatService.MarkShuttingDown();
            _heartbeatService.Stop();
        }
        
        private void OnProcessExit(object? sender, EventArgs e)
        {
            if (_isShuttingDown) return;
            Logger.Info("AstroManager: Process exit detected, sending offline notification...");
            _isShuttingDown = true;
            _heartbeatService.MarkShuttingDown();
            _heartbeatService.Stop();
        }
        
        private void OnDispatcherShutdown(object? sender, EventArgs e)
        {
            if (_isShuttingDown) return;
            Logger.Info("AstroManager: Dispatcher shutdown detected, sending offline notification...");
            _isShuttingDown = true;
            _heartbeatService.MarkShuttingDown();
            _heartbeatService.Stop();
        }
        
        private void UpdateEquipmentStatus()
        {
            try
            {
                var cameraInfo = _cameraMediator.GetInfo();
                var telescopeInfo = _telescopeMediator.GetInfo();
                var focuserInfo = _focuserMediator.GetInfo();
                var filterWheelInfo = _filterWheelMediator.GetInfo();
                var guiderInfo = _guiderMediator.GetInfo();
                var rotatorInfo = _rotatorMediator?.GetInfo();
                var domeInfo = _domeMediator?.GetInfo();
                var weatherInfo = _weatherDataMediator?.GetInfo();
                var flatPanelInfo = _flatDeviceMediator?.GetInfo();
                var safetyMonitorInfo = _safetyMonitorMediator?.GetInfo();
                
                Logger.Debug($"AstroManager: Equipment check - Camera={cameraInfo?.Connected}, Mount={telescopeInfo?.Connected}, Focuser={focuserInfo?.Connected}, FilterWheel={filterWheelInfo?.Connected}, Guider={guiderInfo?.Connected}, FlatPanel={flatPanelInfo?.Connected}, SafetyMonitor={safetyMonitorInfo?.Connected}");
                
                var timeSinceLastWeatherUpdate = (DateTime.Now - _lastWeatherUpdate).TotalSeconds;
                if (weatherInfo?.Connected == true && timeSinceLastWeatherUpdate >= WeatherUpdateIntervalSeconds)
                {
                    _lastWeatherUpdate = DateTime.Now;
                    
                    double? SafeValue(double val) => double.IsNaN(val) || double.IsInfinity(val) ? null : val;
                    
                    var temp = SafeValue(weatherInfo.Temperature);
                    var humidity = SafeValue(weatherInfo.Humidity);
                    var dewPoint = SafeValue(weatherInfo.DewPoint);
                    var pressure = SafeValue(weatherInfo.Pressure);
                    var cloudCover = SafeValue(weatherInfo.CloudCover);
                    var rainRate = SafeValue(weatherInfo.RainRate);
                    var windSpeed = SafeValue(weatherInfo.WindSpeed);
                    var windDirection = SafeValue(weatherInfo.WindDirection);
                    var windGust = SafeValue(weatherInfo.WindGust);
                    var skyQuality = SafeValue(weatherInfo.SkyQuality);
                    var skyTemperature = SafeValue(weatherInfo.SkyTemperature);
                    var starFWHM = SafeValue(weatherInfo.StarFWHM);
                    
                    Logger.Info($"AstroManager: Weather update - Temp={temp}, Humidity={humidity}, DewPoint={dewPoint}, CloudCover={cloudCover}, SkyQuality={skyQuality}");
                    
                    _heartbeatService.SetWeatherData(
                        temperature: temp,
                        humidity: humidity,
                        dewPoint: dewPoint,
                        pressure: pressure,
                        cloudCover: cloudCover,
                        rainRate: rainRate,
                        windSpeed: windSpeed,
                        windDirection: windDirection,
                        windGust: windGust,
                        skyQuality: skyQuality,
                        skyTemperature: skyTemperature,
                        starFWHM: starFWHM
                    );
                }
                else if (weatherInfo?.Connected != true)
                {
                    _heartbeatService.SetWeatherData();
                }
                
                _heartbeatService.SetEquipmentStatus(
                    cameraConnected: cameraInfo?.Connected ?? false,
                    telescopeConnected: telescopeInfo?.Connected ?? false,
                    focuserConnected: focuserInfo?.Connected ?? false,
                    filterWheelConnected: filterWheelInfo?.Connected ?? false,
                    guiderConnected: guiderInfo?.Connected ?? false,
                    rotatorConnected: rotatorInfo?.Connected,
                    domeConnected: domeInfo?.Connected,
                    weatherConnected: weatherInfo?.Connected,
                    flatPanelConnected: flatPanelInfo?.Connected,
                    safetyMonitorConnected: safetyMonitorInfo?.Connected,
                    isSafe: safetyMonitorInfo?.Connected == true ? (bool?)safetyMonitorInfo.IsSafe : null
                );
                
                string? currentTrackingRate = null;
                if (telescopeInfo?.Connected == true && telescopeInfo.TrackingEnabled)
                {
                    var trackingRate = telescopeInfo.TrackingRate;
                    var trackingModes = telescopeInfo.TrackingModes;
                    
                    if (trackingRate != null)
                    {
                        var trackingModeField = trackingRate.GetType().GetField("TrackingMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (trackingModeField != null)
                        {
                            var modeValue = trackingModeField.GetValue(trackingRate);
                            if (modeValue != null)
                            {
                                currentTrackingRate = modeValue.ToString();
                            }
                        }
                    }
                }
                
                double? mountRaJ2000 = null;
                double? mountDecJ2000 = null;
                if (telescopeInfo?.Connected == true && telescopeInfo.Coordinates != null)
                {
                    var coords = telescopeInfo.Coordinates;
                    if (coords.Epoch == Epoch.JNOW)
                    {
                        coords = coords.Transform(Epoch.J2000);
                    }
                    mountRaJ2000 = coords.RA;
                    mountDecJ2000 = coords.Dec;
                }
                
                try
                {
                    var isRunningFunc = _sequenceMediator.IsAdvancedSequenceRunning;
                    var isRunning = isRunningFunc?.Invoke() ?? false;
                    
                    string? sequenceName = null;
                    try
                    {
                        // Must run on WPF dispatcher to access UI-related objects
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null && !dispatcher.CheckAccess())
                        {
                            dispatcher.Invoke(() =>
                            {
                                sequenceName = GetSequenceNameViaReflection();
                            });
                        }
                        else
                        {
                            sequenceName = GetSequenceNameViaReflection();
                        }
                    }
                    catch (Exception refEx) 
                    { 
                        Logger.Debug($"AstroManager: Reflection error getting sequence name: {refEx.Message}");
                    }
                    
                    // Pass: isRunning, running sequence name (only when running), loaded sequence name (always)
                    var runningName = isRunning ? sequenceName : null;
                    _heartbeatService.SetSequenceStatus(isRunning, runningName, sequenceName);

                    // Hard-clear stale operation state when sequence transitions from running -> stopped.
                    if (_lastObservedSequenceRunning == true && !isRunning)
                    {
                        Logger.Info("AstroManager: Sequence stop detected - forcing heartbeat state clear");
                        _heartbeatService.ClearCurrentState();
                        _ = _heartbeatService.ForceStatusUpdateAsync();
                    }

                    _lastObservedSequenceRunning = isRunning;
                }
                catch (Exception seqEx)
                {
                    Logger.Debug($"AstroManager: Failed to get sequence status: {seqEx.Message}");
                }
                
                _heartbeatService.SetDetailedEquipmentStatus(
                    mountRa: mountRaJ2000,
                    mountDec: mountDecJ2000,
                    mountAlt: telescopeInfo?.Connected == true ? telescopeInfo.Altitude : null,
                    mountAz: telescopeInfo?.Connected == true ? telescopeInfo.Azimuth : null,
                    sideOfPier: telescopeInfo?.Connected == true ? telescopeInfo.SideOfPier.ToString() : null,
                    trackingRate: currentTrackingRate,
                    isTracking: telescopeInfo?.Connected == true ? (bool?)telescopeInfo.TrackingEnabled : null,
                    isParked: telescopeInfo?.Connected == true ? (bool?)telescopeInfo.AtPark : null,
                    isSlewing: telescopeInfo?.Connected == true ? (bool?)telescopeInfo.Slewing : null,
                    focuserPosition: focuserInfo?.Connected == true ? focuserInfo.Position : null,
                    focuserTemp: focuserInfo?.Connected == true ? focuserInfo.Temperature : null,
                    isFocuserMoving: focuserInfo?.Connected == true ? (bool?)focuserInfo.IsMoving : null,
                    selectedFilter: filterWheelInfo?.Connected == true ? filterWheelInfo.SelectedFilter?.Name : null,
                    filterWheelFilters: filterWheelInfo?.Connected == true 
                        ? _profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters?.Select(f => f.Name).ToList() 
                        : null,
                    rotatorAngle: rotatorInfo?.Connected == true ? rotatorInfo.Position : null,
                    rotatorReverse: rotatorInfo?.Connected == true ? GetRotatorReverse() : null,
                    rotatorCanReverse: rotatorInfo?.Connected == true ? GetRotatorCanReverse() : null,
                    flatPanelLightOn: flatPanelInfo?.Connected == true ? (bool?)flatPanelInfo.LightOn : null,
                    flatPanelBrightness: flatPanelInfo?.Connected == true ? flatPanelInfo.Brightness : null,
                    flatPanelCoverState: flatPanelInfo?.Connected == true ? flatPanelInfo.CoverState.ToString() : null,
                    flatPanelSupportsOpenClose: flatPanelInfo?.Connected == true ? (bool?)flatPanelInfo.SupportsOpenClose : null,
                    guidingRaRms: guiderInfo?.Connected == true ? guiderInfo.RMSError?.RA?.Arcseconds : null,
                    guidingDecRms: guiderInfo?.Connected == true ? guiderInfo.RMSError?.Dec?.Arcseconds : null,
                    isGuiding: guiderInfo?.Connected == true ? (bool?)(guiderInfo.RMSError?.Total?.Arcseconds > 0) : null,
                    isCalibrating: _isCalibrating,
                    cameraTemp: cameraInfo?.Connected == true ? cameraInfo.Temperature : null,
                    cameraTargetTemp: cameraInfo?.Connected == true ? cameraInfo.TemperatureSetPoint : null,
                    coolerPower: cameraInfo?.Connected == true ? cameraInfo.CoolerPower : null,
                    isCoolerOn: cameraInfo?.Connected == true ? (bool?)cameraInfo.CoolerOn : null,
                    binning: cameraInfo?.Connected == true ? cameraInfo.BinX : null,
                    isExposing: cameraInfo?.Connected == true ? (bool?)cameraInfo.IsExposing : null,
                    exposureDuration: cameraInfo?.Connected == true && cameraInfo.IsExposing ? GetExposureDuration(cameraInfo) : null,
                    exposureElapsed: cameraInfo?.Connected == true && cameraInfo.IsExposing ? GetExposureElapsed(cameraInfo) : null
                );
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Failed to update equipment status: {ex.Message}");
            }
        }

        private async Task TestConnectionAsync()
        {
            ConnectionStatus = "Testing connection...";
            ConnectionStatusColor = WpfBrushes.Gray;

            var (success, message, config) = await _apiClient.TestConnectionAsync();
            
            ConnectionStatus = message;
            ConnectionStatusColor = success ? WpfBrushes.Green : WpfBrushes.Red;

            if (success && config != null)
            {
                ClearError();
                SharedSchedulerLog.Instance.Initialize(_apiClient);
                await SyncMeridianFlipSettingsAsync();
                RaisePropertyChanged(nameof(ObservatoryName));
                RaisePropertyChanged(nameof(EquipmentName));
            }
            else
            {
                SetError(message);
            }
        }

        private async Task SyncMeridianFlipSettingsAsync()
        {
            try
            {
                var mfSettings = _profileService.ActiveProfile?.MeridianFlipSettings;
                if (mfSettings == null)
                {
                    Logger.Debug("AstroManager: No meridian flip settings in NINA profile");
                    return;
                }

                var isEnabled = mfSettings.MinutesAfterMeridian > 0 || mfSettings.PauseTimeBeforeMeridian > 0;
                
                await _apiClient.UpdateMeridianFlipSettingsAsync(
                    enabled: isEnabled,
                    minutesAfterMeridian: mfSettings.MinutesAfterMeridian,
                    pauseTimeBeforeFlip: mfSettings.PauseTimeBeforeMeridian,
                    maxMinutesToMeridian: mfSettings.PauseTimeBeforeMeridian
                );
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Failed to sync meridian flip settings: {ex.Message}");
            }
        }

        private async Task SyncTargetsAsync()
        {
            if (string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "Please enter a license key first";
                ConnectionStatusColor = WpfBrushes.Orange;
                SetError("No license key");
                return;
            }

            ConnectionStatus = "Syncing targets...";
            ConnectionStatusColor = WpfBrushes.Gray;

            var (success, message, targets) = await _apiClient.SyncScheduledTargetsAsync();
            
            ConnectionStatus = message;
            ConnectionStatusColor = success ? WpfBrushes.Green : WpfBrushes.Red;
            
            if (success)
            {
                ClearError();
            }
            else
            {
                SetError(message);
            }

            if (success && targets != null)
            {
                _targetStore.UpdateTargets(targets);
                RefreshTargetsList();
                
                RaisePropertyChanged(nameof(CachedTargetCount));
                RaisePropertyChanged(nameof(LastSyncTime));
                RaisePropertyChanged(nameof(ObservatoryName));
                RaisePropertyChanged(nameof(EquipmentName));

                if (_settings.AutoExportAfterSync)
                {
                    ExportTargets();
                }

                Logger.Info($"[SYNC] Synced {targets.Count} targets from API:");
                foreach (var t in targets)
                {
                    var goalCount = t.ImagingGoals?.Count ?? 0;
                    var totalGoalExp = t.ImagingGoals?.Sum(g => g.GoalExposureCount) ?? 0;
                    var completedExp = t.ImagingGoals?.Sum(g => g.CompletedExposures) ?? 0;
                    Logger.Info($"[SYNC]   - {t.Name} (Id={t.Id}): Status={t.Status}, Priority={t.Priority}, Goals={goalCount}, Exposures={completedExp}/{totalGoalExp}");
                    if (t.ImagingGoals != null)
                    {
                        foreach (var g in t.ImagingGoals)
                        {
                            Logger.Info($"[SYNC]       Goal {g.Id}: {g.Filter} {g.ExposureTimeSeconds}s - {g.CompletedExposures}/{g.GoalExposureCount} (Enabled={g.IsEnabled})");
                        }
                    }
                }
                
                await RefreshQueueAsync();
                
                try
                {
                    LicensedObservatory = await _apiClient.GetLicenseObservatoryAsync();
                    if (LicensedObservatory != null)
                    {
                        Logger.Info($"AstroManager: Loaded licensed observatory '{LicensedObservatory.Name}' at ({LicensedObservatory.Latitude:F4}, {LicensedObservatory.Longitude:F4})");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"AstroManager: Failed to load licensed observatory: {ex.Message}");
                }
            }
        }

        private void RefreshTargetsList()
        {
            _isRefreshingTargetGrid = true;
            var cachedTargets = _targetStore.GetAllTargets();
            
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                Targets.Clear();
                foreach (var target in cachedTargets.OrderBy(t => t.Name))
                {
                    Targets.Add(target);
                }
            });
            
            _isRefreshingTargetGrid = false;
            RaisePropertyChanged(nameof(Targets));
            RaisePropertyChanged(nameof(FilteredTargets));
            RaisePropertyChanged(nameof(CachedTargetCount));
            RefreshCollapsibleTargetGroups();
        }

        private async Task LoadSettingsFromApiAsync()
        {
            if (string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "Please enter a license key first";
                ConnectionStatusColor = WpfBrushes.Orange;
                return;
            }

            ConnectionStatus = "Loading settings from API...";
            ConnectionStatusColor = WpfBrushes.Gray;

            try
            {
                var templates = await _apiClient.GetExposureTemplatesAsync();
                if (templates != null)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        ExposureTemplates.Clear();
                        foreach (var t in templates.Where(x => x.IsActive).OrderBy(x => x.Name))
                        {
                            ExposureTemplates.Add(t);
                        }
                    });
                    RaisePropertyChanged(nameof(ExposureTemplateCount));
                }

                var configs = await _apiClient.GetSchedulerConfigurationsAsync();
                if (configs != null)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        SchedulerConfigurations.Clear();
                        foreach (var c in configs.OrderBy(x => x.Name))
                        {
                            SchedulerConfigurations.Add(c);
                        }
                    });
                    RaisePropertyChanged(nameof(SchedulerConfigCount));
                    var defaultConfig = configs.FirstOrDefault(c => c.IsDefault) ?? configs.FirstOrDefault();
                    SchedulerConfig = defaultConfig;
                    if (defaultConfig != null && !SelectedPreviewConfigId.HasValue)
                    {
                        SelectedPreviewConfigId = defaultConfig.Id;
                    }
                }
                
                var profiles = await _apiClient.GetMoonAvoidanceProfilesAsync();
                if (profiles != null)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        MoonAvoidanceProfiles.Clear();
                        foreach (var p in profiles.OrderBy(x => x.Name))
                        {
                            MoonAvoidanceProfiles.Add(p);
                        }
                    });
                    RaisePropertyChanged(nameof(MoonAvoidanceProfileCount));
                }
                
                var targetTemplates = await _apiClient.GetSchedulerTargetTemplatesAsync();
                if (targetTemplates != null)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        SchedulerTargetTemplates.Clear();
                        foreach (var t in targetTemplates.OrderBy(x => x.Name))
                        {
                            SchedulerTargetTemplates.Add(t);
                        }
                    });
                    RaisePropertyChanged(nameof(SchedulerTargetTemplateCount));
                    RaisePropertyChanged(nameof(SelectedTargetSchedulerTemplate));
                    RaisePropertyChanged(nameof(HasSelectedTargetSchedulerTemplate));
                }
                
                var schedulerMode = await _apiClient.GetSchedulerModeAsync();
                _currentSchedulerMode = schedulerMode;
                _heartbeatService.SetSchedulerMode(schedulerMode);
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    RaisePropertyChanged(nameof(IsManualMode));
                    RaisePropertyChanged(nameof(SchedulerModeDisplay));
                    RaisePropertyChanged(nameof(SchedulerModeDescription));
                });

                _dataStore.UpdateAll(
                    observatory: null,
                    exposureTemplates: templates,
                    schedulerConfigs: configs,
                    moonAvoidanceProfiles: profiles,
                    schedulerTemplates: targetTemplates);

                ConnectionStatus = $"Loaded {ExposureTemplateCount} exp, {SchedulerConfigCount} cfg, {SchedulerTargetTemplateCount} tpl, {MoonAvoidanceProfileCount} moon";
                ConnectionStatusColor = WpfBrushes.Green;
            }
            catch (Exception ex)
            {
                ConnectionStatus = "Failed to load settings - using cached data";
                ConnectionStatusColor = WpfBrushes.Orange;
                Logger.Error($"AstroManager: Failed to load settings from API: {ex.Message}");
                LoadSettingsFromCache();
            }
        }
        
        private void LoadSettingsFromCache()
        {
            if (!_dataStore.HasCachedData)
            {
                Logger.Warning("AstroManager: No cached data available for offline operation");
                return;
            }
            
            Logger.Info("AstroManager: Loading settings from cache for offline operation");
            
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                ExposureTemplates.Clear();
                foreach (var t in _dataStore.ExposureTemplates.Where(x => x.IsActive).OrderBy(x => x.Name))
                {
                    ExposureTemplates.Add(t);
                }
                RaisePropertyChanged(nameof(ExposureTemplateCount));
                
                SchedulerConfigurations.Clear();
                foreach (var c in _dataStore.SchedulerConfigurations.OrderBy(x => x.Name))
                {
                    SchedulerConfigurations.Add(c);
                }
                RaisePropertyChanged(nameof(SchedulerConfigCount));
                
                var defaultConfig = _dataStore.GetDefaultSchedulerConfiguration();
                if (defaultConfig != null)
                {
                    SchedulerConfig = defaultConfig;
                    if (!SelectedPreviewConfigId.HasValue)
                    {
                        SelectedPreviewConfigId = defaultConfig.Id;
                    }
                }
                
                MoonAvoidanceProfiles.Clear();
                foreach (var p in _dataStore.MoonAvoidanceProfiles.OrderBy(x => x.Name))
                {
                    MoonAvoidanceProfiles.Add(p);
                }
                RaisePropertyChanged(nameof(MoonAvoidanceProfileCount));
                
                SchedulerTargetTemplates.Clear();
                foreach (var t in _dataStore.SchedulerTemplates.OrderBy(x => x.Name))
                {
                    SchedulerTargetTemplates.Add(t);
                }
                RaisePropertyChanged(nameof(SchedulerTargetTemplateCount));
            });
            
            ConnectionStatus = $"Offline: {ExposureTemplateCount} exp, {SchedulerConfigCount} cfg (cached {_dataStore.LastSyncUtc?.ToLocalTime().ToString("g") ?? "unknown"})";
            ConnectionStatusColor = WpfBrushes.Orange;
        }

        private async Task SaveSelectedTargetAsync()
        {
            if (_selectedTarget == null) return;
            
            if (string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "License key required to save targets";
                ConnectionStatusColor = WpfBrushes.Red;
                Logger.Warning("AstroManager: Cannot save target - no license key configured");
                return;
            }

            _targetStore.UpdateTarget(_selectedTarget);
            
            if (!string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "Saving target...";
                ConnectionStatusColor = WpfBrushes.Gray;
                
                var success = await _apiClient.UpdateTargetAsync(_selectedTarget);
                
                if (success)
                {
                    Logger.Info($"AstroManager: Saved target {_selectedTarget.Name} to server");
                    
                    if (_selectedTarget.IsMosaic)
                    {
                        ConnectionStatus = "Loading panels...";
                        var updatedTarget = await _apiClient.GetTargetByIdAsync(_selectedTarget.Id);
                        if (updatedTarget != null)
                        {
                            _selectedTarget.Panels = updatedTarget.Panels;
                            _targetStore.UpdateTarget(_selectedTarget);
                            
                            RaisePropertyChanged(nameof(SelectedTargetPanels));
                            RaisePropertyChanged(nameof(SelectedTargetHasPanels));
                            RaisePropertyChanged(nameof(SelectedTargetMosaicInfo));
                            Logger.Info($"AstroManager: Loaded {updatedTarget.Panels?.Count ?? 0} panels for mosaic target");
                        }
                    }
                    
                    ConnectionStatus = $"Saved: {_selectedTarget.Name}";
                    ConnectionStatusColor = WpfBrushes.Green;
                }
                else
                {
                    ConnectionStatus = "Server unavailable - saved locally";
                    ConnectionStatusColor = WpfBrushes.Orange;
                    Logger.Warning($"AstroManager: Failed to save target {_selectedTarget.Name} to server - saved locally");
                }
            }
            
            UpdateSelectedTargetBackup();
            RefreshTargetsList();
        }

        
        private void BrowseExportPath()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Export/Import Folder",
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrEmpty(_settings.ExportImportPath) && Directory.Exists(_settings.ExportImportPath))
            {
                dialog.SelectedPath = _settings.ExportImportPath;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ExportImportPath = dialog.SelectedPath;
            }
        }

        private void ExportTargets()
        {
            var path = _targetStore.GetDefaultExportPath();
            
            if (_targetStore.ExportToFile(path))
            {
                ConnectionStatus = $"Exported to {Path.GetFileName(path)}";
                ConnectionStatusColor = WpfBrushes.Green;
            }
            else
            {
                ConnectionStatus = "Export failed";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }

        private void ImportTargets()
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Import Scheduled Targets"
            };

            if (!string.IsNullOrEmpty(_settings.ExportImportPath) && Directory.Exists(_settings.ExportImportPath))
            {
                dialog.InitialDirectory = _settings.ExportImportPath;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                if (_targetStore.ImportFromFile(dialog.FileName))
                {
                    ConnectionStatus = $"Imported from {Path.GetFileName(dialog.FileName)}";
                    ConnectionStatusColor = WpfBrushes.Green;
                    
                    RaisePropertyChanged(nameof(CachedTargetCount));
                    RaisePropertyChanged(nameof(ObservatoryName));
                    RaisePropertyChanged(nameof(EquipmentName));
                }
                else
                {
                    ConnectionStatus = "Import failed";
                    ConnectionStatusColor = WpfBrushes.Red;
                }
            }
        }

        private void ClearCache()
        {
            ConfirmDialogTitle = "Clear Cache";
            ConfirmDialogMessage = "Are you sure you want to clear all cached targets?";
            _confirmDialogAction = () =>
            {
                _targetStore.Clear();
                RefreshTargetsList();
                RaisePropertyChanged(nameof(CachedTargetCount));
                ConnectionStatus = "Cache cleared";
                ConnectionStatusColor = WpfBrushes.Gray;
            };
            ShowConfirmDialog = true;
        }

        private async Task DeleteSelectedTargetAsync()
        {
            if (_selectedTarget == null) return;
            
            var targetToDelete = _selectedTarget;
            ConfirmDialogTitle = "Delete Target";
            ConfirmDialogMessage = $"Are you sure you want to delete '{targetToDelete.Name}'?";
            _confirmDialogAction = async () => await ExecuteDeleteTargetAsync(targetToDelete);
            ShowConfirmDialog = true;
        }
        
        private async Task ExecuteDeleteTargetAsync(ScheduledTargetDto targetToDelete)
        {
            if (string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "License key required to delete targets";
                ConnectionStatusColor = WpfBrushes.Red;
                return;
            }
            
            var targetName = targetToDelete.Name;
            _targetStore.RemoveTarget(targetToDelete.Id);
            await _apiClient.DeleteTargetAsync(targetToDelete.Id);
            
            SelectedTarget = null;
            RefreshTargetsList();
            
            ConnectionStatus = $"Deleted: {targetName}";
            ConnectionStatusColor = WpfBrushes.Green;
        }

        private void ExportSettings()
        {
            var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                Title = "Export Plugin Settings",
                FileName = "astromanager_settings.json"
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    var settings = new
                    {
                        HeartbeatIntervalSeconds = _settings.HeartbeatIntervalSeconds,
                        UseCachedTargetsOnConnectionLoss = _settings.UseCachedTargetsOnConnectionLoss,
                        AutoExportAfterSync = _settings.AutoExportAfterSync,
                        ExportImportPath = _settings.ExportImportPath
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(dialog.FileName, json);
                    
                    ConnectionStatus = "Settings exported";
                    ConnectionStatusColor = WpfBrushes.Green;
                }
                catch (Exception ex)
                {
                    ConnectionStatus = "Export failed";
                    ConnectionStatusColor = WpfBrushes.Red;
                    Logger.Error($"Failed to export settings: {ex.Message}");
                }
            }
        }

        private void ImportSettings()
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                Title = "Import Plugin Settings"
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    var json = File.ReadAllText(dialog.FileName);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("HeartbeatIntervalSeconds", out var hbInterval))
                        _settings.HeartbeatIntervalSeconds = hbInterval.GetInt32();
                    if (root.TryGetProperty("UseCachedTargetsOnConnectionLoss", out var useCached))
                        _settings.UseCachedTargetsOnConnectionLoss = useCached.GetBoolean();
                    if (root.TryGetProperty("AutoExportAfterSync", out var autoExport))
                        _settings.AutoExportAfterSync = autoExport.GetBoolean();
                    if (root.TryGetProperty("ExportImportPath", out var exportPath))
                        _settings.ExportImportPath = exportPath.GetString() ?? _settings.ExportImportPath;
                    
                    RaisePropertyChanged(nameof(HeartbeatIntervalSeconds));
                    RaisePropertyChanged(nameof(UseCachedTargetsOnConnectionLoss));
                    RaisePropertyChanged(nameof(AutoExportAfterSync));
                    RaisePropertyChanged(nameof(ExportImportPath));
                    
                    ConnectionStatus = "Settings imported";
                    ConnectionStatusColor = WpfBrushes.Green;
                }
                catch (Exception ex)
                {
                    ConnectionStatus = "Import failed";
                    ConnectionStatusColor = WpfBrushes.Red;
                    Logger.Error($"Failed to import settings: {ex.Message}");
                }
            }
        }

        private void OpenAstroManager()
        {
            try
            {
                var url = _settings.ApiUrl?.Replace("/api", "").TrimEnd('/') ?? "https://astromanager.app";
                if (!url.StartsWith("http"))
                    url = "https://" + url;
                    
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to open AstroManager: {ex.Message}");
            }
        }

        private void OpenDocumentation()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://docs.astro.sleeman.at/nina-plugin",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to open documentation: {ex.Message}");
            }
        }

        #endregion
    }
}
