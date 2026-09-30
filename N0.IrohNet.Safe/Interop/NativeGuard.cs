using System.Runtime.InteropServices;

namespace N0.IrohNet;

/// <summary>
/// Temporarily protects an owned native container pointer for the duration of a single blocking FFI call.
/// </summary>
internal ref struct NativeGuard
{
    private readonly SafeHandle _handle;
    private nint _pointer;

    public NativeGuard(SafeHandle handle)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _pointer = 0;
        if (!handle.IsClosed && !handle.IsInvalid)
        {
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
            }
            catch (ObjectDisposedException)
            {
                added = false;
            }

            if (added)
            {
                _pointer = handle.DangerousGetHandle();
            }
        }
    }

    /// <summary>Gets a value indicating whether the underlying container pointer was acquired and is valid for use.</summary>
    public bool IsValid => _pointer != 0;

    /// <summary>Gets the native container pointer, valid only while this guard is alive.</summary>
    public nint Pointer => _pointer;

    public void Dispose()
    {
        if (_pointer != 0)
        {
            _pointer = 0;
            _handle.DangerousRelease();
        }
    }
}
