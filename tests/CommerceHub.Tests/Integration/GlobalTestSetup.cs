using NUnit.Framework;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace CommerceHub.Tests.Integration;

/// <summary>
/// Runs once before any integration test in this namespace.
/// Starts MongoDB and RabbitMQ containers that are shared across all test fixtures.
/// </summary>
[SetUpFixture]
public sealed class GlobalTestSetup
{
    internal static MongoDbContainer  Mongo  = null!;
    internal static RabbitMqContainer Rabbit = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Mongo  = new MongoDbBuilder().Build();
        Rabbit = new RabbitMqBuilder().Build();
        await Task.WhenAll(Mongo.StartAsync(), Rabbit.StartAsync());
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await Task.WhenAll(Mongo.StopAsync(), Rabbit.StopAsync());
        await Mongo.DisposeAsync();
        await Rabbit.DisposeAsync();
    }
}
