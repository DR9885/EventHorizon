﻿using System;
using System.Linq;
using System.Threading;
using Insperex.EventHorizon.EventStore.Interfaces.Factory;
using Insperex.EventHorizon.EventStore.Interfaces.Stores;
using Insperex.EventHorizon.EventStore.Models;
using Insperex.EventHorizon.EventStore.Test.Fakers;
using Insperex.EventHorizon.EventStore.Test.Models;
using Microsoft.Extensions.DependencyInjection;
using NBench;

namespace Insperex.EventHorizon.EventStore.Benchmark.Base;

public abstract class BaseInsertBenchmark
{
    private readonly IServiceProvider _provider;
    private Counter _counter;
    private ICrudStore<Snapshot<ExampleStoreState>> _snapshotStore;
    private ExampleStoreState[] _states;
    private Snapshot<ExampleStoreState>[] _snapshots;

    protected BaseInsertBenchmark(IServiceProvider provider)
    {
        _provider = provider;
    }
    
    [PerfSetup]
    public void Setup(BenchmarkContext context)
    {
        _snapshotStore = _provider.GetRequiredService<ISnapshotStoreFactory<ExampleStoreState>>().GetSnapshotStore();
        _states = EventStoreFakers.StateFaker.Generate(1000).ToArray();
        _snapshots = _states.Select(x => new Snapshot<ExampleStoreState>(x.Id, x)).ToArray();
        _counter = context.GetCounter("TestCounter");
    }

    [PerfCleanup]
    public void Cleanup()
    {
        _snapshotStore.DropDatabaseAsync(CancellationToken.None).Wait();
    }

    [PerfBenchmark(Description = "Test Save Throughput", 
        NumberOfIterations = 3, RunMode = RunMode.Throughput, RunTimeMilliseconds = 1000, TestMode = TestMode.Test)]
    [CounterThroughputAssertion("TestCounter", MustBe.GreaterThan, 0.5d)]
    public void BenchmarkBulkSave()
    {
        _snapshotStore.UpsertAsync(_snapshots, CancellationToken.None).Wait();
        _counter.Increment();
    }

    [PerfBenchmark(Description = "Test Insert Throughput", 
        NumberOfIterations = 3, RunMode = RunMode.Throughput, RunTimeMilliseconds = 1000, TestMode = TestMode.Test)]
    [CounterThroughputAssertion("TestCounter", MustBe.GreaterThan, 0.5d)]
    public void BenchmarkBulkInsert()
    {
        _snapshotStore.InsertAsync(_snapshots, CancellationToken.None).Wait();
        _counter.Increment();
    }
}