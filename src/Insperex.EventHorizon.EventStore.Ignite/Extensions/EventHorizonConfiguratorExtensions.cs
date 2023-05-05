﻿using Apache.Ignite.Core;
using Apache.Ignite.Core.Client;
using Insperex.EventHorizon.Abstractions;
using Insperex.EventHorizon.Abstractions.Util;
using Insperex.EventHorizon.EventStore.Ignite.Models;
using Insperex.EventHorizon.EventStore.Interfaces.Factory;
using Insperex.EventHorizon.EventStore.Locks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insperex.EventHorizon.EventStore.Ignite.Extensions;

public static class EventHorizonConfiguratorExtensions
{
    public static EventHorizonConfigurator AddIgniteSnapshotStore(this EventHorizonConfigurator configurator, IConfiguration config)
    {
        AddIgniteStore(configurator, config);
        configurator.Collection.AddSingleton(typeof(ISnapshotStoreFactory<>), typeof(IgniteEventStoreFactory<>));
        configurator.Collection.AddSingleton(typeof(ILockStoreFactory<>), typeof(IgniteEventStoreFactory<>));
        return configurator;
    }

    public static EventHorizonConfigurator AddIgniteViewStore(this EventHorizonConfigurator configurator, IConfiguration config)
    {
        AddIgniteStore(configurator, config);
        configurator.Collection.AddSingleton(typeof(IViewStoreFactory<>), typeof(IgniteEventStoreFactory<>));
        return configurator;
    }

    private static void AddIgniteStore(this EventHorizonConfigurator configurator, IConfiguration config)
    {
        configurator.Collection.Configure<IgniteConfig>(config.GetSection("Ignite"));
        configurator.Collection.AddSingleton(typeof(LockFactory<>));
        configurator.Collection.AddSingleton<AttributeUtil>();
    }
}
