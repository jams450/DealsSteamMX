namespace Deals.BusinessLogic.Models.GgDeals;

public sealed record GgDealsGamePrice(
    int SteamAppId,
    string Title,
    string Url,
    int? CurrentRetailMinor,
    int? CurrentKeyshopsMinor,
    int? HistoricalRetailMinor,
    int? HistoricalKeyshopsMinor,
    string Currency);
