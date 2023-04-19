﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Insperex.EventHorizon.Abstractions.Interfaces.Internal;
using Insperex.EventHorizon.Abstractions.Models;
using Insperex.EventHorizon.EventStreaming.Interfaces.Streaming;
using Insperex.EventHorizon.EventStreaming.Pulsar.Utils;
using Insperex.EventHorizon.EventStreaming.Subscriptions;
using Insperex.EventHorizon.EventStreaming.Tracing;
using Insperex.EventHorizon.EventStreaming.Util;
using Pulsar.Client.Api;
using Pulsar.Client.Common;
using Pulsar.Client.Otel;

namespace Insperex.EventHorizon.EventStreaming.Pulsar;

public class PulsarTopicConsumer<T> : ITopicConsumer<T> where T : ITopicMessage, new()
{
    private readonly PulsarClient _client;
    private readonly SubscriptionConfig<T> _config;
    private readonly OtelConsumerInterceptor.OTelConsumerInterceptor<T> _intercept;
    private IConsumer<T> _consumer;
    private Dictionary<string, MessageId> _messageIdDict;

    public PulsarTopicConsumer(PulsarClient client, SubscriptionConfig<T> config)
    {
        _client = client;
        _config = config;
        _intercept = new OtelConsumerInterceptor.OTelConsumerInterceptor<T>(
            TraceConstants.ActivitySourceName, PulsarClient.Logger);
        _consumer = GetConsumerAsync().Result;
    }

    public async Task<MessageContext<T>[]> NextBatchAsync(CancellationToken ct)
    {
        try
        {
            var messages = await _consumer.BatchReceiveAsync(ct);
            if (!messages.Any())
            {
                await Task.Delay(_config.NoBatchDelay, ct);
                return null;
            }
            
            _messageIdDict = messages
                .Select((x, i) => new { Key = i.ToString(), Value = x.MessageId })
                .ToDictionary(x => x.Key, x => x.Value);

            var topic = _config.Topics.Length == 1 ? _config.Topics.First() : null;
            var contexts =  messages
                .Select((x,i) => new MessageContext<T>
                {
                    Data = x.GetValue(),
                    TopicData = PulsarMessageMapper.MapTopicData(i.ToString(), x, topic ?? x.MessageId.TopicName)
                })
                .ToArray();
        
            return contexts;
        }
        catch (AlreadyClosedException)
        {
            // Ignore AlreadyClosedException
            return null;
        }

    }

    public async Task AckAsync(params MessageContext<T>[] messages)
    {
        if (messages?.Any() != true) return;
        foreach (var message in messages)
            await _consumer.AcknowledgeAsync(_messageIdDict[message.TopicData.Id]);
    }

    public async Task NackAsync(params MessageContext<T>[] messages)
    {
        if (messages?.Any() != true) return;
        foreach (var message in messages)
            await _consumer.NegativeAcknowledge(_messageIdDict[message.TopicData.Id]);
    }

    public void Dispose()
    {
        _consumer.DisposeAsync().GetAwaiter().GetResult();
        _consumer = null;
    }

    private async Task<IConsumer<T>> GetConsumerAsync()
    {
        if (_consumer != null)
            return _consumer;

        var builder = _client.NewConsumer(Schema.JSON<T>())
            .ConsumerName(NameUtil.AssemblyNameWithGuid)
            .SubscriptionType(SubscriptionType.KeyShared)
            .SubscriptionName(_config.SubscriptionName)
            .Intercept(_intercept);

        if (_config.Topics != null)
            builder = _config.Topics.Length == 1
                ? builder.Topic(_config.Topics.First())
                : builder.Topics(_config.Topics);

        if (_config.IsBeginning != null)
            builder = builder.SubscriptionInitialPosition(
                _config.IsBeginning == true
                    ? SubscriptionInitialPosition.Earliest
                    : SubscriptionInitialPosition.Latest);

        if (_config.BatchSize != null)
            builder = builder.ReceiverQueueSize(_config.BatchSize.Value);

        var consumer = await builder.SubscribeAsync();

        if (_config.StartDateTime != null)
            await consumer.SeekAsync(_config.StartDateTime.Value.Ticks);

        // Return
        return _consumer = consumer;
    }
}