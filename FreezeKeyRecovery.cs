using System;
using System.Threading;
using System.Threading.Tasks;

internal static partial class StarcraftMapUnprotector
{
    private struct FreezeTypeByteCheck
    {
        public int DwordIndex;
        public int Shift;
        public int MaxValue;
    }

    private static readonly FreezeTypeByteCheck[][] FreezeTypeChecksByColumn = BuildFreezeTypeChecksByColumn();

    private static FreezeTypeByteCheck[][] BuildFreezeTypeChecksByColumn()
    {
        var lists = new System.Collections.Generic.List<FreezeTypeByteCheck>[FreezeStride];
        for (int i = 0; i < lists.Length; i++)
            lists[i] = new System.Collections.Generic.List<FreezeTypeByteCheck>();

        for (int c = 0; c < 16; c++)
            AddFreezeTypeCheck(lists, c * 20 + 15, 23);

        for (int a = 0; a < 64; a++)
            AddFreezeTypeCheck(lists, 320 + a * 32 + 26, 63);

        var result = new FreezeTypeByteCheck[FreezeStride][];
        for (int i = 0; i < result.Length; i++)
            result[i] = lists[i].ToArray();

        return result;
    }

    private static void AddFreezeTypeCheck(
        System.Collections.Generic.List<FreezeTypeByteCheck>[] lists,
        int byteOffset,
        int maxValue)
    {
        int dwordIndex = byteOffset / 4;
        int column = dwordIndex % FreezeStride;
        lists[column].Add(new FreezeTypeByteCheck
        {
            DwordIndex = dwordIndex,
            Shift = (byteOffset & 3) * 8,
            MaxValue = maxValue
        });
    }

