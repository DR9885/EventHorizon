﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Insperex.EventHorizon.Abstractions.Interfaces.Internal;
using Insperex.EventHorizon.EventStreaming.Interfaces.Streaming;
using Insperex.EventHorizon.EventStreaming.Tracing;
using Microsoft.Extensions.Logging;

namespace Insperex.EventHorizon.EventStreaming.Publishers;

public class Publisher<T> : IDisposable
    where T : class, ITopicMessage, new()
{
    private readonly PublisherConfig _config;
    private readonly ILogger<Publisher<T>> _logger;
    private readonly string _typeName;
    private ITopicProducer<T> _producer;

    public Publisher(IStreamFactory factory, PublisherConfig config, ILogger<Publisher<T>> logger)
    {
        _config = config;
        _logger = logger;
        _typeName = typeof(T).Name;
        _producer = factory.CreateProducer<T>(config);
    }

    public void Dispose()
    {
        _producer?.Dispose();
        _producer = null;
    }

    public async Task<Publisher<T>> PublishAsync(params T[] messages)
    {
        // Defensive
        if (messages.Any() != true) return this;

        // Get topic
        using var activity = TraceConstants.ActivitySource.StartActivity();
        activity?.SetTag(TraceConstants.Tags.Count, messages.Length);
        try
        {
            await _producer.SendAsync(messages);
            _logger.LogInformation("Sent {Count} {Type} {Topic}", messages.Length, _typeName, _config.Topic);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Failed to Send {Count} {Type} {Error}",
                messages.Length, _typeName, ex.Message);
            throw;
        }

        return this;
    }
}