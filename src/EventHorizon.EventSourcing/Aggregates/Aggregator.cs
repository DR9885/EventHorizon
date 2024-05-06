﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EventHorizon.Abstractions.Formatters;
using EventHorizon.Abstractions.Interfaces;
using EventHorizon.Abstractions.Interfaces.Internal;
using EventHorizon.Abstractions.Models;
using EventHorizon.Abstractions.Models.TopicMessages;
using EventHorizon.Abstractions.Reflection;
using EventHorizon.Abstractions.Serialization.Compression.Extensions;
using EventHorizon.EventStore;
using EventHorizon.EventStore.Interfaces;
using EventHorizon.EventStore.Interfaces.Stores;
using EventHorizon.EventStreaming;
using EventHorizon.EventStreaming.Publishers;
using Microsoft.Extensions.Logging;

namespace EventHorizon.EventSourcing.Aggregates;

public class Aggregator<TParent, TState>
    where TParent : IStateParent<TState>, new()
    where TState : class, IState
{
    private readonly Type _stateType = typeof(TState);
    private readonly string _stateTypeName = typeof(TState).Name;
    private readonly ILogger<Aggregator<TParent, TState>> _logger;
    private readonly StreamingClient _streamingClient;
    private readonly AggregatorConfig<TState> _config;
    private readonly Dictionary<string, object> _publisherDict = new();
    private readonly Store<TParent, TState> _store;
    private readonly StateDetail _stateDetail;
    private readonly Publisher<Event> _publisher;

    public Aggregator(
        ICrudStore<TParent> crudStore,
        StreamingClient streamingClient,
        Formatter formatter,
        AggregatorConfig<TState> config,
        ILogger<Aggregator<TParent, TState>> logger)
    {
        _store = new StoreBuilder<TParent, TState>(crudStore).AddCompression(config.StateCompression).Build();
        _streamingClient = streamingClient;
        _config = config;
        _publisher = GetPublisher<Event>(formatter.GetTopic<Event>(_stateType));
        _stateDetail = ReflectionFactory.GetStateDetail(_stateType);
        _logger = logger;
    }

    #region Save

    public async Task SaveAllAsync(Dictionary<string, Aggregate<TState>> aggregateDict)
    {
        // Save Snapshots, Events, and Publish Responses for Successful Saves
        await SaveSnapshotsAsync(aggregateDict).ConfigureAwait(false);
        await PublishEventsAsync(aggregateDict).ConfigureAwait(false);

        // Log Groups of failed snapshots
        var aggStatusLookup = aggregateDict.Values.ToLookup(x => x.Error);
        foreach (var group in aggStatusLookup)
        {
            if (group.Key == null) continue;
            var first = group.First();
            _logger.LogError("{State} {Count} had {Status} => {Error}",
                _stateTypeName, group.Count(), first.StatusCode, first.Error);
        }
    }

    private async Task SaveSnapshotsAsync(Dictionary<string, Aggregate<TState>> aggregateDict)
    {
        try
        {
            // Save Snapshots and then track failures
            var sw = Stopwatch.StartNew();
            var parents = aggregateDict.Values
                .Where(x => x.Error == null)
                .Where(x => x.IsDirty)
                .Select(x => new TParent
                {
                    Id = x.Id,
                    SequenceId = x.SequenceId,
                    Payload = x.Payload,
                    CreatedDate = x.CreatedDate,
                    UpdatedDate = x.UpdatedDate
                })
                .ToArray();

            if (parents.Any() != true)
                return;

            var results = await _store.UpsertAllAsync(parents, CancellationToken.None).ConfigureAwait(false);
            foreach (var id in results.FailedIds)
                aggregateDict[id].SetStatus(HttpStatusCode.InternalServerError, "Snapshot Failed to Save");
            foreach (var id in results.PassedIds)
            {
                aggregateDict[id].SetStatus(aggregateDict[id].SequenceId == 1?
                    HttpStatusCode.Created : HttpStatusCode.OK);
                aggregateDict[id].SequenceId++;
            }
            _logger.LogInformation("Saved {Count} {Type} Aggregate(s) in {Duration}",
                aggregateDict.Count, _stateTypeName, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            foreach (var aggregate in aggregateDict.Values)
                aggregate.SetStatus(HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private async Task PublishEventsAsync(Dictionary<string, Aggregate<TState>> aggregateDict)
    {
        var events = aggregateDict.Values
            .Where(x => x.Error == null)
            .SelectMany(x => x.Events)
            .ToArray();

        if (events.Any() != true)
            return;

        try
        {
            await _publisher.PublishAsync(events).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish events");
            var streamIds = events.Select(x => x.StreamId).Distinct().ToArray();
            foreach (var streamId in streamIds)
                aggregateDict[streamId].SetStatus(HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    internal async Task PublishResponseAsync(Response[] responses)
    {
        try
        {
            var responsesLookup = responses.ToLookup(x => x.Topic);
            foreach (var group in responsesLookup)
            {
                var publisher = GetPublisher<Response>(group.Key);
                await publisher.PublishAsync(group.ToArray()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish results");
        }
    }

    private Publisher<TMessage> GetPublisher<TMessage>(string topic)
        where TMessage : class, ITopicMessage
    {
        if (!_publisherDict.ContainsKey(topic))
            _publisherDict[topic] = _streamingClient.CreatePublisher<TMessage>().AddTopic(topic).AddCompression(_config.EventCompression).Build();
        return _publisherDict[topic] as Publisher<TMessage>;
    }

    private static void ResetAll(Dictionary<string, Aggregate<TState>> aggregateDict)
    {
        foreach (var aggregate in aggregateDict.Values)
        {
            aggregate.Events.Clear();
            aggregate.Responses.Clear();
            aggregate.SetStatus(HttpStatusCode.OK);
        }
    }

    #endregion

    #region load

    public async Task<Aggregate<TState>> GetAggregateFromStateAsync(string streamId, CancellationToken ct)
    {
        var result = await GetAggregatesFromStatesAsync(new[] { streamId }, ct).ConfigureAwait(false);
        return result.Values.FirstOrDefault();
    }

    public async Task<Dictionary<string, Aggregate<TState>>> GetAggregatesFromStatesAsync(string[] streamIds, CancellationToken ct)
    {
        try
        {
            // Load Snapshots
            streamIds = streamIds.Distinct().ToArray();
            var snapshots = await _store.GetAllAsync(streamIds, ct).ConfigureAwait(false);
            var parentDict = snapshots.ToDictionary(x => x.Id);

            // Decompress
            foreach(var snapshot in snapshots)
                snapshot.Decompress();

            // Build Aggregate Dict
            var aggregateDict = streamIds
                .Select(x => parentDict.TryGetValue(x, out var value)? new Aggregate<TState>(value) : new Aggregate<TState>(x))
                .ToDictionary(x => x.Id);

            return aggregateDict;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load snapshots");
            return streamIds
                .Select(x =>
                {
                    var agg = new Aggregate<TState>(x);
                    agg.SetStatus(HttpStatusCode.InternalServerError, ex.Message);
                    return agg;
                })
                .ToDictionary(x => x.Id);
        }
    }

    public async Task<Aggregate<TState>> GetAggregateFromEventsAsync(string streamId, DateTime? endDateTime = null)
    {
        var results = await GetAggregatesFromEventsAsync(new[] { streamId }, endDateTime).ConfigureAwait(false);
        return results[streamId];
    }

    public async Task<Dictionary<string, Aggregate<TState>>> GetAggregatesFromEventsAsync(string[] streamIds,
        DateTime? endDateTime = null)
    {
        var events = await GetEventsAsync(streamIds, endDateTime).ConfigureAwait(false);
        var eventLookup = events.ToLookup(x => x.Data.StreamId);
        return streamIds
            .Select(x =>
            {
                var e = eventLookup[x].ToArray();
                return e.Any() ? new Aggregate<TState>(e.ToArray()) : new Aggregate<TState>(x);
            })
            .ToDictionary(x => x.Id);
    }

    public Task<MessageContext<Event>[]> GetEventsAsync(string[] streamIds, DateTime? endDateTime = null)
    {
        var reader = _streamingClient.CreateReader<Event>().AddStateStream<TState>().Keys(streamIds).EndDateTime(endDateTime).Build();
        return reader.GetNextAsync(10000);
    }

    #endregion

    #region Delete

    public async Task DropAllAsync(CancellationToken ct)
    {
        await _store.DropDatabaseAsync(ct).ConfigureAwait(false);
        await _streamingClient.GetAdmin<Event>().DeleteTopicAsync(_stateType, ct: ct).ConfigureAwait(false);

        if(_stateDetail.HandlerDict[typeof(Command)].Count != 0)
            await _streamingClient.GetAdmin<Command>().DeleteTopicAsync(_stateType, ct: ct).ConfigureAwait(false);

        if(_stateDetail.HandlerDict[typeof(Request)].Count != 0)
            await _streamingClient.GetAdmin<Request>().DeleteTopicAsync(_stateType, ct: ct).ConfigureAwait(false);
    }

    #endregion
}