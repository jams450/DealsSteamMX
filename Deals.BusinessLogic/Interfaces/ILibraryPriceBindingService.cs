using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Computes the read-only price binding of a set of library rows in one batch. Strictly a query: it
/// never creates canonical games, never inserts external ids and never writes <c>game_id</c>.
/// </summary>
public interface ILibraryPriceBindingService
{
    /// <summary>
    /// Resolves a binding per row, keyed by <see cref="UserLibrary.UserLibraryId"/>. Rows not present in
    /// the result must be treated as <see cref="LibraryPriceBinding.None"/>.
    /// </summary>
    Task<IReadOnlyDictionary<long, LibraryPriceBinding>> ResolveAsync(
        IReadOnlyList<UserLibrary> rows,
        CancellationToken cancellationToken = default);
}
