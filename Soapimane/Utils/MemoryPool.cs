using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using static Soapimane.Other.LogManager;


namespace Soapimane.Utils

{
    /// <summary>
    /// High-performance memory pool for frame buffers and tensor data.
    /// Provides zero-allocation hot paths for critical AI operations.
    /// </summary>
    public static class MemoryPool
    {
        #region Private Fields

        // Separate pools for different buffer sizes to reduce fragmentation
        private static readonly ConcurrentDictionary<int, ConcurrentQueue<byte[]>> _bytePools = new();
        private static readonly ConcurrentDictionary<int, ConcurrentQueue<float[]>> _floatPools = new();
        private static readonly ConcurrentDictionary<int, ConcurrentQueue<int[]>> _intPools = new();

        // Maximum pool sizes to prevent memory bloat
        private const int MAX_POOL_SIZE_PER_BUCKET = 10;
        private const int MAX_BUFFER_SIZE = 50 * 1024 * 1024; // 50MB max single allocation

        // Statistics
        private static long _totalRentals = 0;
        private static long _totalReturns = 0;
        private static long _allocationsAvoided = 0;
        private static long _newAllocations = 0;

        private static readonly object _statsLock = new object();

        #endregion

        #region Byte Array Pool

        /// <summary>
        /// Rents a byte array from the pool or creates a new one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] RentBytes(int minimumLength)
        {
            Interlocked.Increment(ref _totalRentals);

            if (minimumLength <= 0 || minimumLength > MAX_BUFFER_SIZE)
                throw new ArgumentOutOfRangeException(nameof(minimumLength));

            // Round up to nearest power of 2 or standard size for better pooling
            int bucketSize = GetBucketSize(minimumLength);

            if (_bytePools.TryGetValue(bucketSize, out var pool) && pool.TryDequeue(out var buffer))
            {
                Interlocked.Increment(ref _allocationsAvoided);
                return buffer;
            }

            // No buffer available in pool, allocate new
            Interlocked.Increment(ref _newAllocations);
            return new byte[bucketSize];
        }

        /// <summary>
        /// Returns a byte array to the pool for reuse.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReturnBytes(byte[] buffer, bool clearArray = false)
        {
            if (buffer == null) return;

            Interlocked.Increment(ref _totalReturns);

            int bucketSize = GetBucketSize(buffer.Length);

            // Don't pool oversized or tiny arrays
            if (bucketSize > MAX_BUFFER_SIZE || bucketSize < 64)
            {
                // Just let GC collect it
                return;
            }

            // Clear if requested (security/accuracy)
            if (clearArray)
            {
                Array.Clear(buffer, 0, buffer.Length);
            }

            // Get or create pool for this size
            var pool = _bytePools.GetOrAdd(bucketSize, _ => new ConcurrentQueue<byte[]>());

            // Only keep limited number of buffers per size to prevent memory bloat
            if (pool.Count < MAX_POOL_SIZE_PER_BUCKET)
            {
                pool.Enqueue(buffer);
            }
        }

        /// <summary>
        /// Rents a byte array and wraps it in a disposable handle.
        /// </summary>
        public static PooledByteArray RentBytesPooled(int minimumLength, bool clearOnReturn = false)
        {
            var buffer = RentBytes(minimumLength);
            return new PooledByteArray(buffer, clearOnReturn);
        }

        #endregion

        #region Float Array Pool

        /// <summary>
        /// Rents a float array from the pool or creates a new one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float[] RentFloats(int minimumLength)
        {
            Interlocked.Increment(ref _totalRentals);

            if (minimumLength <= 0 || minimumLength > MAX_BUFFER_SIZE / sizeof(float))
                throw new ArgumentOutOfRangeException(nameof(minimumLength));

            int bucketSize = GetBucketSize(minimumLength * sizeof(float)) / sizeof(float);

            if (_floatPools.TryGetValue(bucketSize, out var pool) && pool.TryDequeue(out var buffer))
            {
                Interlocked.Increment(ref _allocationsAvoided);
                return buffer;
            }

            Interlocked.Increment(ref _newAllocations);
            return new float[bucketSize];
        }

