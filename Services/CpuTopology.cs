using System.Numerics;
using System.Runtime.InteropServices;

namespace StretchCordNG.Services
{
    /// <summary>
    /// Intel hybrid CPUs (12th gen and later) pair performance cores with efficiency cores,
    /// and the Windows scheduler *prefers P-cores for high-priority threads* — exactly
    /// backwards for a streaming companion app: our workers would compete with the game for
    /// P-core time while E-cores sit idle.
    ///
    /// This service discovers the E-core set — the logical processors in the lowest
    /// EfficiencyClass reported by GetLogicalProcessorInformationEx — and pins streaming
    /// worker threads to them, so the game keeps every P-core. Thread priority still orders
    /// work *within* the E-cores, so audio keeps its timeliness.
    ///
    /// On uniform CPUs, or when the OS does not report efficiency classes (older Windows
    /// builds), nothing is pinned and the log says why. The UI thread is never pinned — it
    /// is placed by the scheduler like any foreground thread.
    /// </summary>
    public static class CpuTopology
    {
        private const int RelationProcessorCore = 0;

        private static int _enabled = 1;                    // on by default; AppSettings can switch it
        private static int _probed;
        private static bool _pinning;                       // a usable single-group E-core set exists
        private static nuint _eCoreMask;                    // group-local mask of E-core logical processors
        private static string _describe = "not probed yet";
        private static long _pinWarned;

        public static void SetEnabled(bool value)
            => Interlocked.Exchange(ref _enabled, value ? 1 : 0);

        /// <summary>Human-readable summary of what was found and what happens to workers.</summary>
        public static string Describe
        {
            get
            {
                EnsureProbed();
                return _describe;
            }
        }

        [ThreadStatic] private static int _placement;   // 0 = undecided, 1 = free, 2 = pinned to E-cores
        [ThreadStatic] private static nuint _previousMask;

        /// <summary>
        /// Call from a worker thread to keep it on the efficiency cores (or restore it when
        /// the preference is switched off). Cheap after the first call per thread: one
        /// volatile read and a thread-static check.
        /// </summary>
        public static void EnsureThreadPlacement()
        {
            if (_placement == 0)
            {
                EnsureProbed();
                _placement = 1;
            }

            bool wantPinned = Volatile.Read(ref _enabled) == 1 && _pinning;

            if (wantPinned && _placement != 2)
            {
                var previous = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)_eCoreMask);
                if (previous != UIntPtr.Zero)
                {
                    _previousMask = (nuint)previous;
                    _placement = 2;
                }
                else if (Interlocked.Exchange(ref _pinWarned, 1) == 0)
                {
                    AppLog.Write("cpu: SetThreadAffinityMask refused the E-core mask — leaving the thread to the scheduler");
                }
            }
            else if (!wantPinned && _placement == 2)
            {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)_previousMask);
                _placement = 1;
            }
        }

        private static void EnsureProbed()
        {
            if (Interlocked.Exchange(ref _probed, 1) != 0) return;
            Probe();
            AppLog.Write($"cpu: {_describe}");
        }

        private static unsafe void Probe()
        {
            try
            {
                uint length = 0;
                _ = GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
                if (length == 0)
                {
                    _describe = "uniform or unreportable cpu topology (E-cores unknown)";
                    return;
                }

                var buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                    {
                        _describe = "cpu topology query failed";
                        return;
                    }

                    // Each entry: Relationship (4) | Size (4) | PROCESSOR_RELATIONSHIP payload.
                    // Payload: Flags @+0, EfficiencyClass @+1, Reserved[20], GroupCount @+22,
                    // GROUP_AFFINITY[GroupCount] @+24 (Mask 8 bytes, Group 2 bytes, pad 14).
                    var cores = new List<(byte effClass, ushort group, nuint lpMask)>();
                    byte* p = (byte*)buffer;
                    byte* end = p + length;

                    while (p + 8 <= end)
                    {
                        int relationship = *(int*)p;
                        int size = *(int*)(p + 4);
                        if (size < 8 || p + size > end) break;

                        if (relationship == RelationProcessorCore)
                        {
                            byte effClass = *(p + 8 + 1);
                            ushort groupCount = *(ushort*)(p + 8 + 22);
                            for (int g = 0; g < groupCount; g++)
                            {
                                byte* ga = p + 8 + 24 + 24L * g;
                                cores.Add((effClass, *(ushort*)(ga + 8), *(nuint*)ga));
                            }
                        }

                        p += size;
                    }

                    if (cores.Count == 0)
                    {
                        _describe = "no per-core cpu topology reported";
                        return;
                    }

                    int maxClass = cores.Max(c => c.effClass);
                    int minClass = cores.Min(c => c.effClass);

                    int performanceCores = cores.Count(c => c.effClass == maxClass);
                    int efficiencyCores = cores.Count(c => c.effClass == minClass);

                    if (minClass == maxClass)
                    {
                        _describe = $"uniform cpu ({cores.Count} cores, no efficiency classes reported) — scheduler in charge";
                        return;
                    }

                    // Only pin when every E-core logical processor sits in one processor
                    // group; gaming CPUs are single-group, and SetThreadAffinityMask works
                    // within a thread's current group only.
                    var eCoreEntries = cores.Where(c => c.effClass == minClass).ToList();
                    ushort eGroup = eCoreEntries[0].group;
                    if (eCoreEntries.Any(c => c.group != eGroup))
                    {
                        _describe = $"hybrid cpu ({performanceCores} P-cores, {efficiencyCores} E-cores across processor groups) — scheduler in charge";
                        return;
                    }

                    nuint mask = 0;
                    var lpNumbers = new List<int>();
                    foreach (var c in eCoreEntries)
                    {
                        mask |= c.lpMask;
                        for (int bit = 0; bit < 64; bit++)
                            if ((c.lpMask & ((nuint)1 << bit)) != 0)
                                lpNumbers.Add(eGroup * 64 + bit);
                    }

                    _eCoreMask = mask;
                    _pinning = true;

                    string lpList = lpNumbers.Count <= 16
                        ? string.Join(",", lpNumbers)
                        : $"{lpNumbers[0]}–{lpNumbers[^1]}";
                    _describe = $"hybrid cpu: {performanceCores} P-core(s) + {efficiencyCores} E-core(s) — " +
                                $"stream workers pinned to E-cores (logical processors {lpList}), P-cores left to the game";
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                _describe = $"cpu topology probe failed: {ex.Message}";
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationship, IntPtr buffer, ref uint returnedLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);
    }
}
