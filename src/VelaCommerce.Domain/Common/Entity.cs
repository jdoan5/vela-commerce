namespace VelaCommerce.Domain.Common;

/// <summary>
/// Base for persisted entities. Identifiers are UUIDv7 generated in .NET via
/// <see cref="Guid.CreateVersion7()"/>, so they sort by creation time and index well in
/// PostgreSQL without depending on the server-side uuidv7() function.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    /// <summary>Set by the persistence layer; soft-deleted rows are hidden by a query filter.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public void SoftDelete(DateTimeOffset now) => DeletedAt ??= now;

    public override bool Equals(object? obj) =>
        obj is Entity other && other.GetType() == GetType() && other.Id == Id;

    public override int GetHashCode() => Id.GetHashCode();
}
