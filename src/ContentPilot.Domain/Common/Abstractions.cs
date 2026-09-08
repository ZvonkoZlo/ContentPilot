namespace ContentPilot.Domain.Common;

/// <summary>
/// Every persisted aggregate carries a time-ordered UUIDv7 identity.
/// </summary>
public interface IEntity
{
    Guid Id { get; }
}

/// <summary>
/// Marks an entity as belonging to exactly one tenant. Every implementation is
/// automatically given an EF global query filter; the architecture tests fail the
/// build if one is ever missed.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>
/// Rows that record their own creation/modification timestamps. Populated centrally
/// by the DbContext, never by hand.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Append-only tables opt in here so the DbContext can refuse updates and deletes.
/// </summary>
public interface IAppendOnly;

public abstract class Entity : IEntity
{
    public Guid Id { get; protected set; } = NewId.Create();

    public override bool Equals(object? obj) =>
        obj is Entity other && GetType() == other.GetType() && Id.Equals(other.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>
/// UUIDv7 identity factory. Time-ordered so it indexes like a sequence, random enough
/// to be safe in a URL, and it never leaks a per-tenant row count the way an int does.
/// </summary>
public static class NewId
{
    public static Guid Create() => Guid.CreateVersion7();

    public static Guid CreateAt(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}
