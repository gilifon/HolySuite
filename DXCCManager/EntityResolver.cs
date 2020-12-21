﻿using System;
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
            "C[Q,R,T][3,9]|CQ2~Madeira Is.~256~AF~",
            "CT|CS|CQ[0,5,6,7]~Portugal~272~EU~",
            "CU|CQ[1,8]~Azores~149~EU~",
            "C[V-X]~Uruguay~144~SA~",
            "CY0~Sable I.~211~NA~",
            "CY9~St. Paul I.~252~NA~",
            "D[2-3]~Angola~401~AF~",
            "D4~Cape Verde~409~AF~",
            "D6~Comoros~411~AF~",
            "D[A-R]~Fed. Rep. of Germany~230~EU~",
            "D[U-Z]|4[F,I]~Philippines~375~OC~",
            "E3~Eritrea~51~AF~",
            "E4~Palestine~510~AS~",
            "E5~N. Cook Is.~191~OC~",
            "E5~S. Cook Is.~234~OC~",
            "E6~Niue~188~OC~",
            "E7~Bosnia-Herzegovina~501~EU~",
            "(E[A-H][0-5,7]|A[M,N,O][0-5,7])~Spain~281~EU~",
            "(E[A-H]6|A[M,N,O]6)~Balearic Is.~21~EU~",
            "(E[A-H]8|A[M,N,O]8)~Canary Is.~29~AF~",
            "(E[A-H]9|A[M,N,O]9)~Ceuta & Melilla~32~AF~",
            "E[I-J]~Ireland~245~EU~",
            "EK~Armenia~14~AS~",
            "EL~Liberia~434~AF~",
            "E[P-Q]~Iran~330~AS~",
            "ER~Moldova~179~EU~",
            "ES~Estonia~52~EU~",
            "ET~Ethiopia~53~AF~",
            "E[U,V,W]~Belarus~27~EU~",
            "EX~Kyrgystan~135~AS~",
            "EY~Tajikistan~262~AS~",
            "EZ~Turkmenistan~280~AS~",
            "FG~Guadeloupe~79~NA~",
            "FJ~Saint Barthelemy~516~AF~",
            "FS~Saint Martin~213~NA~",
            "FH~Mayotte~169~OC~",
            "FK~New Caledonia~162~OC~",
            "FM~Martinique~84~NA~",
            "FO~Austral I.~508~OC~",
            "FO~Clipperton I.~36~NA~",
            "FO~French Polynesia~175~OC~",
            "FO~Marquesas I.~509~OC~",
            "FP~St. Pierre & Miquelon~277~NA~",
            "FR/G~Glorioso Is.~99~AF~",
            "FR/J, E~Juan de Nova, Europa~124~AF~",
            "(FR|TO)~Reunion~453~AF~",
            "FR/T~Tromelin I.~276~AF~",
            "TX0~Chesterfield Is.~512~NA~",
            "FT8W~Crozet I.~41~AF~",
            "FT8X~Kerguelen Is.~131~AF~",
            "FT8Z~Amsterdam & St. Paul Is.~10~AF~",
            "FW~Wallis & Futuna Is.~298~OC~",
            "FY~French Guiana~63~SA~",
            "(F[0-9]|F[B,D,E,F,U,V]|T[H,M,Q,V,W,X]|H[W,X,Y])~France~227~EU~",
            "(G[0-9,A,B,F,X]|M|2E)~England~223~EU~",
            "(2D|G[D,T]|M[D,T])~Isle of Man~114~EU~",
            "G[I,N]~Northern Ireland~265~EU~",
            "G[J,H]~Jersey~122~EU~",
            "G[M,S]~Scotland~279~EU~",
            "G[U,P]~Guernsey~106~EU~",
            "G[W,C]|2W~Wales~294~EU~",
            "H4[1-9]~Solomon Is.~185~OC~",
            "H40~Temotu Province~507~OC~",
            "HB[1-9]~Switzerland~287~EU~",
            "HB0~Liechtenstein~251~EU~",
            "H[C,D][0-7,9]~Ecuador~120~SA~",
            "H[C,D]8~Galapagos Is.~71~SA~",
            "HH~Haiti~78~NA~",
            "HI~Dominican Republic~72~NA~",
            "H[J,K][1-9]~Colombia~116~SA~",
            "HK0~Malpelo I.~161~SA~",
            "HK0~San Andres & Providencia~216~NA~",
            "(6K|D[7-9]|HL|D[S,T])~South Korea~137~AS~",
            "(H[O-P]|H3)~Panama~88~NA~",
            "H[Q-R]~Honduras~80~NA~",
            "(HS|E2)~Thailand~387~AS~",
            "HV~Vatican~295~EU~",
            "(HZ|7Z)~Saudi Arabia~378~AS~",
            "H[A,G]~Hungary~239~EU~",
            "I[S,M]~Sardinia~225~EU~",
            "I~Italy~248~EU~",
            "J2~Djibouti~382~AF~",
            "J3~Grenada~77~NA~",
            "J5~Guinea-Bissau~109~AF~",
            "J6~St. Lucia~97~NA~",
            "J7~Dominica~95~NA~",
            "J8~St. Vincent~98~NA~",            
            "JD1~Minami Torishima~177~OC~",
            "JD1~Ogasawara~192~AS~",
            "J[T-V]~Mongolia~363~AS~",
            "JW~Svalbard~259~EU~",
            "JX~Jan Mayen~118~EU~",
            "JY~Jordan~342~AS~",
            "(J[A-S]|7[J-N]|8[J-N])~Japan~339~AS~",
            "KG4~Guantanamo Bay~105~NA~",
            "KH0~Mariana Is.~166~OC~",
            "KH1~Baker & Howland Is.~20~OC~",
            "KH2~Guam~103~OC~",
            "KH3~Johnston I.~123~OC~",
            "KH4~Midway I.~174~OC~",
            "KH5~Palmyra & Jarvis Is.~197~OC~",
            "KH6~Hawaii~110~OC~",
            "KH7~Kure I.~138~OC~",
            "KH8~American Samoa~9~OC~",
            "KH8SI~Swains I.~515~OC~",
            "KH9~Wake I.~297~OC~",
            "KL7~Alaska~6~NA~",
            "KP1~Navassa I.~182~NA~",
            "KP2~Virgin Is.~285~NA~",
            "(KP[3-4]|NP[3-4]|WP[3-4])~Puerto Rico~202~NA~",
            "KP5~Desecheo I.~43~NA~",
            "L[A-N]~Norway~266~EU~",
            "L[O-W]~Argentina~100~SA~",
            "LX~Luxembourg~254~EU~",
            "LY~Lithuania~146~EU~",
            "LZ~Bulgaria~212~EU~",
            "O[A-C]~Peru~136~SA~",
            "OD~Lebanon~354~AS~",
            "OE~Austria~206~EU~",
            "(O[F,G,I]|OH[1-9])~Finland~224~EU~",
            "OH0~Aland Is.~5~EU~",
            "OJ0~Market Reef~167~EU~",
            "O[K-L]~Czech Republic~503~EU~",
            "OM~Slovak Republic~504~EU~",
            "O[N-T]~Belgium~209~EU~",
            "OX~Greenland~237~EU~",
            "OY~Faroe Is.~222~NA~",
            "(OZ|OV|5[P,Q])~Denmark~221~EU~",
            "P2~Papua New Guinea~163~OC~",
            "P4~Aruba~91~SA~",
            "P5~North Korea~344~AS~",
            "P[A-I]~Netherlands~263~EU~",
            "PJ2~Curacao~517~SA~",
            "PJ4~Bonaire~520~SA~",
            "PJ[5,6]~Saba & St. Eustatius~519~NA~",
            "PJ7~St Maarten~518~NA~",
            "P[P-Y][1-9]~Brazil~108~SA~",
            "P[P-Y]0~Fernando de Noronha~56~SA~",
            "P[P-Y]0~St. Peter & St. Paul Rocks~253~SA~",
            "P[P-Y]0~Trindade & Martim Vaz Is.~273~SA~",
            "PZ~Suriname~140~SA~",
            "R1FJ~Franz Josef Land~61~EU~",
            "S0~Western Sahara~302~AF~",
            "S2~Bangladesh~305~AS~",
            "(S5|YU3)~Slovenia~499~EU~",
            "S7~Seychelles~379~AF~",
            "S9~Sao Tome & Principe~219~AF~",
            "S[A-M]|8S~Sweden~284~EU~",
            "ST~Sudan~466~AF~",
            "SU~Egypt~478~AF~",
            "(S[N-R]|3Z|HF[1-9])~Poland~269~EU~",
            "SV/A~Mount Athos~180~EU~",
            "(S[V-Y]5|J49)~Dodecanese~45~EU~",
            "(S[V-Y]9|J45)~Crete~40~EU~",
            "S[V-Z]~Greece~236~EU~",
            "T2~Tuvalu~282~OC~",
            "T30~W. Kiribati (Gilbert Is. )~301~OC~",
            "T31~C. Kiribati (British Phoenix Is.)~31~OC~",
            "T32~E. Kiribati (Line Is.)~48~OC~",
            "T33~Banaba I. (Ocean I.)~490~OC~",
            "T5~Somalia~232~AF~",
            "T7~San Marino~278~EU~",
            "T8~Palau~22~OC~",
            "T[A-C]~Turkey~390~AS~",
            "TF~Iceland~242~EU~",
            "T[G,D]~Guatemala~76~NA~",
            "T[I,E]~Costa Rica~308~NA~",
            "TI9~Cocos I.~37~NA~",
            "TJ~Cameroon~406~AF~",
            "TK~Corsica~214~EU~",
            "TL~Central Africa~408~AF~",
            "TN~Congo~412~AF~",
            "TR~Gabon~420~AF~",
            "TT~Chad~410~AF~",
            "TU~Cote d'Ivoire~428~AF~",
            "TY~Benin~416~AF~",
            "TZ~Mali~442~AF~",
            "(U[A-I][1,3-7]|U[1,3-7]|R[A-Z][1,3-7]|R[1,3-7]|U[A-I]2[^F,^K]|U2[^F,^K]|R[A-Z]2[^F,^K]|R2[^F,^K])~European Russia~54~EU~",
            "(U[A-I]2[F,K]|U2[F,K]|R[A-Z]2[F,K]|R2[F,K])~Kaliningrad~126~EU~",
            "(U[A-I][8,9,0]|U[8,9,0]|R[A-Z][8,9,0]|R[8,9,0])~Asiatic Russia~15~AS~",
            "U[J-M]~Uzbekistan~292~AS~",
            "U[N-Q]~Kazakhstan~130~AS~",
            "(U[R-Z]|E[M-O])~Ukraine~288~EU~",
            "V2~Antigua & Barbuda~94~NA~",
            "V3~Belize~66~NA~",
            "V4~St. Kitts & Nevis~249~NA~",
            "V5~Namibia~464~AF~",
            "V6~Micronesia~173~OC~",
            "V7~Marshall Is.~168~OC~",
            "V8~Brunei Darussalam~345~OC~",
            "VK0~Heard I.~111~AF~",
            "VK0~Macquarie I.~153~OC~",
            "VK9C~Cocos (Keeling) Is.~38~OC~",
            "VK9L~Lord Howe I.~147~OC~",
            "VK9M~Mellish Reef~171~OC~",
            "VK9N~Norfolk I.~189~OC~",
            "VK9W~Willis I.~303~OC~",
            "VK9X~Christmas I.~35~OC~",
            "VP2E~Anguilla~12~NA~",
            "VP2M~Montserrat~96~NA~",
            "VP2V~British Virgin Is.~65~NA~",
            "VP5~Turks & Caicos Is.~89~NA~",
            "VP6~Pitcairn I.~172~OC~",
            "VP6~Ducie I.~513~OC~",
            "VP8~Falkland Is.~141~SA~",
            "(VP8|LU)~South Georgia I.~235~SA~",
            "(VP8|LU)~South Orkney Is.~238~SA~",
            "(VP8|LU)~South Sandwich Is.~240~SA~",
            "(VP8|LU|CE9|HF0|4K1)~South Shetland Is.~241~SA~",
            "VP9~Bermuda~64~NA~",
            "VQ9~Chagos Is.~33~AF~",
            "(VS6|VR2)~Hong Kong~321~AS~",
            "VU|AT~India~324~AS~",
            "VU~Andaman & Nicobar Is.~11~AS~",
            "VU~Lakshadweep Is.~142~AS~",
            "(V[A-G,O,X-Y]|X[J-O])~Canada~1~NA~",
            "(VK|AX)~Australia~150~OC~",
            "X[A-I][0-3,5-9]~Mexico~50~NA~",
            "X[A-I]4~Revillagigedo~204~NA~",
            "XT~Burkina Faso~480~AF~",
            "XU~Cambodia~312~AS~",
            "XW~Laos~143~AS~",
            "XX9~Macao~152~AS~",
            "X[Y-Z]~Myanmar~309~AS~",
            "YA~Afghanistan~3~AS~",
            "Y[B-H]|7C~Indonesia~327~OC~",
            "YI~Iraq~333~AS~",
            "YJ~Vanuatu~158~OC~",
            "YK~Syria~384~AS~",
            "YL~Latvia~145~EU~",
            "YN~Nicaragua~86~NA~",
            "Y[O-R]~Romania~275~EU~",
            "(YS|HU)~El Salvador~74~NA~",
            "Y[T-U,Z]~Serbia~296~EU~",
            "YV0~Aves I.~17~NA~",
            "Y[V-Y]~Venezuela~148~SA~",            
            "Z2~Zimbabwe~452~AF~",
            "Z3~Macedonia~502~EU~",
            "Z6~Republic of Kosovo~522~EU~",
            "Z8~South Sudan (Rep.Of)~521~AF~",
            "ZA~Albania~7~EU~",
            "ZB2~Gibraltar~233~EU~",
            "ZC4~UK Sovereign Base Areas on Cyprus~283~AS~",
            "ZD7~St. Helena~250~AF~",
            "ZD8~Ascension I.~205~AF~",
            "ZD9~Tristan da Cunha & Gough I.~274~AF~",
            "ZF~Cayman Is.~69~NA~",
            "ZK3~Tokelau Is.~270~OC~",
            "ZL7~Chatham Is.~34~OC~",
            "ZL8~Kermadec Is.~133~OC~",
            "ZL9~New Zealand Subantarctic Islands~16~OC~",
            "ZP~Paraguay~132~SA~",
            "Z[R-U]~South Africa~462~AF~",
            "Z[L-M]~New Zealand~170~OC~",
            "ZS8~Prince Edward & Marion Is.~201~AF~",
            "(K|W|N|A[A-K])~United States of America~291~NA~",
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
