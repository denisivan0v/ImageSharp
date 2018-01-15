// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SixLabors.ImageSharp.Memory
{
    /// <inheritdoc />
    /// <summary>
    /// Manages a buffer of value type objects as a Disposable resource.
    /// The backing array is either pooled or comes from the outside.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    internal class Buffer<T> : IBuffer<T>
        where T : struct
    {
        private MemoryManager memoryManager;

        /// <summary>
        /// A pointer to the first element of <see cref="Array"/> when pinned.
        /// </summary>
        private IntPtr pointer;

        /// <summary>
        /// A handle that allows to access the managed <see cref="Array"/> as an unmanaged memory by pinning.
        /// </summary>
        private GCHandle handle;

        /// <summary>
        /// Initializes a new instance of the <see cref="Buffer{T}"/> class.
        /// </summary>
        /// <param name="array">The array to pin.</param>
        public Buffer(T[] array)
        {
            this.Length = array.Length;
            this.Array = array;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Buffer{T}"/> class.
        /// </summary>
        /// <param name="array">The array to pin.</param>
        /// <param name="length">The count of "relevant" elements in 'array'.</param>
        public Buffer(T[] array, int length)
        {
            if (array.Length < length)
            {
                throw new ArgumentException("Can't initialize a PinnedBuffer with array.Length < count", nameof(array));
            }

            this.Length = length;
            this.Array = array;
        }

        internal Buffer(T[] array, int length, MemoryManager memoryManager)
            : this(array, length)
        {
            this.memoryManager = memoryManager;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="Buffer{T}"/> class.
        /// </summary>
        ~Buffer()
        {
            this.UnPin();
        }

        /// <summary>
        /// Gets a value indicating whether this <see cref="Buffer{T}"/> instance is disposed, or has lost ownership of <see cref="Array"/>.
        /// </summary>
        public bool IsDisposedOrLostArrayOwnership { get; private set; }

        /// <summary>
        /// Gets the count of "relevant" elements. It's usually smaller than 'Array.Length' when <see cref="Array"/> is pooled.
        /// </summary>
        public int Length { get; private set; }

        /// <summary>
        /// Gets the backing pinned array.
        /// </summary>
        public T[] Array { get; private set; }

        /// <summary>
        /// Gets a <see cref="Span{T}"/> to the backing buffer.
        /// </summary>
        public Span<T> Span => this;

        /// <summary>
        /// Returns a reference to specified element of the buffer.
        /// </summary>
        /// <param name="index">The index</param>
        /// <returns>The reference to the specified element</returns>
        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                DebugGuard.MustBeLessThan(index, this.Length, nameof(index));

                Span<T> span = this.Span;
                return ref span[index];
            }
        }

        /// <summary>
        /// Converts <see cref="Buffer{T}"/> to an <see cref="ReadOnlySpan{T}"/>.
        /// </summary>
        /// <param name="buffer">The <see cref="Buffer{T}"/> to convert.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator ReadOnlySpan<T>(Buffer<T> buffer)
        {
            return new ReadOnlySpan<T>(buffer.Array, 0, buffer.Length);
        }

        /// <summary>
        /// Converts <see cref="Buffer{T}"/> to an <see cref="Span{T}"/>.
        /// </summary>
        /// <param name="buffer">The <see cref="Buffer{T}"/> to convert.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Span<T>(Buffer<T> buffer)
        {
            return new Span<T>(buffer.Array, 0, buffer.Length);
        }

        /// <summary>
        /// Gets a <see cref="Span{T}"/> to an offseted position inside the buffer.
        /// </summary>
        /// <param name="start">The start</param>
        /// <returns>The <see cref="Span{T}"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> Slice(int start)
        {
            return new Span<T>(this.Array, start, this.Length - start);
        }

        /// <summary>
        /// Gets a <see cref="Span{T}"/> to an offsetted position inside the buffer.
        /// </summary>
        /// <param name="start">The start</param>
        /// <param name="length">The length of the slice</param>
        /// <returns>The <see cref="Span{T}"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> Slice(int start, int length)
        {
            return new Span<T>(this.Array, start, length);
        }

        /// <summary>
        /// Disposes the <see cref="Buffer{T}"/> instance by unpinning the array, and returning the pooled buffer when necessary.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            if (this.IsDisposedOrLostArrayOwnership)
            {
                return;
            }

            this.IsDisposedOrLostArrayOwnership = true;
            this.UnPin();

            this.memoryManager?.Release(this);

            this.memoryManager = null;
            this.Array = null;
            this.Length = 0;

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Unpins <see cref="Array"/> and makes the object "quasi-disposed" so the array is no longer owned by this object.
        /// If <see cref="Array"/> is rented, it's the callers responsibility to return it to it's pool.
        /// </summary>
        /// <returns>The unpinned <see cref="Array"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] TakeArrayOwnership()
        {
            if (this.IsDisposedOrLostArrayOwnership)
            {
                throw new InvalidOperationException(
                    "TakeArrayOwnership() is invalid: either Buffer<T> is disposed or TakeArrayOwnership() has been called multiple times!");
            }

            this.IsDisposedOrLostArrayOwnership = true;
            this.UnPin();
            T[] array = this.Array;
            this.Array = null;
            this.memoryManager = null;
            return array;
        }

        /// <summary>
        /// Clears the contents of this buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            this.Span.Clear();
        }

        /// <summary>
        /// Pins <see cref="Array"/>.
        /// </summary>
        /// <returns>The pinned pointer</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IntPtr Pin()
        {
            if (this.IsDisposedOrLostArrayOwnership)
            {
                throw new InvalidOperationException(
                    "Pin() is invalid on a buffer with IsDisposedOrLostArrayOwnership == true!");
            }

            if (this.pointer == IntPtr.Zero)
            {
                this.handle = GCHandle.Alloc(this.Array, GCHandleType.Pinned);
                this.pointer = this.handle.AddrOfPinnedObject();
            }

            return this.pointer;
        }

        /// <summary>
        /// Unpins <see cref="Array"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UnPin()
        {
            if (this.pointer == IntPtr.Zero || !this.handle.IsAllocated)
            {
                return;
            }

            this.handle.Free();
            this.pointer = IntPtr.Zero;
        }
    }
}