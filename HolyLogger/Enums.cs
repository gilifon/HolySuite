
namespace HolyLogger
{
   public enum Mode
    {
        SSB = 0, CW, DIGI
    }

    public enum State
    {
        New = 0, Edit
    }

    public class ContestCategory
    {
        public string Operator { get; set; }
        public string Mode { get; set; }
        public string Power { get; set; }

        public ContestCategory(string category)
        {
            string c = category.Split(' ')[0];
            if (string.IsNullOrWhiteSpace(c)) return;

            switch (c)
            {
                case "MIX":
                    Operator = "SINGLE-OP"; Mode = "MIX"; Power = "LOW";
                    break;
                case "CW":
                    Operator = "SINGLE-OP"; Mode = "CW"; Power = "LOW";
                    break;
                case "SSB":
                    Operator = "SINGLE-OP"; Mode = "SSB"; Power = "LOW";
                    break;
                case "FT8":
                    Operator = "SINGLE-OP"; Mode = "FT8"; Power = "LOW";
                    break;
                case "DIGI":
                    Operator = "SINGLE-OP"; Mode = "DIGI"; Power = "LOW";
                    break;
                case "QRP":
                    Operator = "SINGLE-OP"; Mode = "QRP"; Power = "QRP";
                    break;
                case "SOB":
                    Operator = "SINGLE-OP"; Mode = "SOB"; Power = "LOW";
                    break;
                case "M5":
                    Operator = "SINGLE-OP"; Mode = "M5"; Power = "LOW";
                    break;
                case "M10":
                    Operator = "SINGLE-OP"; Mode = "M10"; Power = "LOW";
                    break;
                case "POR":
                    Operator = "SINGLE-OP"; Mode = "POR"; Power = "LOW";
                    break;
                case "MOP":
                    Operator = "MULTI-OP"; Mode = "MOP"; Power = "LOW";
                    break;
                case "MM":
                    Operator = "MULTI-OP"; Mode = "MM"; Power = "LOW";
                    break;
                case "MMP":
                    Operator = "MULTI-OP"; Mode = "MMP"; Power = "LOW";
                    break;
                case "4Z9":
                    Operator = "SINGLE-OP"; Mode = "4Z9"; Power = "LOW";
                    break;
                case "SHA":
                    Operator = "SINGLE-OP"; Mode = "SHA"; Power = "LOW";
                    break;
                case "SWL":
                    Operator = "SWL"; Mode = "SWL"; Power = "LOW";
                    break;
                case "NEW":
                    Operator = "SINGLE-OP"; Mode = "NEW"; Power = "LOW";
                    break;
            }
                

        }
    }
}