    private static uint ReadUInt32LE(byte[] data, int offset)
    {
        return (uint)(data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24));
    }

    internal static bool TryRecoverFreezeKeyByFastBruteforce(byte[] trigData, int totalTriggers, out uint recoveredKey)
    {
        recoveredKey = 0;

        int[] anchorOffsets = CollectEncryptedTriggerOffsets(trigData, totalTriggers, 3);
        if (anchorOffsets.Length == 0)
            return false;

        Console.WriteLine("  Freeze key brute-force: direct 2^32 triggerKey search.");
        Console.WriteLine("  Anchors: " + anchorOffsets.Length + " encrypted trigger(s).");

        uint foundKey = 0;
        int found = 0;
        long totalChecked = 0;
        long fastPassed = 0;
        long fullPassed = 0;

        int degreeOfParallelism = Environment.ProcessorCount;
        uint chunkSize = uint.MaxValue / (uint)degreeOfParallelism + 1;

        Parallel.For(0, degreeOfParallelism, new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism }, delegate(int chunk)
        {
            uint start = (uint)chunk * chunkSize;
            uint end = (chunk == degreeOfParallelism - 1) ? uint.MaxValue : start + chunkSize - 1;

            uint[] addSums = new uint[FreezeStride];
            bool[] touched = new bool[FreezeStride];
            int[] touchedColumns = new int[FreezeTabCount];
            byte[] localBuffer = new byte[FreezeTrigSize];
            long localChecked = 0;

            for (uint key = start; ; key++)
            {
                if (Volatile.Read(ref found) != 0)
                    return;

                if (!FastValidateKeyAgainstAnchor(trigData, anchorOffsets[0], key, addSums, touched, touchedColumns))
                    goto next;

                Interlocked.Increment(ref fastPassed);

                if (!ValidateKeyAgainstTrigger(trigData, anchorOffsets[0], key, localBuffer))
                    goto next;

                bool anchorsMatch = true;
                for (int i = 1; i < anchorOffsets.Length; i++)
                {
                    if (!FastValidateKeyAgainstAnchor(trigData, anchorOffsets[i], key, addSums, touched, touchedColumns))
                    {
                        anchorsMatch = false;
                        break;
                    }
                }

                if (!anchorsMatch)
                    goto next;

                Interlocked.Increment(ref fullPassed);

                if (!ValidateKeyAgainstAllEncryptedTriggers(trigData, totalTriggers, key, localBuffer))
                    goto next;

                if (Interlocked.CompareExchange(ref found, 1, 0) == 0)
                {
                    foundKey = key;
                    Console.WriteLine();
                    Console.WriteLine("  Freeze triggerKey found: 0x" + key.ToString("X8"));
                }
                return;

                next:
                localChecked++;
                if (localChecked % 100000000 == 0)
                {
                    Interlocked.Add(ref totalChecked, 100000000);
                    long checkedNow = Volatile.Read(ref totalChecked);
                    double pct = checkedNow / (double)uint.MaxValue * 100.0;
                    Console.Write("\r  Progress: " + pct.ToString("F1") + "% (" +
                                  checkedNow.ToString("N0") + " keys, fast-pass " +
                                  Volatile.Read(ref fastPassed).ToString("N0") +
                                  ", full-pass " + Volatile.Read(ref fullPassed).ToString("N0") + ")   ");
                }

                if (key == end)
                    break;
            }
        });

        Console.WriteLine();
        Console.WriteLine("  Fast-pass candidates: " + Volatile.Read(ref fastPassed).ToString("N0"));
        Console.WriteLine("  Full-pass candidates: " + Volatile.Read(ref fullPassed).ToString("N0"));

        if (found == 0)
        {
            Console.WriteLine("  Freeze triggerKey brute-force failed.");
            return false;
        }

        recoveredKey = foundKey;
        return true;
    }

    private static int[] CollectEncryptedTriggerOffsets(byte[] trigData, int totalTriggers, int maxCount)
    {
        var offsets = new System.Collections.Generic.List<int>();
        for (int t = 0; t < totalTriggers && offsets.Count < maxCount; t++)
        {
            int offset = t * FreezeTrigSize;
            uint flag = BitConverter.ToUInt32(trigData, offset + 2368);
            if (flag >= 0x80000000u)
                offsets.Add(offset);
        }

        return offsets.ToArray();
    }

    private static bool FastValidateKeyAgainstAnchor(
        byte[] trigData,
        int triggerOffset,
        uint key,
        uint[] addSums,
        bool[] touched,
        int[] touchedColumns)
    {
        uint flag = BitConverter.ToUInt32(trigData, triggerOffset + 2368);
        if (flag < 0x80000000u)
            return false;

        int touchedCount = BuildFreezeAddSums(key, GetFreezeCryptFlag(flag), addSums, touched, touchedColumns);
        bool valid = true;

        for (int i = 0; i < touchedCount && valid; i++)
        {
            int column = touchedColumns[i];
            FreezeTypeByteCheck[] checks = FreezeTypeChecksByColumn[column];
            uint add = addSums[column];

            for (int c = 0; c < checks.Length; c++)
            {
                FreezeTypeByteCheck check = checks[c];
                int dwordOffset = triggerOffset + check.DwordIndex * 4;
                uint decrypted = unchecked(ReadUInt32LE(trigData, dwordOffset) + add);
                int value = (int)((decrypted >> check.Shift) & 0xFF);
                if (value > check.MaxValue)
                {
                    valid = false;
                    break;
                }
            }
        }

        ClearFreezeAddSums(addSums, touched, touchedColumns, touchedCount);
        return valid;
    }

    private static int BuildFreezeAddSums(
        uint key,
        uint flag,
        uint[] addSums,
        bool[] touched,
        int[] touchedColumns)
    {
        uint r = FreezeMix2(key, flag);
        r = FreezeMix2(r, key);

        int touchedCount = 0;
        for (int i = 0; i < FreezeTabCount; i++)
        {
            int w = (int)(r % (uint)FreezeStride);
            if (!touched[w])
            {
                touched[w] = true;
                touchedColumns[touchedCount++] = w;
            }

            addSums[w] = unchecked(addSums[w] + FreezeMix2((uint)w, (uint)i));
            r = FreezeMix2(r, key + (uint)i);
        }

        return touchedCount;
    }

    private static void ClearFreezeAddSums(uint[] addSums, bool[] touched, int[] touchedColumns, int touchedCount)
    {
        for (int i = 0; i < touchedCount; i++)
        {
            int column = touchedColumns[i];
            addSums[column] = 0;
            touched[column] = false;
        }
    }

    private static bool ValidateKeyAgainstTrigger(byte[] trigData, int triggerOffset, uint key, byte[] buffer)
    {
        if (!TryDecryptFreezeTrigger(trigData, triggerOffset, key, buffer))
            return false;

        return ValidateTriggerBodyTypes(buffer, 0);
    }

    private static bool ValidateKeyAgainstAllEncryptedTriggers(byte[] trigData, int totalTriggers, uint key, byte[] buffer)
    {
        for (int t = 0; t < totalTriggers; t++)
        {
            int offset = t * FreezeTrigSize;
            uint flag = BitConverter.ToUInt32(trigData, offset + 2368);
            if (flag < 0x80000000u)
                continue;

            if (!ValidateKeyAgainstTrigger(trigData, offset, key, buffer))
                return false;
        }

        return true;
    }

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
