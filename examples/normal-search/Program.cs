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

namespace Splunk.Client.Examples
{
    using Splunk.Client.Helpers;

    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Starts a normal search and polls for completion to find out when the search has finished.
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3;
            using (var service = new Service(SdkHelper.Splunk.Scheme, SdkHelper.Splunk.Host, SdkHelper.Splunk.Port, new Namespace(user: "nobody", app: "search")))
            {
                Run(service).Wait();
            }

            Console.Write("Press return to exit: ");
            Console.ReadLine();
        }

        static async Task Run(Service service)
        {
            await service.LogOnAsync(SdkHelper.Splunk.Username, SdkHelper.Splunk.Password);

            //// Search : Pull model (foreach loop => IEnumerable)

            Job job = await service.Jobs.CreateAsync("search index=_internal | head 10");
            SearchResultStream stream;

            using (stream = await job.GetSearchResultsAsync())
            {
                try
                {
                    foreach (SearchResult result in stream)
                    {
                        Console.WriteLine(string.Format("{0:D8}: {1}", stream.ReadCount, result));
                    }
                    
                    Console.WriteLine("End of search results");
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Format("SearchResults error: {0}", e.Message));
                }
            }

            //// Search : Push model (by way of subscription to search result records => IObservable)

            job = await service.Jobs.CreateAsync("search index=_internal | head 10");

            using (stream = await job.GetSearchResultsAsync())
            {
                var manualResetEvent = new ManualResetEvent(false);

                stream.Subscribe(new Observer<SearchResult>(
                    onNext: (result) =>
                    {
                        Console.WriteLine(string.Format("{0:D8}: {1}", stream.ReadCount, result));
                    },
                    onError: (e) =>
                    {
                        Console.WriteLine(string.Format("SearchResults error: {0}", e.Message));
                        manualResetEvent.Set();
                    },
                    onCompleted: () =>
                    {
                        Console.WriteLine("End of search results");
                        manualResetEvent.Set();
                    }));

                manualResetEvent.WaitOne();
            }

            //// Search : Push Model (subscription IObservable)
            {
                var observableStream = service.ExportSearchResultsObservable("search index=_internal | head 10");
                
                var manualResetEvent = new ManualResetEvent(false);
                observableStream.Subscribe(new Observer<SearchResult>(
                    onNext: (result) =>
                    {
                        Console.WriteLine(string.Format("{0:D8}: {1}", stream.ReadCount, result));
                    },
                    onError: (e) =>
                    {
                        Console.WriteLine(string.Format("SearchResults error: {0}", e.Message));
                        manualResetEvent.Set();
                    },
                    onCompleted: () =>
                    {
                        Console.WriteLine("End of search results");
                        manualResetEvent.Set();
                    }));

                manualResetEvent.WaitOne();
            }
        }
    }
}
