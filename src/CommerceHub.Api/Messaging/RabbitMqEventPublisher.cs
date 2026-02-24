using System.Text;
using System.Text.Json;
using CommerceHub.Api.Configuration;
using CommerceHub.Api.Interfaces;
using RabbitMQ.Client;

namespace CommerceHub.Api.Messaging;

/// <summary>
/// Singleton RabbitMQ publisher. The connection and channel are expensive to create,
/// so they are reused across the application lifetime and disposed on shutdown.
/// </summary>
public sealed class RabbitMqEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly RabbitMqSettings _settings;
    private readonly ILogger<RabbitMqEventPublisher> _logger;

    private RabbitMqEventPublisher(
        IConnection connection,
        IChannel channel,
        RabbitMqSettings settings,
        ILogger<RabbitMqEventPublisher> logger)
    {
        _connection = connection;
        _channel = channel;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Async factory — required because IChannel creation is async in RabbitMQ.Client v7.
    /// Called once during app startup via ServiceCollectionExtensions.
    /// </summary>
    public static async Task<RabbitMqEventPublisher> CreateAsync(
        RabbitMqSettings settings,
        ILogger<RabbitMqEventPublisher> logger)
    {
        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            UserName = settings.Username,
            Password = settings.Password
        };

        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();

        // Declare a durable topic exchange — persists across broker restarts.
        // Topic type allows future consumers to subscribe to patterns like "order.*".
        await channel.ExchangeDeclareAsync(
            exchange: settings.ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false);

        return new RabbitMqEventPublisher(connection, channel, settings, logger);
    }

    public async Task PublishAsync<T>(string routingKey, T payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload);
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var props = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        await _channel.BasicPublishAsync(
            exchange: _settings.ExchangeName,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct);

        _logger.LogInformation(
            "Published event to exchange '{Exchange}' with routing key '{RoutingKey}' | MessageId: {MessageId}",
            _settings.ExchangeName, routingKey, props.MessageId);
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.CloseAsync();
        await _connection.CloseAsync();
    }
}