        /// <summary>
        /// Returns a float array to the pool for reuse.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReturnFloats(float[] buffer, bool clearArray = false)
        {
            if (buffer == null) return;

            Interlocked.Increment(ref _totalReturns);

            int bucketSize = GetBucketSize(buffer.Length * sizeof(float)) / sizeof(float);

            if (bucketSize > MAX_BUFFER_SIZE / sizeof(float) || bucketSize < 16)
                return;

            if (clearArray)
            {
                Array.Clear(buffer, 0, buffer.Length);
            }

            var pool = _floatPools.GetOrAdd(bucketSize, _ => new ConcurrentQueue<float[]>());

            if (pool.Count < MAX_POOL_SIZE_PER_BUCKET)
            {
                pool.Enqueue(buffer);
            }
        }

        /// <summary>
        /// Rents a float array and wraps it in a disposable handle.
        /// </summary>
        public static PooledFloatArray RentFloatsPooled(int minimumLength, bool clearOnReturn = false)
        {
            var buffer = RentFloats(minimumLength);
            return new PooledFloatArray(buffer, clearOnReturn);
        }

        #endregion

        #region Int Array Pool

        /// <summary>
        /// Rents an int array from the pool or creates a new one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int[] RentInts(int minimumLength)
        {
            Interlocked.Increment(ref _totalRentals);

            if (minimumLength <= 0 || minimumLength > MAX_BUFFER_SIZE / sizeof(int))
                throw new ArgumentOutOfRangeException(nameof(minimumLength));

            int bucketSize = GetBucketSize(minimumLength * sizeof(int)) / sizeof(int);

            if (_intPools.TryGetValue(bucketSize, out var pool) && pool.TryDequeue(out var buffer))
            {
                Interlocked.Increment(ref _allocationsAvoided);
                return buffer;
            }

            Interlocked.Increment(ref _newAllocations);
            return new int[bucketSize];
        }

        /// <summary>
        /// Returns an int array to the pool for reuse.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReturnInts(int[] buffer, bool clearArray = false)
        {
            if (buffer == null) return;

            Interlocked.Increment(ref _totalReturns);

            int bucketSize = GetBucketSize(buffer.Length * sizeof(int)) / sizeof(int);

            if (bucketSize > MAX_BUFFER_SIZE / sizeof(int) || bucketSize < 16)
                return;

            if (clearArray)
            {
                Array.Clear(buffer, 0, buffer.Length);
            }

            var pool = _intPools.GetOrAdd(bucketSize, _ => new ConcurrentQueue<int[]>());

            if (pool.Count < MAX_POOL_SIZE_PER_BUCKET)
            {
                pool.Enqueue(buffer);
            }
        }

        #endregion

        #region Bitmap Buffer Pool (Specialized)

        /// <summary>
        /// Specialized pool for bitmap frame buffers.
        /// Pre-allocates common frame sizes.
        /// </summary>
        public static class BitmapPool
        {
            private static readonly ConcurrentDictionary<(int width, int height, int bpp), ConcurrentQueue<byte[]>> _pools = new();

            /// <summary>
            /// Rents a bitmap buffer for the specified dimensions.
            /// </summary>
            public static byte[] RentBuffer(int width, int height, int bytesPerPixel = 4)
            {
                int size = width * height * bytesPerPixel;
                var key = (width, height, bytesPerPixel);

                if (_pools.TryGetValue(key, out var pool) && pool.TryDequeue(out var buffer))
                {
                    return buffer;
                }

                return new byte[size];
            }

            /// <summary>
            /// Returns a bitmap buffer to the pool.
            /// </summary>
            public static void ReturnBuffer(byte[] buffer, int width, int height, int bytesPerPixel = 4, bool clear = false)
            {
                if (buffer == null) return;

                if (clear)
                {
                    Array.Clear(buffer, 0, buffer.Length);
                }

                var key = (width, height, bytesPerPixel);
                var pool = _pools.GetOrAdd(key, _ => new ConcurrentQueue<byte[]>());

                if (pool.Count < 5) // Keep fewer bitmap buffers
                {
                    pool.Enqueue(buffer);
                }
            }

            /// <summary>
            /// Pre-allocates buffers for common frame sizes.
            /// </summary>
            public static void PreallocateCommonSizes()
            {
                int[] commonSizes = { 320, 416, 512, 640 };
                int bytesPerPixel = 3; // RGB

                foreach (int size in commonSizes)
                {
                    var key = (size, size, bytesPerPixel);
                    var pool = _pools.GetOrAdd(key, _ => new ConcurrentQueue<byte[]>());

                    // Pre-allocate 2 buffers per size
                    for (int i = 0; i < 2; i++)
                    {
                        pool.Enqueue(new byte[size * size * bytesPerPixel]);
                    }
                }
            }
        }

