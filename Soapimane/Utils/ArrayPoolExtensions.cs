using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Soapimane.AILogic;

namespace Soapimane.Utils

{
    /// <summary>
    /// Extensions and utilities for ArrayPool usage with prediction data structures.
    /// Provides high-performance, low-allocation pooling for AI operations.
    /// </summary>
    public static class ArrayPoolExtensions
    {
        #region Prediction Array Pool

        /// <summary>
        /// Custom array pool for Prediction objects to avoid frequent allocations.
        /// </summary>
        public static class PredictionPool
        {
            private static readonly ArrayPool<Prediction> _pool = ArrayPool<Prediction>.Create(1000, 10);

            /// <summary>
            /// Rents a prediction array from the pool.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Prediction[] Rent(int minimumLength)
            {
                return _pool.Rent(minimumLength);
            }

            /// <summary>
            /// Returns a prediction array to the pool.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Return(Prediction[] array, bool clearArray = false)
            {
                _pool.Return(array, clearArray);
            }

            /// <summary>
            /// Rents and wraps in a disposable struct for automatic return.
            /// </summary>
            public static PooledPredictionArray RentPooled(int minimumLength, bool clearOnReturn = false)
            {
                var array = Rent(minimumLength);
                return new PooledPredictionArray(array, minimumLength, clearOnReturn);
            }
        }

        /// <summary>
        /// Disposable wrapper for pooled prediction arrays.
        /// </summary>
        public readonly struct PooledPredictionArray : IDisposable
        {
            private readonly Prediction[] _array;
            private readonly int _length;
            private readonly bool _clearOnReturn;

            public Prediction[] Array => _array;
            public int Length => _length;

            public PooledPredictionArray(Prediction[] array, int length, bool clearOnReturn)
            {
                _array = array;
                _length = length;
                _clearOnReturn = clearOnReturn;
            }

            public void Dispose()
            {
                PredictionPool.Return(_array, _clearOnReturn);
            }

            /// <summary>
            /// Gets a span over the valid portion of the array.
            /// </summary>
            public Span<Prediction> AsSpan() => _array.AsSpan(0, _length);

            /// <summary>
            /// Gets a span over the entire rented array.
            /// </summary>
            public Span<Prediction> AsSpanFull() => _array.AsSpan();
        }

        #endregion

        #region Detection Box Pool

        /// <summary>
        /// Array pool for detection box rectangles.
        /// </summary>
        public static class DetectionBoxPool
        {
            private static readonly ArrayPool<System.Drawing.Rectangle> _pool = ArrayPool<System.Drawing.Rectangle>.Create(100, 10);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static System.Drawing.Rectangle[] Rent(int minimumLength)
            {
                return _pool.Rent(minimumLength);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Return(System.Drawing.Rectangle[] array, bool clearArray = false)
            {
                _pool.Return(array, clearArray);
            }
        }

        #endregion

        #region Float Array Pool (for tensor operations)

        /// <summary>
        /// Specialized pool for float arrays used in tensor operations.
        /// </summary>
        public static class TensorFloatPool
        {
            // Use shared pool for common sizes, custom for large
            private static readonly ArrayPool<float> _pool = ArrayPool<float>.Shared;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static float[] Rent(int minimumLength)
            {
                return _pool.Rent(minimumLength);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Return(float[] array, bool clearArray = false)
            {
                _pool.Return(array, clearArray);
            }

            /// <summary>
            /// Rents a buffer for RGB image data (3 * width * height).
            /// </summary>
            public static float[] RentForImage(int width, int height, int channels = 3)
            {
                return Rent(channels * width * height);
            }
        }

        #endregion

        #region Byte Array Pool (for bitmap data)

