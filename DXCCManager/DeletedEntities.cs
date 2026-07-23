using System.Collections.Generic;

namespace DXCCManager
{
    // The DELETED DXCC entities: entities the ARRL has removed from the active DXCC list because the
    // territory dissolved, borders changed, or a treaty lapsed. A QSO with one still counts toward an
    // ALL-TIME total but NOT toward active awards (Honor Roll, 5BDXCC, DXCC Challenge).
    //
    // Keyed by the ARRL/ADIF DXCC entity CODE - the exact value LoTW returns in a confirmation's
    // <DXCC> field - so a confirmation or a logged QSO can be told "this entity is deleted" by code,
    // with no name-matching guesswork. Source: the ADIF 3.1.7 DXCC Entity Code Enumeration
    // (https://www.adif.org/317/ADIF_317.htm), whose "Deleted" flag and codes are the ARRL codes LoTW
    // uses. 62 entities as of ADIF 3.1.7. This set changes very rarely (only when the ARRL deletes an
    // entity), so it is shipped as data rather than downloaded.
    public static class DeletedEntities
    {
        // DXCC code -> entity name, for the deleted entities only.
        public static readonly IReadOnlyDictionary<int, string> ByCode = new Dictionary<int, string>
        {
            { 2, "ABU AIL IS." },
            { 8, "ALDABRA" },
            { 19, "BAJO NUEVO" },
            { 23, "BLENHEIM REEF" },
            { 25, "BRITISH NORTH BORNEO" },
            { 26, "BRITISH SOMALILAND" },
            { 28, "CANAL ZONE" },
            { 30, "CELEBE & MOLUCCA IS." },
            { 39, "COMOROS" },
            { 42, "DAMAO, DIU" },
            { 44, "DESROCHES" },
            { 55, "FARQUHAR" },
            { 57, "FRENCH EQUATORIAL AFRICA" },
            { 58, "FRENCH INDO-CHINA" },
            { 59, "FRENCH WEST AFRICA" },
            { 67, "FRENCH INDIA" },
            { 68, "KUWAIT/SAUDI ARABIA NEUTRAL ZONE" },
            { 81, "GERMANY" },
            { 85, "BONAIRE, CURACAO" },
            { 93, "GEYSER REEF" },
            { 101, "GOA" },
            { 102, "GOLD COAST, TOGOLAND" },
            { 113, "IFNI" },
            { 115, "ITALIAN SOMALILAND" },
            { 119, "JAVA" },
            { 127, "KAMARAN IS." },
            { 128, "KARELO-FINNISH REPUBLIC" },
            { 134, "KINGMAN REEF" },
            { 139, "KURIA MURIA I." },
            { 151, "MALYJ VYSOTSKIJ I." },
            { 154, "YEMEN ARAB REPUBLIC" },
            { 155, "MALAYA" },
            { 164, "MANCHURIA" },
            { 178, "MINERVA REEF" },
            { 183, "NETHERLANDS BORNEO" },
            { 184, "NETHERLANDS NEW GUINEA" },
            { 186, "NEWFOUNDLAND, LABRADOR" },
            { 193, "OKINAWA (RYUKYU IS.)" },
            { 194, "OKINO TORI-SHIMA" },
            { 196, "PALESTINE" },
            { 198, "PAPUA TERRITORY" },
            { 200, "PORTUGUESE TIMOR" },
            { 208, "RUANDA-URUNDI" },
            { 210, "SAAR" },
            { 218, "CZECHOSLOVAKIA" },
            { 220, "SARAWAK" },
            { 226, "SAUDI ARABIA/IRAQ NEUTRAL ZONE" },
            { 228, "SERRANA BANK & RONCADOR CAY" },
            { 229, "GERMAN DEMOCRATIC REPUBLIC" },
            { 231, "SIKKIM" },
            { 243, "PEOPLE'S DEMOCRATIC REP. OF YEMEN" },
            { 244, "SOUTHERN SUDAN" },
            { 255, "ST. MAARTEN, SABA, ST. EUSTATIUS" },
            { 258, "SUMATRA" },
            { 261, "SWAN IS." },
            { 264, "TANGIER" },
            { 267, "TERRITORY OF NEW GUINEA" },
            { 268, "TIBET" },
            { 271, "TRIESTE" },
            { 307, "ZANZIBAR" },
            { 488, "WALVIS BAY" },
            { 493, "PENGUIN IS." },
        };

        // True when this DXCC entity code is a deleted entity.
        public static bool IsDeleted(int dxccCode) => ByCode.ContainsKey(dxccCode);

        // The deleted entity's name, or null if the code is not a deleted entity.
        public static string NameOf(int dxccCode) => ByCode.TryGetValue(dxccCode, out var n) ? n : null;
    }
}