        #endregion

        #region Tensor Buffer Pool (Specialized)

        /// <summary>
        /// Specialized pool for AI tensor data.
        /// </summary>
        public static class TensorPool
        {
            private static readonly ConcurrentDictionary<(int batch, int channels, int height, int width), ConcurrentQueue<float[]>> _pools = new();

            /// <summary>
            /// Rents a tensor buffer for the specified dimensions.
            /// </summary>
            public static float[] RentTensor(int batch, int channels, int height, int width)
            {
                int size = batch * channels * height * width;
                var key = (batch, channels, height, width);

                if (_pools.TryGetValue(key, out var pool) && pool.TryDequeue(out var buffer))
                {
                    return buffer;
                }

                return new float[size];
            }

            /// <summary>
            /// Returns a tensor buffer to the pool.
            /// </summary>
            public static void ReturnTensor(float[] buffer, int batch, int channels, int height, int width, bool clear = false)
            {
                if (buffer == null) return;

                if (clear)
                {
                    Array.Clear(buffer, 0, buffer.Length);
                }

                var key = (batch, channels, height, width);
                var pool = _pools.GetOrAdd(key, _ => new ConcurrentQueue<float[]>());

                if (pool.Count < 3) // Keep fewer tensor buffers (they're large)
                {
                    pool.Enqueue(buffer);
                }
            }

            /// <summary>
            /// Pre-allocates tensors for common model input sizes.
            /// </summary>
            public static void PreallocateCommonTensors()
            {
                int[] commonSizes = { 320, 416, 512, 640 };
                int channels = 3; // RGB

                foreach (int size in commonSizes)
                {
                    var key = (1, channels, size, size);
                    var pool = _pools.GetOrAdd(key, _ => new ConcurrentQueue<float[]>());

                    // Pre-allocate 1 tensor per size
                    pool.Enqueue(new float[1 * channels * size * size]);
                }
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the appropriate bucket size for a requested length.
        /// Uses power-of-2 sizing with some common sizes for better fit.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetBucketSize(int minimumLength)
        {
            // Common sizes for better fit
            if (minimumLength <= 64) return 64;
            if (minimumLength <= 128) return 128;
            if (minimumLength <= 256) return 256;
            if (minimumLength <= 512) return 512;
            if (minimumLength <= 1024) return 1024;
            if (minimumLength <= 2048) return 2048;
            if (minimumLength <= 4096) return 4096;
            if (minimumLength <= 8192) return 8192;
            if (minimumLength <= 16384) return 16384;
            if (minimumLength <= 32768) return 32768;
            if (minimumLength <= 65536) return 65536;
            if (minimumLength <= 131072) return 131072;
            if (minimumLength <= 262144) return 262144;
            if (minimumLength <= 524288) return 524288;
            if (minimumLength <= 1048576) return 1048576;
            if (minimumLength <= 2097152) return 2097152;
            if (minimumLength <= 4194304) return 4194304;
            if (minimumLength <= 8388608) return 8388608;
            if (minimumLength <= 16777216) return 16777216;
            if (minimumLength <= 33554432) return 33554432;
            if (minimumLength <= 67108864) return 67108864;

            // Round up to next power of 2 for larger sizes
            int size = 134217728; // 128MB
            while (size < minimumLength && size < MAX_BUFFER_SIZE)
            {
                size *= 2;
            }

            return Math.Min(size, MAX_BUFFER_SIZE);
        }

        /// <summary>
        /// Gets current pool statistics.
        /// </summary>
        public static PoolStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new PoolStatistics
                {
                    TotalRentals = _totalRentals,
                    TotalReturns = _totalReturns,
                    AllocationsAvoided = _allocationsAvoided,
                    NewAllocations = _newAllocations,
                    BytePoolBuckets = _bytePools.Count,
                    FloatPoolBuckets = _floatPools.Count,
                    IntPoolBuckets = _intPools.Count,
                    BytePoolTotalBuffers = _bytePools.Values.Sum(q => q.Count),
                    FloatPoolTotalBuffers = _floatPools.Values.Sum(q => q.Count),
                    IntPoolTotalBuffers = _intPools.Values.Sum(q => q.Count)
                };
            }
        }

