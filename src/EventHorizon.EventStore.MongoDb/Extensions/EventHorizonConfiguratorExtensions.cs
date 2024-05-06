﻿using System;
using EventHorizon.Abstractions;
using EventHorizon.EventStore.Interfaces.Stores;
using EventHorizon.EventStore.Locks;
using EventHorizon.EventStore.MongoDb.Models;
using EventHorizon.EventStore.MongoDb.Stores;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace EventHorizon.EventStore.MongoDb.Extensions;

public static class EventHorizonConfiguratorExtensions
{
    static EventHorizonConfiguratorExtensions()
    {
        // Allow all to serialize
        BsonSerializer.RegisterSerializer(new ObjectSerializer(_ => true));
    }

    public static EventHorizonConfigurator AddMongoDbClient(this EventHorizonConfigurator configurator, Action<MongoConfig> onConfig)
    {
        configurator.Collection.AddSingleton(typeof(LockFactory<>));
        configurator.AddClientResolver<MongoClientResolver, MongoClient>();
        configurator.Collection.Configure(onConfig);
        return configurator;
    }

    public static EventHorizonConfigurator AddMongoDbSnapshotStore(this EventHorizonConfigurator configurator, Action<MongoConfig> onConfig)
    {
        AddMongoDbClient(configurator, onConfig);
        configurator.Collection.AddSingleton(typeof(ISnapshotStore<>), typeof(MongoSnapshotStore<>));
        configurator.Collection.AddSingleton(typeof(ILockStore<>), typeof(MongoLockStore<>));
        return configurator;
    }

    public static EventHorizonConfigurator AddMongoDbViewStore(this EventHorizonConfigurator configurator, Action<MongoConfig> onConfig)
    {
        AddMongoDbClient(configurator, onConfig);
        configurator.Collection.AddSingleton(typeof(IViewStore<>), typeof(MongoViewStore<>));
        return configurator;
    }
}