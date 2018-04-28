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
            "1A0~Sov. Mil. Order of Malta~247~AS",
            "1S~Spratly Is.~246~EU",
            "3A~Monaco~260~EU",
            "3B[6,7]~Agalega & St. Brandon~4~AF",
            "3B8~Mauritius~165~AF",
            "3B9~Rodriguez I.~207~AF",
            "3C~Equatorial Guinea~49~AF",
            "3C0~Pagalu I.~195~AF",
            "3D2~Fiji~176~OC",
            "3D2~Conway Reef~489~OC",
            "3D2~Rotuma I.~460~OC",
            "3DA~Swaziland~468~AF",
            "3V~Tunisia~474~AF",
            "(3W|XV)~Vietnam~293~AS",
            "3X~Guinea~107~AF",
            "3Y~Bouvet~24~AF",
            "3Y~Peter I I.~199~AN",
            "4[J,K]~Azerbaijan~18~AS",
            "4L~Georgia~75~AS",
            "4O~Montenegro~514~EU",
            "4[P-S]~Sri Lanka~315~AS",
            "4U[0-9]ITU~ITU HQ~117~EU",
            "4U[0-9]UN~United Nations HQ~289~NA",
            "4W6~East Timor~511~OC",
            "4[X,Z]~Israel~336~AS",
            "5A~Libya~436~AF",
            "5B~Cyprus~215~AS",
            "5[H-I]~Tanzania~470~AF",
            "5[N-O]~Nigeria~450~AF",
            "5[R-S]~Madagascar~438~AF",
            "5T~Mauritania~444~AF",
            "5U~Niger~187~AF",
            "5V~Togo~483~AF",
            "5W~Western Samoa~190~OC",
            "5X~Uganda~286~AF",
            "5[Y-Z]~Kenya~430~AF",
            "6[V-W]~Senegal~456~AF",
            "6Y~Jamaica~82~NA",
            "7O~Yemen~492~AS",
            "7P~Lesotho~432~AF",
            "7Q~Malawi~440~AF",
            "7[T-Y]~Algeria~400~AF",
            "8P~Barbados~62~NA",
            "8Q~Maldives~159~AS",
            "8R~Guyana~129~SA",
            "9A~Croatia~497~EU",
            "9G~Ghana~424~AF",
            "9H~Malta~257~EU",
            "9[I-J]~Zambia~482~AF",
            "9K~Kuwait~348~AS",
            "9L~Sierra Leone~458~AF",
            "9M[2,4]~West Malaysia~299~AS",
            "9M[6,8]~East Malaysia~46~OC",
            "9N~Nepal~369~AS",
            "9[Q-T]~Dem. Rep. Of Congo~414~AF",
            "9U~Burundi~404~AF",
            "9V~Singapore~381~AS",
            "9X~Rwanda~454~AF",
            "9[Y-Z]~Trinidad & Tobago~90~SA",
            "A2~Botswana~402~AF",
            "A3~Tonga~160~OC",
            "A4~Oman~370~AS",
            "A5~Bhutan~306~AS",
            "A6~United Arab Emirates~391~AS",
            "A7~Qatar~376~AS",
            "A9~Bahrain~304~AS",
            "A[P-S]~Pakistan~372~AS",
            "B[Y,T]~China~318~AS",
            "BS7~Scarborough Reef~506~AS",
            "BV~Taiwan~386~AS",
            "BV9P~Pratas~505~AS",
            "C2~Nauru~157~OC",
            "C3~Andorra~203~EU",
            "C5~The Gambia~422~AF",
            "C6~Bahamas~60~NA",
            "C[8-9]~Mozambique~181~AF",
            "C[A-E]~Chile~112~SA",
            "CE0~Easter I.~47~SA",
            "CE0~Juan Fernandez Is.~125~SA",
            "CE0~San Felix & San Ambrosio~217~SA",
            "(CE9|KC4)~Antarctica~13~AN",
            "C[M,O]~Cuba~70~NA",
            "CN~Morocco~446~AF",
            "CP~Bolivia~104~SA",
            "CT~Portugal~272~EU",
            "CT3~Madeira Is.~256~AF",
            "CU~Azores~149~EU",
            "C[V-X]~Uruguay~144~SA",
            "CY0~Sable I.~211~NA",
            "CY9~St. Paul I.~252~NA",
            "D[2-3]~Angola~401~AF",
            "D4~Cape Verde~409~AF",
            "D6~Comoros~411~AF",
            "D[A-L]~Fed. Rep. of Germany~230~EU",
            "D[U-Z]~Philippines~375~OC",
            "E3~Eritrea~51~AF",
            "E4~Palestine~510~AS",
            "E5~N. Cook Is.~191~OC",
            "E5~S. Cook Is.~234~OC",
            "E6~Niue~188~OC",
            "E7~Bosnia-Herzegovina~501~EU",
            "E[A-H][0-5,7]~Spain~281~EU",
            "E[A-H]6~Balearic Is.~21~EU",
            "E[A-H]8~Canary Is.~29~AF",
            "E[A-H]9~Ceuta & Melilla~32~AF",
            "E[I-J]~Ireland~245~EU",
            "EK~Armenia~14~AS",
            "EL~Liberia~434~AF",
            "E[P-Q]~Iran~330~AS",
            "ER~Moldova~179~EU",
            "ES~Estonia~52~EU",
            "ET~Ethiopia~53~AF",
            "E[U,V,W]~Belarus~27~EU",
            "EX~Kyrgystan~135~AS",
            "EY~Tajikistan~262~AS",
            "EZ~Turkmenistan~280~AS",
            "F~France~227~EU",
            "FG~Guadeloupe~79~NA",
            "FJ~Saint Barthelemy~516~AF",
            "FS~Saint Martin~213~NA",
            "FH~Mayotte~169~OC",
            "FK~New Caledonia~162~OC",
            "FM~Martinique~84~NA",
            "FO~Austral I.~508~OC",
            "FO~Clipperton I.~36~NA",
            "FO~French Polynesia~175~OC",
            "FO~Marquesas I.~509~OC",
            "FP~St. Pierre & Miquelon~277~NA",
            "FR/G~Glorioso Is.~99~AF",
            "FR/J, E~Juan de Nova, Europa~124~AF",
            "FR~Reunion~453~AF",
            "FR/T~Tromelin I.~276~AF",
            "TX0~Chesterfield Is.~512~NA",
            "FT8W~Crozet I.~41~AF",
            "FT8X~Kerguelen Is.~131~AF",
            "FT8Z~Amsterdam & St. Paul Is.~10~AF",
            "FW~Wallis & Futuna Is.~298~OC",
            "FY~French Guiana~63~SA",
            "G[0-9,X]~England~223~EU",
            "G[D,T]~Isle of Man~114~EU",
            "G[I,N]~Northern Ireland~265~EU",
            "G[J,H]~Jersey~122~EU",
            "G[M,S]~Scotland~279~EU",
            "G[U,P]~Guernsey~106~EU",
            "G[W,C]~Wales~294~EU",
            "H4[1-9]~Solomon Is.~185~OC",
            "H40~Temotu Province~507~OC",
            "H[A,G]~Hungary~239~EU",
            "HB[1-9]~Switzerland~287~EU",
            "HB0~Liechtenstein~251~EU",
            "H[C,D][0-7,9]~Ecuador~120~SA",
            "H[C,D]8~Galapagos Is.~71~SA",
            "HH~Haiti~78~NA",
            "HI~Dominican Republic~72~NA",
            "H[J,K][1-9]~Colombia~116~SA",
            "HK0~Malpelo I.~161~SA",
            "HK0~San Andres & Providencia~216~NA",
            "HL~South Korea~137~AS",
            "H[O-P]~Panama~88~NA",
            "H[Q-R]~Honduras~80~NA",
            "HS~Thailand~387~AS",
            "HV~Vatican~295~EU",
            "HZ~Saudi Arabia~378~AS",
            "I~Italy~248~EU",
            "I[S,M]~Sardinia~225~EU",
            "J2~Djibouti~382~AF",
            "J3~Grenada~77~NA",
            "J5~Guinea-Bissau~109~AF",
            "J6~St. Lucia~97~NA",
            "J7~Dominica~95~NA",
            "J8~St. Vincent~98~NA",
            "J[A-S]~Japan~339~AS",
            "JD1~Minami Torishima~177~OC",
            "JD1~Ogasawara~192~AS",
            "J[T-V]~Mongolia~363~AS",
            "JW~Svalbard~259~EU",
            "JX~Jan Mayen~118~EU",
            "JY~Jordan~342~AS",
            "(K|W|N|A[A-K])~United States of America~291~NA",
            "KG4~Guantanamo Bay~105~NA",
            "KH0~Mariana Is.~166~OC",
            "KH1~Baker & Howland Is.~20~OC",
            "KH2~Guam~103~OC",
            "KH3~Johnston I.~123~OC",
            "KH4~Midway I.~174~OC",
            "KH5~Palmyra & Jarvis Is.~197~OC",
            "KH6~Hawaii~110~OC",
            "KH7~Kure I.~138~OC",
            "KH8~American Samoa~9~OC",
            "KH8SI~Swains I.~515~OC",
            "KH9~Wake I.~297~OC",
            "KL7~Alaska~6~NA",
            "KP1~Navassa I.~182~NA",
            "KP2~Virgin Is.~285~NA",
            "KP4~Puerto Rico~202~NA",
            "KP5~Desecheo I.~43~NA",
            "L[A-N]~Norway~266~EU",
            "L[O-W]~Argentina~100~SA",
            "LX~Luxembourg~254~EU",
            "LY~Lithuania~146~EU",
            "LZ~Bulgaria~212~EU",
            "O[A-C]~Peru~136~SA",
            "OD~Lebanon~354~AS",
            "OE~Austria~206~EU",
            "(O[F,G,I]|OH[1-9])~Finland~224~EU",
            "OH0~Aland Is.~5~EU",
            "OJ0~Market Reef~167~EU",
            "O[K-L]~Czech Republic~503~EU",
            "OM~Slovak Republic~504~EU",
            "O[N-T]~Belgium~209~EU",
            "OX~Greenland~237~EU",
            "OY~Faroe Is.~222~NA",
            "OZ~Denmark~221~EU",
            "P2~Papua New Guinea~163~OC",
            "P4~Aruba~91~SA",
            "P5~North Korea~344~AS",
            "P[A-I]~Netherlands~263~EU",
            "PJ2~Curacao~517~SA",
            "PJ4~Bonaire~520~SA",
            "PJ[5,6]~Saba & St. Eustatius~519~NA",
            "PJ7~St Maarten~518~NA",
            "P[P-Y][1-9]~Brazil~108~SA",
            "P[P-Y]0~Fernando de Noronha~56~SA",
            "P[P-Y]0~St. Peter & St. Paul Rocks~253~SA",
            "P[P-Y]0~Trindade & Martim Vaz Is.~273~SA",
            "PZ~Suriname~140~SA",
            "R1FJ~Franz Josef Land~61~EU",
            "S0~Western Sahara~302~AF",
            "S2~Bangladesh~305~AS",
            "(S5|YU3)~Slovenia~499~EU",
            "S7~Seychelles~379~AF",
            "S9~Sao Tome & Principe~219~AF",
            "S[A-M]~Sweden~284~EU",
            "S[N-R]~Poland~269~EU",
            "ST~Sudan~466~AF",
            "SU~Egypt~478~AF",
            "S[V-Z]~Greece~236~EU",
            "SV/A~Mount Athos~180~EU",
            "SV5~Dodecanese~45~EU",
            "SV9~Crete~40~EU",
            "T2~Tuvalu~282~OC",
            "T30~W. Kiribati (Gilbert Is. )~301~OC",
            "T31~C. Kiribati (British Phoenix Is.)~31~OC",
            "T32~E. Kiribati (Line Is.)~48~OC",
            "T33~Banaba I. (Ocean I.)~490~OC",
            "T5~Somalia~232~AF",
            "T7~San Marino~278~EU",
            "T8~Palau~22~OC",
            "T[A-C]~Turkey~390~AS",
            "TF~Iceland~242~EU",
            "T[G,D]~Guatemala~76~NA",
            "T[I,E]~Costa Rica~308~NA",
            "TI9~Cocos I.~37~NA",
            "TJ~Cameroon~406~AF",
            "TK~Corsica~214~EU",
            "TL~Central Africa~408~AF",
            "TN~Congo~412~AF",
            "TR~Gabon~420~AF",
            "TT~Chad~410~AF",
            "TU~Cote d'Ivoire~428~AF",
            "TY~Benin~416~AF",
            "TZ~Mali~442~AF",
            "(U[A-I][1,3,4,6]|R[A-Z])~European Russia~54~EU",
            "UA2~Kaliningrad~126~EU",
            "(U[A-I][8,9,0]|R[A-Z])~Asiatic Russia~15~AS",
            "U[J-M]~Uzbekistan~292~AS",
            "U[N-Q]~Kazakhstan~130~AS",
            "(U[R-Z]|E[M-O])~Ukraine~288~EU",
            "V2~Antigua & Barbuda~94~NA",
            "V3~Belize~66~NA",
            "V4~St. Kitts & Nevis~249~NA",
            "V5~Namibia~464~AF",
            "V6~Micronesia~173~OC",
            "V7~Marshall Is.~168~OC",
            "V8~Brunei Darussalam~345~OC",
            "V[E,O,Y]~Canada~1~NA",
            "VK~Australia~150~OC",
            "VK0~Heard I.~111~AF",
            "VK0~Macquarie I.~153~OC",
            "VK9C~Cocos (Keeling) Is.~38~OC",
            "VK9L~Lord Howe I.~147~OC",
            "VK9M~Mellish Reef~171~OC",
            "VK9N~Norfolk I.~189~OC",
            "VK9W~Willis I.~303~OC",
            "VK9X~Christmas I.~35~OC",
            "VP2E~Anguilla~12~NA",
            "VP2M~Montserrat~96~NA",
            "VP2V~British Virgin Is.~65~NA",
            "VP5~Turks & Caicos Is.~89~NA",
            "VP6~Pitcairn I.~172~OC",
            "VP6~Ducie I.~513~OC",
            "VP8~Falkland Is.~141~SA",
            "(VP8|LU)~South Georgia I.~235~SA",
            "(VP8|LU)~South Orkney Is.~238~SA",
            "(VP8|LU)~South Sandwich Is.~240~SA",
            "(VP8|LU|CE9|HF0|4K1)~South Shetland Is.~241~SA",
            "VP9~Bermuda~64~NA",
            "VQ9~Chagos Is.~33~AF",
            "(VS6|VR2)~Hong Kong~321~AS",
            "VU~India~324~AS",
            "VU~Andaman & Nicobar Is.~11~AS",
            "VU~Lakshadweep Is.~142~AS",
            "X[A-I][0-3,5-9]~Mexico~50~NA",
            "X[A-I]4~Revillagigedo~204~NA",
            "XT~Burkina Faso~480~AF",
            "XU~Cambodia~312~AS",
            "XW~Laos~143~AS",
            "XX9~Macao~152~AS",
            "X[Y-Z]~Myanmar~309~AS",
            "YA~Afghanistan~3~AS",
            "Y[B-H]~Indonesia~327~OC",
            "YI~Iraq~333~AS",
            "YJ~Vanuatu~158~OC",
            "YK~Syria~384~AS",
            "YL~Latvia~145~EU",
            "YN~Nicaragua~86~NA",
            "Y[O-R]~Romania~275~EU",
            "YS~El Salvador~74~NA",
            "Y[T-U,Z]~Serbia~296~EU",
            "Y[V-Y]~Venezuela~148~SA",
            "YV0~Aves I.~17~NA",
            "Z2~Zimbabwe~452~AF",
            "Z3~Macedonia~502~EU",
            "Z6~Republic of Kosovo~522~EU",
            "Z8~South Sudan (Rep.Of)~521~AF",
            "ZA~Albania~7~EU",
            "ZB2~Gibraltar~233~EU",
            "ZC4~UK Sovereign Base Areas on Cyprus~283~AS",
            "ZD7~St. Helena~250~AF",
            "ZD8~Ascension I.~205~AF",
            "ZD9~Tristan da Cunha & Gough I.~274~AF",
            "ZF~Cayman Is.~69~NA",
            "ZK3~Tokelau Is.~270~OC",
            "Z[L-M]~New Zealand~170~OC",
            "ZL7~Chatham Is.~34~OC",
            "ZL8~Kermadec Is.~133~OC",
            "ZL9~New Zealand Subantarctic Islands~16~OC",
            "ZP~Paraguay~132~SA",
            "Z[R-U]~South Africa~462~AF",
            "ZS8~Prince Edward & Marion Is.~201~AF"
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
                FinalDXCCs.Add(new DXCC() { Prefixes = dxccPrefix, Entity = dxccEntity, Name = dxccName, Continent = dxccContinent });
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

        public string GetDXCCName(string callsign)
        {
            foreach (DXCC item in FinalDXCCs)
            {
                if (!string.IsNullOrWhiteSpace(item.Prefixes) && prefixesRegexCache["^(" + item.Prefixes + ".*)"].IsMatch(callsign) && !string.IsNullOrWhiteSpace(item.Name))
                {
                    return item.Name;
                }
            }
            return callsign.Length > 2 ? callsign.Substring(0, 2) : "Unkown";
        }

        public string GetContinent(string callsign)
        {
            foreach (DXCC item in FinalDXCCs)
            {
                if (!string.IsNullOrWhiteSpace(item.Prefixes) && prefixesRegexCache["^(" + item.Prefixes + ".*)"].IsMatch(callsign))
                {
                    return item.Continent;
                }
            }
            return "XX";
        }

        private struct DXCC
        {
            public string Prefixes { get; set; }
            public string Name { get; set; }
            public string Entity { get; set; }
            public string Continent { get; set; }
        }
    }
}
