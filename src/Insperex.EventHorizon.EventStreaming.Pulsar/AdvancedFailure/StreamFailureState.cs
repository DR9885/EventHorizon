﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Insperex.EventHorizon.Abstractions.Interfaces.Internal;
using Insperex.EventHorizon.Abstractions.Models;
using Insperex.EventHorizon.EventStreaming.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Insperex.EventHorizon.EventStreaming.Pulsar.AdvancedFailure;

/// <summary>
/// Data structure to keep track of which streams have entered a non-normal (failure/recovery) state.
/// </summary>
/// <typeparam name="T">Type of message from the primary topic.</typeparam>
/// <remarks>
/// The data structure acts as a write-behind cache as well, persisting any state change events in the
/// failure state topic.
///
/// It is not strictly read-behind, though, since it requires explicit call to update itself from the
/// failure state topic.
/// </remarks>
public class StreamFailureState<T> where T : ITopicMessage, new()
{
    private readonly ILogger<StreamFailureState<T>> _logger;
    private readonly FailureStateTopic<T> _failureStateTopic;
    private readonly RetrySchedule _retrySchedule;

    public StreamFailureState(SubscriptionConfig<T> config, ILogger<StreamFailureState<T>> logger,
        FailureStateTopic<T> failureStateTopic)
    {
        _retrySchedule = config.RetryBackoffPolicy == null
            ? new RetrySchedule()
            : new RetrySchedule(config.RetryBackoffPolicy);
        _logger = logger;
        _failureStateTopic = failureStateTopic;
    }

    #region Lifecycle

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _failureStateTopic.InitializeAsync(ct);
    }

    #endregion Lifecycle

    #region Queries

    public IEnumerable<StreamState> StreamsForRetry()
    {
        return _failureStateTopic.GetStreams()
            .Select(s => new StreamState
            {
                StreamId = s.StreamId,
                Topics = s.Topics
                    .Where(t =>
                        !t.Value.IsUpToDate
                        && (!t.Value.NextRetry.HasValue || DateTime.UtcNow >= t.Value.NextRetry.Value))
                    .ToDictionary(t => t.Key, t => t.Value)
            })
            .Where(s => s.Topics.Any());
    }

    /// <summary>
    /// Searches for given topic/streams and returns their status.
    /// </summary>
    public (string StreamId, TopicState Topic)[] FindTopicStreams(
        HashSet<(string Topic, string StreamId)> topicStreams)
    {
        var streamIds = topicStreams.Select(ts => ts.StreamId).Distinct().ToArray();
        var streams = _failureStateTopic.FindStreams(streamIds).ToArray();

        return streams
            .SelectMany(s => s.Topics.Select(t =>
                new {Key = (t.Key, s.StreamId), Topic = t.Value}))
            .Where(ts => topicStreams.Contains(ts.Key))
            .Select(ts => (ts.Key.StreamId, ts.Topic))
            .ToArray();
    }

    #endregion Queries

    #region Commands

    public async Task StreamTopicsUpToDate(string streamId, string[] topics)
    {
        var streamState = _failureStateTopic.FindStream(streamId);
        var anyUpdates = false;

        foreach (var topic in streamState.Topics.Keys)
        {
            if (!streamState.Topics[topic].IsUpToDate && topics.Contains(topic))
            {
                streamState.Topics[topic].IsUpToDate = true;
                anyUpdates = true;
            }
        }

        if (anyUpdates) await _failureStateTopic.Publish(streamState);
    }

    public async Task StreamTopicsResolved(string streamId, string[] topics)
    {
        var streamState = _failureStateTopic.FindStream(streamId);

        if (streamState != null)
        {
            streamState.Topics = streamState.Topics
                .Where(kv => !kv.Value.IsUpToDate || !topics.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            await _failureStateTopic.Publish(streamState);
        }
    }

    public async Task MessageFailed(MessageContext<T> message)
    {
        _logger.LogInformation($"Msg FAIL: {message.TopicData.Topic} => {message.Data.StreamId} => {message.TopicData.Id}");

        var streamState = EnsureTopicForStream(message);
        var topicState = streamState.Topics[message.TopicData.Topic];

        if (topicState.NextRetry.HasValue) topicState.TimesRetried++;

        var nextRetryInterval = _retrySchedule.NextInterval(topicState.TimesRetried);
        topicState.NextRetry = message.TopicData.CreatedDate.Add(nextRetryInterval);

        await _failureStateTopic.Publish(streamState);
    }

    public async Task MessageSucceeded(MessageContext<T> message)
    {
        var streamState = EnsureTopicForStream(message);
        var topicState = streamState.Topics[message.TopicData.Topic];

        topicState.TimesRetried = 0;
        topicState.NextRetry = null;

        await _failureStateTopic.Publish(streamState);
    }

    private StreamState EnsureTopicForStream(MessageContext<T> message)
    {
        var streamId = message.Data.StreamId;
        var topic = message.TopicData.Topic;
        long.TryParse(message.TopicData.Id, CultureInfo.InvariantCulture, out var sequenceId);
        var messagePublishTime = message.TopicData.CreatedDate;

        var streamState = _failureStateTopic.FindStream(streamId) ?? new StreamState {StreamId = streamId};
        streamState.Topics ??= new();

        var topics = streamState.Topics;
        if (!topics.ContainsKey(topic))
        {
            topics.Add(topic, new TopicState
            {
                TopicName = topic,
                LastSequenceId = sequenceId,
                LastMessagePublishTime = messagePublishTime,
                TimesRetried = 0,
                NextRetry = null,
            });
        }
        else
        {
            var topicState = topics[topic];
            topicState.LastSequenceId = sequenceId;
            topicState.LastMessagePublishTime = messagePublishTime;
        }

        return streamState;
    }

    #endregion Commands
}
