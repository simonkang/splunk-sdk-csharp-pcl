﻿/*
 * Copyright 2014 Splunk, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"): you may
 * not use this file except in compliance with the License. You may obtain
 * a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations
 * under the License.
 */

//// TODO:
//// [O] Contracts
//// [O] Documentation

namespace Splunk.Client
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml;

    /// <summary>
    /// Represents an enumerable, observable stream of <see cref="SearchResult"/> 
    /// records.
    /// </summary>
    public sealed class SearchResultStream : Observable<SearchResult>, IDisposable, IEnumerable<SearchResult>
    {
        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="SearchResultStream"/> class.
        /// </summary>
        /// <param name="response">
        /// The object for reading search results.
        /// </param>
        SearchResultStream(Response response)
        {
            this.cancellationTokenSource = new CancellationTokenSource();
            this.metadata = Metadata.Missing;
            this.response = response;

            this.resultAwaiter = new SearchResultAwaiter(this, cancellationTokenSource.Token);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether the current <see cref="SearchResultStream"/> 
        /// is yielding the final results from a search job.
        /// </summary>
        public bool IsFinal
        {
            get { return this.metadata.IsFinal; }
        }

        /// <summary>
        /// Gets the read-only list of field names that may appear in a 
        /// <see cref="SearchResult"/>.
        /// </summary>
        /// <remarks>
        /// Be aware that any given result may contain a subset of these fields.
        /// </remarks>
        public IReadOnlyList<string> FieldNames
        {
            get { return this.metadata.FieldNames; }
        }

        /// <summary>
        /// Gets the <see cref="SearchResult"/> read count for the current
        /// <see cref="SearchResultStream"/>.
        /// </summary>
        public long ReadCount
        {
            get { return this.resultAwaiter.ReadCount; }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Asynchronously creates a new <see cref="SearchResultStream"/> 
        /// instance using the specified <see cref="Response"/>.
        /// </summary>
        /// <param name="response">
        /// An object from which the search results are read. 
        /// </param>
        /// <returns>
        /// A <see cref="SearchResultStream"/> object.
        /// </returns>
        internal static async Task<SearchResultStream> CreateAsync(Response response)
        {
            XmlReader reader = response.XmlReader;

            await reader.MoveToDocumentElementAsync("results", "response");

            if (reader.Name == "response")
            {
                await response.ThrowRequestExceptionAsync();
            }

            var stream = new SearchResultStream(response);
            return stream;
        }

        /// <summary>
        /// Releases all disposable resources used by the current <see cref="SearchResultStream"/>.
        /// </summary>
        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            if (this.resultAwaiter.IsEnumerating)
            {
                this.cancellationTokenSource.Cancel();
                this.resultAwaiter.Wait();
            }

            this.cancellationTokenSource.Dispose();
            this.response.Dispose();
            this.disposed = true;
        }

        /// <summary>
        /// Returns an enumerator that iterates through search result <see 
        /// cref="Result"/> objects asynchronously.
        /// </summary>
        /// <returns>
        /// A <see cref="SearchResult"/> enumerator structure for the <see 
        /// cref="SearchResultStream"/>.
        /// </returns>
        /// <remarks>
        /// You can use the <see cref="GetEnumerator"/> method to
        /// <list type="bullet">
        /// <item><description>
        ///   Perform LINQ to Objects queries to obtain a filtered set of 
        ///   search result records.</description></item>
        /// <item><description>
        ///   Append search results to an existing <see cref="Result"/>
        ///   collection.</description></item>
        /// </list>
        /// </remarks>
        public IEnumerator<SearchResult> GetEnumerator()
        {
            this.EnsureNotDisposed();

            for (SearchResult result; this.resultAwaiter.TryTake(out result); )
            {
                yield return result;
            }
        }

        /// <inheritdoc cref="GetEnumerator">
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        /// <summary>
        /// Asynchronously pushes <see cref="SearchResult"/> objects to observers 
        /// and then completes.
        /// </summary>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        protected override async Task PushObservations()
        {
            this.EnsureNotDisposed();

            for (var result = await this.resultAwaiter; result != null; result = await this.resultAwaiter)
            {
                this.OnNext(result);
            }

            this.OnCompleted();
        }

        #endregion

        #region Privates/internals

        readonly CancellationTokenSource cancellationTokenSource;
        readonly SearchResultAwaiter resultAwaiter;
        readonly Response response;

        volatile Metadata metadata;
        bool disposed;

        ReadState ReadState
        {
            get { return this.response.XmlReader.ReadState; }
        }

        void EnsureNotDisposed()
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException("Search result stream");
            }
        }

        async Task ReadMetadataAsync()
        {
            var metadata = new Metadata();

            await metadata.ReadXmlAsync(this.response.XmlReader);
            this.metadata = metadata;
        }

        async Task<SearchResult> ReadResultAsync()
        {
            var reader = this.response.XmlReader;

            if (reader.NodeType == XmlNodeType.Element && reader.Name == "results")
            {
                return null;
            }

            Debug.Assert(reader.ReadState <= ReadState.Interactive);
            reader.MoveToElement();

            reader.EnsureMarkup(XmlNodeType.Element, "result");

            var result = new SearchResult();
            await result.ReadXmlAsync(reader);
            await reader.ReadAsync();

            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "results")
            {
                await reader.ReadAsync();
            }
            else
            {
                reader.EnsureMarkup(XmlNodeType.Element, "result");
            }

            return result;
        }

        #endregion

        #region Types

        sealed class Metadata
        {
            #region Fields

            public static readonly Metadata Missing = new Metadata()
            {
                FieldNames = new ReadOnlyCollection<string>(new List<string>()),
            };

            #endregion

            #region Properties

            /// <summary>
            /// Gets a value indicating whether this <see cref="SearchPreview"/> 
            /// contains the final results from a search job.
            /// </summary>
            public bool IsFinal
            { get; private set; }

            /// <summary>
            /// Gets the read-only list of field names that may appear in a 
            /// <see cref="SearchResult"/>.
            /// </summary>
            /// <remarks>
            /// Be aware that any given result will contain a subset of these 
            /// fields.
            /// </remarks>
            public IReadOnlyList<string> FieldNames
            { get; private set; }

            #endregion

            #region Methods

            /// <summary>
            /// Asynchronously reads data into the current <see cref="SearchResultStream"/>.
            /// </summary>
            /// <param name="reader">
            /// The <see cref="XmlReader"/> from which to read.
            /// </param>
            /// <returns>
            /// A <see cref="Task"/> representing the operation.
            /// </returns>
            public async Task ReadXmlAsync(XmlReader reader)
            {
                var fieldNames = new List<string>();

                this.FieldNames = fieldNames;
                this.IsFinal = true;

                if (!await reader.MoveToDocumentElementAsync("results"))
                {
                    return;
                }

                string preview = reader.GetRequiredAttribute("preview");
                this.IsFinal = !BooleanConverter.Instance.Convert(preview);

                if (!await reader.ReadAsync())
                {
                    return;
                }

                reader.EnsureMarkup(XmlNodeType.Element, "meta");
                await reader.ReadAsync();
                reader.EnsureMarkup(XmlNodeType.Element, "fieldOrder");

                await reader.ReadEachDescendantAsync("field", async (r) =>
                {
                    await r.ReadAsync();
                    var fieldName = await r.ReadContentAsStringAsync();
                    fieldNames.Add(fieldName);
                });

                await reader.ReadEndElementSequenceAsync("fieldOrder", "meta");

                if (reader.NodeType == XmlNodeType.Element && reader.Name == "messages")
                {
                    //// Skip messages

                    await reader.ReadEachDescendantAsync("msg", (r) =>
                    {
                        return Task.FromResult(true);
                    });

                    reader.EnsureMarkup(XmlNodeType.EndElement, "messages");
                    await reader.ReadAsync();
                }
            }

            #endregion
        }

        sealed class SearchResultAwaiter : INotifyCompletion
        {
            #region Constructors

            public SearchResultAwaiter(SearchResultStream stream, CancellationToken token)
            {
                this.task = new Task(this.ReadResultsAsync, token);
                this.cancellationToken = token;
                this.stream = stream;
                this.readCount = 0;
                this.task.Start();
            }

            #endregion

            #region Properties

            public bool IsEnumerating
            {
                get { return this.enumerated < 2; }
            }

            public long ReadCount
            {
                get { return Interlocked.Read(ref this.readCount); }
            }
            
            #endregion

            #region Methods supporting IDisposable and IEnumerable<SearchResult>

            /// <summary>
            /// 
            /// </summary>
            /// <param name="result"></param>
            /// <returns></returns>
            public bool TryTake(out SearchResult result)
            {
                result = this.AwaitResultAsync().Result;
                return result != null;
            }

            /// <summary>
            /// 
            /// </summary>
            public void Wait()
            {
                this.task.Wait();
            }

            #endregion

            #region Members called by the async await state machine

            //// The async await state machine requires that you implement 
            //// INotifyCompletion and provide three additional members: 
            //// 
            ////     IsCompleted property
            ////     GetAwaiter method
            ////     GetResult method
            ////
            //// INotifyCompletion itself defines just one member:
            ////
            ////     OnCompletion method
            ////
            //// See Jeffrey Richter's excellent discussion of the topic of 
            //// awaiters in CLR via C# (4th Edition).

            /// <summary>
            /// Tells the state machine if any results are available.
            /// </summary>
            public bool IsCompleted
            {
                get
                {
                    bool result = this.enumerated == 2 || this.results.Count > 0;
                    return result;
                }
            }

            /// <summary>
            /// Returns the current <see cref="SearchResultAwaiter"/> to the
            /// state machine.
            /// </summary>
            /// <returns>
            /// A reference to the current <see cref="SearchResultAwaiter"/>.
            /// </returns>
            public SearchResultAwaiter GetAwaiter()
            { return this; }

            /// <summary>
            /// Returns the current <see cref="SearchResult"/> from the current
            /// <see cref="SearchResultStream"/>.
            /// </summary>
            /// <returns>
            /// The current <see cref="SearchResult"/> or <c>null</c>.
            /// </returns>
            public SearchResult GetResult()
            {
                SearchResult result = null;

                results.TryDequeue(out result);
                return result;
            }

            /// <summary>
            /// Tells the current <see cref="SearchResultAwaiter"/> what method
            /// to invoke on completion.
            /// </summary>
            /// <param name="continuation">
            /// The method to call on completion.
            /// </param>
            public void OnCompleted(Action continuation)
            {
                Volatile.Write(ref this.continuation, continuation);
            }

            #endregion

            #region Privates/internals

            ConcurrentQueue<SearchResult> results = new ConcurrentQueue<SearchResult>();
            CancellationToken cancellationToken;
            SearchResultStream stream;
            Action continuation;
            int enumerated;
            long readCount;
            Task task;

            async Task<SearchResult> AwaitResultAsync()
            {
                return await this;
            }

            void Continue()
            {
                Action continuation = Interlocked.Exchange(ref this.continuation, null);

                if (continuation != null)
                {
                    continuation();
                }

                this.cancellationToken.ThrowIfCancellationRequested();
            }

            async void ReadResultsAsync()
            {
                if (Interlocked.CompareExchange(ref this.enumerated, 1, 0) != 0)
                {
                    throw new InvalidOperationException("Stream has been enumerated; The enumeration operation may not execute again.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                await this.stream.ReadMetadataAsync();

                while (this.stream.ReadState <= ReadState.Interactive)
                {
                    this.cancellationToken.ThrowIfCancellationRequested();

                    while (this.stream.ReadState <= ReadState.Interactive)
                    {
                        SearchResult result = await this.stream.ReadResultAsync();

                        if (result == null)
                        {
                            break;
                        }

                        Interlocked.Increment(ref this.readCount);
                        this.results.Enqueue(result);
                        this.Continue();
                    }

                    await stream.ReadMetadataAsync();
                }

                this.Continue();
                enumerated = 2;
            }

            #endregion
        }

        #endregion
    }
}
