using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DXCCManager
{
    public class EntityResolver
    {
        private List<string> DXCCs = new List<string>()
        {
            "1A0~Sov. Mil. Order of Malta~247~AS~JM75",
            "1S~Spratly Is.~246~EU~FN30",
            "3A~Monaco~260~EU~JN33",
            "3B[6,7]~Agalega & St. Brandon~4~AF~LH89",
            "3B8~Mauritius~165~AF~LG89",
            "3B9~Rodriguez I.~207~AF~MH10",
            "3C~Equatorial Guinea~49~AF~JJ41",
            "3C0~Pagalu I.~195~AF~JI28",
            "3D2~Fiji~176~OC~RH81",
            "3D2~Conway Reef~489~OC~RG78",
            "3D2~Rotuma I.~460~OC~RH87",
            "3DA~Swaziland~468~AF~KG52",
            "3V~Tunisia~474~AF~JM33",
            "(3W|XV)~Vietnam~293~AS~OJ28",
            "3X~Guinea~107~AF~IJ39",
            "3Y~Bouvet~24~AF~JD15",
            "3Y~Peter I I.~199~AN~EC41",
            "4[J,K]~Azerbaijan~18~AS~LM28",
            "4L~Georgia~75~AS~LN01",
            "4O~Montenegro~514~EU~JN91",
            "4[P-S]~Sri Lanka~315~AS~MJ96",
            "4U[0-9]ITU~ITU HQ~117~EU~JN36",
            "4U[0-9]|4U[0-9]UN~United Nations HQ~289~NA~FN30",
            "4W6~East Timor~511~OC~PI20",
            "4[X-Z]~Israel~336~AS~KL79~KL79",
            "5A~Libya~436~AF~JL45",
            "5B|H2|C4~Cyprus~215~AS~KM64",
            "5[H-I]~Tanzania~470~AF~KH78",
            "5[N-O]~Nigeria~450~AF~JJ16",
            "5[R-S]~Madagascar~438~AF~LG15",
            "5T~Mauritania~444~AF~IK16",
            "5U~Niger~187~AF~JK02",
            "5V~Togo~483~AF~JJ06",
            "5W~Western Samoa~190~OC~AH35",
            "5X~Uganda~286~AF~KI48",
            "5[Y-Z]~Kenya~430~AF~KI78",
            "6[V-W]~Senegal~456~AF~IK12",
            "6Y~Jamaica~82~NA~FK08",
            "7O~Yemen~492~AS~LK12",
            "7P~Lesotho~432~AF~KF39",
            "7Q~Malawi~440~AF~KH65",
            "7[T-Y]~Algeria~400~AF~IL56",
            "8P~Barbados~62~NA~GK03",
            "8Q~Maldives~159~AS~MI69",
            "8R~Guyana~129~SA~FJ94",
            "9A~Croatia~497~EU~JN64",
            "9G~Ghana~424~AF~IJ84",
            "9H~Malta~257~EU~JM75",
            "9[I-J]~Zambia~482~AF~KH12",
            "9K~Kuwait~348~AS~LL38",
            "9L~Sierra Leone~458~AF~IJ37",
            "9M[2,4]~West Malaysia~299~AS~OJ02",
            "9M[6,8]~East Malaysia~46~OC~OJ41",
            "9N~Nepal~369~AS~NL08",
            "9[Q-T]~Dem. Rep. Of Congo~414~AF~JI64",
            "9U~Burundi~404~AF~KI45",
            "9V~Singapore~381~AS~OJ11",
            "9X~Rwanda~454~AF~KI47",
            "9[Y-Z]~Trinidad & Tobago~90~SA~FK90",
            "A2~Botswana~402~AF~KG03",
            "A3~Tonga~160~OC~AG28",
            "A4~Oman~370~AS~LK66",
            "A5~Bhutan~306~AS~NL46",
            "A6~United Arab Emirates~391~AS~LL52",
            "A7~Qatar~376~AS~LL54",
            "A9~Bahrain~304~AS~LL55",
            "A[P-S]~Pakistan~372~AS~ML05",
            "(B[A-R]|BT|BY)~China~318~AS~MM68",
            "BS7~Scarborough Reef~506~AS~OK85",
            "BV9P~Pratas~505~AS~OL80",
            "BV|BX~Taiwan~386~AS~PL01",
            "C2~Nauru~157~OC~RI39",
            "C3~Andorra~203~EU~JN02",
            "C5~The Gambia~422~AF~IK13",
            "C6~Bahamas~60~NA~FL04",
            "C[8-9]~Mozambique~181~AF~KG56",
            "(C[A-E]|XQ[1-8])~Chile~112~SA~FD26",
            "CE0~Easter I.~47~SA~DG52",
            "CE0~Juan Fernandez Is.~125~SA~EF96",
            "CE0~San Felix & San Ambrosio~217~SA~EG93",
            "(CE9|KC4)~Antarctica~13~AN~AA00",
            "C[M,O]~Cuba~70~NA~EL71",
            "CN~Morocco~446~AF~IL23",
            "CP~Bolivia~104~SA~FG58",
            "C[Q,R,T][3,9]|CQ2~Madeira Is.~256~AF~IM12",
            "CT|CS|CQ[0,5,6,7]~Portugal~272~EU~IM56",
            "CU|CQ[1,8]~Azores~149~EU~HM49",
            "C[V-X]~Uruguay~144~SA~GF05",
            "CY0~Sable I.~211~NA~FN93",
            "CY9~St. Paul I.~252~NA~FN97",
            "D[2-3]~Angola~401~AF~JH52",
            "D4~Cape Verde~409~AF~HK74",
            "D6~Comoros~411~AF~LH17",
            "D[A-R]~Fed. Rep. of Germany~230~EU~JN37",
            "D[U-Z]|4[F,I]~Philippines~375~OC~OJ88",
            "E3~Eritrea~51~AF~KK84",
            "E4~Palestine~510~AS~KM71",
            "E5~N. Cook Is.~191~OC~AH78",
            "E5~S. Cook Is.~234~OC~AH81",
            "E6~Niue~188~OC~AH50",
            "E7~Bosnia-Herzegovina~501~EU~JN83",
            "(E[A-H][0-5,7]|A[M,N,O][0-5,7])~Spain~281~EU~IM66",
            "(E[A-H]6|A[M,N,O]6)~Balearic Is.~21~EU~JM08",
            "(E[A-H]8|A[M,N,O]8)~Canary Is.~29~AF~IL07",
            "(E[A-H]9|A[M,N,O]9)~Ceuta & Melilla~32~AF~IM75",
            "E[I-J]~Ireland~245~EU~IO51",
            "EK~Armenia~14~AS~LM29",
            "EL~Liberia~434~AF~IJ46",
            "E[P-Q]~Iran~330~AS~LL58",
            "ER~Moldova~179~EU~KN37",
            "ES~Estonia~52~EU~KO17",
            "ET~Ethiopia~53~AF~KJ67",
            "E[U,V,W]~Belarus~27~EU~KO11",
            "EX~Kyrgystan~135~AS~MM49",
            "EY~Tajikistan~262~AS~MM37",
            "EZ~Turkmenistan~280~AS~LM67",
            "FG~Guadeloupe~79~NA~FK95",
            "FJ~Saint Barthelemy~516~AF~FK87",
            "FS~Saint Martin~213~NA~FK88",
            "FH~Mayotte~169~OC~LH26",
            "FK~New Caledonia~162~OC~RG19",
            "FM~Martinique~84~NA~FK94",
            "FO~Austral I.~508~OC~Bg28",
            "FO~Clipperton I.~36~NA~DK50",
            "FO~French Polynesia~175~OC~BG89",
            "FO~Marquesas I.~509~OC~BI91",
            "FP~St. Pierre & Miquelon~277~NA~GN16",
            "FR/G~Glorioso Is.~99~AF~LH38",
            "FR/J, E~Juan de Nova, Europa~124~AF~LG07",
            "(FR|TO)~Reunion~453~AF~LG78",
            "FR/T~Tromelin I.~276~AF~LH74",
            "TX0~Chesterfield Is.~512~NA~LH13",
            "FT8W~Crozet I.~41~AF~LE53",
            "FT8X~Kerguelen Is.~131~AF~ME40",
            "FT8Z~Amsterdam & St. Paul Is.~10~AF~MF81",
            "FW~Wallis & Futuna Is.~298~OC~AH16",
            "FY~French Guiana~63~SA~Gj22",
            "(F[0-9]|F[B,D,E,F,U,V]|T[H,M,Q,V,W,X]|H[W,X,Y])~France~227~EU~IN77",
            "(G[0-9,A,B,F,X]|M|2E)~England~223~EU~IO70",
            "(2D|G[D,T]|M[D,T])~Isle of Man~114~EU~IO74",
            "G[I,N]~Northern Ireland~265~EU~IO54",
            "G[J,H]~Jersey~122~EU~IN89",
            "G[M,S]~Scotland~279~EU~IO65",
            "G[U,P]~Guernsey~106~EU~IN89",
            "G[W,C]|2W~Wales~294~EU~IO71",
            "H4[1-9]~Solomon Is.~185~OC~QH98",
            "H40~Temotu Province~507~OC~RH29",
            "HB[1-9]~Switzerland~287~EU~JN35",
            "HB0~Liechtenstein~251~EU~JN47",
            "H[C,D][0-7,9]~Ecuador~120~SA~EI95",
            "H[C,D]8~Galapagos Is.~71~SA~EI48",
            "HH~Haiti~78~NA~FK28",
            "HI~Dominican Republic~72~NA~FK47",
            "H[J,K][1-9]~Colombia~116~SA~FI29",
            "HK0~Malpelo I.~161~SA~EJ93",
            "HK0~San Andres & Providencia~216~NA~EK92",
            "(6K|D[7-9]|HL|D[S,T])~South Korea~137~AS~PM34",
            "(H[O-P]|H3)~Panama~88~NA~EJ88",
            "H[Q-R]~Honduras~80~NA~EK54",
            "(HS|E2)~Thailand~387~AS~NJ96",
            "HV~Vatican~295~EU~JN61",
            "(HZ|7Z)~Saudi Arabia~378~AS~KL76",
            "H[A,G]~Hungary~239~EU~JN76",
            "I[S,M]~Sardinia~225~EU~JM48",
            "I~Italy~248~EU~JN51",
            "J2~Djibouti~382~AF~LK00",
            "J3~Grenada~77~NA~FK91",
            "J5~Guinea-Bissau~109~AF~IK11",
            "J6~St. Lucia~97~NA~FK93",
            "J7~Dominica~95~NA~FK95",
            "J8~St. Vincent~98~NA~FK92",            
            "JD1~Minami Torishima~177~OC~QL64",
            "JD1~Ogasawara~192~AS~QL04",
            "J[T-V]~Mongolia~363~AS~MN48",
            "JW~Svalbard~259~EU~JQ58",
            "JX~Jan Mayen~118~EU~IQ50",
            "JY~Jordan~342~AS~KL79",
            "(J[A-S]|7[J-N]|8[J-N])~Japan~339~AS~PM85",
            "KG4~Guantanamo Bay~105~NA~FK29",
            "KH0~Mariana Is.~166~OC~QK24",
            "KH1~Baker & Howland Is.~20~OC~AJ10",
            "KH2~Guam~103~OC~QK23",
            "KH3~Johnston I.~123~OC~AK56",
            "KH4~Midway I.~174~OC~AL18",
            "KH5~Palmyra & Jarvis Is.~197~OC~AJ85",
            "KH6~Hawaii~110~OC~BL01",
            "KH7~Kure I.~138~OC~AL08",
            "KH8~American Samoa~9~OC~AH45",
            "KH8SI~Swains I.~515~OC~AH48",
            "KH9~Wake I.~297~OC~RK39",
            "KL7~Alaska~6~NA~AO01",
            "KP1~Navassa I.~182~NA~FK28",
            "KP2~Virgin Is.~285~NA~FK77",
            "(KP[3-4]|NP[3-4]|WP[3-4])~Puerto Rico~202~NA~FK67",
            "KP5~Desecheo I.~43~NA~FK68",
            "L[A-N]~Norway~266~EU~JO28",
            "L[O-W]~Argentina~100~SA~FD38",
            "LX~Luxembourg~254~EU~JN29",
            "LY~Lithuania~146~EU~KO05",
            "LZ~Bulgaria~212~EU~KN11",
            "O[A-C]~Peru~136~SA~EI93",
            "OD~Lebanon~354~AS~KM73",
            "OE~Austria~206~EU~JN46",
            "(O[F,G,I]|OH[1-9])~Finland~224~EU~KP00",
            "OH0~Aland Is.~5~EU~KP00",
            "OJ0~Market Reef~167~EU~JP90",
            "O[K-L]~Czech Republic~503~EU~JN68",
            "OM~Slovak Republic~504~EU~JN87",
            "O[N-T]~Belgium~209~EU~JN29",
            "OX~Greenland~237~EU~FQ37",
            "OY~Faroe Is.~222~NA~IP61",
            "(OZ|OV|OU|5[P,Q])~Denmark~221~EU~JO44",
            "P2~Papua New Guinea~163~OC~QH49",
            "P4~Aruba~91~SA~FK42",
            "P5~North Korea~344~AS~PM27",
            "P[A-I]~Netherlands~263~EU~JO11",
            "PJ2~Curacao~517~SA~FK52",
            "PJ4~Bonaire~520~SA~FK52",
            "PJ[5,6]~Saba & St. Eustatius~519~NA~FK87",
            "PJ7~St Maarten~518~NA~FK88",
            "P[P-Y][1-9]~Brazil~108~SA~FH49",
            "P[P-Y]0~Fernando de Noronha~56~SA~HI36",
            "P[P-Y]0~St. Peter & St. Paul Rocks~253~SA~HJ50",
            "P[P-Y]0~Trindade & Martim Vaz Is.~273~SA~HG49",
            "PZ~Suriname~140~SA~GJ04",
            "R1FJ~Franz Josef Land~61~EU~LQ59",
            "S0~Western Sahara~302~AF~IL10",
            "S2~Bangladesh~305~AS~NL41",
            "(S5|YU3)~Slovenia~499~EU~JN65",
            "S7~Seychelles~379~AF~LI30",
            "S9~Sao Tome & Principe~219~AF~JI39",
            "S[A-M]|8S~Sweden~284~EU~JO57",
            "ST~Sudan~466~AF~KJ18",
            "SU~Egypt~478~AF~KL22",
            "(S[N-R]|3Z|HF[1-9])~Poland~269~EU~JN99",
            "SV/A~Mount Athos~180~EU~KN20",
            "(S[V-Y]5|J49)~Dodecanese~45~EU~KM26",
            "(S[V-Y]9|J45)~Crete~40~EU~KM15",
            "S[V-Z]~Greece~236~EU~KM06",
            "T2~Tuvalu~282~OC~RH87",
            "T30~W. Kiribati (Gilbert Is. )~301~OC~RI78",
            "T31~C. Kiribati (British Phoenix Is.)~31~OC~AI25",
            "T32~E. Kiribati (Line Is.)~48~OC~AJ94",
            "T33~Banaba I. (Ocean I.)~490~OC~RI49",
            "T5~Somalia~232~AF~LI08",
            "T7~San Marino~278~EU~JN63",
            "T8~Palau~22~OC~PJ54",
            "T[A-C]~Turkey~390~AS~KM36",
            "TF~Iceland~242~EU~HP74",
            "T[G,D]~Guatemala~76~NA~EK43",
            "T[I,E]~Costa Rica~308~NA~EJ79",
            "TI9~Cocos I.~37~NA~EJ65",
            "TJ~Cameroon~406~AF~JJ41",
            "TK~Corsica~214~EU~JN41",
            "TL~Central Africa~408~AF~JJ73",
            "TN~Congo~412~AF~JI55",
            "TR~Gabon~420~AF~JI48",
            "TT~Chad~410~AF~JJ77",
            "TU~Cote d'Ivoire~428~AF~IJ56",
            "TY~Benin~416~AF~JJ06",
            "TZ~Mali~442~AF~IK42",
            "(U[A-I][1,3-7]|U[1,3-7]|R[A-Z][1,3-7]|R[1,3-7]|U[A-I]2[^F,^K]|U2[^F,^K]|R[A-Z]2[^F,^K]|R2[^F,^K])~European Russia~54~EU~KN84",
            "(U[A-I]2[F,K]|U2[F,K]|R[A-Z]2[F,K]|R2[F,K])~Kaliningrad~126~EU~JO94",
            "(U[A-I][8,9,0]|U[8,9,0]|R[A-Z][8,9,0]|R[8,9,0])~Asiatic Russia~15~AS~LO49",
            "U[J-M]~Uzbekistan~292~AS~LN81",
            "U[N-Q]~Kazakhstan~130~AS~LN37",
            "(U[R-Z]|E[M-O])~Ukraine~288~EU~KO10",
            "V2~Antigua & Barbuda~94~NA~FK97",
            "V3~Belize~66~NA~EK55",
            "V4~St. Kitts & Nevis~249~NA~FK87",
            "V5~Namibia~464~AF~JG68",
            "V6~Micronesia~173~OC~PJ88",
            "V7~Marshall Is.~168~OC~RJ28",
            "V8~Brunei Darussalam~345~OC~OJ74",
            "VK0~Heard I.~111~AF~MD66",
            "VK0~Macquarie I.~153~OC~QD95",
            "VK9C~Cocos (Keeling) Is.~38~OC~NH87",
            "VK9L~Lord Howe I.~147~OC~QF98",
            "VK9M~Mellish Reef~171~OC~QH72",
            "VK9N~Norfolk I.~189~OC~RG30",
            "VK9W~Willis I.~303~OC~QG68",
            "VK9X~Christmas I.~35~OC~OH29",
            "VP2E~Anguilla~12~NA~FK88",
            "VP2M~Montserrat~96~NA~FK86",
            "VP2V~British Virgin Is.~65~NA~FK78",
            "VP5~Turks & Caicos Is.~89~NA~FL31",
            "VP6~Pitcairn I.~172~OC~CG44",
            "VP6~Ducie I.~513~OC~CG75",
            "VP8~Falkland Is.~141~SA~FD97",
            "(VP8|LU)~South Georgia I.~235~SA~HD05",
            "(VP8|LU)~South Orkney Is.~238~SA~GC69",
            "(VP8|LU)~South Sandwich Is.~240~SA~HD60",
            "(VP8|LU|CE9|HF0|4K1)~South Shetland Is.~241~SA~FC86",
            "VP9~Bermuda~64~NA~FM72",
            "VQ9~Chagos Is.~33~AF~MI53",
            "(VS6|VR2)~Hong Kong~321~AS~OL72",
            "VU|AT~India~324~AS~MJ88",
            "VU~Andaman & Nicobar Is.~11~AS~NJ66",
            "VU~Lakshadweep Is.~142~AS~MK52",
            "(V[A-G,O,X-Y]|X[J-O])~Canada~1~NA~FN63",
            "(VK|AX)~Australia~150~OC~QF44",
            "X[A-I][0-3,5-9]~Mexico~50~NA~DK78",
            "X[A-I]4~Revilla Gigedo~204~NA~DK28",
            "XT~Burkina Faso~480~AF~IJ79",
            "XU~Cambodia~312~AS~OK10",
            "XW~Laos~143~AS~OK07",
            "XX9~Macao~152~AS~OL61",
            "X[Y-Z]~Myanmar~309~AS~NK69",
            "YA~Afghanistan~3~AS~ML09",
            "Y[B-H]|7C~Indonesia~327~OC~NI89",
            "YI~Iraq~333~AS~KM92",
            "YJ~Vanuatu~158~OC~RH33",
            "YK~Syria~384~AS~KM75",
            "YL~Latvia~145~EU~KO06",
            "YN~Nicaragua~86~NA~EK61",
            "Y[O-R]~Romania~275~EU~KN04",
            "(YS|HU)~El Salvador~74~NA~EK43",
            "Y[T-U,Z]~Serbia~296~EU~JN93",
            "YV0~Aves I.~17~NA~FK85",
            "Y[V-Y]~Venezuela~148~SA~FJ37",            
            "Z2~Zimbabwe~452~AF~KG39",
            "Z3~Macedonia~502~EU~KN00",
            "Z6~Republic of Kosovo~522~EU~KN01",
            "Z8~South Sudan~521~AF~KJ27",
            "ZA~Albania~7~EU~JM99",
            "ZB2~Gibraltar~233~EU~IM76",
            "ZC4~UK Sovereign Base Areas on Cyprus~283~AS~KM64",
            "ZD7~St. Helena~250~AF~IM73",
            "ZD8~Ascension I.~205~AF~II22",
            "ZD9~Tristan da Cunha & Gough I.~274~AF~IE59",
            "ZF~Cayman Is.~69~NA~EK99",
            "ZK3~Tokelau Is.~270~OC~AI31",
            "ZL7~Chatham Is.~34~OC~AE15",
            "ZL8~Kermadec Is.~133~OC~AF08",
            "ZL9~New Zealand Subantarctic Islands~16~OC~AB80",
            "ZP~Paraguay~132~SA~FG87",
            "Z[R-U]~South Africa~462~AF~JF86",
            "Z[L-M]~New Zealand~170~OC~RF64",
            "ZS8~Prince Edward & Marion Is.~201~AF~KE83",
            "(K|W|N|A[A-K])~United States of America~291~NA~DM57",
        };

        private static Dictionary<string, Regex> prefixesRegexCache = new Dictionary<string, Regex>(320);
        private static Dictionary<string, Regex> EntitiesRegexCache = new Dictionary<string, Regex>(20);

        private List<DXCC> FinalDXCCs;

        public EntityResolver()
        {
            GroupDXCCs();
            CachePrefixesRegexPatterns();
        }
        
        
        
        private void GroupDXCCs()
        {
            FinalDXCCs = new List<DXCC>(340);

            foreach (string dxcc in DXCCs)
            {
                string[] entityInfo = dxcc.Split('~');
                string dxccPrefix = entityInfo[0] + ".*";
                string dxccName = entityInfo[1];
                string dxccEntity = entityInfo[2];
                string dxccContinent = entityInfo[3];
                string dxccLocator = entityInfo[4];
                FinalDXCCs.Add(new DXCC() { Prefixes = dxccPrefix, Entity = dxccEntity, Name = dxccName, Continent = dxccContinent, Locator = dxccLocator });
            }
        }
       
        private void CachePrefixesRegexPatterns()
        {
            foreach (DXCC item in FinalDXCCs)
            {
                if (!prefixesRegexCache.ContainsKey("^(" + item.Prefixes + ".*)"))
                {
                    Regex compiledRegex = new Regex("^(" + item.Prefixes + ".*)", RegexOptions.Compiled);
                    prefixesRegexCache.Add("^(" + item.Prefixes + ".*)", compiledRegex);
                }
            }
        }        

        public DXCC GetDXCC(string callsign)
        {
            foreach (DXCC item in FinalDXCCs)
            {
                if (!string.IsNullOrWhiteSpace(item.Prefixes) && prefixesRegexCache["^(" + item.Prefixes + ".*)"].IsMatch(callsign.ToUpper()) && !string.IsNullOrWhiteSpace(item.Name))
                {
                    return item;
                }
            }
            return new DXCC() { Continent = "XX", Entity = "-1", Name = "Unknown", Prefixes = callsign.Length >= 2 ? callsign.ToUpper().Substring(0, 2) : callsign.ToUpper() };
        }

        public string GetContinent(string callsign)
        {
            foreach (DXCC item in FinalDXCCs)
            {
                if (!string.IsNullOrWhiteSpace(item.Prefixes) && prefixesRegexCache["^(" + item.Prefixes + ".*)"].IsMatch(callsign.ToUpper()))
                {
                    return item.Continent;
                }
            }
            return "XX";
        }
        public string GetLocator(string callsign)
        {
            foreach (DXCC item in FinalDXCCs)
            {
                if (!string.IsNullOrWhiteSpace(item.Prefixes) && prefixesRegexCache["^(" + item.Prefixes + ".*)"].IsMatch(callsign.ToUpper()))
                {
                    return item.Locator;
                }
            }
            return "";
        }
        
    }
}
