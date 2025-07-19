using System;
using System.Diagnostics;
using System.Threading.Tasks;
using OpenSim.Services.AssetService;
using OpenSim.Framework;
using Nini.Config;

namespace OpenSim.Tests
{
    public class AsyncPerformanceTest
    {
        private AssetService assetService;

        public AsyncPerformanceTest()
        {
            var config = new IniConfigSource();
            assetService = new AssetService(config);
        }

        public async Task RunPerformanceComparison()
        {
            const int iterations = 100;
            var assetIds = GenerateTestAssetIds(iterations);

            // Test synchronous performance
            var syncStopwatch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var asset = assetService.Get(assetIds[i]);
            }
            syncStopwatch.Stop();

            // Test asynchronous performance
            var asyncStopwatch = Stopwatch.StartNew();
            var tasks = new Task<AssetBase>[iterations];
            for (int i = 0; i < iterations; i++)
            {
                tasks[i] = assetService.GetAsync(assetIds[i]);
            }
            await Task.WhenAll(tasks);
            asyncStopwatch.Stop();

            Console.WriteLine($"Sync time: {syncStopwatch.ElapsedMilliseconds}ms");
            Console.WriteLine($"Async time: {asyncStopwatch.ElapsedMilliseconds}ms");
            Console.WriteLine($"Improvement: {(float)syncStopwatch.ElapsedMilliseconds / asyncStopwatch.ElapsedMilliseconds:F2}x");
        }

        private string[] GenerateTestAssetIds(int count)
        {
            var ids = new string[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = Guid.NewGuid().ToString();
            }
            return ids;
        }
    }
}