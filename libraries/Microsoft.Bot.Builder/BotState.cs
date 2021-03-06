﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Microsoft.Bot.Builder
{
    /// <summary>
    /// Reads and writes state for your bot to storage.
    /// </summary>
    public abstract class BotState : IMiddleware, IPropertyManager
    {
        private readonly string _contextServiceKey;
        private readonly IStorage _storage;

        /// <summary>
        /// Initializes a new instance of the <see cref="BotState"/> class.
        /// </summary>
        /// <param name="storage">The storage provider to use.</param>
        /// <param name="contextServiceKey">the key for caching on the context services dictionary.</param>
        public BotState(IStorage storage, string contextServiceKey)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _contextServiceKey = contextServiceKey ?? throw new ArgumentNullException(nameof(contextServiceKey));
        }

        /// <summary>
        /// Create a property definition and register it with this BotState.
        /// </summary>
        /// <typeparam name="T">type of property.</typeparam>
        /// <param name="name">name of the property.</param>
        /// <returns>The created state property accessor.</returns>
        public IStatePropertyAccessor<T> CreateProperty<T>(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            return new BotStatePropertyAccessor<T>(this, name);
        }

        /// <summary>
        /// Processess an incoming activity.
        /// </summary>
        /// <param name="turnContext">The context object for this turn.</param>
        /// <param name="next">The delegate to call to continue the bot middleware pipeline.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        /// <remarks>This middleware loads the state object on the leading edge of the middleware pipeline
        /// and persists the state object on the trailing edge. Note this is different than BotStateSet,
        /// which does not pre-load the set on entry into the pipeline.
        /// </remarks>
        public async Task OnTurnAsync(ITurnContext turnContext, NextDelegate next, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (turnContext == null)
            {
                throw new ArgumentNullException(nameof(turnContext));
            }

            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            // Load state
            await LoadAsync(turnContext, true, cancellationToken).ConfigureAwait(false);

            // process activity
            await next(cancellationToken).ConfigureAwait(false);

            // Save changes
            await SaveChangesAsync(turnContext, false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads in  the current state object and caches it in the context object for this turm.
        /// </summary>
        /// <param name="turnContext">The context object for this turn.</param>
        /// <param name="force">Optional. True to bypass the cache.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        public async Task LoadAsync(ITurnContext turnContext, bool force = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (turnContext == null)
            {
                throw new ArgumentNullException(nameof(turnContext));
            }

            var cachedState = turnContext.TurnState.Get<CachedBotState>(_contextServiceKey);
            var storageKey = GetStorageKey(turnContext);
            if (force || cachedState == null || cachedState.State == null)
            {
                var items = await _storage.ReadAsync(new[] { storageKey }, cancellationToken).ConfigureAwait(false);
                items.TryGetValue(storageKey, out object val);
                turnContext.TurnState[_contextServiceKey] = new CachedBotState((IDictionary<string, object>)val ?? new Dictionary<string, object>());
            }
        }

        /// <summary>
        /// If it has changed, writes to storage the state object that is cached in the current context object for this turn.
        /// </summary>
        /// <param name="turnContext">The context object for this turn.</param>
        /// <param name="force">Optional. True to save state to storage whether or not there are changes.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        public async Task SaveChangesAsync(ITurnContext turnContext, bool force = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (turnContext == null)
            {
                throw new ArgumentNullException(nameof(turnContext));
            }

            var cachedState = turnContext.TurnState.Get<CachedBotState>(_contextServiceKey);
            if (force || (cachedState != null && cachedState.IsChanged()))
            {
                var key = GetStorageKey(turnContext);
                var changes = new Dictionary<string, object>
                {
                    { key, cachedState.State },
                };
                await _storage.WriteAsync(changes).ConfigureAwait(false);
                cachedState.Hash = cachedState.ComputeHash(cachedState.State);
                return;
            }
        }

        /// <summary>
        /// Reset the state cache in the turn context to it's default form.
        /// </summary>
        /// <param name="turnContext">The context object for this turn.</param>
        /// <param name="cancellationToken">cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task ClearStateAsync(ITurnContext turnContext, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (turnContext == null)
            {
                throw new ArgumentNullException(nameof(turnContext));
            }

            var cachedState = turnContext.TurnState.Get<CachedBotState>(_contextServiceKey);
            if (cachedState != null)
            {
                turnContext.TurnState[_contextServiceKey] = new CachedBotState();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// When overridden in a derived class, gets the key to use when reading and writing state to and from storage.
        /// </summary>
        /// <param name="turnContext">The context object for this turn.</param>
        /// <returns>The storage key.</returns>
        protected abstract string GetStorageKey(ITurnContext turnContext);

        /// <summary>
        /// Gets a property from the state cache in the turn context.
        /// </summary>
        /// <typeparam name="T">The property type.</typeparam>
        /// <param name="turnContext">The context object for this turn.</param>
        /// <param name="propertyName">The name of the property to get.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        /// <remarks>If the task is successful, the result contains the property value.</remarks>
        protected Task<T> GetPropertyValueAsync<T>(ITurnContext turnContext, string propertyName, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (turnContext == null)
            {
                throw new ArgumentNullException(nameof(turnContext));
            }

            if (propertyName == null)
            {
                throw new ArgumentNullException(nameof(propertyName));
            }

            var cachedState = turnContext.TurnState.Get<CachedBotState>(_contextServiceKey);

            // if there is no value, this will throw, to signal to IPropertyAccesor that a default value should be computed
            // This allows this to work with value types
            return Task.FromResult((T)cachedState.State[propertyName]);
        }

        /// <summary>
        /// Deletes a property from the state cache in the turn context.
        /// </summary>
        /// <param name="turnContext">The context object for this turn.</param>
        /// <param name="propertyName">The name of the property to delete.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        protected Task DeletePropertyValueAsync(ITurnContext turnContext, string propertyName, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (turnContext == null)
            {
                throw new ArgumentNullException(nameof(turnContext));
            }

            if (propertyName == null)
            {
                throw new ArgumentNullException(nameof(propertyName));
            }

            var cachedState = turnContext.TurnState.Get<CachedBotState>(_contextServiceKey);
            cachedState.State.Remove(propertyName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Set the value of a property in the state cache in the turn context.
        /// </summary>
        /// <param name="turnContext">The context object for this turn.</param>
        /// <param name="propertyName">The name of the property to set.</param>
        /// <param name="value">The value to set on the property.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        protected Task SetPropertyValueAsync(ITurnContext turnContext, string propertyName, object value, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (turnContext == null)
            {
                throw new ArgumentNullException(nameof(turnContext));
            }

            if (propertyName == null)
            {
                throw new ArgumentNullException(nameof(propertyName));
            }

            var cachedState = turnContext.TurnState.Get<CachedBotState>(_contextServiceKey);
            cachedState.State[propertyName] = value;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Internal cached bot state.
        /// </summary>
        private class CachedBotState
        {
            public CachedBotState(IDictionary<string, object> state = null)
            {
                State = state ?? new Dictionary<string, object>();
                Hash = ComputeHash(State);
            }

            public IDictionary<string, object> State { get; set; }

            public string Hash { get; set; }

            public bool IsChanged()
            {
                return Hash != ComputeHash(State);
            }

            internal string ComputeHash(object obj)
            {
                return JsonConvert.SerializeObject(obj);
            }
        }

        /// <summary>
        /// Implements IPropertyAccessor for an IPropertyContainer.
        /// </summary>
        /// <typeparam name="T">type of value the propertyAccessor accesses.</typeparam>
        private class BotStatePropertyAccessor<T> : IStatePropertyAccessor<T>
        {
            private BotState _botState;

            public BotStatePropertyAccessor(BotState botState, string name)
            {
                _botState = botState;
                Name = name;
            }

            /// <summary>
            /// Gets name of the property.
            /// </summary>
            /// <value>
            /// name of the property.
            /// </value>
            public string Name { get; private set; }

            /// <summary>
            /// Delete the property.
            /// </summary>
            /// <param name="turnContext">The turn context.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
            public Task DeleteAsync(ITurnContext turnContext, CancellationToken cancellationToken)
            {
                return _botState.DeletePropertyValueAsync(turnContext, Name, cancellationToken);
            }

            /// <summary>
            /// Get the property value.
            /// </summary>
            /// <param name="turnContext">The context object for this turn.</param>
            /// <param name="defaultValueFactory">Defines the default value. Invoked when no value been set for the requested state property.  If defaultValueFactory is defined as null, the MissingMemberException will be thrown if the underlying property is not set.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
            public async Task<T> GetAsync(ITurnContext turnContext, Func<T> defaultValueFactory, CancellationToken cancellationToken)
            {
                await _botState.LoadAsync(turnContext, false, cancellationToken).ConfigureAwait(false);
                try
                {
                    return await _botState.GetPropertyValueAsync<T>(turnContext, Name, cancellationToken).ConfigureAwait(false);
                }
                catch (KeyNotFoundException)
                {
                    // ask for default value from factory
                    if (defaultValueFactory == null)
                    {
                        throw new MissingMemberException("Property not set and no default provided.");
                    }

                    var result = defaultValueFactory();

                    // save default value for any further calls
                    await SetAsync(turnContext, result, cancellationToken).ConfigureAwait(false);
                    return result;
                }
            }

            /// <summary>
            /// Set the property value.
            /// </summary>
            /// <param name="turnContext">turn context.</param>
            /// <param name="value">value.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
            public async Task SetAsync(ITurnContext turnContext, T value, CancellationToken cancellationToken)
            {
                await _botState.LoadAsync(turnContext, false, cancellationToken).ConfigureAwait(false);
                await _botState.SetPropertyValueAsync(turnContext, Name, value, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
