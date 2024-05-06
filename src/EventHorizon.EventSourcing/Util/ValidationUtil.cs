﻿using System.Linq;
using EventHorizon.Abstractions.Exceptions;
using EventHorizon.Abstractions.Interfaces;
using EventHorizon.Abstractions.Interfaces.Actions;
using EventHorizon.Abstractions.Interfaces.Handlers;
using EventHorizon.Abstractions.Reflection;
using EventHorizon.EventStore.Interfaces;
using EventHorizon.EventStore.Models;

namespace EventHorizon.EventSourcing.Util;

public class ValidationUtil
{
    public void Validate<T, TS>()
        where T : class, IStateParent<TS>
        where TS : class, IState
    {
        if(typeof(T) == typeof(Snapshot<TS>))
            ValidateSnapshot<TS>();

        if(typeof(T) == typeof(View<TS>))
            ValidateView<TS>();
    }

    public void ValidateSnapshot<TState>()
        where TState : IState
    {
        var type = typeof(TState);
        var types = new[] { type };
        var stateDetail = ReflectionFactory.GetStateDetail(type);

        var commandErrors = stateDetail.Validate<ICommand>(typeof(IHandleCommand<>),"Handle");
        var requestErrors = stateDetail.Validate<IRequest>(typeof(IHandleRequest<,>),"Handle");
        var eventErrors = stateDetail.Validate<IEvent>(typeof(IApplyEvent<>),"Apply");

        var errors = commandErrors.Concat(requestErrors).Concat(eventErrors).ToArray();
        if (!errors.Any()) return;

        throw new MissingHandlersException(type, stateDetail.SubStates, errors);
    }

    public void ValidateView<TState>()
        where TState : IState
    {
        var type = typeof(TState);
        var stateDetail = ReflectionFactory.GetStateDetail(type);
        var eventErrors = stateDetail.Validate<IEvent>(typeof(IApplyEvent<>),"Apply");
        if (!eventErrors.Any()) return;

        throw new MissingHandlersException(type, stateDetail.SubStates, eventErrors);
    }
}