        /// <summary>
        /// Clears all pools and releases memory.
        /// </summary>
        public static void ClearAllPools()
        {
            foreach (var pool in _bytePools.Values)
            {
                while (pool.TryDequeue(out _)) { }
            }
            _bytePools.Clear();

            foreach (var pool in _floatPools.Values)
            {
                while (pool.TryDequeue(out _)) { }
            }
            _floatPools.Clear();

            foreach (var pool in _intPools.Values)
            {
                while (pool.TryDequeue(out _)) { }
            }
            _intPools.Clear();

            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Log(LogLevel.Info, "All memory pools cleared");

        }

        /// <summary>
        /// Pre-allocates common buffer sizes to reduce runtime allocations.
        /// </summary>
        public static void PreallocateCommonBuffers()
        {
            // Pre-allocate bitmap buffers
            BitmapPool.PreallocateCommonSizes();

            // Pre-allocate tensor buffers
            TensorPool.PreallocateCommonTensors();

            // Pre-allocate some general purpose buffers
            int[] commonSizes = { 1024, 4096, 16384, 65536, 262144 };
            
            foreach (int size in commonSizes)
            {
                var bytePool = _bytePools.GetOrAdd(size, _ => new ConcurrentQueue<byte[]>());
                for (int i = 0; i < 3; i++)
                {
                    bytePool.Enqueue(new byte[size]);
                }

                var floatPool = _floatPools.GetOrAdd(size / sizeof(float), _ => new ConcurrentQueue<float[]>());
                for (int i = 0; i < 3; i++)
                {
                    floatPool.Enqueue(new float[size / sizeof(float)]);
                }
            }

            Log(LogLevel.Info, "Common buffer sizes pre-allocated");

        }

        #endregion

        #region Disposable Wrappers

        /// <summary>
        /// Disposable wrapper for pooled byte arrays.
        /// </summary>
        public readonly struct PooledByteArray : IDisposable
        {
            public byte[] Buffer { get; }
            private readonly bool _clearOnReturn;

            public PooledByteArray(byte[] buffer, bool clearOnReturn)
            {
                Buffer = buffer;
                _clearOnReturn = clearOnReturn;
            }

            public void Dispose()
            {
                ReturnBytes(Buffer, _clearOnReturn);
            }
        }

        /// <summary>
        /// Disposable wrapper for pooled float arrays.
        /// </summary>
        public readonly struct PooledFloatArray : IDisposable
        {
            public float[] Buffer { get; }
            private readonly bool _clearOnReturn;

            public PooledFloatArray(float[] buffer, bool clearOnReturn)
            {
                Buffer = buffer;
                _clearOnReturn = clearOnReturn;
            }

            public void Dispose()
            {
                ReturnFloats(Buffer, _clearOnReturn);
            }
        }

        #endregion

        #region Statistics Structure

        /// <summary>
        /// Memory pool statistics.
        /// </summary>
        public struct PoolStatistics
        {
            public long TotalRentals;
            public long TotalReturns;
            public long AllocationsAvoided;
            public long NewAllocations;
            public int BytePoolBuckets;
            public int FloatPoolBuckets;
            public int IntPoolBuckets;
            public int BytePoolTotalBuffers;
            public int FloatPoolTotalBuffers;
            public int IntPoolTotalBuffers;

            public double PoolEfficiency => TotalRentals > 0 
                ? (double)AllocationsAvoided / TotalRentals * 100 
                : 0;

            public override string ToString()
            {
                return $"Memory Pool Stats:\n" +
                       $"  Rentals: {TotalRentals:N0}\n" +
                       $"  Returns: {TotalReturns:N0}\n" +
                       $"  Allocations Avoided: {AllocationsAvoided:N0} ({PoolEfficiency:F1}%)\n" +
                       $"  New Allocations: {NewAllocations:N0}\n" +
                       $"  Byte Pools: {BytePoolBuckets} (Buffers: {BytePoolTotalBuffers})\n" +
                       $"  Float Pools: {FloatPoolBuckets} (Buffers: {FloatPoolTotalBuffers})\n" +
                       $"  Int Pools: {IntPoolBuckets} (Buffers: {IntPoolTotalBuffers})";
            }
        }

        #endregion
    }
}
