﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Insperex.EventHorizon.Abstractions.Models;
using Insperex.EventHorizon.Abstractions.Models.TopicMessages;
using Insperex.EventHorizon.EventStreaming.Interfaces.Streaming;
using Insperex.EventHorizon.EventStreaming.Subscriptions;
using Xunit.Abstractions;

namespace Insperex.EventHorizon.EventStreaming.Test.Shared;

/// <summary>
/// Similar to <see cref="ListStreamConsumer{T}"/> except it deliberately nacks some messages.
/// Exactly how many and how likely to nack is configurable.
/// </summary>
public class PartialNackListStreamConsumer : IStreamConsumer<Event>
{
    private readonly ITestOutputHelper _outputHelper;
    private readonly double _failureRate;
    private readonly int _numStreamsToFail;
    private readonly int _maxNacksPerMessage;
    private readonly int _maxTotalNacks;
    private int _totalNacks;
    private long _totalMessagesProcessed;
    private readonly HashSet<string> _streams = new();
    private readonly HashSet<string> _acceptedMessages = new();
    private readonly Dictionary<string, int> _messageFailures = new();
    private readonly HashSet<string> _failedStreams = new();
    private readonly Random _random = new();

    public PartialNackListStreamConsumer(ITestOutputHelper outputHelper,
        double failureRate, int numStreamsToFail, int maxNacksPerMessage, int maxTotalNacks)
    {
        _outputHelper = outputHelper;
        _failureRate = failureRate;
        _numStreamsToFail = numStreamsToFail;
        _maxNacksPerMessage = maxNacksPerMessage;
        _maxTotalNacks = maxTotalNacks;
    }

    public readonly BlockingCollection<MessageContext<Event>> List = new();

    public Task OnBatch(SubscriptionContext<Event> context)
    {
        foreach (var message in context.Messages)
        {
            if (!_acceptedMessages.Contains(MessageKey(message)))
            {
                _streams.Add(message.Data.StreamId);

                if (ShouldNackMessage(message))
                {
                    context.Nack(message);
                    MarkMessageAsNacked(message);
                }
                else
                {
                    List.Add(message);
                    _acceptedMessages.Add(MessageKey(message));
                }
            }
        }

        _totalMessagesProcessed += context.Messages.Length;

        return Task.CompletedTask;
    }

    public void Report()
    {
        _outputHelper.WriteLine("PartialNackListStreamConsumer REPORT:");
        _outputHelper.WriteLine($" - Total message processing instances: {_totalMessagesProcessed}");
        _outputHelper.WriteLine($" - Total nacks: {_totalNacks}");
        _outputHelper.WriteLine($" - Messages failed at least once: {_messageFailures.Count}");
        _outputHelper.WriteLine($" - Messages in final list: {List.Count}");
        _outputHelper.WriteLine($" - Total streams: {_streams.Count}");
        _outputHelper.WriteLine($" - Streams failed at least once: {_failedStreams.Count}");
    }

    private bool ShouldNackMessage(MessageContext<Event> message)
    {
        if (_totalNacks >= _maxTotalNacks)
        {
            // Too many nacks altogether! We will not nack anything any more.
            return false;
        }
        else if (_messageFailures.GetValueOrDefault(MessageKey(message), 0) >= _maxNacksPerMessage)
        {
            // We've nacked this message too many times. Will not nack again.
            return false;
        }
        else if (_failedStreams.Count < _numStreamsToFail && !_failedStreams.Contains(message.Data.StreamId))
        {
            // Haven't passed our quota of streams to put in failure mode. Fail fail fail!!
            return true;
        }
        else
        {
            // Randomly decide whether to nack according to configured threshold.
            return _random.NextDouble() <= _failureRate;
        }
    }

    private void MarkMessageAsNacked(MessageContext<Event> message)
    {
        _totalNacks++;
        if (_totalNacks >= _maxTotalNacks)
            _outputHelper.WriteLine($"PartialNackListStreamConsumer: reached max nacks ({_maxTotalNacks})");

        var key = MessageKey(message);
        if (_messageFailures.TryGetValue(key, out int nackCount))
        {
            _messageFailures[key] = nackCount + 1;
        }
        else
        {
            _messageFailures.Add(key, 1);
        }

        _failedStreams.Add(message.Data.StreamId);
    }

    private static string MessageKey(MessageContext<Event> message)
    {
        return $"{message.TopicData.Topic}-{message.Data.SequenceId}";
    }
}