namespace search_realtime
{
    using Splunk.Client;
    using Splunk.Client.Helpers;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    class Program
    {
        static void Main(string[] args)
        {
            using (var service = new Service(SDKHelper.UserConfigure.scheme, SDKHelper.UserConfigure.host, SDKHelper.UserConfigure.port, new Namespace(user: "nobody", app: "search")))
            {
                Task task = Run(service);
                while (!task.IsCanceled)
                {
                    Task.Delay(500).Wait();
                }
            }
        }

        private static async Task Run(Service service)
        {
            await service.LoginAsync(SDKHelper.UserConfigure.username, SDKHelper.UserConfigure.password);
            Console.WriteLine("Type any key to cancel.");

            string searchQuery = "search index=_internal | stats count by method";

            Job realtimeJob = await service.Jobs.CreateAsync(searchQuery, new JobArgs
            {
                SearchMode = SearchMode.Realtime,
                EarliestTime = "rt-1h",
                LatestTime = "rt",
            });

            var tokenSource = new CancellationTokenSource();

            var task = Task.Run(async () =>
            {
                Console.ReadLine();

                await realtimeJob.CancelAsync();
                tokenSource.Cancel();
            });

            while (!tokenSource.IsCancellationRequested)
            {
                SearchResultStream searchResults;

                searchResults = await realtimeJob.GetSearchResultsPreviewAsync();
                Console.WriteLine("fieldnames:" + searchResults.FieldNames.Count);
                Console.WriteLine("fieldname list:" + string.Join(";", searchResults.FieldNames.ToArray()));

                foreach (var result in searchResults)
                {
                    Console.WriteLine("result:" + result.ToString());
                }

                Console.WriteLine("");
                await Task.Delay(2000, tokenSource.Token);
            }
        }
    }
}