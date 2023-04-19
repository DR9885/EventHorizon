﻿namespace Insperex.EventHorizon.Abstractions.Models.TopicMessages;

public enum AggregateStatus
{
    Ok,
    CommandTimedOut,
    LoadSnapshotFailed,
    HandlerFailed,
    BeforeSaveFailed,
    SaveSnapshotFailed,
    SaveEventsFailed,
}