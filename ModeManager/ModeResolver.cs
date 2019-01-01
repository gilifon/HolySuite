using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModeManager
{
    public class ModeResolver
    {
        public List<Mode> Modes = new List<Mode>()
        {
            new Mode {Name="AM"},
            new Mode {Name="ARDOP"},
            new Mode {Name="ATV"},
            new Mode {Name="C4FM"},
            new Mode {Name="CHIP", Submodes=new List<Mode>() {new Mode {Name="CHIP64"},new Mode {Name="CHIP128"}}},
            new Mode {Name="CLO"},
            new Mode {Name="CONTESTI"},
            new Mode {Name="CW", Submodes=new List<Mode>() {new Mode {Name="PCW"}}},
            new Mode {Name="DIGITALVOICE"},
            new Mode {Name="DOMINO", Submodes=new List<Mode>() {new Mode {Name="DOMINOEX"},new Mode {Name="DOMINOF"}}},
            new Mode {Name="DSTAR"},
            new Mode {Name="FAX"},
            new Mode {Name="FM"},
            new Mode {Name="FSK441"},
            new Mode {Name="FT8"},
            new Mode {Name="HELL", Submodes=new List<Mode>() {new Mode {Name="FMHELL"},
                new Mode {Name="FSKHELL"},
                new Mode {Name="HELL80"},
                new Mode {Name="HFSK"},
                new Mode {Name="PSKHELL"}}},
            new Mode {Name="ISCAT", Submodes=new List<Mode>() {new Mode {Name="ISCAT-A"}, new Mode {Name="ISCAT-B"}}},
            new Mode {Name="JT4", Submodes=new List<Mode>() {new Mode {Name="JT4A"},
                new Mode {Name="JT4B"},
                new Mode {Name="JT4C"},
                new Mode {Name="JT4D"},
                new Mode {Name="JT4E"},
                new Mode {Name="JT4F"}, new Mode {Name="JT4G"}}},
            new Mode {Name="JT6M"},
            new Mode {Name="JT9", Submodes=new List<Mode>() {new Mode {Name="JT9-1"},
                new Mode {Name="JT9-2"},
                new Mode {Name="JT9-5"},
                new Mode {Name="JT9-10"},
                new Mode {Name="JT9-30"},
                new Mode {Name="JT9A"},
                new Mode {Name="JT9B"},
                new Mode {Name="JT9C"},
                new Mode {Name="JT9D"},
                new Mode {Name="JT9E"},
                new Mode {Name="JT9E FAST"},
                new Mode {Name="JT9F"},
                new Mode {Name="JT9F FAST"},
                new Mode {Name="JT9G"},
                new Mode {Name="JT9G FAST"},
                new Mode {Name="JT9H"},
                new Mode {Name="JT9H FAST"}}},
            new Mode {Name="JT44"},
            new Mode {Name="JT65", Submodes=new List<Mode>() {new Mode {Name="JT65A"},
                new Mode {Name="JT65B"},
                new Mode {Name="JT65B2"},
                new Mode {Name="JT65C"},
                new Mode {Name="JT65C2"}}},
            new Mode {Name="MFSK", Submodes=new List<Mode>() {new Mode {Name="FSQCALL"},
                new Mode {Name="MFSK4"},
                new Mode {Name="MFSK8"},
                new Mode {Name="MFSK11"},
                new Mode {Name="MFSK16"},
                new Mode {Name="MFSK22"},
                new Mode {Name="MFSK31"},
                new Mode {Name="MFSK32"},
                new Mode {Name="MFSK64"},
                new Mode {Name="MFSK128"}}},
            new Mode {Name="MSK144"},
            new Mode {Name="MT63"},
            new Mode {Name="OLIVIA", Submodes=new List<Mode>() {new Mode {Name="OLIVIA 4/125"},
                new Mode {Name="OLIVIA 4/250"},
                new Mode {Name="OLIVIA 8/250"},
                new Mode {Name="OLIVIA 8/500"},
                new Mode {Name="OLIVIA 16/500"},
                new Mode {Name="OLIVIA 16/1000"},
                new Mode {Name="OLIVIA 32/1000"}}},
            new Mode {Name="OPERA", Submodes=new List<Mode>() {new Mode {Name="OPERA-BEACON"},new Mode {Name="OPERA-QSO"}}},
            new Mode {Name="PAC", Submodes=new List<Mode>() {new Mode {Name="PAC2"}, new Mode {Name="PAC3"}, new Mode {Name="PAC4"}}},
            new Mode {Name="PAX", Submodes=new List<Mode>() {new Mode {Name="PAX2"}}},
            new Mode {Name="PKT"},
            new Mode {Name="PSK", Submodes=new List<Mode>() {new Mode {Name="FSK31"},
                new Mode {Name="PSK10"},
                new Mode {Name="PSK31"},
                new Mode {Name="PSK63"},
                new Mode {Name="PSK63F"},
                new Mode {Name="PSK125"},
                new Mode {Name="PSK250"},
                new Mode {Name="PSK500"},
                new Mode {Name="PSK1000"},
                new Mode {Name="PSKAM10"},
                new Mode {Name="PSKAM31"},
                new Mode {Name="PSKAM50"},
                new Mode {Name="PSKFEC31"},
                new Mode {Name="QPSK31"},
                new Mode {Name="QPSK63"},
                new Mode {Name="QPSK125"},
                new Mode {Name="QPSK250"},
                new Mode {Name="QPSK500"},
                new Mode {Name="SIM31"}}},
            new Mode {Name="PSK2K"},
            new Mode {Name="Q15"},
            new Mode {Name="QRA64", Submodes=new List<Mode>() {new Mode {Name="QRA64A"},
                new Mode {Name="QRA64B"},
                new Mode {Name="QRA64C"},
                new Mode {Name="QRA64D"},
                new Mode {Name="QRA64E"}}},
            new Mode {Name="ROS", Submodes=new List<Mode>() {new Mode {Name="ROS-EME"},new Mode {Name="ROS-HF"},new Mode {Name="ROS-MF"}}},
            new Mode {Name="RTTY", Submodes=new List<Mode>() {new Mode {Name="ASCI"}}},
            new Mode {Name="RTTYM"},
            new Mode {Name="SSB", Submodes=new List<Mode>() {new Mode {Name="LSB"},new Mode {Name="USB"}}},
            new Mode {Name="SSTV"},
            new Mode {Name="T10"},
            new Mode {Name="THOR"},
            new Mode {Name="THRB", Submodes=new List<Mode>() {new Mode {Name="THRBX"} } },
            new Mode {Name="TOR", Submodes=new List<Mode>() {new Mode {Name="AMTORFEC"},new Mode {Name="GTOR"}}},
            new Mode {Name="V4"},
            new Mode {Name="VOI"},
            new Mode {Name="WINMOR"},
            new Mode {Name="WSPR"},
            new Mode {Name="AMTORFEC"},
            new Mode {Name="ASCI"},
            new Mode {Name="CHIP64"},
            new Mode {Name="CHIP128"},
            new Mode {Name="DOMINOF"},
            new Mode {Name="FMHELL"},
            new Mode {Name="FSK31"},
            new Mode {Name="GTOR"},
            new Mode {Name="HELL80"},
            new Mode {Name="HFSK"},
            new Mode {Name="JT4A"},
            new Mode {Name="JT4B"},
            new Mode {Name="JT4C"},
            new Mode {Name="JT4D"},
            new Mode {Name="JT4E"},
            new Mode {Name="JT4F"},
            new Mode {Name="JT4G"},
            new Mode {Name="JT65A"},
            new Mode {Name="JT65B"},
            new Mode {Name="JT65C"},
            new Mode {Name="MFSK8"},
            new Mode {Name="MFSK16"},
            new Mode {Name="PAC2"},
            new Mode {Name="PAC3"},
            new Mode {Name="PAX2"},
            new Mode {Name="PCW"},
            new Mode {Name="PSK10"},
            new Mode {Name="PSK31"},
            new Mode {Name="PSK63"},
            new Mode {Name="PSK63F"},
            new Mode {Name="PSK125"},
            new Mode {Name="PSKAM10"},
            new Mode {Name="PSKAM31"},
            new Mode {Name="PSKAM50"},
            new Mode {Name="PSKFEC31"},
            new Mode {Name="PSKHELL"},
            new Mode {Name="QPSK31"},
            new Mode {Name="QPSK63"},
            new Mode {Name="QPSK125"},
            new Mode {Name="THRBX"}
        };

        public ModeResolver()
        {

        }

        public string GetValidMode(string mode)
        {
            //Mode fromsubmode = Modes.Where(p => p.Submodes.Where(s => s.Name == mode.ToUpper().Trim()).FirstOrDefault() != null).FirstOrDefault();
            Mode fromsubmode = Modes.Where(d => d.Submodes != null && d.Submodes.Any(s => s.Name == mode.ToUpper().Trim())).FirstOrDefault();
            if (fromsubmode != null)
            {
                return fromsubmode.Name;
            }
            else
            {
                Mode frommode = Modes.FirstOrDefault(p => p.Name == mode.ToUpper().Trim());
                if (frommode != null)
                {
                    return frommode.Name;
                }
                else
                {
                    return mode;
                }
            }
        }

        public class Mode
        {
            public string Name{ get; set; }
            public IEnumerable<Mode> Submodes { get; set; }
            public override string ToString()
            {
                return Name;
            }
        }
    }
    
}
