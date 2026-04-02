using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NINA.Astrometry;
using NINA.Core.Utility;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace AstroManager.NinaPlugin.Controls
{
    /// <summary>
    /// Simplified altitude chart for current target in dock panel.
    /// Shows target altitude, moon, horizon, and current time marker.
    /// </summary>
    public partial class CurrentTargetAltitudeChart : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty TargetCoordinatesProperty =
            DependencyProperty.Register(nameof(TargetCoordinates), typeof(Coordinates),
                typeof(CurrentTargetAltitudeChart), new PropertyMetadata(null, OnDataChanged));

        public static readonly DependencyProperty ObserverLatitudeProperty =
            DependencyProperty.Register(nameof(ObserverLatitude), typeof(double),
                typeof(CurrentTargetAltitudeChart), new PropertyMetadata(0.0, OnDataChanged));

        public static readonly DependencyProperty ObserverLongitudeProperty =
            DependencyProperty.Register(nameof(ObserverLongitude), typeof(double),
                typeof(CurrentTargetAltitudeChart), new PropertyMetadata(0.0, OnDataChanged));

        public static readonly DependencyProperty MinAltitudeProperty =
            DependencyProperty.Register(nameof(MinAltitude), typeof(double),
                typeof(CurrentTargetAltitudeChart), new PropertyMetadata(30.0, OnDataChanged));

        public static readonly DependencyProperty AstronomicalDuskProperty =
            DependencyProperty.Register(nameof(AstronomicalDusk), typeof(DateTime?),
                typeof(CurrentTargetAltitudeChart), new PropertyMetadata(null, OnDataChanged));

        public static readonly DependencyProperty AstronomicalDawnProperty =
            DependencyProperty.Register(nameof(AstronomicalDawn), typeof(DateTime?),
                typeof(CurrentTargetAltitudeChart), new PropertyMetadata(null, OnDataChanged));

        public Coordinates TargetCoordinates
        {
            get => (Coordinates)GetValue(TargetCoordinatesProperty);
            set => SetValue(TargetCoordinatesProperty, value);
        }

        public double ObserverLatitude
        {
            get => (double)GetValue(ObserverLatitudeProperty);
            set => SetValue(ObserverLatitudeProperty, value);
        }

        public double ObserverLongitude
        {
            get => (double)GetValue(ObserverLongitudeProperty);
            set => SetValue(ObserverLongitudeProperty, value);
        }

        public double MinAltitude
        {
            get => (double)GetValue(MinAltitudeProperty);
            set => SetValue(MinAltitudeProperty, value);
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

        public CurrentTargetAltitudeChart()
        {
            InitializeComponent();
        }

        private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CurrentTargetAltitudeChart chart)
                chart.DrawChart();
        }

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawChart();
        }

        private void DrawChart()
        {
            ChartCanvas.Children.Clear();

            if (ChartCanvas.ActualWidth < 10 || ChartCanvas.ActualHeight < 10)
                return;

            if (!AstronomicalDusk.HasValue || !AstronomicalDawn.HasValue)
                return;

            double width = ChartCanvas.ActualWidth;
            double height = ChartCanvas.ActualHeight;
            double margin = 20;
            double chartWidth = width - margin * 2;
            double chartHeight = height - margin - 5;

            DateTime startTime = AstronomicalDusk.Value;
            DateTime endTime = AstronomicalDawn.Value;
            double totalMinutes = (endTime - startTime).TotalMinutes;

            if (totalMinutes <= 0) return;

            // Draw night background
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
            Canvas.SetTop(nightRect, 2);
            ChartCanvas.Children.Add(nightRect);

            // Draw horizon line (red dashed)
            double horizonY = 2 + chartHeight - (MinAltitude / 90.0) * chartHeight;
            var horizonLine = new Line
            {
                X1 = margin,
                Y1 = horizonY,
                X2 = margin + chartWidth,
                Y2 = horizonY,
                Stroke = new SolidColorBrush(WpfColor.FromArgb(128, 255, 68, 68)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };
            ChartCanvas.Children.Add(horizonLine);

            // Draw grid lines (30°, 60°)
            foreach (int alt in new[] { 30, 60 })
            {
                double y = 2 + chartHeight - (alt / 90.0) * chartHeight;
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
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(100, 100, 100)),
                    FontSize = 7
                };
                Canvas.SetLeft(label, 1);
                Canvas.SetTop(label, y - 5);
                ChartCanvas.Children.Add(label);
            }

            // Draw time labels
            DateTime labelTime = new DateTime(startTime.Year, startTime.Month, startTime.Day, startTime.Hour, 0, 0, DateTimeKind.Utc);
            if (labelTime < startTime) labelTime = labelTime.AddHours(1);

            while (labelTime < endTime)
            {
                double x = margin + ((labelTime - startTime).TotalMinutes / totalMinutes) * chartWidth;
                if (x >= margin && x <= margin + chartWidth)
                {
                    DateTime localTime = labelTime.ToLocalTime();
                    var timeLabel = new TextBlock
                    {
                        Text = localTime.ToString("HH"),
                        Foreground = new SolidColorBrush(WpfColor.FromRgb(100, 100, 100)),
                        FontSize = 7
                    };
                    Canvas.SetLeft(timeLabel, x - 6);
                    Canvas.SetTop(timeLabel, height - 10);
                    ChartCanvas.Children.Add(timeLabel);
                }
                labelTime = labelTime.AddHours(2);
            }

            // Calculate and draw moon altitude
            var moonPoints = new PointCollection();
            for (int i = 0; i <= 48; i++)
            {
                DateTime time = startTime.AddMinutes(i * totalMinutes / 48);
                try
                {
                    var moonAlt = AstroUtil.GetMoonAltitude(time, ObserverLatitude, ObserverLongitude);
                    if (moonAlt >= 0)
                    {
                        double x = margin + (i / 48.0) * chartWidth;
                        double y = 2 + chartHeight - (moonAlt / 90.0) * chartHeight;
                        moonPoints.Add(new WpfPoint(x, y));
                    }
                }
                catch { }
            }

            if (moonPoints.Count > 1)
            {
                var moonLine = new Polyline
                {
                    Points = moonPoints,
                    Stroke = new SolidColorBrush(WpfColor.FromRgb(160, 160, 160)),
                    StrokeThickness = 1,
                    Opacity = 0.6
                };
                ChartCanvas.Children.Add(moonLine);
            }

            // Draw target altitude curve if we have coordinates
            if (TargetCoordinates != null)
            {
                var targetPoints = new PointCollection();
                for (int i = 0; i <= 48; i++)
                {
                    DateTime time = startTime.AddMinutes(i * totalMinutes / 48);
                    try
                    {
                        var altAz = TargetCoordinates.Transform(
                            Angle.ByDegree(ObserverLatitude),
                            Angle.ByDegree(ObserverLongitude),
                            time);
                        
                        double alt = altAz.Altitude.Degree;
                        if (alt >= 0)
                        {
                            double x = margin + (i / 48.0) * chartWidth;
                            double y = 2 + chartHeight - (alt / 90.0) * chartHeight;
                            targetPoints.Add(new WpfPoint(x, y));
                        }
                    }
                    catch { }
                }

                if (targetPoints.Count > 1)
                {
                    // Fill under curve
                    var fillPoints = new PointCollection(targetPoints);
                    fillPoints.Add(new WpfPoint(targetPoints[targetPoints.Count - 1].X, 2 + chartHeight));
                    fillPoints.Add(new WpfPoint(targetPoints[0].X, 2 + chartHeight));

                    var fillPolygon = new Polygon
                    {
                        Points = fillPoints,
                        Fill = new SolidColorBrush(WpfColor.FromArgb(40, 78, 201, 176))
                    };
                    ChartCanvas.Children.Add(fillPolygon);

                    // Draw curve
                    var targetLine = new Polyline
                    {
                        Points = targetPoints,
                        Stroke = new SolidColorBrush(WpfColor.FromRgb(78, 201, 176)),
                        StrokeThickness = 2,
                        StrokeLineJoin = PenLineJoin.Round
                    };
                    ChartCanvas.Children.Add(targetLine);
                }
            }

            // Draw "NOW" line
            DateTime now = DateTime.UtcNow;
            if (now >= startTime && now <= endTime)
            {
                double nowX = margin + ((now - startTime).TotalMinutes / totalMinutes) * chartWidth;
                var nowLine = new Line
                {
                    X1 = nowX,
                    Y1 = 2,
                    X2 = nowX,
                    Y2 = 2 + chartHeight,
                    Stroke = new SolidColorBrush(WpfColor.FromRgb(255, 255, 0)),
                    StrokeThickness = 2
                };
                ChartCanvas.Children.Add(nowLine);
            }
        }
    }
}
