namespace Deals.BusinessLogic.Models.Itad;

public sealed record ItadAmount(int? AmountMinor, string? Currency);

public sealed record ItadHistoryLow(ItadAmount? All, ItadAmount? YearToDate, ItadAmount? ThreeMonths);

public sealed record ItadGamePrices(
    string ItadId,
    ItadHistoryLow? HistoryLowes,
    IReadOnlyList<ItadDeal> Deals);
