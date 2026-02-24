namespace CommerceHub.Api.Interfaces;

public interface IEventPublisher
{
    Task PublishAsync<T>(string routingKey, T payload, CancellationToken ct = default);
}
