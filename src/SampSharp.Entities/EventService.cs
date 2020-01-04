﻿// SampSharp
// Copyright 2019 Tim Potze
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SampSharp.Core;
using SampSharp.Core.Logging;
using SampSharp.Entities.Utilities;

namespace SampSharp.Entities
{
    /// <summary>
    /// Represents the event service.
    /// </summary>
    /// <seealso cref="IEventService" />
    public class EventService : IEventService
    {
        private static readonly Type[] DefaultParameterTypes =
        {
            typeof(int),
            typeof(bool),
            typeof(float),
            typeof(string),
            typeof(Entity)
            // TODO: Callbacks with parameter length are not yet supported
            //typeof(int[]),
            //typeof(bool[]),
            //typeof(float[]),
        };

        private static readonly MethodInfo GetComponentInfo =
            typeof(Entity).GetMethod(nameof(Entity.GetComponent), BindingFlags.Public | BindingFlags.Instance);

        private static readonly MethodInfo GetServiceInfo =
            typeof(EventService).GetMethod(nameof(GetService), BindingFlags.NonPublic | BindingFlags.Static);

        private readonly Dictionary<string, Event> _events = new Dictionary<string, Event>();
        private readonly IGameModeClient _gameModeClient;
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventService" /> class.
        /// </summary>
        public EventService(IGameModeClient gameModeClient, IServiceProvider serviceProvider)
        {
            _gameModeClient = gameModeClient;
            _serviceProvider = serviceProvider;

            CreateEventsFromAssemblies();
        }

        /// <inheritdoc />
        public void EnableEvent(string name, Type[] parameters)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            var handler = BuildInvoke(name);
            _gameModeClient.RegisterCallback(name, handler.Target, handler.Method, parameters);
        }

        /// <inheritdoc />
        public void UseMiddleware(string name, Func<EventDelegate, EventDelegate> middleware)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (middleware == null) throw new ArgumentNullException(nameof(middleware));

            if (!_events.TryGetValue(name, out var @event))
                _events[name] = @event = new Event(Invoke);

            @event.Middleware.Add(middleware);

            // In order to chain the middleware from first to last, the middleware must be nested from last to first
            EventDelegate invoke = Invoke;
            for (var i = @event.Middleware.Count - 1; i >= 0; i--)
            {
                invoke = @event.Middleware[i](invoke);
            }

