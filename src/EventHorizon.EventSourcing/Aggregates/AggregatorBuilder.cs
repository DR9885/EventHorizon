﻿using System;
using System.Threading;
using EventHorizon.Abstractions.Formatters;
using EventHorizon.Abstractions.Interfaces;
using EventHorizon.Abstractions.Serialization.Compression;
using EventHorizon.EventSourcing.Util;
using EventHorizon.EventStore.Interfaces;
using EventHorizon.EventStore.Interfaces.Stores;
using EventHorizon.EventStore.Locks;
using EventHorizon.EventStore.Models;
using EventHorizon.EventStreaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventHorizon.EventSourcing.Aggregates;

public class AggregatorBuilder<TParent, T>
    where TParent : class, IStateParent<T>, new()
    where T : class, IState
{
    private readonly ICrudStore<TParent> _crudStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ValidationUtil _validationUtil;
    private readonly IServiceProvider _provider;
    private readonly StreamingClient _streamingClient;
    private bool _isValidationEnabled = true;
    private readonly LockFactory<T> _lockFactory;
    private readonly ILogger<AggregatorBuilder<TParent, T>> _logger;
    private Compression? _stateCompression;
    private Compression? _eventCompression;

    public AggregatorBuilder(
        IServiceProvider provider,
        StreamingClient streamingClient,
        ILoggerFactory loggerFactory)
    {
        _crudStore = typeof(TParent).Name == typeof(Snapshot<>).Name?
            (ICrudStore<TParent>)provider.GetRequiredService<ISnapshotStore<T>>() :
            (ICrudStore<TParent>)provider.GetRequiredService<IViewStore<T>>();
        _lockFactory = provider.GetRequiredService<LockFactory<T>>();
        _validationUtil = provider.GetRequiredService<ValidationUtil>();
        _provider = provider;
        _streamingClient = streamingClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AggregatorBuilder<TParent, T>>();
    }

    public AggregatorBuilder<TParent, T> IsValidationEnabled(bool isValidationEnabled)
    {
        _isValidationEnabled = isValidationEnabled;
        return this;
    }

    public AggregatorBuilder<TParent, T> StateCompression(Compression? stateCompression)
    {
        _stateCompression = stateCompression;
        return this;
    }

    public AggregatorBuilder<TParent, T> EventCompression(Compression? eventCompression)
    {
        _eventCompression = eventCompression;
        return this;
    }

    public Aggregator<TParent, T> Build()
    {
        var config = new AggregatorConfig<T>
        {
            IsValidationEnabled = _isValidationEnabled,
            StateCompression = _stateCompression,
            EventCompression = _eventCompression
        };

        // Create Store
        var @lock = _lockFactory.CreateLock($"Migrate-{typeof(T).Name}", Environment.MachineName).WaitForLockAsync().GetAwaiter().GetResult();
        _logger.LogInformation("{Store} Store - Start {TParent} {T} Migration {Host}", _crudStore.GetType().Name, typeof(TParent).Name, typeof(T).Name, Environment.MachineName);
        _crudStore.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        _logger.LogInformation("{Store} Store - Finished {TParent} {T} Migration {Host}", _crudStore.GetType().Name, typeof(TParent).Name, typeof(T).Name, Environment.MachineName);
        @lock.DisposeAsync().GetAwaiter().GetResult();

        // Validate Handlers if Enabled
        if(config.IsValidationEnabled)
            _validationUtil.Validate<TParent, T>();

        var logger = _loggerFactory.CreateLogger<Aggregator<TParent, T>>();
        return new Aggregator<TParent, T>(_crudStore, _streamingClient, _provider.GetRequiredService<Formatter>(), config, logger);
    }
}