        /// <summary>
        /// Specialized pool for byte arrays used in bitmap operations.
        /// </summary>
        public static class BitmapBytePool
        {
            private static readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static byte[] Rent(int minimumLength)
            {
                return _pool.Rent(minimumLength);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Return(byte[] array, bool clearArray = false)
            {
                _pool.Return(array, clearArray);
            }

            /// <summary>
            /// Rents a buffer for 32bpp bitmap data (4 * width * height).
            /// </summary>
            public static byte[] RentForBitmap32bpp(int width, int height)
            {
                return Rent(4 * width * height);
            }

            /// <summary>
            /// Rents a buffer for 24bpp bitmap data (3 * width * height).
            /// </summary>
            public static byte[] RentForBitmap24bpp(int width, int height)
            {
                return Rent(3 * width * height);
            }
        }

        #endregion

        #region Span-based Operations

        /// <summary>
        /// Clears a span without allocation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClearSpan<T>(Span<T> span) where T : unmanaged
        {
            span.Clear();
        }

        /// <summary>
        /// Fills a span with a value without allocation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FillSpan<T>(Span<T> span, T value) where T : unmanaged
        {
            span.Fill(value);
        }

        /// <summary>
        /// Copies data between spans safely.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopySpan<T>(ReadOnlySpan<T> source, Span<T> destination) where T : unmanaged
        {
            source.CopyTo(destination);
        }

        #endregion

        #region Stackalloc Helpers

        /// <summary>
        /// Rents a small buffer from stack or pool depending on size.
        /// Uses ArrayPool to avoid returning stackalloc spans from this method.
        /// </summary>
        public static Span<T> RentTemporary<T>(int length, out IDisposable? returnHandle) where T : unmanaged
        {
            T[] rented = ArrayPool<T>.Shared.Rent(length);
            returnHandle = new ArrayPoolReturnHandle<T>(rented);
            return rented.AsSpan(0, length);
        }

        /// <summary>
        /// Handle for returning ArrayPool arrays.
        /// </summary>
        private class ArrayPoolReturnHandle<T> : IDisposable
        {
            private readonly T[] _array;
            private bool _disposed;

            public ArrayPoolReturnHandle(T[] array)
            {
                _array = array;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    ArrayPool<T>.Shared.Return(_array);
                    _disposed = true;
                }
            }
        }

        #endregion

        #region Performance Metrics

        /// <summary>
        /// Tracks pool usage statistics.
        /// </summary>
        public static class PoolMetrics
        {
            private static long _predictionRentals = 0;
            private static long _predictionReturns = 0;
            private static long _tensorRentals = 0;
            private static long _tensorReturns = 0;
            private static long _bitmapRentals = 0;
            private static long _bitmapReturns = 0;

            public static void RecordPredictionRental() => System.Threading.Interlocked.Increment(ref _predictionRentals);
            public static void RecordPredictionReturn() => System.Threading.Interlocked.Increment(ref _predictionReturns);
            public static void RecordTensorRental() => System.Threading.Interlocked.Increment(ref _tensorRentals);
            public static void RecordTensorReturn() => System.Threading.Interlocked.Increment(ref _tensorReturns);
            public static void RecordBitmapRental() => System.Threading.Interlocked.Increment(ref _bitmapRentals);
            public static void RecordBitmapReturn() => System.Threading.Interlocked.Increment(ref _bitmapReturns);

            public static string GetReport()
            {
                return $"ArrayPool Metrics:\n" +
                       $"  Predictions: {_predictionRentals:N0} rentals, {_predictionReturns:N0} returns\n" +
                       $"  Tensors: {_tensorRentals:N0} rentals, {_tensorReturns:N0} returns\n" +
                       $"  Bitmaps: {_bitmapRentals:N0} rentals, {_bitmapReturns:N0} returns";
            }

            public static void Reset()
            {
                System.Threading.Interlocked.Exchange(ref _predictionRentals, 0);
                System.Threading.Interlocked.Exchange(ref _predictionReturns, 0);
                System.Threading.Interlocked.Exchange(ref _tensorRentals, 0);
                System.Threading.Interlocked.Exchange(ref _tensorReturns, 0);
                System.Threading.Interlocked.Exchange(ref _bitmapRentals, 0);
                System.Threading.Interlocked.Exchange(ref _bitmapReturns, 0);
            }
        }

        #endregion
    }
}
