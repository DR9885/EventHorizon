﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Insperex.EventHorizon.Abstractions.Interfaces;
using Insperex.EventHorizon.Abstractions.Models;
using Insperex.EventHorizon.Abstractions.Models.TopicMessages;
using Insperex.EventHorizon.Abstractions.Util;
using Insperex.EventHorizon.EventStore.Interfaces;
using Insperex.EventHorizon.EventStore.Interfaces.Stores;
using Insperex.EventHorizon.EventStreaming;
using Microsoft.Extensions.Logging;

namespace Insperex.EventHorizon.EventSourcing.Aggregates;

public class Aggregator<TParent, T>
    where TParent : class, IStateParent<T>, new()
    where T : class, IState
{
    private readonly AggregateConfig<T> _config;
    private readonly ICrudStore<TParent> _crudStore;
    private readonly ILogger<Aggregator<TParent, T>> _logger;
    private readonly StreamingClient _streamingClient;

    public Aggregator(
        ICrudStore<TParent> crudStore,
        StreamingClient streamingClient,
        AggregateConfig<T> config,
        ILogger<Aggregator<TParent, T>> logger)
    {
        _crudStore = crudStore;
        _streamingClient = streamingClient;
        _config = config;
        _logger = logger;
    }

    internal AggregateConfig<T> GetConfig()
    {
        return _config;
    }

    public async Task RebuildAllAsync(CancellationToken ct)
    {
        var minDateTime = await _crudStore.GetLastUpdatedDateAsync(ct);

        // NOTE: return with one ms forward because mongodb rounds to one ms
        minDateTime = minDateTime.AddMilliseconds(1);

        var reader = _streamingClient.CreateReader<Event>().AddTopic<T>().StartDateTime(minDateTime).Build();

        while (!ct.IsCancellationRequested)
        {
            var events = await reader.GetNextAsync(1000);
            if (!events.Any()) break;

            var lookup = events.ToLookup(x => x.Data.StreamId);
            var streamIds = lookup.Select(x => x.Key).ToArray();
            var models = await _crudStore.GetAllAsync(streamIds, ct);
            var modelsDict = models.ToDictionary(x => x.Id);
            var dict = new Dictionary<string, Aggregate<T>>();
            foreach (var streamId in streamIds)
            {
                var agg = modelsDict.ContainsKey(streamId)
                    ? new Aggregate<T>(modelsDict[streamId])
                    : new Aggregate<T>(streamId);

                foreach (var message in lookup[streamId])
                    agg.Apply(message.Data);

                dict[agg.Id] = agg;
            }

            if(! dict.Any()) return;
            await SaveSnapshotsAsync(dict);
            await PublishEventsAsync(dict);
            ResetAll(dict);
        }
    }

    #region Save

    internal async Task SaveSnapshotsAsync(Dictionary<string, Aggregate<T>> aggregateDict)
    {
        try
        {
            // Save Snapshots and then track failures
            var parents = aggregateDict.Values
                .Where(x => x.Status == AggregateStatus.Ok)
                .Select(x => new TParent
                {
                    Id = x.Id,
                    SequenceId = x.SequenceId,
                    State = x.State,
                    CreatedDate = x.CreatedDate,
                    UpdatedDate = x.UpdatedDate
                })
                .ToArray();
            var results = await _crudStore.UpsertAsync(parents, CancellationToken.None);
            foreach (var failedId in results.FailedIds)
                aggregateDict[failedId].SetStatus(AggregateStatus.SaveSnapshotFailed);
        }
        catch (Exception ex)
        {
            foreach (var aggregate in aggregateDict.Values)
                aggregate.SetStatus(AggregateStatus.SaveSnapshotFailed, ex.Message);
        }
    }

    internal async Task PublishEventsAsync(Dictionary<string, Aggregate<T>> aggregateDict)
    {
        var events = aggregateDict.Values
            .Where(x => x.Status == AggregateStatus.Ok)
            .SelectMany(x => x.GetEvents())
            .ToArray();

        if (events.Any() != true)
            return;
        
        try
        {
            var publisher = _streamingClient.CreatePublisher<Event>().AddTopic<T>().Build();
            await publisher.PublishAsync(events);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish events");
            var streamIds = events.Select(x => x.StreamId).Distinct().ToArray();
            foreach (var streamId in streamIds)
                aggregateDict[streamId].SetStatus(AggregateStatus.SaveEventsFailed);
        }
    }

    internal async Task PublishResponseAsync(Dictionary<string, Aggregate<T>> aggregateDict, bool forFailed)
    {
        try
        {
            var responsesLookup = aggregateDict.Values
                .Where(x => !forFailed? 
                    x.Status == AggregateStatus.Ok 
                    : x.Status != AggregateStatus.Ok)
                .SelectMany(x => x.GetResponses())
                .ToLookup(x => x.SenderId);
            foreach (var group in responsesLookup)
            {
                var publisher = _streamingClient.CreatePublisher<Response>().AddTopic<T>(group.Key).Build();
                await publisher.PublishAsync(group.ToArray());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish results");
        }
    }

    internal void ResetAll(Dictionary<string, Aggregate<T>> aggregateDict)
    {
        foreach (var aggregate in aggregateDict.Values)
        {
            aggregate.ClearAll();
            if (aggregate.Status == AggregateStatus.Ok)
                aggregate.SequenceId++;
            aggregate.SetStatus(AggregateStatus.Ok);
        }
    }
    
    #endregion

    #region load

    public async Task<Aggregate<T>> GetAggregateFromSnapshotAsync(string streamId, CancellationToken ct)
    {
        var result = await GetAggregatesFromSnapshotsAsync(new[] { streamId }, ct);
        return result.Values.FirstOrDefault();
    }

    public async Task<Dictionary<string, Aggregate<T>>> GetAggregatesFromSnapshotsAsync(string[] streamIds, CancellationToken ct) 
    {
        try
        {
            // Load Snapshots
            streamIds = streamIds.Distinct().ToArray();
            var snapshots = await _crudStore.GetAllAsync(streamIds, ct);
            var parentDict = snapshots.ToDictionary(x => x.Id);

            // Build Aggregate Dict
            var aggregateDict = streamIds
                .Select(x => parentDict.TryGetValue(x, out var value)? new Aggregate<T>(value) : new Aggregate<T>(x))
                .ToDictionary(x => x.Id);

            return aggregateDict;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load snapshots");
            return streamIds
                .Select(x =>
                {
                    var agg = new Aggregate<T>(x);
                    agg.SetStatus(AggregateStatus.LoadSnapshotFailed, ex.Message);
                    return agg;
                })
                .ToDictionary(x => x.Id);
        }
    }

    public async Task<Aggregate<T>> GetAggregateFromEventsAsync(string streamId, DateTime? endDateTime = null)
    {
        var results = await GetAggregatesFromEventsAsync(new[] { streamId }, endDateTime);
        return results[streamId];
    }

    public async Task<Dictionary<string, Aggregate<T>>> GetAggregatesFromEventsAsync(string[] streamIds,
        DateTime? endDateTime = null)
    {
        var events = await GetEventsAsync(streamIds, endDateTime);
        var eventLookup = events.ToLookup(x => x.Data.StreamId);
        return streamIds
            .Select(x =>
            {
                var e = eventLookup[x].ToArray();
                return e.Any() ? new Aggregate<T>(e.ToArray()) : new Aggregate<T>(x);
            })
            .ToDictionary(x => x.Id);
    }

    public Task<MessageContext<Event>[]> GetEventsAsync(string[] streamIds, DateTime? endDateTime = null)
    {
        var reader = _streamingClient.CreateReader<Event>().AddTopic<T>().StreamIds(streamIds).EndDateTime(endDateTime).Build();
        return reader.GetNextAsync(10000);
    }

    #endregion
}