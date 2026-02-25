namespace CommerceHub.Api.Configuration;

public class MongoDbSettings
{
    public required string ConnectionString { get; set; }
    public required string DatabaseName { get; set; }
    public string OrdersCollection { get; set; } = "Orders";
    public string ProductsCollection { get; set; } = "Products";
    public string AuditCollection { get; set; } = "AuditLogs";
    public string IdempotencyKeysCollection { get; set; } = "IdempotencyKeys";
}
