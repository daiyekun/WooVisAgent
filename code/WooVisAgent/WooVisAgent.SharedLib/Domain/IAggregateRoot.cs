namespace WooVisAgent.SharedLib.Domain;

public interface IAggregateRoot : IEntity;

public interface IAggregateRoot<TId> : IEntity<TId>;