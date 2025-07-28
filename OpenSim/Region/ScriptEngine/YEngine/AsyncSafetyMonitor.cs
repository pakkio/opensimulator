/*
 * AsyncSafetyMonitor.cs - Race Condition and Concurrency Anomaly Detection
 * Part of TIER 4 YEngine Async Optimizations
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenSim.Region.ScriptEngine.Shared;

namespace OpenSim.Region.ScriptEngine.Yengine
{
    public static class AsyncSafetyMonitor
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        
        // Thread safety tracking
        private static readonly ConcurrentDictionary<string, AsyncOperationTracker> m_ActiveOperations = new();
        private static readonly ConcurrentDictionary<int, string> m_ThreadOperations = new();
        
        // Race condition detection
        private static readonly ConcurrentDictionary<string, DateTime> m_LastAccess = new();
        private static readonly object m_DeadlockDetectionLock = new object();
        
        // Statistics
        private static volatile int m_TotalOperations = 0;
        private static volatile int m_ConcurrentOperations = 0;
        private static volatile int m_DetectedAnomalies = 0;
        
        public static bool EnableMonitoring { get; set; } = true;
        public static bool VerboseLogging { get; set; } = false;
        
        public class AsyncOperationTracker
        {
            public string OperationId { get; set; }
            public string ThreadName { get; set; }
            public int ThreadId { get; set; }
            public DateTime StartTime { get; set; }
            public string OperationType { get; set; }
            public string ResourceId { get; set; }
            public StackTrace CallStack { get; set; }
        }
        
        /// <summary>
        /// Track start of async operation
        /// </summary>
        public static string TrackOperationStart(string operationType, string resourceId = null)
        {
            if (!EnableMonitoring) return null;
            
            var operationId = Guid.NewGuid().ToString("N")[..8];
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var threadName = Thread.CurrentThread.Name ?? $"Thread-{threadId}";
            
            var tracker = new AsyncOperationTracker
            {
                OperationId = operationId,
                ThreadName = threadName,
                ThreadId = threadId,
                StartTime = DateTime.UtcNow,
                OperationType = operationType,
                ResourceId = resourceId ?? "unknown",
                CallStack = VerboseLogging ? new StackTrace(1, true) : null
            };
            
            m_ActiveOperations[operationId] = tracker;
            m_ThreadOperations[threadId] = operationId;
            
            Interlocked.Increment(ref m_TotalOperations);
            Interlocked.Increment(ref m_ConcurrentOperations);
            
            // Check for potential race condition
            CheckRaceCondition(operationType, resourceId);
            
            if (VerboseLogging)
            {
                m_log.InfoFormat("[AsyncSafety] START: {0} ({1}) on {2} for resource {3}", 
                    operationId, operationType, threadName, resourceId);
            }
            
            return operationId;
        }
        
        /// <summary>
        /// Track end of async operation
        /// </summary>
        public static void TrackOperationEnd(string operationId, bool success = true)
        {
            if (!EnableMonitoring || string.IsNullOrEmpty(operationId)) return;
            
            if (m_ActiveOperations.TryRemove(operationId, out AsyncOperationTracker tracker))
            {
                var duration = DateTime.UtcNow - tracker.StartTime;
                m_ThreadOperations.TryRemove(tracker.ThreadId, out _);
                
                Interlocked.Decrement(ref m_ConcurrentOperations);
                
                // Check for suspiciously long operations (potential deadlock)
                if (duration.TotalSeconds > 5.0)
                {
                    m_log.WarnFormat("[AsyncSafety] SLOW OPERATION: {0} took {1}ms - {2}", 
                        operationId, duration.TotalMilliseconds, tracker.OperationType);
                    Interlocked.Increment(ref m_DetectedAnomalies);
                }
                
                if (VerboseLogging)
                {
                    m_log.InfoFormat("[AsyncSafety] END: {0} ({1}) completed in {2}ms - {3}", 
                        operationId, tracker.OperationType, duration.TotalMilliseconds, 
                        success ? "SUCCESS" : "FAILED");
                }
            }
        }
        
        /// <summary>
        /// Check for potential race conditions
        /// </summary>
        private static void CheckRaceCondition(string operationType, string resourceId)
        {
            if (string.IsNullOrEmpty(resourceId)) return;
            
            var key = $"{operationType}:{resourceId}";
            var now = DateTime.UtcNow;
            
            if (m_LastAccess.TryGetValue(key, out DateTime lastAccess))
            {
                var timeSinceLastAccess = now - lastAccess;
                
                // If same resource accessed within 10ms by different operation, potential race
                if (timeSinceLastAccess.TotalMilliseconds < 10)
                {
                    m_log.WarnFormat("[AsyncSafety] POTENTIAL RACE: Resource '{0}' accessed within {1}ms", 
                        resourceId, timeSinceLastAccess.TotalMilliseconds);
                    Interlocked.Increment(ref m_DetectedAnomalies);
                }
            }
            
            m_LastAccess[key] = now;
        }
        
        /// <summary>
        /// Detect potential deadlocks
        /// </summary>
        public static void CheckForDeadlocks()
        {
            if (!EnableMonitoring) return;
            
            lock (m_DeadlockDetectionLock)
            {
                var suspiciousOperations = new List<AsyncOperationTracker>();
                var now = DateTime.UtcNow;
                
                foreach (var operation in m_ActiveOperations.Values)
                {
                    var duration = now - operation.StartTime;
                    
                    // Operations running for more than 30 seconds are suspicious
                    if (duration.TotalSeconds > 30)
                    {
                        suspiciousOperations.Add(operation);
                    }
                }
                
                if (suspiciousOperations.Count > 0)
                {
                    m_log.ErrorFormat("[AsyncSafety] DEADLOCK DETECTION: {0} operations stuck for >30s", 
                        suspiciousOperations.Count);
                    
                    foreach (var op in suspiciousOperations)
                    {
                        m_log.ErrorFormat("[AsyncSafety] STUCK: {0} ({1}) on {2} for {3}s", 
                            op.OperationId, op.OperationType, op.ThreadName, 
                            (now - op.StartTime).TotalSeconds);
                            
                        if (op.CallStack != null)
                        {
                            m_log.ErrorFormat("[AsyncSafety] STACK: {0}", op.CallStack.ToString());
                        }
                    }
                    
                    Interlocked.Add(ref m_DetectedAnomalies, suspiciousOperations.Count);
                }
            }
        }
        
        /// <summary>
        /// Get monitoring statistics
        /// </summary>
        public static string GetStatistics()
        {
            return $"AsyncSafety Stats: Total={m_TotalOperations}, Active={m_ConcurrentOperations}, Anomalies={m_DetectedAnomalies}";
        }
        
        /// <summary>
        /// Reset monitoring statistics
        /// </summary>
        public static void ResetStatistics()
        {
            Interlocked.Exchange(ref m_TotalOperations, 0);
            Interlocked.Exchange(ref m_DetectedAnomalies, 0);
            m_LastAccess.Clear();
        }
        
        /// <summary>
        /// Force cleanup of stale operations
        /// </summary>
        public static void CleanupStaleOperations()
        {
            var now = DateTime.UtcNow;
            var staleOperations = new List<string>();
            
            foreach (var kvp in m_ActiveOperations)
            {
                if ((now - kvp.Value.StartTime).TotalMinutes > 5) // 5 minutes timeout
                {
                    staleOperations.Add(kvp.Key);
                }
            }
            
            foreach (var staleId in staleOperations)
            {
                if (m_ActiveOperations.TryRemove(staleId, out AsyncOperationTracker stale))
                {
                    m_log.WarnFormat("[AsyncSafety] CLEANUP: Removed stale operation {0} ({1})", 
                        staleId, stale.OperationType);
                    m_ThreadOperations.TryRemove(stale.ThreadId, out _);
                    Interlocked.Decrement(ref m_ConcurrentOperations);
                }
            }
        }
    }
}