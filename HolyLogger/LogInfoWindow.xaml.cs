using System.Windows;
using LiveCharts;
using LiveCharts.Defaults;
using LiveCharts.Wpf;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for LogInfoWindow.xaml
    /// </summary>
    public partial class LogInfoWindow : Window
    {
        public SeriesCollection Bands { get; set; }
        public SeriesCollection Bands2 { get; set; }
        public LogInfoWindow()
        {
            InitializeComponent();
            BindCharts();

            Bands = new SeriesCollection
            {
                new PieSeries
                {
                    Title = "10M",
                    Values = new ChartValues<ObservableValue> { new ObservableValue(8) },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "12M",
                    Values = new ChartValues<ObservableValue> { new ObservableValue(6) },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "15M",
                    Values = new ChartValues<ObservableValue> { new ObservableValue(10) },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "17M",
                    Values = new ChartValues<ObservableValue> { new ObservableValue(4) },
                    DataLabels = true
                }
            };
            Bands2 = new SeriesCollection
            {
                new PieSeries
                {
                    Title = "10M",
                    Values = new ChartValues<ObservableValue> { new ObservableValue(48) },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "12M",
                    Values = new ChartValues<ObservableValue> { new ObservableValue(16) },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "15M",
                    Values = new ChartValues<ObservableValue> { new ObservableValue(10) },
                    DataLabels = true
                },
                new PieSeries
                {
                    Title = "17M",
                    Values = new ChartValues<ObservableValue> { new ObservableValue(14) },
                    DataLabels = true
                }
            };

            DataContext = this;
        }

        private void BindCharts()
        {

        }
    }
}
