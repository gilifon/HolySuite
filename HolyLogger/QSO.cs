using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HolyLogger
{
    public class QSO
    {
        public int id { get; set; }
        public string my_call { get; set; }
        public string my_square { get; set; }
        public string frequency { get; set; }
        public string callsign { get; set; }
        public string rst_rcvd { get; set; }
        public string rst_sent { get; set; }
        public DateTime timestamp { get; set; }
        public string mode { get; set; }
        public string exchange { get; set; }
        public string comment { get; set; }
        public string band { get; set; }

        public string niceTimestamp
        {
            get { return timestamp.ToShortDateString() + " " + timestamp.ToLongTimeString(); }
        }

        //public override string ToString()
        //{
        //    //return string.Format("{0}  :  {1}  :  {2}  :  {3}  :  {4}  :  {5}  :  {5}  :  {6}  :  {7}  :  {8}",
        //    return string.Format("{0}  :  {1}  :  {2}  :  {3}Mhz",
        //      dx_callsign, exchange, timestamp.ToShortDateString() + " " + timestamp.ToShortTimeString(), frequancy );
        //}
    }
}
