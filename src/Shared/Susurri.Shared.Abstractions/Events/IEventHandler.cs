﻿namespace Susurri.Shared.Abstractions.Events;

public interface IEventHandler<in TEvent> where TEvent : class, IEvent
{
    Task HandleAsync(TEvent @event);
}