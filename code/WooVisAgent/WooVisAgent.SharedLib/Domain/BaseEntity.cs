namespace WooVisAgent.SharedLib.Domain;

public abstract class BaseEntity<TId> : IEntity<TId>
{
    public TId Id { get; set; } = default!;
}