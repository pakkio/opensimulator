using System;
using System.Diagnostics;
using System.Threading.Tasks;
using OpenSim.Services.AssetService;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;

namespace OpenSim.Demo
{
    /// <summary>
    /// Demonstrates the performance benefits of async asset loading
    /// Shows how async prevents thread pool exhaustion under load
    /// </summary>
    public class AsyncDemo
    {
        public static async Task RunDemo()
        {
            Console.WriteLine("=== OpenSim Async Performance Demo ===");
            
            // Simulate 10 concurrent asset requests
            const int concurrentRequests = 10;
            var assetIds = new string[concurrentRequests];
            for (int i = 0; i < concurrentRequests; i++)
            {
                assetIds[i] = Guid.NewGuid().ToString();
            }

            // Mock asset service for demonstration
            IAssetService assetService = new MockAssetService();

            // Test 1: Traditional synchronous approach
            Console.WriteLine("\n1. Synchronous Asset Loading:");
            var syncWatch = Stopwatch.StartNew();
            
            for (int i = 0; i < concurrentRequests; i++)
            {
                var asset = assetService.Get(assetIds[i]);
                Console.WriteLine($"  Asset {i+1}: {(asset != null ? "loaded" : "not found")}");
            }
            
            syncWatch.Stop();
            Console.WriteLine($"  Total time: {syncWatch.ElapsedMilliseconds}ms");
            Console.WriteLine($"  Thread pool status: {System.Threading.ThreadPool.ThreadCount} threads");

            // Test 2: Modern asynchronous approach
            Console.WriteLine("\n2. Asynchronous Asset Loading:");
            var asyncWatch = Stopwatch.StartNew();
            
            var tasks = new Task<AssetBase>[concurrentRequests];
            for (int i = 0; i < concurrentRequests; i++)
            {
                tasks[i] = assetService.GetAsync(assetIds[i]);
            }
            
            var results = await Task.WhenAll(tasks);
            asyncWatch.Stop();
            
            for (int i = 0; i < results.Length; i++)
            {
                Console.WriteLine($"  Asset {i+1}: {(results[i] != null ? "loaded" : "not found")}");
            }
            
            Console.WriteLine($"  Total time: {asyncWatch.ElapsedMilliseconds}ms");
            Console.WriteLine($"  Thread pool status: {System.Threading.ThreadPool.ThreadCount} threads");
            
            // Show improvement
            var improvement = (double)syncWatch.ElapsedMilliseconds / asyncWatch.ElapsedMilliseconds;
            Console.WriteLine($"\n✅ Performance improvement: {improvement:F1}x faster");
            Console.WriteLine("✅ Reduced thread pool pressure");
            Console.WriteLine("✅ Better scalability under load");
        }
    }

    /// <summary>
    /// Mock asset service that simulates database/network delays
    /// </summary>
    public class MockAssetService : IAssetService
    {
        public AssetBase Get(string id)
        {
            // Simulate blocking database call
            System.Threading.Thread.Sleep(100);
            return new AssetBase(id, "test", (sbyte)0, "system");
        }

        public async Task<AssetBase> GetAsync(string id)
        {
            // Simulate non-blocking async database call
            await Task.Delay(100);
            return new AssetBase(id, "test", (sbyte)0, "system");
        }

        // Other interface members (minimal implementation for demo)
        public AssetBase Get(string id, string ForeignAssetService, bool StoreOnLocalGrid) => null;
        public AssetMetadata GetMetadata(string id) => null;
        public byte[] GetData(string id) => null;
        public AssetBase GetCached(string id) => null;
        public bool Get(string id, object sender, AssetRetrieved handler) => false;
        public void Get(string id, string ForeignAssetService, bool StoreOnLocalGrid, SimpleAssetRetrieved callBack) { }
        public bool[] AssetsExist(string[] ids) => new bool[ids.Length];
        public string Store(AssetBase asset) => asset.ID;
        public bool UpdateContent(string id, byte[] data) => false;
        public bool Delete(string id) => false;
    }
}