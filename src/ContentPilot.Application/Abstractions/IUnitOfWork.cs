namespace ContentPilot.Application.Abstractions;

/// <summary>
/// The transactional boundary use cases commit through. Enqueueing a job and changing
/// the state that job depends on are one write or neither — that single property is why
/// the queue lives in the same database as the domain.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> inside an explicit transaction, committing once.
    /// Use it when a single logical step spans several saves.
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default);
}
