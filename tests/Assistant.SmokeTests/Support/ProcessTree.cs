using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Assistant.SmokeTests.Support;

/// <summary>The programs a process started, and the ones they started: what must be gone when the app that owns them is.</summary>
internal static class ProcessTree
{
    private const uint SnapshotProcess = 0x2;

    /// <summary>The names and ids of every program that runs below this process.</summary>
    public static IReadOnlyList<(int Id, string Name)> Descendants() => Descendants(Environment.ProcessId);

    public static IReadOnlyList<(int Id, string Name)> Descendants(int root)
    {
        var parents = new Dictionary<int, List<(int Id, string Name)>>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == -1)
        {
            return [];
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var found = Process32First(snapshot, ref entry); found; found = Process32Next(snapshot, ref entry))
            {
                if (!parents.TryGetValue((int)entry.ParentId, out var children))
                {
                    parents[(int)entry.ParentId] = children = [];
                }

                children.Add(((int)entry.Id, entry.ExeFile));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var result = new List<(int Id, string Name)>();
        var pending = new Queue<int>([root]);
        while (pending.TryDequeue(out var parent))
        {
            if (!parents.TryGetValue(parent, out var children))
            {
                continue;
            }

            foreach (var child in children.Where(child => child.Id != root && result.All(known => known.Id != child.Id)))
            {
                result.Add(child);
                pending.Enqueue(child.Id);
            }
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint Id;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
