namespace WooVisAgent.SharedLib.Domain;

public interface IEntity;

public interface IEntity<TId> : IEntity
{
    TId Id { get; set; }
}