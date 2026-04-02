using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Shared.Model.DTO.Common;
using Shared.Model.DTO.Scheduler;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace AstroManager.NinaPlugin.Controls
{
    public partial class AltitudeChartControl : System.Windows.Controls.UserControl
    {
        // Trackball elements
        private Line? _trackballLine;
        private Border? _tooltipBorder;
        private TextBlock? _tooltipText;
        
        // Chart layout cache for mouse tracking
        private double _chartMargin = 25;
        private double _chartWidth;
        private double _chartHeight;
        private DateTime _chartStartTime;
        private DateTime _chartEndTime;
        private double _horizonAltitude = 30.0;
        
        // Cached session data for tooltip lookup
        private List<SchedulerPreviewSessionDto> _cachedSessions = new();
        private List<SchedulerPreviewSkippedTargetDto> _cachedSkippedTargets = new();

        public static readonly DependencyProperty SessionsProperty =
            DependencyProperty.Register(nameof(Sessions), typeof(IEnumerable<SchedulerPreviewSessionDto>), 
                typeof(AltitudeChartControl), new PropertyMetadata(null, OnDataChanged));

        public static readonly DependencyProperty SkippedTargetsProperty =
            DependencyProperty.Register(nameof(SkippedTargets), typeof(IEnumerable<SchedulerPreviewSkippedTargetDto>), 
                typeof(AltitudeChartControl), new PropertyMetadata(null, OnDataChanged));

        public static readonly DependencyProperty AstronomicalDuskProperty =
            DependencyProperty.Register(nameof(AstronomicalDusk), typeof(DateTime?), 
                typeof(AltitudeChartControl), new PropertyMetadata(null, OnDataChanged));

        public static readonly DependencyProperty AstronomicalDawnProperty =
            DependencyProperty.Register(nameof(AstronomicalDawn), typeof(DateTime?), 
                typeof(AltitudeChartControl), new PropertyMetadata(null, OnDataChanged));

        public static readonly DependencyProperty SelectedTargetIdProperty =
            DependencyProperty.Register(nameof(SelectedTargetId), typeof(Guid?), 
                typeof(AltitudeChartControl), new PropertyMetadata(null, OnDataChanged));

        public IEnumerable<SchedulerPreviewSessionDto> Sessions
        {
            get => (IEnumerable<SchedulerPreviewSessionDto>)GetValue(SessionsProperty);
            set => SetValue(SessionsProperty, value);
        }

        public IEnumerable<SchedulerPreviewSkippedTargetDto> SkippedTargets
        {
            get => (IEnumerable<SchedulerPreviewSkippedTargetDto>)GetValue(SkippedTargetsProperty);
            set => SetValue(SkippedTargetsProperty, value);
        }

        public DateTime? AstronomicalDusk
        {
            get => (DateTime?)GetValue(AstronomicalDuskProperty);
            set => SetValue(AstronomicalDuskProperty, value);
        }

        public DateTime? AstronomicalDawn
        {
            get => (DateTime?)GetValue(AstronomicalDawnProperty);
            set => SetValue(AstronomicalDawnProperty, value);
        }

        public Guid? SelectedTargetId
        {
            get => (Guid?)GetValue(SelectedTargetIdProperty);
            set => SetValue(SelectedTargetIdProperty, value);
        }
        
        public static readonly DependencyProperty CustomHorizonProperty =
            DependencyProperty.Register(nameof(CustomHorizon), typeof(List<AltAzCoordDto>), 
                typeof(AltitudeChartControl), new PropertyMetadata(null, OnDataChanged));
                
        public static readonly DependencyProperty MinAltitudeProperty =
            DependencyProperty.Register(nameof(MinAltitude), typeof(double), 
                typeof(AltitudeChartControl), new PropertyMetadata(30.0, OnDataChanged));
        
        public static readonly DependencyProperty ChartStartTimeProperty =
            DependencyProperty.Register(nameof(ChartStartTime), typeof(DateTime?), 
                typeof(AltitudeChartControl), new PropertyMetadata(null, OnDataChanged));
        
        public List<AltAzCoordDto>? CustomHorizon
        {
            get => (List<AltAzCoordDto>?)GetValue(CustomHorizonProperty);
            set => SetValue(CustomHorizonProperty, value);
        }
        
        public double MinAltitude
        {
            get => (double)GetValue(MinAltitudeProperty);
            set => SetValue(MinAltitudeProperty, value);
        }
        
        /// <summary>
        /// Optional start time for the chart. If set and later than AstronomicalDusk, chart starts here.
        /// Use for "Now" or selected start time.
        /// </summary>
        public DateTime? ChartStartTime
        {
            get => (DateTime?)GetValue(ChartStartTimeProperty);
            set => SetValue(ChartStartTimeProperty, value);
        }

        public AltitudeChartControl()
        {
            InitializeComponent();
            
            // Setup mouse events for trackball
            ChartCanvas.MouseMove += ChartCanvas_MouseMove;
            ChartCanvas.MouseLeave += ChartCanvas_MouseLeave;
            ChartCanvas.MouseEnter += ChartCanvas_MouseEnter;
        }

        private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AltitudeChartControl chart)
                chart.DrawChart();
        }

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawChart();
        }
        
        private void ChartCanvas_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Create trackball line if not exists
            if (_trackballLine == null)
            {
                _trackballLine = new Line
                {
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(180, 255, 255, 255)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    IsHitTestVisible = false
                };
            }
            
            // Create tooltip if not exists
            if (_tooltipBorder == null)
            {
                _tooltipText = new TextBlock
                {
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(220, 220, 220)),
                    FontSize = 10,
                    TextWrapping = TextWrapping.NoWrap
                };
                
                _tooltipBorder = new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromArgb(230, 30, 30, 35)),
                    BorderBrush = new SolidColorBrush(WpfColor.FromRgb(80, 80, 90)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = _tooltipText,
                    IsHitTestVisible = false
                };
            }
        }
        
        private void ChartCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Remove trackball and tooltip
            if (_trackballLine != null && ChartCanvas.Children.Contains(_trackballLine))
                ChartCanvas.Children.Remove(_trackballLine);
            if (_tooltipBorder != null && ChartCanvas.Children.Contains(_tooltipBorder))
                ChartCanvas.Children.Remove(_tooltipBorder);
        }
        
        private void ChartCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!AstronomicalDusk.HasValue || !AstronomicalDawn.HasValue || _chartWidth <= 0)
                return;
                
            var pos = e.GetPosition(ChartCanvas);
            double x = pos.X;
            
            // Check if within chart area
            if (x < _chartMargin || x > _chartMargin + _chartWidth)
            {
                ChartCanvas_MouseLeave(sender, e);
                return;
            }
            
            // Calculate time at mouse position
            double totalMinutes = (_chartEndTime - _chartStartTime).TotalMinutes;
            double minutesFromStart = ((x - _chartMargin) / _chartWidth) * totalMinutes;
            DateTime timeAtMouse = _chartStartTime.AddMinutes(minutesFromStart);
            DateTime timeAtMouseLocal = timeAtMouse.ToLocalTime();
            
            // Find data at this time
            var tooltipLines = new List<string>();
            tooltipLines.Add($"⏱ {timeAtMouseLocal:HH:mm}");
            tooltipLines.Add($"───────────");
            
            // Check sessions at this time - show altitude, moon distance, required moon distance, horizon
            bool hasSession = false;
            foreach (var session in _cachedSessions)
            {
                if (timeAtMouse >= session.StartTimeUtc && timeAtMouse <= session.EndTimeUtc)
                {
                    hasSession = true;
                    var panelInfo = session.PanelNumber.HasValue ? $" (P{session.PanelNumber})" : "";
                    tooltipLines.Add($"🎯 {session.TargetName}{panelInfo}");
                    
                    // Find altitude/moon data at this time from session's AltitudeData
                    int altDataCount = session.AltitudeData?.Count ?? 0;
                    if (session.AltitudeData != null && session.AltitudeData.Any())
                    {
                        var closest = session.AltitudeData
                            .OrderBy(d => Math.Abs((d.TimeUtc - timeAtMouse).TotalMinutes))
                            .FirstOrDefault();
                            
                        if (closest != null)
                        {
                            // Show filter from altitude data point (changes based on batch sequence)
                            var currentFilter = !string.IsNullOrEmpty(closest.Filter) ? closest.Filter : session.Filter.ToString();
                            tooltipLines.Add($"   🔬 Filter: {currentFilter}");
                            tooltipLines.Add($"   📐 Altitude: {closest.Altitude:F1}°");
                            
                            // Moon info: distance, illumination, required
                            if (closest.MoonDistance.HasValue && closest.MoonDistance > 0)
                            {
                                var moonIllum = session.MoonIllumination > 0 ? $" ({session.MoonIllumination:F0}%)" : "";
                                tooltipLines.Add($"   🌙 Moon dist: {closest.MoonDistance:F1}°{moonIllum}");
                            }
                            if (session.RequiredMoonDistance.HasValue && session.RequiredMoonDistance > 0)
                                tooltipLines.Add($"   📏 Min required: {session.RequiredMoonDistance:F1}°");
                            
                            // Show horizon at target's azimuth (custom horizon or min altitude)
                            double horizonAtAz = CustomHorizon != null && CustomHorizon.Any() 
                                ? GetHorizonAltitudeAtAzimuth(closest.Azimuth)
                                : MinAltitude;
                            tooltipLines.Add($"   🏔 Horizon: {horizonAtAz:F1}° (az {closest.Azimuth:F0}°)");
                        }
                    }
                    else
                    {
                        // Fallback to average altitude if no per-point data
                        tooltipLines.Add($"   🔬 Filter: {session.Filter}");
                        tooltipLines.Add($"   📐 Altitude: {session.AverageAltitude:F1}° (avg)");
                        var moonIllum = session.MoonIllumination > 0 ? $" ({session.MoonIllumination:F0}%)" : "";
                        tooltipLines.Add($"   🌙 Moon dist: {session.MoonDistance:F1}°{moonIllum}");
                        if (session.RequiredMoonDistance.HasValue && session.RequiredMoonDistance > 0)
                            tooltipLines.Add($"   📏 Min required: {session.RequiredMoonDistance:F1}°");
                        tooltipLines.Add($"   🏔 Horizon: {MinAltitude:F1}°");
                    }
                }
            }
            
            // Only show general horizon if no session at this time
            if (!hasSession)
            {
                tooltipLines.Add($"No target scheduled");
                tooltipLines.Add($"───────────");
                tooltipLines.Add($"🏔 Min Alt: {MinAltitude:F1}°");
            }
            
            // Update trackball line
            if (_trackballLine != null)
            {
                _trackballLine.X1 = x;
                _trackballLine.Y1 = 5;
                _trackballLine.X2 = x;
                _trackballLine.Y2 = 5 + _chartHeight;
                
                if (!ChartCanvas.Children.Contains(_trackballLine))
                    ChartCanvas.Children.Add(_trackballLine);
            }
            
            // Update tooltip
            if (_tooltipBorder != null && _tooltipText != null)
            {
                _tooltipText.Text = string.Join("\n", tooltipLines);
                
                // Position tooltip (avoid going off-screen)
                double tooltipX = x + 10;
                double tooltipY = pos.Y - 20;
                
                // Measure tooltip size
                _tooltipBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                var tooltipSize = _tooltipBorder.DesiredSize;
                
                // Adjust if would go off right edge
                if (tooltipX + tooltipSize.Width > ChartCanvas.ActualWidth - 5)
                    tooltipX = x - tooltipSize.Width - 10;
                    
                // Adjust if would go off bottom
                if (tooltipY + tooltipSize.Height > ChartCanvas.ActualHeight - 5)
                    tooltipY = ChartCanvas.ActualHeight - tooltipSize.Height - 5;
                    
                // Adjust if would go off top
                if (tooltipY < 5)
                    tooltipY = 5;
                
                Canvas.SetLeft(_tooltipBorder, tooltipX);
                Canvas.SetTop(_tooltipBorder, tooltipY);
                
                if (!ChartCanvas.Children.Contains(_tooltipBorder))
                    ChartCanvas.Children.Add(_tooltipBorder);
            }
        }

        private void DrawChart()
        {
            ChartCanvas.Children.Clear();

            var sessions = Sessions?.ToList() ?? new List<SchedulerPreviewSessionDto>();
            var skippedTargets = SkippedTargets?.ToList() ?? new List<SchedulerPreviewSkippedTargetDto>();
            
            // Debug logging to NINA logs
            NINA.Core.Utility.Logger.Debug($"[AltitudeChart] DrawChart: Sessions={sessions.Count}, SkippedTargets={skippedTargets.Count}, " +
                $"Dusk={AstronomicalDusk:HH:mm}, Dawn={AstronomicalDawn:HH:mm}, " +
                $"CanvasSize={ChartCanvas.ActualWidth:F0}x{ChartCanvas.ActualHeight:F0}");
            
            if (skippedTargets.Any())
            {
                var withAltData = skippedTargets.Count(s => s.AltitudeData?.Any() == true);
                NINA.Core.Utility.Logger.Debug($"[AltitudeChart] Skipped targets with altitude data: {withAltData}/{skippedTargets.Count}");
            }
            
            // Need either sessions or skipped targets with altitude data, plus valid twilight times
            bool hasData = sessions.Any() || skippedTargets.Any(s => s.AltitudeData?.Any() == true);
            if (!hasData || ChartCanvas.ActualWidth < 10 || ChartCanvas.ActualHeight < 10)
            {
                NINA.Core.Utility.Logger.Warning($"[AltitudeChart] Early return: hasData={hasData}, width={ChartCanvas.ActualWidth:F0}, height={ChartCanvas.ActualHeight:F0}");
                return;
            }
            
            if (!AstronomicalDusk.HasValue || !AstronomicalDawn.HasValue)
            {
                NINA.Core.Utility.Logger.Warning($"[AltitudeChart] Early return: Missing twilight times (Dusk={AstronomicalDusk}, Dawn={AstronomicalDawn})");
                return;
            }

            double width = ChartCanvas.ActualWidth;
            double height = ChartCanvas.ActualHeight;
            double margin = 25;
            double chartWidth = width - margin * 2;
            double chartHeight = height - margin - 10;

            // Use twilight times for consistent time range, but respect ChartStartTime if set
            DateTime startTime = AstronomicalDusk.Value;
            DateTime endTime = AstronomicalDawn.Value;
            
            // If ChartStartTime is set and later than dusk, use it as the chart start
            if (ChartStartTime.HasValue && ChartStartTime.Value > startTime && ChartStartTime.Value < endTime)
            {
                startTime = ChartStartTime.Value;
            }
            
            double totalMinutes = (endTime - startTime).TotalMinutes;

            if (totalMinutes <= 0) return;
            
            // Cache layout and data for tooltip/trackball
            _chartMargin = margin;
            _chartWidth = chartWidth;
            _chartHeight = chartHeight;
            _chartStartTime = startTime;
            _chartEndTime = endTime;
            _cachedSessions = sessions;
            _cachedSkippedTargets = skippedTargets;

            // Draw background (night sky gradient)
            var nightRect = new System.Windows.Shapes.Rectangle
            {
                Width = chartWidth,
                Height = chartHeight,
                Fill = new LinearGradientBrush(
                    WpfColor.FromRgb(20, 25, 40),
                    WpfColor.FromRgb(10, 12, 20),
                    90)
            };
            Canvas.SetLeft(nightRect, margin);
            Canvas.SetTop(nightRect, 5);
            ChartCanvas.Children.Add(nightRect);

            // Store effective horizon for tooltip
            _horizonAltitude = MinAltitude;
            
            // Draw moon altitude line in background (light grey)
            // Collect moon altitude from all sessions' AltitudeData
            var moonAltPoints = new SortedDictionary<DateTime, double>();
            foreach (var session in sessions)
            {
                if (session.AltitudeData != null)
                {
                    foreach (var dp in session.AltitudeData)
                    {
                        if (dp.MoonAltitude.HasValue && !moonAltPoints.ContainsKey(dp.TimeUtc))
                        {
                            moonAltPoints[dp.TimeUtc] = dp.MoonAltitude.Value;
                        }
                    }
                }
            }
            
            // Draw moon altitude curve if we have data
            if (moonAltPoints.Count > 1)
            {
                var moonPoints = new PointCollection();
                foreach (var kvp in moonAltPoints)
                {
                    if (kvp.Key >= startTime && kvp.Key <= endTime && kvp.Value >= 0)
                    {
                        double x = margin + ((kvp.Key - startTime).TotalMinutes / totalMinutes) * chartWidth;
                        double y = 5 + chartHeight - (kvp.Value / 90.0) * chartHeight;
                        moonPoints.Add(new WpfPoint(x, y));
                    }
                }
                
                if (moonPoints.Count > 1)
                {
                    var moonLine = new Polyline
                    {
                        Points = moonPoints,
                        Stroke = new SolidColorBrush(WpfColor.FromRgb(180, 180, 180)), // Light grey
                        StrokeThickness = 1.5,
                        StrokeLineJoin = PenLineJoin.Round,
                        Opacity = 0.6
                    };
                    ChartCanvas.Children.Add(moonLine);
                }
            }

            // Draw grid lines
            for (int alt = 0; alt <= 90; alt += 30)
            {
                double y = 5 + chartHeight - (alt / 90.0) * chartHeight;
                var gridLine = new Line
                {
                    X1 = margin,
                    Y1 = y,
                    X2 = margin + chartWidth,
                    Y2 = y,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(40, 255, 255, 255)),
                    StrokeThickness = 1
                };
                ChartCanvas.Children.Add(gridLine);

                var label = new TextBlock
                {
                    Text = $"{alt}°",
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(144, 144, 144)),
                    FontSize = 8
                };
                Canvas.SetLeft(label, 2);
                Canvas.SetTop(label, y - 6);
                ChartCanvas.Children.Add(label);
            }

            // Draw time labels (in local time)
            int hourInterval = totalMinutes > 600 ? 2 : 1;
            DateTime labelTime = new DateTime(startTime.Year, startTime.Month, startTime.Day, startTime.Hour, 0, 0);
            if (labelTime < startTime) labelTime = labelTime.AddHours(1);
            
            // Track if we need to show day at start
            bool isFirstLabel = true;
            DateTime? previousLabelDate = null;

            while (labelTime < endTime)
            {
                double x = margin + ((labelTime - startTime).TotalMinutes / totalMinutes) * chartWidth;
                if (x >= margin && x <= margin + chartWidth)
                {
                    // Convert to local time for display
                    DateTime labelTimeLocal = labelTime.ToLocalTime();
                    
                    // Show day label at start of timeline and at midnight (date change)
                    bool showDay = isFirstLabel || (previousLabelDate.HasValue && labelTimeLocal.Date != previousLabelDate.Value.Date);
                    string labelText = showDay 
                        ? labelTimeLocal.ToString("dd.MM HH:mm") 
                        : labelTimeLocal.ToString("HH:mm");
                    
                    var timeLabel = new TextBlock
                    {
                        Text = labelText,
                        Foreground = new SolidColorBrush(showDay ? WpfColor.FromRgb(180, 180, 180) : WpfColor.FromRgb(144, 144, 144)),
                        FontSize = 8,
                        FontWeight = showDay ? FontWeights.SemiBold : FontWeights.Normal
                    };
                    Canvas.SetLeft(timeLabel, x - (showDay ? 20 : 12));
                    Canvas.SetTop(timeLabel, height - 12);
                    ChartCanvas.Children.Add(timeLabel);
                    
                    isFirstLabel = false;
                    previousLabelDate = labelTimeLocal;

                    var timeLine = new Line
                    {
                        X1 = x,
                        Y1 = 5,
                        X2 = x,
                        Y2 = 5 + chartHeight,
                        Stroke = new SolidColorBrush(WpfColor.FromArgb(30, 255, 255, 255)),
                        StrokeThickness = 1
                    };
                    ChartCanvas.Children.Add(timeLine);
                }
                labelTime = labelTime.AddHours(hourInterval);
            }

            // Color palette for different targets
            var colors = new[]
            {
                WpfColor.FromRgb(78, 201, 176),  // Teal
                WpfColor.FromRgb(86, 156, 214),  // Blue
                WpfColor.FromRgb(214, 157, 41),  // Orange
                WpfColor.FromRgb(206, 145, 206), // Purple
                WpfColor.FromRgb(156, 220, 156), // Green
                WpfColor.FromRgb(220, 156, 156), // Red
            };

            // Group sessions by target
            var targetGroups = sessions.GroupBy(s => s.TargetName).ToList();
            int colorIndex = 0;
            var selectedId = SelectedTargetId;

            foreach (var group in targetGroups)
            {
                var color = colors[colorIndex % colors.Length];
                colorIndex++;
                
                // Check if this target is selected
                var isSelected = group.Any(s => s.TargetId == selectedId);
                var opacity = selectedId.HasValue && !isSelected ? 0.3 : 1.0;
                var strokeThickness = isSelected ? 3.0 : 2.0;

                foreach (var session in group)
                {
                    // Draw session block
                    double x1 = margin + ((session.StartTimeUtc - startTime).TotalMinutes / totalMinutes) * chartWidth;
                    double x2 = margin + ((session.EndTimeUtc - startTime).TotalMinutes / totalMinutes) * chartWidth;

                    // Draw altitude curve using REAL AltitudeData from session
                    var points = new PointCollection();
                    
                    if (session.AltitudeData != null && session.AltitudeData.Any())
                    {
                        // Use real altitude data points
                        foreach (var dataPoint in session.AltitudeData.OrderBy(d => d.TimeUtc))
                        {
                            if (dataPoint.TimeUtc >= startTime && dataPoint.TimeUtc <= endTime)
                            {
                                double x = margin + ((dataPoint.TimeUtc - startTime).TotalMinutes / totalMinutes) * chartWidth;
                                double alt = Math.Min(90, Math.Max(0, dataPoint.Altitude));
                                double y = 5 + chartHeight - (alt / 90.0) * chartHeight;
                                points.Add(new WpfPoint(x, y));
                            }
                        }
                    }
                    else
                    {
                        // Fallback: use average altitude as a flat line if no AltitudeData
                        double alt = session.AverageAltitude > 0 ? session.AverageAltitude : 45;
                        alt = Math.Min(90, Math.Max(0, alt));
                        double y = 5 + chartHeight - (alt / 90.0) * chartHeight;
                        points.Add(new WpfPoint(x1, y));
                        points.Add(new WpfPoint(x2, y));
                    }

                    // Draw the curve
                    if (points.Count > 1)
                    {
                        var polyline = new Polyline
                        {
                            Points = points,
                            Stroke = new SolidColorBrush(color),
                            StrokeThickness = strokeThickness,
                            StrokeLineJoin = PenLineJoin.Round,
                            Opacity = opacity
                        };
                        ChartCanvas.Children.Add(polyline);

                        // Fill area under curve
                        var fillPoints = new PointCollection(points);
                        // Close polygon at bottom: last point -> bottom-right -> bottom-left -> first point
                        fillPoints.Add(new WpfPoint(points[points.Count - 1].X, 5 + chartHeight));
                        fillPoints.Add(new WpfPoint(points[0].X, 5 + chartHeight));
                        
                        var fillOpacity = (byte)(40 * opacity);
                        var polygon = new Polygon
                        {
                            Points = fillPoints,
                            Fill = new SolidColorBrush(WpfColor.FromArgb(fillOpacity, color.R, color.G, color.B))
                        };
                        ChartCanvas.Children.Add(polygon);
                        
                        // Draw dynamic horizon line for this session (combines custom horizon + MinAltitude)
                        if (session.AltitudeData != null && session.AltitudeData.Any())
                        {
                            var horizonPoints = new PointCollection();
                            foreach (var dataPoint in session.AltitudeData.OrderBy(d => d.TimeUtc))
                            {
                                if (dataPoint.TimeUtc >= startTime && dataPoint.TimeUtc <= endTime)
                                {
                                    double x = margin + ((dataPoint.TimeUtc - startTime).TotalMinutes / totalMinutes) * chartWidth;
                                    // GetHorizonAltitudeAtAzimuth returns Max(customHorizon, MinAltitude) or just MinAltitude
                                    double horizonAlt = GetHorizonAltitudeAtAzimuth(dataPoint.Azimuth);
                                    double y = 5 + chartHeight - (horizonAlt / 90.0) * chartHeight;
                                    horizonPoints.Add(new WpfPoint(x, y));
                                }
                            }
                            
                            if (horizonPoints.Count > 1)
                            {
                                var sessionHorizonLine = new Polyline
                                {
                                    Points = horizonPoints,
                                    Stroke = new SolidColorBrush(WpfColor.FromArgb(180, 255, 68, 68)), // Red to match legend
                                    StrokeThickness = 1.5,
                                    StrokeDashArray = new DoubleCollection { 3, 2 },
                                    StrokeLineJoin = PenLineJoin.Round,
                                    Opacity = opacity
                                };
                                ChartCanvas.Children.Add(sessionHorizonLine);
                            }
                        }
                    }

                    // Draw target name label with panel number if mosaic
                    if (x2 - x1 > 40)
                    {
                        var labelText = session.PanelNumber.HasValue 
                            ? $"{TruncateText(session.TargetName, (int)((x2 - x1) / 6))} (P{session.PanelNumber})"
                            : TruncateText(session.TargetName, (int)((x2 - x1) / 5));
                        var nameLabel = new TextBlock
                        {
                            Text = labelText,
                            Foreground = new SolidColorBrush(color),
                            FontSize = 9,
                            FontWeight = FontWeights.SemiBold
                        };
                        Canvas.SetLeft(nameLabel, x1 + 2);
                        Canvas.SetTop(nameLabel, 8);
                        ChartCanvas.Children.Add(nameLabel);
                    }
                }
            }

            // Skipped targets are shown in a separate chart below the skipped targets list
            // No longer drawn in the main scheduled sessions chart

            // Draw meridian flip indicators for sessions where transit actually occurs during session
            // Only show transit line if the meridian crossing happens within the session window (strict)
            var drawnTransits = new HashSet<string>(); // Track unique "panelId" to avoid duplicates
            foreach (var session in sessions)
            {
                if (session.TransitTimeUtc.HasValue)
                {
                    var transitTime = session.TransitTimeUtc.Value;
                    
                    // Only draw transit line if it occurs strictly within this session's time window
                    var transitOccursDuringSession = transitTime >= session.StartTimeUtc && transitTime <= session.EndTimeUtc;
                    
                    if (!transitOccursDuringSession)
                        continue; // Skip - transit doesn't happen during this session
                    
                    // Create unique key using PanelId (for mosaics) or TargetId (for regular targets)
                    // This ensures each panel gets its own transit line if scheduled during its transit
                    var uniqueKey = session.PanelId?.ToString() ?? session.TargetId.ToString();
                    
                    // Only draw if not already drawn for this panel/target AND transit is within chart bounds
                    if (!drawnTransits.Contains(uniqueKey) && transitTime >= startTime && transitTime <= endTime)
                    {
                        drawnTransits.Add(uniqueKey);
                        double transitX = margin + ((transitTime - startTime).TotalMinutes / totalMinutes) * chartWidth;
                        
                        // Draw transit line (magenta, dashed)
                        var transitLine = new Line
                        {
                            X1 = transitX,
                            Y1 = 5,
                            X2 = transitX,
                            Y2 = 5 + chartHeight,
                            Stroke = new SolidColorBrush(WpfColor.FromRgb(200, 100, 200)),
                            StrokeThickness = 1.5,
                            StrokeDashArray = new DoubleCollection { 6, 3 },
                            Opacity = 0.8
                        };
                        ChartCanvas.Children.Add(transitLine);
                        
                        // Draw "M" label at bottom with target/panel info
                        var panelInfo = session.PanelNumber > 0 ? $" P{session.PanelNumber}" : "";
                        var transitLabel = new TextBlock
                        {
                            Text = "M",
                            Foreground = new SolidColorBrush(WpfColor.FromRgb(200, 100, 200)),
                            FontSize = 8,
                            FontWeight = FontWeights.Bold,
                            ToolTip = $"{session.TargetName}{panelInfo} meridian: {transitTime.ToLocalTime():HH:mm}"
                        };
                        Canvas.SetLeft(transitLabel, transitX - 4);
                        Canvas.SetTop(transitLabel, 5 + chartHeight + 1);
                        ChartCanvas.Children.Add(transitLabel);
                        
                        // Draw flip window if enabled (shaded region) - only the part within this session
                        if (session.MeridianFlipStartUtc.HasValue && session.MeridianFlipEndUtc.HasValue)
                        {
                            var flipStart = session.MeridianFlipStartUtc.Value;
                            var flipEnd = session.MeridianFlipEndUtc.Value;
                            
                            // Clamp flip window to session boundaries (only show overlap with session)
                            if (flipStart < session.StartTimeUtc) flipStart = session.StartTimeUtc;
                            if (flipEnd > session.EndTimeUtc) flipEnd = session.EndTimeUtc;
                            
                            // Also clamp to chart bounds
                            if (flipStart < startTime) flipStart = startTime;
                            if (flipEnd > endTime) flipEnd = endTime;
                            
                            if (flipStart < flipEnd && flipEnd >= startTime && flipStart <= endTime)
                            {
                                double flipX1 = margin + ((flipStart - startTime).TotalMinutes / totalMinutes) * chartWidth;
                                double flipX2 = margin + ((flipEnd - startTime).TotalMinutes / totalMinutes) * chartWidth;
                                
                                var flipRect = new System.Windows.Shapes.Rectangle
                                {
                                    Width = flipX2 - flipX1,
                                    Height = chartHeight,
                                    Fill = new SolidColorBrush(WpfColor.FromArgb(30, 200, 100, 200)),
                                    ToolTip = $"{session.TargetName}{panelInfo} flip window: {flipStart.ToLocalTime():HH:mm}-{flipEnd.ToLocalTime():HH:mm}"
                                };
                                Canvas.SetLeft(flipRect, flipX1);
                                Canvas.SetTop(flipRect, 5);
                                ChartCanvas.Children.Add(flipRect);
                            }
                        }
                    }
                }
            }

            // Draw current time line if within range
            var now = DateTime.UtcNow;
            if (now >= startTime && now <= endTime)
            {
                double nowX = margin + ((now - startTime).TotalMinutes / totalMinutes) * chartWidth;
                var nowLine = new Line
                {
                    X1 = nowX,
                    Y1 = 5,
                    X2 = nowX,
                    Y2 = 5 + chartHeight,
                    Stroke = new SolidColorBrush(WpfColor.FromRgb(76, 175, 80)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                ChartCanvas.Children.Add(nowLine);
                
                // Add "Now" label below the line
                var nowLabel = new TextBlock
                {
                    Text = "Now",
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(76, 175, 80)),
                    FontSize = 8,
                    FontWeight = FontWeights.Bold
                };
                Canvas.SetLeft(nowLabel, nowX - 10); // Center the label
                Canvas.SetTop(nowLabel, 5 + chartHeight + 2); // Below the chart
                ChartCanvas.Children.Add(nowLabel);
            }
        }

        private string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength || maxLength < 4)
                return text ?? "";
            return text.Substring(0, maxLength - 2) + "..";
        }
        
        /// <summary>
        /// Get the horizon altitude at a given time. For now returns MinAltitude.
        /// TODO: Calculate azimuth from target position and interpolate custom horizon.
        /// </summary>
        private double GetHorizonAltitudeAtTime(DateTime timeUtc)
        {
            // If no custom horizon, return the minimum altitude
            if (CustomHorizon == null || !CustomHorizon.Any())
                return MinAltitude;
            
            // For tooltip, we just return the minimum altitude since we don't have target azimuth at this point
            // The custom horizon is displayed as a line on the chart for visual reference
            return MinAltitude;
        }
        
        /// <summary>
        /// Interpolate altitude from custom horizon at a given azimuth
        /// </summary>
        private double GetHorizonAltitudeAtAzimuth(double azimuth)
        {
            if (CustomHorizon == null || !CustomHorizon.Any())
                return MinAltitude;
            
            // Normalize azimuth to 0-360
            while (azimuth < 0) azimuth += 360;
            while (azimuth >= 360) azimuth -= 360;
            
            // Sort horizon points by azimuth
            var sortedPoints = CustomHorizon.OrderBy(p => p.Azimuth).ToList();
            
            // Find the two points that bracket the azimuth
            AltAzCoordDto? lower = null;
            AltAzCoordDto? upper = null;
            
            for (int i = 0; i < sortedPoints.Count; i++)
            {
                if (sortedPoints[i].Azimuth <= azimuth)
                    lower = sortedPoints[i];
                if (sortedPoints[i].Azimuth >= azimuth && upper == null)
                    upper = sortedPoints[i];
            }
            
            // Handle wrap-around
            if (lower == null) lower = sortedPoints.Last();
            if (upper == null) upper = sortedPoints.First();
            
            // If same point or very close, return that altitude (enforcing MinAltitude floor)
            if (lower == upper || Math.Abs(lower.Azimuth - upper.Azimuth) < 0.001)
                return Math.Max(lower.Altitude, MinAltitude);
            
            // Linear interpolation
            double range = upper.Azimuth - lower.Azimuth;
            if (range < 0) range += 360; // Handle wrap-around
            
            double t = (azimuth - lower.Azimuth) / range;
            if (t < 0) t += 1;
            
            var interpolatedAlt = lower.Altitude + t * (upper.Altitude - lower.Altitude);
            // Enforce MinAltitude as floor value
            return Math.Max(interpolatedAlt, MinAltitude);
        }
    }
}
