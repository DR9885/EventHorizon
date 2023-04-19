﻿using Insperex.EventHorizon.Abstractions.Util;
using Insperex.EventHorizon.EventStreaming.Interfaces.Streaming;
using Insperex.EventHorizon.EventStreaming.Publishers;
using Insperex.EventHorizon.EventStreaming.Readers;
using Insperex.EventHorizon.EventStreaming.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insperex.EventHorizon.EventStreaming.InMemory.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryEventStream(this IServiceCollection collection)
    {
        collection.Replace(ServiceDescriptor.Describe(
            typeof(IStreamFactory),
            typeof(InMemoryStreamFactory),
            ServiceLifetime.Singleton));

        collection.AddSingleton(typeof(StreamingClient));
        collection.AddSingleton(typeof(PublisherBuilder<>));
        collection.AddSingleton(typeof(ReaderBuilder<>));
        collection.AddSingleton(typeof(SubscriptionBuilder<>));
        collection.AddSingleton<AttributeUtil>();

        return collection;
    }
}