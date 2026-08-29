using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HolyLogger
{
    // THE THREE BARS IN THE CLUSTER HEADER: A, K and SFI - what the sun and the earth's magnetic field
    // are doing, which is the first thing an operator wants to know before he decides which band to
    // work. The numbers come from NOAA (SolarDataService); this is only the picture of them.
    //
    // THE SAME RANGES AND THE SAME COLOURS AS THE HOLY CLUSTER, taken from its own code, so an operator
    // with both open never sees two answers to one question:
    //
    //          range      green            orange            red
    //   A      0 - 100    under 14         14 to 80          over 80
    //   K      0 - 9      under 3          3 to 5            over 5
    //   SFI    0 - 200    OVER 120         83 to 120         under 83     <- the other way round
    //
    // SFI is reversed because a high flux is GOOD and a high A or K is bad. That is not a detail worth
    // hiding: the bar for a quiet band and the bar for a dead band would otherwise look the same.
    public partial class MainWindow
    {
        private Rectangle _propAFill, _propKFill, _propSfiFill;
        private TextBlock _propAText, _propKText, _propSfiText;
        private StackPanel _propagationPanel;
        private DispatcherTimer _propagationTimer;

        // The three columns, in the order they are GIVEN UP as the window narrows: A first, then K,
        // and SFI last. Not an arbitrary order - A is a daily average, K says what the field is doing
        // now, and SFI is the number most operators look at before choosing a band. Whatever room
        // there is goes to the most useful of them.
        private readonly System.Collections.Generic.List<StackPanel> _propColumnsByDropOrder =
            new System.Collections.Generic.List<StackPanel>();

        private const double PropBarBoxWidth = 22;
        private const double PropBarBoxHeight = 66;

        // The height inside the tube, once its border and padding are taken off. The ruler is exactly
        // this tall, always; the reading is a fraction of it. One number, so the two cannot drift apart.
        private const double PropBarUsableHeight = PropBarBoxHeight - 4;

        // Built once with the cluster window and handed to its header. Returns null for nothing to show,
        // which the caller treats as "add nothing" rather than having to know why.
        private StackPanel BuildPropagationBars()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                // 24 on the right: the panel is right-aligned, so a BIGGER right margin is what moves
                // the three bars left, away from the window edge. Settled by eye - 15px in, then 5 back.
                Margin = new Thickness(0, 0, 24, 0),
                ToolTip = "Space weather from NOAA, read once an hour.\n"
                        + "A and K: the earth's magnetic field — lower is better.\n"
                        + "SFI: solar flux — higher is better."
            };
            _propagationPanel = panel;

            var aColumn = BuildOneBar("A", out _propAFill, out _propAText);
            var kColumn = BuildOneBar("K", out _propKFill, out _propKText);
            var sfiColumn = BuildOneBar("SFI", out _propSfiFill, out _propSfiText);

            panel.Children.Add(aColumn);
            panel.Children.Add(kColumn);
            panel.Children.Add(sfiColumn);

            _propColumnsByDropOrder.Clear();
            _propColumnsByDropOrder.Add(aColumn);     // goes first
            _propColumnsByDropOrder.Add(kColumn);
            _propColumnsByDropOrder.Add(sfiColumn);   // goes last

            ShowPropagation(SolarDataService.Latest);
            return panel;
        }

        // One bar: its letter on top, the tube, and the number underneath. The tube holds a fixed
        // gradient strip - the scale, always the same - and beside it the reading, which grows from the
        // bottom. The strip is what makes a lone bar readable: half way up the tube means nothing
        // without the colours it is standing against.
        private StackPanel BuildOneBar(string label, out Rectangle fill, out TextBlock valueText)
        {
            var column = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };

            column.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            });

            // THE RULER IS ALWAYS FULL HEIGHT. Given no height of its own it stretched to whatever the
            // reading beside it happened to be - so the scale shrank with the value it was there to
            // measure against, and half way up the ruler meant nothing at all. It is the fixed thing
            // in this picture; only the bar beside it moves.
            var scale = new Rectangle
            {
                Width = 5,
                Height = PropBarUsableHeight,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 0)
            };
            fill = new Rectangle
            {
                Width = 9,
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = 0
            };

            var inside = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };
            inside.Children.Add(scale);
            inside.Children.Add(fill);

            // WHITE INSIDE, IN EVERY THEME, and the same white The Holy Cluster uses: its tubes are
            // Tailwind's gray-100 (#F3F4F6) with a gray-300 (#D1D5DB) edge. NOT the theme's colours -
            // this box is a copy of an instrument the operator already reads elsewhere, and an
            // instrument that changes colour with the program around it is a different instrument.
            // The coloured strip and the reading are what carry meaning here; they need a plain,
            // unchanging ground to be read against.
            var tube = new Border
            {
                Width = PropBarBoxWidth,
                Height = PropBarBoxHeight,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2, 2, 2, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                Child = inside
            };
            column.Children.Add(tube);

            // The scale's own colours, at THIS bar's own boundaries. Set here rather than in the
            // update, because it never changes: it is the ruler, not the reading.
            if (label == "A")   scale.Fill = PropagationScale(100, 14, 80, false);
            if (label == "K")   scale.Fill = PropagationScale(9, 3, 5, false);
            if (label == "SFI") scale.Fill = PropagationScale(200, 83, 120, true);

            valueText = new TextBlock
            {
                Text = "—",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            };
            column.Children.Add(valueText);

            return column;
        }

        // Bottom to top, with the colours changing at THIS bar's own thresholds - so the strip beside
        // the A bar turns orange a seventh of the way up and the K bar a third of the way up, which is
        // where those numbers actually turn. One strip drawn for all three would be a ruler with the
        // wrong marks on it, and the reading beside it would look wrong even when it was right.
        //
        // Green at the bottom for A and K (a quiet field is good); red at the bottom for SFI (no flux
        // is bad). The same fractions The Holy Cluster's own bars use.
        private static LinearGradientBrush PropagationScale(double max, double lowMid, double midHigh,
                                                            bool reversed)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 1),   // bottom
                EndPoint = new Point(0, 0)      // top
            };
            Color low = reversed ? Colors.Red : Colors.Green;
            Color high = reversed ? Colors.Green : Colors.Red;

            double firstStop = lowMid / max;
            double secondStop = midHigh / max;

            // Hard edges, not a blend: the boundary is a number, and a smear across it would say the
            // opposite - that somewhere around here it gets a bit worse.
            brush.GradientStops.Add(new GradientStop(low, 0.0));
            brush.GradientStops.Add(new GradientStop(low, firstStop));
            brush.GradientStops.Add(new GradientStop(Colors.Orange, firstStop));
            brush.GradientStops.Add(new GradientStop(Colors.Orange, secondStop));
            brush.GradientStops.Add(new GradientStop(high, secondStop));
            brush.GradientStops.Add(new GradientStop(high, 1.0));
            return brush;
        }

        // Called on the screen thread only.
        private void ShowPropagation(SolarDataService.Reading reading)
        {
            if (_propAFill == null) return;

            SetOneBar(_propAFill, _propAText, reading?.AIndex, 100, 14, 80, false, "0");
            SetOneBar(_propKFill, _propKText, reading?.KIndex, 9, 3, 5, false, "0");
            SetOneBar(_propSfiFill, _propSfiText, reading?.Sfi, 200, 83, 120, true, "0");

            if (_propagationPanel != null && reading != null && reading.ReadAtUtc.HasValue)
                _propagationPanel.ToolTip =
                    "Space weather from NOAA, read once an hour.\n"
                    + "A and K: the earth's magnetic field — lower is better.\n"
                    + "SFI: solar flux — higher is better.\n\n"
                    + "Last read " + reading.ReadAtUtc.Value.ToLocalTime().ToString("HH:mm") + " local.";
        }

        // NOT MEASURED IS NOT ZERO. A missing reading leaves the tube empty and prints a dash: an empty
        // bar that says "0" would be read as a dead sun rather than as a number nobody has yet.
        private static void SetOneBar(Rectangle fill, TextBlock text, double? value,
                                      double max, double lowMid, double midHigh, bool reversed,
                                      string format)
        {
            if (!value.HasValue)
            {
                fill.Height = 0;
                text.Text = "—";
                return;
            }

            double v = Math.Max(0, Math.Min(max, value.Value));
            fill.Height = Math.Max(1, PropBarUsableHeight * (v / max));

            Color c;
            if (v <= lowMid) c = reversed ? Colors.Red : Colors.Green;
            else if (v <= midHigh) c = Colors.Orange;
            else c = reversed ? Colors.Green : Colors.Red;
            fill.Fill = new SolidColorBrush(c);

            text.Text = value.Value.ToString(format);
        }

        // THEY GO ONE AT A TIME, not as a group.
        //
        // The cluster can be dragged down to 355 px, which is the band row and nothing else - the
        // spacer between the left block and the right column is gone by then, and a bar left standing
        // would be squeezed against the bands or over the mode checkboxes, which are placed by hand on
        // a canvas and know nothing about it.
        //
        // But there is no reason to take away three bars because there is no room for three. Room for
        // two shows two. The order they are given up in is A, then K, then SFI - see
        // _propColumnsByDropOrder.
        //
        // 372 is the narrowest window that has room for one bar beside everything else; each further
        // bar wants its own width on top of that. Measured from the bars themselves rather than
        // guessed, so a change to their size or spacing carries through by itself.
        private const double PropagationOneBarNeedsWidth = 372;

        private void UpdatePropagationBarsVisibility()
        {
            if (_propagationPanel == null || clusterWindow == null) return;
            if (_propColumnsByDropOrder.Count == 0) return;

            double width = clusterWindow.ActualWidth > 0 ? clusterWindow.ActualWidth : clusterWindow.Width;

            // What one bar takes, including the gaps either side of it. Before the first layout pass
            // ActualWidth is 0, so the built-in width stands in.
            double perBar = _propColumnsByDropOrder[_propColumnsByDropOrder.Count - 1].ActualWidth;
            if (perBar <= 0) perBar = PropBarBoxWidth + 4;

            // How many fit: one at the base width, and one more for every bar's width above it.
            int fits = 0;
            if (width >= PropagationOneBarNeedsWidth)
                fits = 1 + (int)Math.Floor((width - PropagationOneBarNeedsWidth) / perBar);
            if (fits > _propColumnsByDropOrder.Count) fits = _propColumnsByDropOrder.Count;

            // Hidden from the front of the list - the ones given up first.
            int hide = _propColumnsByDropOrder.Count - fits;
            for (int i = 0; i < _propColumnsByDropOrder.Count; i++)
                _propColumnsByDropOrder[i].Visibility = i < hide ? Visibility.Collapsed : Visibility.Visible;

            // The panel itself only disappears when nothing is left in it, so its margin never takes
            // room from the header for bars that are not there.
            _propagationPanel.Visibility = fits > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Started when the cluster window opens, stopped when it closes - there is no point reading
        // space weather for a window nobody has open.
        private void StartPropagationUpdates()
        {
            // Its own handler rather than a line inside the window's existing one: nothing that was
            // in the cluster before is touched by these bars.
            if (clusterWindow != null)
            {
                clusterWindow.SizeChanged -= ClusterWindow_SizeChangedForPropagation;
                clusterWindow.SizeChanged += ClusterWindow_SizeChangedForPropagation;
                UpdatePropagationBarsVisibility();
            }

            SolarDataService.Updated -= OnSolarDataUpdated;
            SolarDataService.Updated += OnSolarDataUpdated;

            if (_propagationTimer == null)
            {
                _propagationTimer = new DispatcherTimer();
                _propagationTimer.Tick += (s, e) =>
                {
                    // The interval is set from the LAST result: an hour after a good read, a minute
                    // after a bad one. (The first tick comes 5 seconds after the window opens, which is
                    // why this is set here rather than at Start.)
                    _propagationTimer.Interval = SolarDataService.LastReadSucceeded
                        ? SolarDataService.RefreshEvery
                        : SolarDataService.RetryAfterFailure;

                    // On a worker, so not one moment of this touches the thread that draws the window.
                    var ignored = System.Threading.Tasks.Task.Run(() => SolarDataService.RefreshAsync());
                };
            }
            // THE FIRST READ IS NOT DONE NOW.
            //
            // "Now" is the moment the cluster window opens, and for an operator whose cluster opens
            // with the program that is the middle of starting up. Three web requests there cost more
            // than the reading is worth - the FIRST request a program makes also has to work out the
            // system's proxy settings, which on Windows can take seconds by itself - and the sun's
            // numbers have not moved in the last hour anyway. Startup went from 13 seconds to 20 with
            // the bars in, and this is what it was.
            //
            // Five seconds later the window is up, the log is drawn, and nobody is waiting.
            _propagationTimer.Interval = TimeSpan.FromSeconds(5);
            _propagationTimer.Start();
        }

        private void ClusterWindow_SizeChangedForPropagation(object sender, SizeChangedEventArgs e)
        {
            UpdatePropagationBarsVisibility();
        }

        private void StopPropagationUpdates()
        {
            SolarDataService.Updated -= OnSolarDataUpdated;
            if (clusterWindow != null)
                clusterWindow.SizeChanged -= ClusterWindow_SizeChangedForPropagation;
            _propagationTimer?.Stop();
        }

        // Raised on whichever thread did the reading, so it comes back to this one before touching
        // anything on the screen.
        private void OnSolarDataUpdated(SolarDataService.Reading reading)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => ShowPropagation(reading)));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
