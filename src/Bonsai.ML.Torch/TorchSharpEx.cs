﻿#nullable enable
#pragma warning disable CS1573 // Parameter has no matching param tag in the XML comment
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static TorchSharp.torch;

namespace Bonsai.ML.Torch;

internal unsafe static class TorchSharpEx
{
    [DllImport("LibTorchSharp")]
    private static extern IntPtr THSTensor_new(IntPtr rawArray, DeleterCallback deleter, long* dimensions, int numDimensions, sbyte type, sbyte dtype, int deviceType, int deviceIndex, byte requires_grad);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeleterCallback(IntPtr context);

    // Torch does not expect the deleter callback to be able to be null since it's a C++ reference and LibTorchSharp
    // does not expose the functions used to create a tensor without a deleter callback, so we must use a no-op callback
    private static readonly DeleterCallback NullDeleterCallback = _ => { };

    // Acts as GC root for unmanaged callbacks, value is unused
    private static readonly ConcurrentDictionary<DeleterCallback, nint> ActiveDeleterCallbacks = new();

    /// <summary>Creates a <see cref="Tensor"/> from unmanaged memory that is owned by a managed object</summary>
    /// <param name="data">The unmanaged memory that will back the tensor, must remain valid and fixed for the lifetime of the tensor</param>
    /// <param name="managedAnchor">The managed .NET object which owns <paramref name="data"/></param>
    public static Tensor CreateTensorFromUnmanagedMemoryWithManagedAnchor(
        IntPtr data, 
        object managedAnchor, 
        ReadOnlySpan<long> 
        dimensions, 
        ScalarType dataType,
        Device device
    )
    {
        //PERF: Ideally the deleter would receive the GCHandle as the context rather than the pointer to the unmanaged memory since that's
        // would allow us to use a GCHandle to root the anchor and free it directly rather than capturing it in the lambda.
        // Torch itself has the ability to set the context to something else via `TensorMaker::context(void* value, ContextDeleter deleter)`,
        // but unfortunately this method isn't exposed in LibTorchSharp.
        // This is similar to the inefficient method TorchSharp uses, which has quite a lot of unecessary overhead (particularly the unmanaged
        // delegate allocation), but we do skip some aspects like the GC handle allocation.
        // It may be tempting to use a GCHandle and a static delegate, looking up the GC handle from the native memory pointer, but doing this
        // without breaking the ability to create redundant tensors over the same data is overly complicated.
        DeleterCallback? deleter = null;
        deleter = (data) =>
        {
            GC.KeepAlive(managedAnchor);

            if (!ActiveDeleterCallbacks.TryRemove(deleter!, out _))
                Debug.Fail($"The same tensor data handle deleter was called more than once!");
        };

        if (!ActiveDeleterCallbacks.TryAdd(deleter, default))
            Debug.Fail("Unreachable");

        device ??= get_default_device();

        fixed (long* dimensionsPtr = &dimensions[0])
        {
            IntPtr tensorHandle = THSTensor_new(data, deleter, dimensionsPtr, dimensions.Length, (sbyte)dataType, (sbyte)dataType, (int)device.type, device.index, 0);
            if (tensorHandle == IntPtr.Zero)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                tensorHandle = THSTensor_new(data, deleter, dimensionsPtr, dimensions.Length, (sbyte)dataType, (sbyte)dataType, (int)device.type, device.index, 0);
            }

            if (tensorHandle == IntPtr.Zero)
                CheckForErrors();

            return Tensor.UnsafeCreateTensor(tensorHandle);
        }
    }

    internal readonly ref struct StackTensor
    {
        public readonly Tensor Tensor;
        private readonly object? Anchor;

        internal StackTensor(Tensor tensor, object? anchor)
        {
            Tensor = tensor;
            Anchor = anchor;
        }

        public void Dispose()
        {
            Tensor.Dispose();
            GC.KeepAlive(Anchor);
        }
    }

    /// <summary>Creates a tensor which is associated with a stack scope.</summary>
    /// <param name="data">The unmanaged memory that will back the tensor, must remain valid and fixed for the lifetime of the tensor</param>
    /// <param name="managedAnchor">An optional managed .NET object which owns <paramref name="data"/></param>
    /// <remarks>
    /// The returned stack tensor must be disposed. The tensor it refers to will not be valid outside of the scope where it was allocated.
    /// </remarks>
    internal static StackTensor CreateStackTensor(
        IntPtr data, 
        object? managedAnchor, 
        ReadOnlySpan<long> dimensions, 
        ScalarType dataType,
        Device device
    )
    {
        device ??= get_default_device();
        
        fixed (long* dimensionsPtr = &dimensions[0])
        {
            IntPtr tensorHandle = THSTensor_new(data, NullDeleterCallback, dimensionsPtr, dimensions.Length, (sbyte)dataType, (sbyte)dataType, (int)device.type, device.index, 0);
            if (tensorHandle == IntPtr.Zero)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                tensorHandle = THSTensor_new(data, NullDeleterCallback, dimensionsPtr, dimensions.Length, (sbyte)dataType, (sbyte)dataType, (int)device.type, device.index, 0);
            }

            if (tensorHandle == IntPtr.Zero)
                CheckForErrors();

            Tensor result = Tensor.UnsafeCreateTensor(tensorHandle);
            return new StackTensor(result, data);
        }
    }

    /// <summary>Gets a pointer to the tensor's backing memory</summary>
    /// <remarks>The data backing a tensor is not necessarily contiguous or even present on the CPU, consider other strategies before using this method.</remarks>
    public static IntPtr DangerousGetDataPointer(this Tensor tensor)
    {
        [DllImport("LibTorchSharp")]
        static extern IntPtr THSTensor_data(IntPtr handle);

        IntPtr data = THSTensor_data(tensor.Handle);
        if (data == IntPtr.Zero)
            CheckForErrors();
        return data;
    }
}
