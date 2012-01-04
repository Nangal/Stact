// Copyright 2010 Chris Patterson
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use 
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed 
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace Stact
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using Actors;
    using Actors.Behaviors;
    using Internal;
    using Magnum.Extensions;
    using Routing;


    public class StactActor<TState> :
        Actor<TState>,
        ActorInternals
    {
        readonly ActorBehaviorFactory<TState> _applicatorFactory;
        readonly RoutingEngine _engine;
        readonly Fiber _fiber;
        readonly HashSet<ReceiveHandle> _pending;
        readonly Scheduler _scheduler;
        readonly ActorRef _self;
        readonly TState _state;
        BehaviorHandle _currentBehavior;
        HandlerStack<ActorExceptionHandler> _exceptionHandlers;
        HandlerStack<ActorExitHandler> _exitHandlers;

        public StactActor(Fiber fiber, Scheduler scheduler, ActorBehaviorFactory<TState> applicatorFactory,
                          TState state)
        {
            _fiber = fiber;
            _scheduler = scheduler;
            _applicatorFactory = applicatorFactory;
            _state = state;

            _pending = new HashSet<ReceiveHandle>();

            _exceptionHandlers = new HandlerStack<ActorExceptionHandler>(DefaultExceptionHandler);
            _exitHandlers = new HandlerStack<ActorExitHandler>(DefaultExitHandler);

            _engine = new MessageRoutingEngine();

            _self = new LocalActorReference<TState>(this);

            Receive<Exit>(request => HandleExit);
        }

        public RoutingEngine Engine
        {
            get { return _engine; }
        }

        public ActorRef Self
        {
            get { return _self; }
        }

        public TState State
        {
            get { return _state; }
        }

        public void Send<T>(Message<T> message)
        {
            var kill = message as Message<Kill>;
            if (kill != null)
            {
                HandleKill(kill);
                return;
            }

            _fiber.Add(() => _engine.Send(message));
        }

        public ActorInternals Internals
        {
            get { return this; }
        }

        public void Apply<TBehavior>()
            where TBehavior : class, Behavior<TState>
        {
            if (_currentBehavior != null)
                _currentBehavior.Remove();
            ActorBehavior<TState> applicator = _applicatorFactory.CreateActorBehavior<TBehavior>();

            _currentBehavior = applicator.ApplyTo(this);
        }

        public TimeoutHandle SetTimeout(TimeSpan timeout, Action timeoutCallback)
        {
            ScheduledOperation handle = _scheduler.Schedule(timeout, _fiber, timeoutCallback);

            return new TimeoutHandleImpl(handle);
        }

        public ExceptionHandlerHandle SetExceptionHandler(ActorExceptionHandler exceptionHandler)
        {
            _exceptionHandlers.Push(exceptionHandler);

            ExceptionHandlerHandle handler = new ActorExceptionHandlerHandle(_exceptionHandlers,
                                                                                     exceptionHandler);
            return handler;
        }

        public ExitHandlerHandle SetExitHandler(ActorExitHandler exitHandler)
        {
            _exitHandlers.Push(exitHandler);

            ExitHandlerHandle handler = new ActorExitHandlerHandle(_exitHandlers, exitHandler);
            return handler;
        }

        public ReceiveHandle Receive<T>(SelectiveConsumer<Message<T>> consumer)
        {
            var receive = new ActorReceiveHandle<TState, T>(consumer, x => _pending.Remove(x));

            return Receive(receive);
        }

        public Fiber Fiber
        {
            get { return _fiber; }
        }

        public Scheduler Scheduler
        {
            get { return _scheduler; }
        }

        public void OnError(Exception exception)
        {
            _exceptionHandlers.Enumerate(handlerEnumerator =>
                {
                    NextExceptionHandler toNextHandler = null;
                    toNextHandler = ex =>
                        {
                            if (handlerEnumerator.MoveNext())
                            {
                                ActorExceptionHandler nextHandler = handlerEnumerator.Current;
                                if (nextHandler != null)
                                    nextHandler(ex, toNextHandler);
                            }
                        };

                    toNextHandler(exception);
                });
        }

        void DefaultExceptionHandler(Exception exception, NextExceptionHandler next)
        {
            Debug.WriteLine(
                            string.Format("Exception {0} occurred, exiting...\n{1}",
                                          exception.GetType().ToShortTypeName(), exception));

            _self.Send<Exit>();
        }

        void DefaultExitHandler(Message<Exit> message, NextExitHandler next)
        {
            Debug.WriteLine("Exit requested, exiting...\n");

            if (message.Sender != null)
                _fiber.Add(() => message.Respond(message.Body));

            _engine.Shutdown();
        }

        ReceiveHandle Receive<T>(ActorReceiveHandle<TState, T> receiver)
        {
            _engine.Configure(x => { x.SelectiveReceive<T>(receiver.Accept); });

            _pending.Add(receiver);
            return receiver;
        }

        void HandleExit(Message<Exit> message)
        {
            _exitHandlers.Enumerate(handlerEnumerator =>
                {
                    NextExitHandler toNextHandler = null;
                    toNextHandler = ex =>
                        {
                            if (handlerEnumerator.MoveNext())
                            {
                                ActorExitHandler nextHandler = handlerEnumerator.Current;
                                if (nextHandler != null)
                                    nextHandler(ex, toNextHandler);
                            }
                        };

                    toNextHandler(message);
                });
        }

        void HandleKill(Message<Kill> message)
        {
            ThreadPool.QueueUserWorkItem(x =>
                {
                    try
                    {
                        _fiber.Kill();
                        _engine.Shutdown();
                    }
                    catch
                    {
                    }
                });
        }


        class TimeoutHandleImpl :
            TimeoutHandle
        {
            readonly ScheduledOperation _scheduledOperation;

            public TimeoutHandleImpl(ScheduledOperation scheduledOperation)
            {
                _scheduledOperation = scheduledOperation;
            }

            public void Cancel()
            {
                _scheduledOperation.Cancel();
            }
        }
    }
}