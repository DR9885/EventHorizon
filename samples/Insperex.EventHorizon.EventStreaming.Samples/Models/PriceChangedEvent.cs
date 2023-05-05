using Insperex.EventHorizon.Abstractions.Attributes;
using Insperex.EventHorizon.Abstractions.Interfaces;
using Insperex.EventHorizon.Abstractions.Interfaces.Actions;
using Insperex.EventHorizon.Abstractions.Models.TopicMessages;
using Insperex.EventHorizon.EventStreaming.Pulsar.Attributes;

namespace Insperex.EventHorizon.EventStreaming.Samples.Models;

public abstract record PriceChanged(string Id, int Price) : IEvent;


[Stream("feed1")]
[PulsarConfig("test_pricing")]
public record Feed1PriceChanged(string Id, int Price) : PriceChanged (Id, Price);

[Stream("feed2")]
[PulsarConfig("test_pricing")]
public record Feed2PriceChanged(string Id, int Price) : PriceChanged (Id, Price);
