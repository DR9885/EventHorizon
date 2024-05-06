﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventHorizon.Abstractions.Interfaces.Internal;
using EventHorizon.Abstractions.Models;
using EventHorizon.Abstractions.Reflection;
using EventHorizon.EventStreaming.Interfaces.Streaming;
using EventHorizon.EventStreaming.Pulsar.Models;
using EventHorizon.EventStreaming.Pulsar.Utils;
using EventHorizon.EventStreaming.Readers;
using Pulsar.Client.Api;
using Pulsar.Client.Common;
using Range = Pulsar.Client.Api.Range;

namespace EventHorizon.EventStreaming.Pulsar;

public class PulsarTopicReader<TMessage> : ITopicReader<TMessage>
    where TMessage : ITopicMessage
{
    private readonly PulsarClient _pulsarClient;
    private readonly ReaderConfig _config;
    private readonly ITopicAdmin<TMessage> _admin;
    private IReader<TMessage> _reader;

    public PulsarTopicReader(
        PulsarClient pulsarClient,
        ReaderConfig config,
        ITopicAdmin<TMessage> admin)
    {
        _pulsarClient = pulsarClient;
        _config = config;
        _admin = admin;
    }

    public async Task<MessageContext<TMessage>[]> GetNextAsync(int batchSize, TimeSpan timeout)
    {
        var list = new List<MessageContext<TMessage>>();
        var reader = await GetReaderAsync().ConfigureAwait(false);

        Message<TMessage> message;
        do
        {
            try
            {
                var cts = new CancellationTokenSource(timeout);
                message = await reader.ReadNextAsync(cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // ignore
                message = null;
            }

            // Defensive
            if (message == null)
                continue;

            // Note: reader key hashing isn't perfect, need to check here.
            if (_config.Keys != null && !_config.Keys.Contains(message?.Key))
                continue;

            // Stop at EndDateTime
            if (_config.EndDateTime != null
                && PulsarMessageMapper.PublishDateFromTimestamp(message.PublishTime) > _config.EndDateTime)
                break;

            var topicData = PulsarMessageMapper.MapTopicData(list.Count.ToString(CultureInfo.InvariantCulture), message, _config.Topic);
            list.Add(new MessageContext<TMessage>(message.GetValue(), topicData, _config.TypeDict));
        } while (message != null && list.Count < batchSize && await reader.HasMessageAvailableAsync().ConfigureAwait(false));

        return list.ToArray();
    }

    private async Task<IReader<TMessage>> GetReaderAsync()
    {
        if (_reader != null)
            return _reader;

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _admin.RequireTopicAsync(_config.Topic, cts.Token).ConfigureAwait(false);

        var builder = _pulsarClient.NewReader(Schema.JSON<TMessage>())
            .Topic(_config.Topic)
            .ReaderName(AssemblyUtil.AssemblyNameWithGuid)
            .ReceiverQueueSize(1000);

        if (_config.IsBeginning != null)
            builder = builder.StartMessageId(
                _config.IsBeginning == true
                    ? MessageId.Earliest
                    : MessageId.Latest);

        if (_config.Keys?.Any() == true)
            builder = builder.KeyHashRange(_config.Keys
                .Select(x => MurmurHash3.Hash(x) % PulsarTopicConstants.HashKey)
                .Select(x => new Range(x, x))
                .ToArray());

        _reader = await builder.CreateAsync().ConfigureAwait(false);

        // Move After StartDateTime
        if (_config.StartDateTime != null)
            await _reader.SeekAsync(_config.StartDateTime.Value.Ticks).ConfigureAwait(false);

        return _reader;
    }

    public async ValueTask DisposeAsync()
    {
        if(_reader != null) await _reader.DisposeAsync().ConfigureAwait(false);
        _reader = null;
    }
}