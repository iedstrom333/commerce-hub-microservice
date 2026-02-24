namespace CommerceHub.Api.Configuration;

public class RabbitMqSettings
{
    public required string Host { get; set; }
    public int Port { get; set; } = 5672;
    public required string Username { get; set; }
    public required string Password { get; set; }
    public string ExchangeName { get; set; } = "commerce_hub";
    public string OrderCreatedRoutingKey { get; set; } = "order.created";
}
