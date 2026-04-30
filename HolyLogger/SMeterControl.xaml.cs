using System.Windows.Controls;

namespace HolyLogger
{
    public partial class SMeterControl : UserControl
    {
        private const double SZoneWidth = 99.0;
        private const double SIntervals = 8.0; // S1 to S9 = 8 intervals

        public SMeterControl()
        {
            InitializeComponent();
        }

        public void SetSValue(int s)
        {
            if (s <= 0)
            {
                IndicatorFill.Width = 0;
                return;
            }
            if (s > 9) s = 9;
            IndicatorFill.Width = (s - 1) * (SZoneWidth / SIntervals);
        }
    }
}
