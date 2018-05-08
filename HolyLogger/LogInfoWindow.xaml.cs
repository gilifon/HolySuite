using System.Windows;
using LiveCharts;
using LiveCharts.Defaults;
using LiveCharts.Wpf;
using System;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for LogInfoWindow.xaml
    /// </summary>
    public partial class LogInfoWindow : Window
    {
        public SeriesCollection Band { get; set; }
        public SeriesCollection Mode { get; set; }

        public ObservableValue Band6 { get; set; }
        public ObservableValue Band10 { get; set; }
        public ObservableValue Band12 { get; set; }
        public ObservableValue Band15 { get; set; }
        public ObservableValue Band17 { get; set; }
        public ObservableValue Band20 { get; set; }
        public ObservableValue Band30 { get; set; }
        public ObservableValue Band40 { get; set; }
        public ObservableValue Band60 { get; set; }
        public ObservableValue Band80 { get; set; }
        public ObservableValue Band160 { get; set; }

        public ObservableValue CW { get; set; }
        public ObservableValue SSB { get; set; }
        public ObservableValue PSK { get; set; }
        public ObservableValue RTTY { get; set; }
        public ObservableValue FT8 { get; set; }

        public Func<ChartPoint, string> PointLabel { get; set; }

        public LogInfoWindow()
        {
            InitializeComponent();

            Band6 = new ObservableValue();
            Band10 = new ObservableValue();
            Band12 = new ObservableValue();
            Band15 = new ObservableValue();
            Band17 = new ObservableValue();
            Band20 = new ObservableValue();
            Band30 = new ObservableValue();
            Band40 = new ObservableValue();
            Band60 = new ObservableValue();
            Band80 = new ObservableValue();
            Band160 = new ObservableValue();

            CW = new ObservableValue();
            SSB = new ObservableValue();
            PSK = new ObservableValue();
            RTTY = new ObservableValue();
            FT8 = new ObservableValue();

            PointLabel = chartPoint => string.Format("{0} ({1:P})", chartPoint.SeriesView.Title, chartPoint.Participation);

            BindCharts();
            DataContext = this;
        }

        private void BindCharts()
        {
            Band = new SeriesCollection
            {
                new PieSeries
                {
                    Title = "6M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band6 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "10M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band10 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "12M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band12 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "15M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band15 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "17M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band17 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "20M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band20 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "30M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band30 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "40M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band40 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "60M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band60 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "80M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band80 },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "160M",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { Band160 },
                    DataLabels = true
                },
            };
            Mode = new SeriesCollection
            {
                new PieSeries
                {
                    Title = "CW",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { CW },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "SSB",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { SSB },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "PSK",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { PSK },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "RTTY",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { RTTY },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "FT8",
                    LabelPoint = PointLabel,
                    Values = new ChartValues<ObservableValue> { FT8 },
                    DataLabels = true
                },
            };
        }
    }
}
