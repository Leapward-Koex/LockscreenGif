using System.Runtime.InteropServices;

namespace LockscreenGif.Services;

/// <summary>Retains DXGI enumeration indices, matching FFmpeg's D3D11VA device option.</summary>
internal sealed class DxgiHardwareAdapterEnumerator : IHardwareAdapterEnumerator
{
    public Task<IReadOnlyList<int>> EnumerateAsync(CancellationToken token) => Task.Run(() => Enumerate(token), token);

    private static unsafe IReadOnlyList<int> Enumerate(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var interfaceId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref interfaceId, out var factory));
        try
        {
            var indices = new List<int>();
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                nint adapter = 0;
                var result = enumerate(factory, index, &adapter);
                if (result == unchecked((int)0x887A0002)) // DXGI_ERROR_NOT_FOUND
                {
                    return indices;
                }
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10];
                    AdapterDescription description;
                    Marshal.ThrowExceptionForHR(getDescription(adapter, &description));
                    if ((description.Flags & 2) == 0) // DXGI_ADAPTER_FLAG_SOFTWARE
                    {
                        indices.Add(checked((int)index));
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid interfaceId, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubsystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }
}
