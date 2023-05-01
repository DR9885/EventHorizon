﻿using Insperex.EventHorizon.Abstractions;
using Insperex.EventHorizon.Abstractions.Util;
using Insperex.EventHorizon.EventStore.Interfaces.Factory;
using Insperex.EventHorizon.EventStore.Locks;
using Insperex.EventHorizon.EventStore.MongoDb.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace Insperex.EventHorizon.EventStore.MongoDb.Extensions;

public static class EventHorizonConfiguratorExtensions
{
    static EventHorizonConfiguratorExtensions()
    {
        // Allow all to serialize
        BsonSerializer.RegisterSerializer(new ObjectSerializer(_ => true));
    }

    public static EventHorizonConfigurator AddMongoDbSnapshotStore(this EventHorizonConfigurator configurator)
    {
        AddMongoDbStore(configurator);
        configurator.Collection.AddSingleton(typeof(ISnapshotStoreFactory<>), typeof(MongoStoreFactory<>));
        return configurator;
    }

    public static EventHorizonConfigurator AddMongoDbLockStore(this EventHorizonConfigurator configurator)
    {
        AddMongoDbStore(configurator);
        configurator.Collection.AddSingleton(typeof(ILockStoreFactory<>), typeof(MongoStoreFactory<>));
        return configurator;
    }

    public static EventHorizonConfigurator AddMongoDbViewStore(this EventHorizonConfigurator configurator)
    {
        AddMongoDbStore(configurator);
        configurator.Collection.AddSingleton(typeof(IViewStoreFactory<>), typeof(MongoStoreFactory<>));
        return configurator;
    }

    private static void AddMongoDbStore(this EventHorizonConfigurator configurator)
    {
        var config = configurator.Config.GetSection("MongoDb").Get<MongoConfig>();
        configurator.Collection.TryAddSingleton<IMongoClient>(x => new MongoClient(MongoUrl.Create(config.ConnectionString)));
        configurator.Collection.AddSingleton(typeof(LockFactory<>));
        configurator.Collection.AddSingleton<AttributeUtil>();
    }
}