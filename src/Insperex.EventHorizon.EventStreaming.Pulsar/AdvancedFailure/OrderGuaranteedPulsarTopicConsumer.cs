﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Insperex.EventHorizon.Abstractions.Interfaces.Internal;
using Insperex.EventHorizon.Abstractions.Models;
using Insperex.EventHorizon.Abstractions.Util;
using Insperex.EventHorizon.EventStreaming.Interfaces.Streaming;
using Insperex.EventHorizon.EventStreaming.Pulsar.Interfaces;
using Insperex.EventHorizon.EventStreaming.Pulsar.Models;
using Insperex.EventHorizon.EventStreaming.Subscriptions;
using Insperex.EventHorizon.EventStreaming.Util;
using Microsoft.Extensions.Logging;

namespace Insperex.EventHorizon.EventStreaming.Pulsar.AdvancedFailure;

/// <summary>
/// Pulsar topic consumer that uses advanced failure handling method to guarantee message order even
/// if some messages are nacked. Pulsar has no such order guarantees on nack, so the library must
/// enforce order outside of the Pulsar brokers.
/// </summary>
/// <typeparam name="T">Type of message from the primary topic.</typeparam>
public class OrderGuaranteedPulsarTopicConsumer<T> : ITopicConsumer<T> where T : class, ITopicMessage, new()
{
    /// <summary>
    /// The main algorithm handled by this consumer cycles continuously through
    /// the following phases.
    /// </summary>
    private enum BatchPhase
    {
        /// <summary>
        /// Process failed and subsequent messages from streams that are in or are recovering from a failed state.
        /// </summary>
        FailureRetry,

        /// <summary>
        /// Process new messages from the main topic.
        /// </summary>
        Normal
    }

    private readonly SubscriptionConfig<T> _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OrderGuaranteedPulsarTopicConsumer<T>> _logger;
    private readonly IPulsarKeyHashRangeProvider _keyHashRangeProvider;
    private readonly StreamFailureState<T> _streamFailureState;
    private readonly FailedMessageRetryHandler<T> _failedMessageRetryHandler;
    private readonly PrimaryTopicConsumer<T> _primaryTopicConsumer;
    private readonly Dictionary<BatchPhase, ITopicConsumer<T>> _phaseHandlers;
    private readonly OnCheckTimer _statsQueryTimer = new(TimeSpan.FromMinutes(1));

    private readonly string _consumerName = NameUtil.AssemblyNameWithGuid;
    private PulsarKeyHashRanges _keyHashRanges;

    private BatchPhase _phase = BatchPhase.Normal;

    public OrderGuaranteedPulsarTopicConsumer(
        PulsarClientResolver clientResolver,
        SubscriptionConfig<T> config,
        IStreamFactory streamFactory,
        ILoggerFactory loggerFactory,
        IPulsarKeyHashRangeProvider keyHashRangeProvider)
    {
        _logger = loggerFactory.CreateLogger<OrderGuaranteedPulsarTopicConsumer<T>>();

        var admin = (PulsarTopicAdmin<T>)streamFactory.CreateAdmin<T>();
        _config = config;
        _loggerFactory = loggerFactory;
        _keyHashRangeProvider = keyHashRangeProvider;

        FailureStateTopic<T> failureStateTopic = new(_config, clientResolver, admin,
            _loggerFactory.CreateLogger<FailureStateTopic<T>>());
        _streamFailureState = new(_config, _loggerFactory.CreateLogger<StreamFailureState<T>>(),
            failureStateTopic);
        _primaryTopicConsumer = new(_streamFailureState, clientResolver,
            _loggerFactory.CreateLogger<PrimaryTopicConsumer<T>>(),
            _config, admin, _consumerName);
        _failedMessageRetryHandler = new(_config, _streamFailureState, streamFactory,
            clientResolver, _loggerFactory.CreateLogger<FailedMessageRetryHandler<T>>());

        _phaseHandlers = new()
        {
            [BatchPhase.Normal] = _primaryTopicConsumer,
            [BatchPhase.FailureRetry] = _failedMessageRetryHandler,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _primaryTopicConsumer.DisposeAsync();
    }

    public async Task<MessageContext<T>[]> NextBatchAsync(CancellationToken ct)
    {
        _phase = _phase switch
        {
            BatchPhase.FailureRetry => BatchPhase.Normal,
            BatchPhase.Normal => BatchPhase.FailureRetry,
            _ => BatchPhase.Normal,
        };

        await _primaryTopicConsumer.InitializeAsync();
        await _streamFailureState.InitializeAsync(ct);

        if (_keyHashRanges == null)
        {
            // Pause a moment to ensure Pulsar can deliver key hash ranges when we query stats.
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        if (_keyHashRanges == null || ShouldQuerySubscriptionStats())
        {
            _keyHashRanges = await GetSubscriptionHashRanges(ct);
            _failedMessageRetryHandler.KeyHashRanges = _keyHashRanges;
        }

        if (_phase == BatchPhase.FailureRetry)
        {
            try
            {
                var messages = await _failedMessageRetryHandler.NextBatchAsync(ct);
                if (messages.Any())
                {
                    _logger.LogInformation($"Failure retry processing: got {messages.Length} events in batch");
                    return messages;
                }
                _phase = BatchPhase.Normal;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on failure phase batch retrieval");
                throw;
            }
        }

        // Normal phase.
        return await _primaryTopicConsumer.NextBatchAsync(ct);
    }

    public async Task FinalizeBatchAsync(MessageContext<T>[] acks, MessageContext<T>[] nacks)
    {
        await _phaseHandlers[_phase].FinalizeBatchAsync(acks, nacks);
    }

    /// <summary>
    /// Checks whether it's an appropriate time to query stats for the current subscription.
    /// </summary>
    private bool ShouldQuerySubscriptionStats() => _statsQueryTimer.Check();

    /// <summary>
    /// Query the Pulsar admin API for allotted key hash ranges for this consumer.
    /// </summary>
    private async Task<PulsarKeyHashRanges> GetSubscriptionHashRanges(CancellationToken ct)
    {
        return await _keyHashRangeProvider.GetSubscriptionHashRanges(_config.Topics.First(),
            _config.SubscriptionName, _consumerName, ct);
    }
}