            @event.Invoke = invoke;
        }

        private object Invoke(EventContext context)
        {
            object result = null;

            if (context.Name == null || !_events.TryGetValue(context.Name, out var evt)) return null;

            foreach (var sysEvt in evt.Invokers)
            {
                var system = _serviceProvider.GetService(sysEvt.TargetType);

                // System is not loaded. Skip invoking target.
                if (system == null)
                    continue;

                result = sysEvt.Invoke(system, context) ?? result;
            }

            return result;
        }

        private void CreateEventsFromAssemblies()
        {
            // Find methods with EventAttribute in any ISystem in any assembly.
            var events = MethodScanner.Create()
                .IncludeAllAssemblies()
                .IncludeNonPublicMethods()
                .Implements<ISystem>()
                .Scan<EventAttribute>();

            // Gather event data, compile invoker and add the data to the events collection.
            foreach (var (method, attribute) in events)
            {
                CoreLog.LogDebug("Adding event listener on {0}.{1}.", method.DeclaringType, method.Name);

                var name = attribute.Name ?? method.Name;

                if (!_events.TryGetValue(name, out var @event))
                    _events[name] = @event = new Event(Invoke);

                var argsPtr = 0; // The current pointer in the event arguments array.
                var parameterSources = method.GetParameters()
                    .Select(info => new ParameterSource {Info = info})
                    .ToArray();

                // Determine the source of each parameter.
                foreach (var source in parameterSources)
                {
                    var type = source.Info.ParameterType;

                    if (typeof(Component).IsAssignableFrom(type))
                    {
                        // Components are provided by the entity in the arguments array of the event.
                        source.ParameterIndex = argsPtr++;
                        source.ComponentType = type;
                    }
                    else if (DefaultParameterTypes.Contains(type))
                    {
                        // Default types are passed straight trough.
                        source.ParameterIndex = argsPtr++;
                    }
                    else
                    {
                        // Other types are provided trough Dependency Injection.
                        source.ServiceType = type;
                        @event.HasDependencies = true;
                    }
                }

                var invoker = CreateInvoker(method, parameterSources, argsPtr);
                @event.Invokers.Add(invoker);
            }
        }

        private InvokerInfo CreateInvoker(MethodInfo method, ParameterSource[] parameterInfos, int callbackParamCount)
        {
            var compiled = Compile(method, parameterInfos);

            return new InvokerInfo
            {
                TargetType = method.DeclaringType,
                Invoke = (instance, eventContext) =>
                {
                    var args = eventContext.Arguments;
                    if (callbackParamCount != args.Length)
                    {
                        CoreLog.Log(CoreLogLevel.Warning,
                            $"Callback parameter count mismatch {callbackParamCount} != {args.Length}");
                        return null;
                    }

                    return compiled(instance, args, eventContext);
                }
            };
        }

        private Func<object[], object> BuildInvoke(string name)
        {
            var context = new EventContextImpl();
            context.SetName(name);
            
            return args =>
            {
                context.SetArguments(args);

                if (!_events.TryGetValue(name, out var @event))
                    return null;

                IServiceScope scope = null;
                if (@event.HasDependencies)
                {
                     scope = _serviceProvider.CreateScope();
                     context.SetEventServices(scope.ServiceProvider);
                }
                else
                {
                    context.SetEventServices(_serviceProvider);
                }

                if (@event.Invoke == null)
                {
                    // In order to chain the middleware from start to end, the middleware must be nested from end to beginning
                    EventDelegate invoke = Invoke;
                    for (var i = @event.Middleware.Count - 1; i >= 0; i--)
                    {
                        invoke = @event.Middleware[i](invoke);
                    }

                    @event.Invoke = invoke;
                }

                var result = @event.Invoke(context);

                scope?.Dispose();

                return result;
            };
        }

        private static Func<object, object[], EventContext, object> Compile(MethodInfo methodInfo,
            ParameterSource[] parameterSources)
        {
            if (methodInfo.DeclaringType == null)
                throw new ArgumentException("Method must have declaring type", nameof(methodInfo));

            // Input arguments
            var instanceArg = Expression.Parameter(typeof(object), "instance");
            var argsArg = Expression.Parameter(typeof(object[]), "args");
            var eventContextArg = Expression.Parameter(typeof(EventContext), "eventContext");

            var methodArguments = new Expression[parameterSources.Length];
            for (var i = 0; i < parameterSources.Length; i++)
            {
                var parameterType = parameterSources[i].Info.ParameterType;
                if (parameterType.IsByRef) throw new NotSupportedException();

                if (parameterSources[i].ComponentType != null)
                {
                    // Get component from entity
                    Expression index = Expression.Constant(parameterSources[i].ParameterIndex);

                    Expression getValue = Expression.ArrayIndex(argsArg, index);
                    getValue = Expression.Convert(getValue, typeof(Entity));
                    methodArguments[i] = Expression.Call(getValue,
                        GetComponentInfo.MakeGenericMethod(parameterSources[i].ComponentType));
                }
                else if (parameterSources[i].ServiceType != null)
                {
                    // Get service
                    var getServiceCall = Expression.Call(GetServiceInfo, eventContextArg,
                        Expression.Constant(parameterType, typeof(Type)));
                    methodArguments[i] = Expression.Convert(getServiceCall, parameterType);
                }
                else if (parameterSources[i].ParameterIndex >= 0)
                {
                    // Pass through
                    Expression index = Expression.Constant(parameterSources[i].ParameterIndex);

                    var getValue = Expression.ArrayIndex(argsArg, index);
                    methodArguments[i] = Expression.Convert(getValue, parameterType);
                }
            }

            var service = Expression.Convert(instanceArg, methodInfo.DeclaringType);
            Expression body = Expression.Call(service, methodInfo, methodArguments);

            if (body.Type == typeof(void))
                body = Expression.Block(body, Expression.Constant(null));
            else if (body.Type != typeof(object)) body = Expression.Convert(body, typeof(object));

            var lambda =
                Expression.Lambda<Func<object, object[], EventContext, object>>(body, instanceArg, argsArg,
                    eventContextArg);

            return lambda.Compile();
        }

        private static object GetService(EventContext eventContext, Type type)
        {
            var service = eventContext.EventServices.GetService(type);
            return service ?? throw new InvalidOperationException();
        }

        private class Event
        {
            public readonly List<InvokerInfo> Invokers = new List<InvokerInfo>();

            public readonly List<Func<EventDelegate, EventDelegate>> Middleware =
                new List<Func<EventDelegate, EventDelegate>>();

            public Event(EventDelegate invoke)
            {
                Invoke = invoke;
            }

            public EventDelegate Invoke;

            public bool HasDependencies;
        }

        private class InvokerInfo
        {
            public Func<object, EventContext, object> Invoke;
            public Type TargetType;
        }

        private class ParameterSource
        {
            public Type ComponentType;
            public ParameterInfo Info;
            public int ParameterIndex = -1;
            public Type ServiceType;
        }
    }
}