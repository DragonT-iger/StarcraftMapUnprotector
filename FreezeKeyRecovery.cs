using System;
using System.Threading;
using System.Threading.Tasks;

internal static partial class StarcraftMapUnprotector
{
    internal static uint RecoverFreezeKey(uint flag, int[] targetWlist)
    {
        int stride = FreezeStride; // 74
        int tabCount = FreezeTabCount; // 16

        Console.WriteLine("  Freeze key recovery: brute-forcing 2^32 keys with wlist constraints...");
        Console.WriteLine("  Target wlist: [" + string.Join(", ", targetWlist) + "]");

        uint foundKey = 0;
        int found = 0;
        long totalChecked = 0;

        int degreeOfParallelism = Environment.ProcessorCount;
        uint chunkSize = uint.MaxValue / (uint)degreeOfParallelism + 1;

        Parallel.For(0, degreeOfParallelism, new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism }, delegate(int chunk)
        {
            uint start = (uint)chunk * chunkSize;
            uint end = (chunk == degreeOfParallelism - 1) ? uint.MaxValue : start + chunkSize - 1;
            long localCount = 0;

            for (uint key = start; ; key++)
            {
                if (Volatile.Read(ref found) != 0) return;

                uint r = FreezeMix2(key, flag);
                r = FreezeMix2(r, key);

                if (r % (uint)stride != (uint)targetWlist[0])
                    goto next;

                for (int i = 0; i < tabCount; i++)
                {
                    r = FreezeMix2(r, key + (uint)i);
                    if (i + 1 < tabCount && r % (uint)stride != (uint)targetWlist[i + 1])
                        goto next;
                }

                if (Interlocked.CompareExchange(ref found, 1, 0) == 0)
                {
                    foundKey = key;
                    Console.WriteLine("\n  KEY FOUND: 0x" + key.ToString("X8"));
                }
                return;

                next:
                localCount++;
                if (localCount % 100000000 == 0)
                {
                    Interlocked.Add(ref totalChecked, 100000000);
                    long tc = Volatile.Read(ref totalChecked);
                    double pct = tc / (double)uint.MaxValue * 100.0;
                    Console.Write("\r  Progress: " + pct.ToString("F1") + "% (" + tc.ToString("N0") + " keys)   ");
                }

                if (key == end) break;
            }
        });

        Console.WriteLine();

        if (found == 0)
        {
            Console.WriteLine("  Key recovery failed: no key matched the wlist constraints.");
        }

        return found != 0 ? foundKey : 0;
    }

    internal static int[] RecoverWlistFromDump(byte[] encTrigData, int encTrigOffset, byte[] dumpTrigData, int dumpTrigOffset)
    {
        int stride = FreezeStride; // 74

        // Compute diffs per dword position
        int[] diffCount = new int[stride];
        uint[] diffSum = new uint[stride]; // sum of adddw values per w group
        int[] groupCount = new int[stride]; // how many members per w group
        uint[][] groupAdddws = new uint[stride][];
        for (int w = 0; w < stride; w++)
            groupAdddws[w] = new uint[8];

        for (int i = 0; i < FreezeTrigBodySize / 4; i++)
        {
            uint enc = BitConverter.ToUInt32(encTrigData, encTrigOffset + i * 4);
            uint dec = BitConverter.ToUInt32(dumpTrigData, dumpTrigOffset + i * 4);
            if (enc != dec)
            {
                int w = i % stride;
                uint adddw = dec - enc;
                int idx = groupCount[w];
                if (idx < 8)
                    groupAdddws[w][idx] = adddw;
                groupCount[w]++;
            }
        }

        // Identify encryption groups (8 members) and find majority adddw
        var encGroups = new System.Collections.Generic.Dictionary<int, uint>();
        for (int w = 0; w < stride; w++)
        {
            if (groupCount[w] < 6) continue;

            // Find majority adddw
            uint majority = groupAdddws[w][0];
            int majorityCount = 0;
            for (int a = 0; a < Math.Min(groupCount[w], 8); a++)
            {
                int cnt = 0;
                for (int b = 0; b < Math.Min(groupCount[w], 8); b++)
                {
                    if (groupAdddws[w][b] == groupAdddws[w][a]) cnt++;
                }
                if (cnt > majorityCount)
                {
                    majorityCount = cnt;
                    majority = groupAdddws[w][a];
                }
            }
            encGroups[w] = majority;
        }

        // Assign tabs: for each group, adddw = mix2(w, i) → find i
        int[] wlistByTab = new int[16];
        bool[] tabAssigned = new bool[16];
        for (int i = 0; i < 16; i++) wlistByTab[i] = -1;

        foreach (var kv in encGroups)
        {
            int w = kv.Key;
            uint adddw = kv.Value;
            for (int i = 0; i < 16; i++)
            {
                if (FreezeMix2((uint)w, (uint)i) == adddw && !tabAssigned[i])
                {
                    wlistByTab[i] = w;
                    tabAssigned[i] = true;
                    break;
                }
            }
        }

        // Check for double-tab (two tabs sharing same w, sum of adddws)
        int[] unassignedTabs = FindUnassignedTabs(tabAssigned);
        if (unassignedTabs.Length == 2)
        {
            foreach (var kv in encGroups)
            {
                int w = kv.Key;
                if (wlistByTab[0] == w || Array.Exists(wlistByTab, x => x == w && Array.IndexOf(wlistByTab, x) >= 0))
                {
                    // Check if already assigned
                    bool alreadySingle = false;
                    for (int i = 0; i < 16; i++)
                    {
                        if (wlistByTab[i] == w) { alreadySingle = true; break; }
                    }
                    if (alreadySingle) continue;
                }

                uint adddw = kv.Value;
                int a = unassignedTabs[0], b = unassignedTabs[1];
                uint sum = FreezeMix2((uint)w, (uint)a) + FreezeMix2((uint)w, (uint)b);
                if (sum == adddw)
                {
                    wlistByTab[a] = w;
                    wlistByTab[b] = w;
                    tabAssigned[a] = true;
                    tabAssigned[b] = true;
                    Console.WriteLine("  Double-tab detected: tabs " + a + " and " + b + " share w=" + w);
                    break;
                }
            }
        }

        // Verify all tabs assigned
        for (int i = 0; i < 16; i++)
        {
            if (wlistByTab[i] < 0)
            {
                Console.WriteLine("  WARNING: tab " + i + " not assigned.");
                return null;
            }
        }

        Console.WriteLine("  Recovered wlist: [" + string.Join(", ", wlistByTab) + "]");
        return wlistByTab;
    }

    private static int[] FindUnassignedTabs(bool[] assigned)
    {
        var list = new System.Collections.Generic.List<int>();
        for (int i = 0; i < assigned.Length; i++)
        {
            if (!assigned[i]) list.Add(i);
        }
        return list.ToArray();
    }
}
