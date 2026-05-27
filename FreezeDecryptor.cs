using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

internal static partial class StarcraftMapUnprotector
{
    private const uint FreezeT2Const = 0x8ADA4053;
    private const uint FreezeMixConst = 0x10F874F3;
    private const int FreezeTrigBodySize = 2368;
    private const int FreezeTabCount = 16;
    private const int FreezeStride = FreezeTrigBodySize / 32; // 74
    private const int FreezeTrigSize = 2400;
    private const int FreezeTrigDwordCount = FreezeTrigBodySize / 4;
    private const int FreezeAllTabsMask = (1 << FreezeTabCount) - 1;
    private const int StaticWlistMaxSolutions = 32;
    private const uint FreezeDeathsBase = 0x58A364u;
    private const uint FreezeCurrentPlayerAddress = 0x6509B0u;
    private const uint FreezeCurrentPlayerEpd = (FreezeCurrentPlayerAddress - FreezeDeathsBase) / 4;
    private const int FreezeRuntimeTriggerStride = 2408;
    private const int FreezeRuntimeTriggerBodyOffset = 8;
    private static readonly uint[] FreezeRuntimeTriggerBases = new uint[] { 0x51A280u, 0x51CA08u };

    private sealed class StaticWlistCandidate
    {
        public int W;
        public int TabMask;
        public int TabCount;
        public int MatchCount;
        public int Score;
    }

    private sealed class StaticWlistSolution
    {
        public int[] Wlist;
        public int Score;
    }

    private sealed class FreezeVmState
    {
        public readonly Dictionary<uint, uint> Memory = new Dictionary<uint, uint>();
        public readonly byte[] WorkingTrigData;
        public readonly bool[] EncryptedTriggers;
        public readonly int TotalTriggers;
        public readonly uint RuntimeTriggerBase;
        public uint CurrentPlayer;
        public int TriggerBodyWrites;
        public int EncryptedTriggerWrites;

        public FreezeVmState(byte[] workingTrigData, bool[] encryptedTriggers, int totalTriggers, uint runtimeTriggerBase)
        {
            WorkingTrigData = workingTrigData;
            EncryptedTriggers = encryptedTriggers;
            TotalTriggers = totalTriggers;
            RuntimeTriggerBase = runtimeTriggerBase;
            CurrentPlayer = 0;
            Memory[FreezeCurrentPlayerEpd] = 0;
        }
    }

    private sealed class FreezeVmAddress
    {
        public int TriggerIndex;
        public int BodyOffset;
    }

    private static uint FreezeT2(uint x)
    {
        uint xsq = x * x;
        uint x4 = xsq * xsq;
        return x * (xsq * (x4 + 1) + 1) + FreezeT2Const;
    }

    private static uint FreezeMix2(uint x, uint y)
    {
        return FreezeT2(x) + y + FreezeMixConst;
    }

    private static uint ComputeCryptKeyVal(uint[] seedKey)
    {
        uint v = 0;
        v = FreezeMix2(v, seedKey[0]);
        v = FreezeMix2(v, seedKey[1]);
        v = FreezeMix2(v, seedKey[2]);
        v = FreezeMix2(v, seedKey[3]);
        v = FreezeMix2(v, 0);
        return v;
    }

    private static bool TryDecryptFreezeTrigger(byte[] trigger, int offset, uint key, byte[] output)
    {
        uint flag = BitConverter.ToUInt32(trigger, offset + 2368);
        if (flag < 0x80000000u)
            return false;

        if (output != null)
            Buffer.BlockCopy(trigger, offset, output, 0, FreezeTrigSize);

        flag -= 0x80000000u;
        uint r = FreezeMix2(key, flag);
        r = FreezeMix2(r, key);

        int[] wlist = new int[FreezeTabCount];
        for (int i = 0; i < FreezeTabCount; i++)
        {
            wlist[i] = (int)(r % (uint)FreezeStride);
            r = FreezeMix2(r, key + (uint)i);
        }

        for (int i = 0; i < FreezeTabCount; i++)
        {
            int w = wlist[i];
            uint adddw = FreezeMix2((uint)w, (uint)i);
            for (int j = 0; j < 8; j++)
            {
                int pos = w * 4;
                if (output != null)
                {
                    uint dw = BitConverter.ToUInt32(output, pos);
                    dw += adddw;
                    output[pos] = (byte)dw;
                    output[pos + 1] = (byte)(dw >> 8);
                    output[pos + 2] = (byte)(dw >> 16);
                    output[pos + 3] = (byte)(dw >> 24);
                }
                w += FreezeStride;
            }
        }

        return true;
    }

    internal static int[] RecoverStaticWlistFromEncryptedTrigger(byte[] trigData, int offset)
    {
        for (int threshold = 6; threshold >= 4; threshold--)
        {
            int[] wlist = RecoverStaticWlistFromEncryptedTrigger(trigData, offset, threshold);
            if (wlist != null)
                return wlist;
        }
        return null;
    }

    private static int[] RecoverStaticWlistFromEncryptedTrigger(byte[] trigData, int offset, int threshold)
    {
        var candidates = new List<StaticWlistCandidate>();
        for (int w = 0; w < FreezeStride; w++)
        {
            for (int subsetSize = 1; subsetSize <= 4; subsetSize++)
            {
                AddStaticWlistCandidatesForSize(
                    candidates,
                    trigData,
                    offset,
                    w,
                    threshold,
                    subsetSize,
                    0,
                    0,
                    0,
                    0);
            }
        }

        if (candidates.Count == 0)
            return null;

        candidates.Sort(CompareStaticCandidates);

        var solutions = new List<StaticWlistSolution>();
        var selected = new List<StaticWlistCandidate>();
        var usedW = new bool[FreezeStride];
        SearchStaticWlistSolutions(candidates, 0, usedW, selected, solutions);

        if (solutions.Count == 0)
            return null;

        solutions.Sort(CompareStaticSolutions);

        int[] validWlist = null;
        for (int i = 0; i < solutions.Count && i < StaticWlistMaxSolutions; i++)
        {
            byte[] testBuf = new byte[FreezeTrigSize];
            if (!ApplyFreezeWlistDecryption(trigData, offset, solutions[i].Wlist, testBuf))
                continue;

            if (!ValidateDecryptedTrigger(testBuf))
                continue;

            if (validWlist == null)
            {
                validWlist = solutions[i].Wlist;
                continue;
            }

            if (!SameWlist(validWlist, solutions[i].Wlist))
            {
                Console.WriteLine("  WARNING: Static wlist recovery is ambiguous; skipping static decrypt.");
                return null;
            }
        }

        if (validWlist != null)
        {
            Console.WriteLine("  Static wlist recovered (threshold " + threshold + "): [" +
                              string.Join(", ", validWlist) + "]");
        }

        return validWlist;
    }

    private static void AddStaticWlistCandidatesForSize(
        List<StaticWlistCandidate> candidates,
        byte[] trigData,
        int offset,
        int w,
        int threshold,
        int subsetSize,
        int startTab,
        int depth,
        int tabMask,
        uint adddwSum)
    {
        if (depth == subsetSize)
        {
            uint expected = unchecked(0u - adddwSum);
            int matches = 0;
            for (int j = 0; j < 8; j++)
            {
                int pos = offset + (w + j * FreezeStride) * 4;
                if (BitConverter.ToUInt32(trigData, pos) == expected)
                    matches++;
            }

            if (matches >= threshold)
            {
                candidates.Add(new StaticWlistCandidate
                {
                    W = w,
                    TabMask = tabMask,
                    TabCount = subsetSize,
                    MatchCount = matches,
                    Score = matches * 100 - subsetSize * 10
                });
            }

            return;
        }

        int remaining = subsetSize - depth;
        for (int tab = startTab; tab <= FreezeTabCount - remaining; tab++)
        {
            uint nextSum = unchecked(adddwSum + FreezeMix2((uint)w, (uint)tab));
            AddStaticWlistCandidatesForSize(
                candidates,
                trigData,
                offset,
                w,
                threshold,
                subsetSize,
                tab + 1,
                depth + 1,
                tabMask | (1 << tab),
                nextSum);
        }
    }

    private static void SearchStaticWlistSolutions(
        List<StaticWlistCandidate> candidates,
        int selectedMask,
        bool[] usedW,
        List<StaticWlistCandidate> selected,
        List<StaticWlistSolution> solutions)
    {
        if (solutions.Count >= StaticWlistMaxSolutions)
            return;

        if (selectedMask == FreezeAllTabsMask)
        {
            int[] wlist = new int[FreezeTabCount];
            for (int i = 0; i < FreezeTabCount; i++)
                wlist[i] = -1;

            int score = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                StaticWlistCandidate c = selected[i];
                score += c.Score;
                for (int tab = 0; tab < FreezeTabCount; tab++)
                {
                    if ((c.TabMask & (1 << tab)) != 0)
                        wlist[tab] = c.W;
                }
            }

            solutions.Add(new StaticWlistSolution { Wlist = wlist, Score = score });
            return;
        }

        int bestTab = -1;
        int bestCount = int.MaxValue;
        for (int tab = 0; tab < FreezeTabCount; tab++)
        {
            int tabBit = 1 << tab;
            if ((selectedMask & tabBit) != 0)
                continue;

            int count = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                StaticWlistCandidate c = candidates[i];
                if ((c.TabMask & tabBit) == 0)
                    continue;
                if ((c.TabMask & selectedMask) != 0)
                    continue;
                if (usedW[c.W])
                    continue;
                count++;
            }

            if (count == 0)
                return;

            if (count < bestCount)
            {
                bestCount = count;
                bestTab = tab;
            }
        }

        int bestTabBit = 1 << bestTab;
        for (int i = 0; i < candidates.Count; i++)
        {
            StaticWlistCandidate c = candidates[i];
            if ((c.TabMask & bestTabBit) == 0)
                continue;
            if ((c.TabMask & selectedMask) != 0)
                continue;
            if (usedW[c.W])
                continue;

            usedW[c.W] = true;
            selected.Add(c);
            SearchStaticWlistSolutions(
                candidates,
                selectedMask | c.TabMask,
                usedW,
                selected,
                solutions);
            selected.RemoveAt(selected.Count - 1);
            usedW[c.W] = false;

            if (solutions.Count >= StaticWlistMaxSolutions)
                return;
        }
    }

    private static int CompareStaticCandidates(StaticWlistCandidate a, StaticWlistCandidate b)
    {
        int cmp = b.Score.CompareTo(a.Score);
        if (cmp != 0) return cmp;
        cmp = b.MatchCount.CompareTo(a.MatchCount);
        if (cmp != 0) return cmp;
        cmp = a.TabCount.CompareTo(b.TabCount);
        if (cmp != 0) return cmp;
        cmp = a.W.CompareTo(b.W);
        if (cmp != 0) return cmp;
        return a.TabMask.CompareTo(b.TabMask);
    }

    private static int CompareStaticSolutions(StaticWlistSolution a, StaticWlistSolution b)
    {
        return b.Score.CompareTo(a.Score);
    }

    private static bool SameWlist(int[] a, int[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }

    private static uint EpdFromDeathsTarget(uint player, ushort unit, uint currentPlayer)
    {
        uint resolvedPlayer = (player == 13u) ? currentPlayer : player;
        return unchecked(resolvedPlayer + (uint)unit * 12u);
    }

    private static uint AddressFromEpd(uint epd)
    {
        return unchecked(FreezeDeathsBase + epd * 4u);
    }

    private static bool TryMapRuntimeTriggerBody(uint address, uint runtimeTriggerBase, int totalTriggers, out FreezeVmAddress mapped)
    {
        mapped = null;

        if (address < runtimeTriggerBase)
            return false;

        uint relative = address - runtimeTriggerBase;
        uint totalSize = unchecked((uint)(totalTriggers * FreezeRuntimeTriggerStride));
        if (relative >= totalSize)
            return false;

        int triggerIndex = (int)(relative / (uint)FreezeRuntimeTriggerStride);
        int offsetInRuntimeTrigger = (int)(relative % (uint)FreezeRuntimeTriggerStride);
        if (triggerIndex < 0 || triggerIndex >= totalTriggers)
            return false;
        if (offsetInRuntimeTrigger < FreezeRuntimeTriggerBodyOffset)
            return false;
        if (offsetInRuntimeTrigger >= FreezeRuntimeTriggerBodyOffset + FreezeTrigSize)
            return false;

        int bodyOffset = offsetInRuntimeTrigger - FreezeRuntimeTriggerBodyOffset;
        if ((bodyOffset & 3) != 0)
            return false;

        mapped = new FreezeVmAddress { TriggerIndex = triggerIndex, BodyOffset = bodyOffset };
        return true;
    }

    private static bool TryReadRuntimeTriggerDword(byte[] trigData, uint runtimeTriggerBase, int totalTriggers, uint epd, out uint value)
    {
        FreezeVmAddress mapped;
        uint address = AddressFromEpd(epd);
        if (!TryMapRuntimeTriggerBody(address, runtimeTriggerBase, totalTriggers, out mapped))
        {
            value = 0;
            return false;
        }

        int offset = mapped.TriggerIndex * FreezeTrigSize + mapped.BodyOffset;
        value = BitConverter.ToUInt32(trigData, offset);
        return true;
    }

    private static uint ReadFreezeVmDword(FreezeVmState state, uint epd)
    {
        uint value;
        if (state.Memory.TryGetValue(epd, out value))
            return value;

        if (TryReadRuntimeTriggerDword(state.WorkingTrigData, state.RuntimeTriggerBase, state.TotalTriggers, epd, out value))
            return value;

        return 0;
    }

    private static uint ApplyDeathsModifier(uint oldValue, uint amount, byte modifier, bool isMasked, uint mask)
    {
        if (isMasked && modifier == 7)
            return (oldValue & ~mask) | (amount & mask);

        if (modifier == 7)
            return amount;
        if (modifier == 8)
            return unchecked(oldValue + amount);
        if (modifier == 9)
            return unchecked(oldValue - amount);

        return oldValue;
    }

    private static bool EvaluateFreezeVmCondition(FreezeVmState state, int conditionOffset)
    {
        byte conditionType = state.WorkingTrigData[conditionOffset + 15];
        if (conditionType == 0)
            return true;

        byte flags = state.WorkingTrigData[conditionOffset + 17];
        if ((flags & 2) != 0)
            return true;

        if (conditionType == 22)
            return true;
        if (conditionType == 23)
            return false;
        if (conditionType != 15)
            return false;

        uint maskOrLocation = BitConverter.ToUInt32(state.WorkingTrigData, conditionOffset);
        uint player = BitConverter.ToUInt32(state.WorkingTrigData, conditionOffset + 4);
        uint amount = BitConverter.ToUInt32(state.WorkingTrigData, conditionOffset + 8);
        ushort unit = BitConverter.ToUInt16(state.WorkingTrigData, conditionOffset + 12);
        byte comparison = state.WorkingTrigData[conditionOffset + 14];
        ushort eudx = BitConverter.ToUInt16(state.WorkingTrigData, conditionOffset + 18);

        uint epd = EpdFromDeathsTarget(player, unit, state.CurrentPlayer);
        uint value = ReadFreezeVmDword(state, epd);
        uint compareValue = (eudx == 0x4353) ? (value & maskOrLocation) : value;

        if (comparison == 0)
            return compareValue >= amount;
        if (comparison == 1)
            return compareValue <= amount;
        if (comparison == 10)
        {
            uint expected = (eudx == 0x4353) ? (amount & maskOrLocation) : amount;
            return compareValue == expected;
        }

        return false;
    }

    private static bool ShouldExecuteFreezeVmTrigger(FreezeVmState state, int triggerOffset)
    {
        for (int i = 0; i < 16; i++)
        {
            int conditionOffset = triggerOffset + i * 20;
            byte conditionType = state.WorkingTrigData[conditionOffset + 15];
            if (conditionType == 0)
                continue;

            if (!EvaluateFreezeVmCondition(state, conditionOffset))
                return false;
        }

        return true;
    }

    private static bool IsSetDeathsOnlyTrigger(byte[] data, int offset)
    {
        bool hasAction = false;
        for (int i = 0; i < 64; i++)
        {
            byte actionType = data[offset + 320 + i * 32 + 26];
            if (actionType == 0)
                continue;
            if (actionType != 45)
                return false;
            hasAction = true;
        }

        return hasAction;
    }

    private static bool IsFreezeVmTraceTrigger(byte[] data, int offset)
    {
        if (!IsSetDeathsOnlyTrigger(data, offset))
            return false;

        if (IsFreezeEudTrigger(data, offset))
            return true;

        for (int i = 0; i < 16; i++)
        {
            int conditionOffset = offset + i * 20;
            byte conditionType = data[conditionOffset + 15];
            if (conditionType == 0)
                continue;
            if (conditionType == 15)
            {
                uint player = BitConverter.ToUInt32(data, conditionOffset + 4);
                ushort eudx = BitConverter.ToUInt16(data, conditionOffset + 18);
                if (player == 13u || player > 27u || eudx == 0x4353)
                    return true;
            }
        }

        for (int i = 0; i < 64; i++)
        {
            int actionOffset = offset + 320 + i * 32;
            byte actionType = data[actionOffset + 26];
            if (actionType != 45)
                continue;

            uint player = BitConverter.ToUInt32(data, actionOffset + 16);
            if (player == 13u || player > 27u)
                return true;
        }

        return false;
    }

    private static void WriteFreezeVmDword(FreezeVmState state, uint epd, uint value)
    {
        state.Memory[epd] = value;

        if (epd == FreezeCurrentPlayerEpd)
        {
            state.CurrentPlayer = value;
            return;
        }

        FreezeVmAddress mapped;
        uint address = AddressFromEpd(epd);
        if (!TryMapRuntimeTriggerBody(address, state.RuntimeTriggerBase, state.TotalTriggers, out mapped))
            return;

        int offset = mapped.TriggerIndex * FreezeTrigSize + mapped.BodyOffset;
        state.WorkingTrigData[offset] = (byte)value;
        state.WorkingTrigData[offset + 1] = (byte)(value >> 8);
        state.WorkingTrigData[offset + 2] = (byte)(value >> 16);
        state.WorkingTrigData[offset + 3] = (byte)(value >> 24);

        state.TriggerBodyWrites++;
        if (state.EncryptedTriggers[mapped.TriggerIndex])
            state.EncryptedTriggerWrites++;
    }

    private static void ExecuteFreezeVmAction(FreezeVmState state, int actionOffset)
    {
        byte actionType = state.WorkingTrigData[actionOffset + 26];
        if (actionType != 45)
            return;

        byte flags = state.WorkingTrigData[actionOffset + 28];
        if ((flags & 2) != 0)
            return;

        uint maskOrLocation = BitConverter.ToUInt32(state.WorkingTrigData, actionOffset);
        uint player = BitConverter.ToUInt32(state.WorkingTrigData, actionOffset + 16);
        uint amount = BitConverter.ToUInt32(state.WorkingTrigData, actionOffset + 20);
        ushort unit = BitConverter.ToUInt16(state.WorkingTrigData, actionOffset + 24);
        byte modifier = state.WorkingTrigData[actionOffset + 27];
        ushort eudx = BitConverter.ToUInt16(state.WorkingTrigData, actionOffset + 30);

        uint epd = EpdFromDeathsTarget(player, unit, state.CurrentPlayer);
        uint oldValue = ReadFreezeVmDword(state, epd);
        uint newValue = ApplyDeathsModifier(oldValue, amount, modifier, eudx == 0x4353, maskOrLocation);
        WriteFreezeVmDword(state, epd, newValue);
    }

    private static void ExecuteFreezeVmTrigger(FreezeVmState state, int triggerOffset)
    {
        if (!ShouldExecuteFreezeVmTrigger(state, triggerOffset))
            return;

        for (int i = 0; i < 64; i++)
            ExecuteFreezeVmAction(state, triggerOffset + 320 + i * 32);
    }

    private static int[] TryRecoverWlistFromVmPatch(byte[] encryptedData, byte[] patchedData, int triggerOffset)
    {
        var candidates = new List<StaticWlistCandidate>();
        for (int w = 0; w < FreezeStride; w++)
        {
            var deltaCounts = new Dictionary<uint, int>();
            for (int j = 0; j < 8; j++)
            {
                int pos = triggerOffset + (w + j * FreezeStride) * 4;
                uint enc = BitConverter.ToUInt32(encryptedData, pos);
                uint dec = BitConverter.ToUInt32(patchedData, pos);
                uint delta = unchecked(dec - enc);
                if (delta == 0)
                    continue;

                int count;
                deltaCounts.TryGetValue(delta, out count);
                deltaCounts[delta] = count + 1;
            }

            foreach (var pair in deltaCounts)
            {
                if (pair.Value < 4)
                    continue;

                for (int subsetSize = 1; subsetSize <= 4; subsetSize++)
                {
                    AddVmPatchWlistCandidatesForSize(
                        candidates,
                        w,
                        pair.Key,
                        pair.Value,
                        subsetSize,
                        0,
                        0,
                        0,
                        0);
                }
            }
        }

        if (candidates.Count == 0)
            return null;

        candidates.Sort(CompareStaticCandidates);
        var solutions = new List<StaticWlistSolution>();
        var selected = new List<StaticWlistCandidate>();
        var usedW = new bool[FreezeStride];
        SearchStaticWlistSolutions(candidates, 0, usedW, selected, solutions);
        if (solutions.Count == 0)
            return null;

        solutions.Sort(CompareStaticSolutions);
        int[] first = solutions[0].Wlist;
        for (int i = 1; i < solutions.Count && i < StaticWlistMaxSolutions; i++)
        {
            if (!SameWlist(first, solutions[i].Wlist))
                return null;
        }

        return first;
    }

    private static void AddVmPatchWlistCandidatesForSize(
        List<StaticWlistCandidate> candidates,
        int w,
        uint targetDelta,
        int matchCount,
        int subsetSize,
        int startTab,
        int depth,
        int tabMask,
        uint adddwSum)
    {
        if (depth == subsetSize)
        {
            if (adddwSum == targetDelta)
            {
                candidates.Add(new StaticWlistCandidate
                {
                    W = w,
                    TabMask = tabMask,
                    TabCount = subsetSize,
                    MatchCount = matchCount,
                    Score = matchCount * 100 - subsetSize * 10
                });
            }

            return;
        }

        int remaining = subsetSize - depth;
        for (int tab = startTab; tab <= FreezeTabCount - remaining; tab++)
        {
            uint nextSum = unchecked(adddwSum + FreezeMix2((uint)w, (uint)tab));
            AddVmPatchWlistCandidatesForSize(
                candidates,
                w,
                targetDelta,
                matchCount,
                subsetSize,
                tab + 1,
                depth + 1,
                tabMask | (1 << tab),
                nextSum);
        }
    }

    private static int TryRecoverFreezeTriggersFromVm(byte[] trigData, int totalTriggers)
    {
        bool[] encryptedTriggers = new bool[totalTriggers];
        int encryptedCount = 0;
        for (int t = 0; t < totalTriggers; t++)
        {
            uint flag = BitConverter.ToUInt32(trigData, t * FreezeTrigSize + 2368);
            if (flag >= 0x80000000u)
            {
                encryptedTriggers[t] = true;
                encryptedCount++;
            }
        }

        if (encryptedCount == 0)
            return 0;

        int bestPatched = 0;
        uint bestBase = 0;
        byte[] bestData = null;
        int[] bestPatchedTriggers = null;

        for (int b = 0; b < FreezeRuntimeTriggerBases.Length; b++)
        {
            uint runtimeBase = FreezeRuntimeTriggerBases[b];
            byte[] working = new byte[trigData.Length];
            Buffer.BlockCopy(trigData, 0, working, 0, trigData.Length);

            var state = new FreezeVmState(working, encryptedTriggers, totalTriggers, runtimeBase);
            int traceTriggers = 0;
            for (int t = 0; t < totalTriggers; t++)
            {
                int offset = t * FreezeTrigSize;
                if (!IsFreezeVmTraceTrigger(working, offset))
                    continue;

                traceTriggers++;
                ExecuteFreezeVmTrigger(state, offset);
            }

            var patchedTriggers = new List<int>();
            for (int t = 0; t < totalTriggers; t++)
            {
                if (!encryptedTriggers[t])
                    continue;

                int offset = t * FreezeTrigSize;
                if (TriggerBodiesEqual(trigData, working, offset))
                    continue;

                byte[] body = new byte[FreezeTrigSize];
                Buffer.BlockCopy(working, offset, body, 0, FreezeTrigSize);
                uint originalFlag = BitConverter.ToUInt32(trigData, offset + 2368);
                RestoreFreezeExecFlags(body, 0, originalFlag);

                if (!ValidateDecryptedTrigger(body))
                    continue;

                patchedTriggers.Add(t);
            }

            if (patchedTriggers.Count > 0)
            {
                Console.WriteLine("  Freeze05 VM trace base 0x" + runtimeBase.ToString("X8") +
                                  ": " + patchedTriggers.Count + "/" + encryptedCount +
                                  " encrypted trigger(s) reconstructed (" +
                                  traceTriggers + " VM trigger(s), " +
                                  state.EncryptedTriggerWrites + " encrypted writes).");
            }

            if (patchedTriggers.Count > bestPatched)
            {
                bestPatched = patchedTriggers.Count;
                bestBase = runtimeBase;
                bestData = working;
                bestPatchedTriggers = patchedTriggers.ToArray();
            }
        }

        if (bestPatched == 0 || bestData == null || bestPatchedTriggers == null)
            return 0;

        for (int i = 0; i < bestPatchedTriggers.Length; i++)
        {
            int t = bestPatchedTriggers[i];
            int offset = t * FreezeTrigSize;
            uint originalFlag = BitConverter.ToUInt32(trigData, offset + 2368);
            int[] wlist = TryRecoverWlistFromVmPatch(trigData, bestData, offset);

            Buffer.BlockCopy(bestData, offset, trigData, offset, FreezeTrigSize);
            RestoreFreezeExecFlags(trigData, offset, originalFlag);

            Console.WriteLine("  Trigger " + t + ": VM trace reconstructed (base=0x" +
                              bestBase.ToString("X8") + ", execFlags=0x" +
                              (originalFlag & 0x0Fu).ToString("X") + ")");
            if (wlist != null)
            {
                Console.WriteLine("    VM-derived wlist: [" + string.Join(", ", wlist) + "]");
            }
        }

        return bestPatched;
    }

    private static bool TriggerBodiesEqual(byte[] a, byte[] b, int offset)
    {
        for (int i = 0; i < FreezeTrigSize; i++)
        {
            if (a[offset + i] != b[offset + i])
                return false;
        }

        return true;
    }

    private static void RestoreFreezeExecFlags(byte[] data, int offset, uint encryptedFlag)
    {
        uint restoredFlag = (encryptedFlag - 0x80000000u) & 0x0Fu;
        data[offset + 2368] = (byte)restoredFlag;
        data[offset + 2369] = 0;
        data[offset + 2370] = 0;
        data[offset + 2371] = 0;
    }

    internal static bool DecryptFreezeTriggerWithWlist(byte[] trigData, int offset, int[] wlist)
    {
        byte[] buf = new byte[FreezeTrigSize];
        if (!ApplyFreezeWlistDecryption(trigData, offset, wlist, buf))
            return false;

        Buffer.BlockCopy(buf, 0, trigData, offset, FreezeTrigSize);
        return true;
    }

    private static bool ApplyFreezeWlistDecryption(byte[] trigData, int offset, int[] wlist, byte[] output)
    {
        if (wlist == null || wlist.Length != FreezeTabCount || output == null || output.Length < FreezeTrigSize)
            return false;

        uint flag = BitConverter.ToUInt32(trigData, offset + 2368);
        if (flag < 0x80000000u)
            return false;

        Buffer.BlockCopy(trigData, offset, output, 0, FreezeTrigSize);

        for (int i = 0; i < FreezeTabCount; i++)
        {
            int w = wlist[i];
            if (w < 0 || w >= FreezeStride)
                return false;

            uint adddw = FreezeMix2((uint)w, (uint)i);
            for (int j = 0; j < 8; j++)
            {
                int pos = (w + j * FreezeStride) * 4;
                uint dw = BitConverter.ToUInt32(output, pos);
                dw = unchecked(dw + adddw);
                output[pos] = (byte)dw;
                output[pos + 1] = (byte)(dw >> 8);
                output[pos + 2] = (byte)(dw >> 16);
                output[pos + 3] = (byte)(dw >> 24);
            }
        }

        uint restoredFlag = (flag - 0x80000000u) & 0x0Fu;
        output[2368] = (byte)restoredFlag;
        output[2369] = 0;
        output[2370] = 0;
        output[2371] = 0;

        return true;
    }

    private static bool ValidateTriggerBodyTypes(byte[] data, int offset)
    {
        for (int c = 0; c < 16; c++)
        {
            if (data[offset + c * 20 + 15] > 23) return false;
        }
        for (int a = 0; a < 64; a++)
        {
            if (data[offset + 320 + a * 32 + 26] > 63) return false;
        }
        return true;
    }

    private static bool ValidateDecryptedTrigger(byte[] decrypted)
    {
        if (!ValidateTriggerBodyTypes(decrypted, 0))
            return false;

        int zeroConditions = 0;
        for (int c = 0; c < 16; c++)
        {
            bool isZero = true;
            for (int b = 0; b < 20; b++)
            {
                if (decrypted[c * 20 + b] != 0) { isZero = false; break; }
            }
            if (isZero) zeroConditions++;
        }

        int zeroActions = 0;
        for (int a = 0; a < 64; a++)
        {
            bool isZero = true;
            for (int b = 0; b < 32; b++)
            {
                if (decrypted[320 + a * 32 + b] != 0) { isZero = false; break; }
            }
            if (isZero) zeroActions++;
        }

        if (zeroConditions < 10 || zeroActions < 40)
            return false;

        return true;
    }

    private static uint BruteForceDecryptionKey(byte[] trigData, int encryptedOffset)
    {
        uint foundKey = 0;
        int found = 0;
        long totalChecked = 0;

        Console.WriteLine("  Brute-forcing Freeze trigger decryption key (2^32 search space)...");

        int degreeOfParallelism = Environment.ProcessorCount;
        uint chunkSize = uint.MaxValue / (uint)degreeOfParallelism + 1;

        Parallel.For(0, degreeOfParallelism, new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism }, delegate(int chunk)
        {
            uint start = (uint)chunk * chunkSize;
            uint end = (chunk == degreeOfParallelism - 1) ? uint.MaxValue : start + chunkSize - 1;
            byte[] localBuf = new byte[FreezeTrigSize];
            long localCount = 0;

            for (uint key = start; ; key++)
            {
                if (Volatile.Read(ref found) != 0) return;

                TryDecryptFreezeTrigger(trigData, encryptedOffset, key, localBuf);
                if (ValidateDecryptedTrigger(localBuf))
                {
                    if (Interlocked.CompareExchange(ref found, 1, 0) == 0)
                    {
                        foundKey = key;
                    }
                    return;
                }

                localCount++;
                if (localCount % 50000000 == 0)
                {
                    Interlocked.Add(ref totalChecked, 50000000);
                    long tc = Volatile.Read(ref totalChecked);
                    double pct = tc / (double)uint.MaxValue * 100.0;
                    Console.Write("\r  Progress: " + pct.ToString("F1") + "%  (" + tc.ToString("N0") + " keys tested)   ");
                }

                if (key == end) break;
            }
        });

        Console.WriteLine();

        if (found == 0)
        {
            Console.WriteLine("  WARNING: Decryption key not found.");
            return 0;
        }

        Console.WriteLine("  Decryption key found: 0x" + foundKey.ToString("X8"));
        return foundKey;
    }

    private static int DecryptAllFreezeTriggers(byte[] trigData, uint key)
    {
        int totalTriggers = trigData.Length / FreezeTrigSize;
        int decrypted = 0;

        for (int t = 0; t < totalTriggers; t++)
        {
            int offset = t * FreezeTrigSize;
            uint flag = BitConverter.ToUInt32(trigData, offset + 2368);
            if (flag < 0x80000000u)
                continue;

            byte[] buf = new byte[FreezeTrigSize];
            TryDecryptFreezeTrigger(trigData, offset, key, buf);
            Buffer.BlockCopy(buf, 0, trigData, offset, FreezeTrigSize);

            uint restoredFlag = flag - 0x80000000u;
            restoredFlag &= 0x0F;
            trigData[offset + 2368] = (byte)restoredFlag;
            trigData[offset + 2369] = 0;
            trigData[offset + 2370] = 0;
            trigData[offset + 2371] = 0;

            decrypted++;
        }

        Console.WriteLine("  Decrypted " + decrypted + " encrypted triggers.");
        return decrypted;
    }

    private static int TryDecryptFreezeTriggersStatically(byte[] trigData, int totalTriggers)
    {
        int decrypted = 0;
        for (int t = 0; t < totalTriggers; t++)
        {
            int offset = t * FreezeTrigSize;
            uint flag = BitConverter.ToUInt32(trigData, offset + 2368);
            if (flag < 0x80000000u)
                continue;

            int[] wlist = RecoverStaticWlistFromEncryptedTrigger(trigData, offset);
            if (wlist == null)
            {
                Console.WriteLine("  WARNING: Static wlist recovery failed for trigger " + t + ".");
                continue;
            }

            if (!DecryptFreezeTriggerWithWlist(trigData, offset, wlist))
            {
                Console.WriteLine("  WARNING: Static wlist decrypt failed for trigger " + t + ".");
                continue;
            }

            Console.WriteLine("  Trigger " + t + ": statically decrypted (execFlags=0x" +
                              (flag & 0x0Fu).ToString("X") + ")");
            decrypted++;
        }

        if (decrypted > 0)
            Console.WriteLine("  Static wlist decrypted " + decrypted + " trigger(s).");

        return decrypted;
    }

    private static int CountEncryptedFreezeTriggers(byte[] trigData, int totalTriggers, out int firstEncryptedOffset)
    {
        int count = 0;
        firstEncryptedOffset = -1;
        for (int t = 0; t < totalTriggers; t++)
        {
            int offset = t * FreezeTrigSize;
            uint flag = BitConverter.ToUInt32(trigData, offset + 2368);
            if (flag >= 0x80000000u)
            {
                if (firstEncryptedOffset < 0)
                    firstEncryptedOffset = offset;
                count++;
            }
        }

        return count;
    }

    private static void DisableFreezeEudTriggers(byte[] trigData, Stats stats)
    {
        int totalTriggers = trigData.Length / 2400;
        int freezeEudCandidates = CountFreezeEudTriggers(trigData);
        if (stats.DecryptedFreezeTriggers == 0 && LooksLikeEudVmTriggerSet(totalTriggers, freezeEudCandidates))
        {
            Console.WriteLine("  Freeze05: preserving EUD VM triggers (" +
                              freezeEudCandidates + "/" + totalTriggers +
                              " SetDeaths-only EUD triggers).");
            return;
        }

        for (int t = 0; t < totalTriggers; t++)
        {
            int offset = t * 2400;
            if (IsFreezeEudTrigger(trigData, offset))
            {
                for (int i = 0; i < 28; i++)
                    trigData[offset + 2372 + i] = 0;

                trigData[offset + 2368] = 0;
                trigData[offset + 2369] = 0;
                trigData[offset + 2370] = 0;
                trigData[offset + 2371] = 0;

                stats.RemovedFreezeEudTriggers++;
            }
        }
    }

    private static int CountFreezeEudTriggers(byte[] trigData)
    {
        int totalTriggers = trigData.Length / 2400;
        int count = 0;
        for (int t = 0; t < totalTriggers; t++)
        {
            if (IsFreezeEudTrigger(trigData, t * 2400))
            {
                count++;
            }
        }

        return count;
    }

    private static bool LooksLikeEudVmTriggerSet(int totalTriggers, int freezeEudCandidates)
    {
        if (totalTriggers <= 0 || freezeEudCandidates < 16)
        {
            return false;
        }

        // In normal maps Freeze05 protection is a small patcher tail. In compact
        // EUD maps the SetDeaths-only EUD triggers are the VM itself; disabling
        // them leaves the map editable but inert at runtime.
        return freezeEudCandidates * 4 >= totalTriggers;
    }

    // Apply a runtime memory dump (from Cheat Engine freeze_dump.lua) to decrypted triggers.
    // dumpData: N × 2400 bytes, index-aligned, no linked-list headers.
    // trigData: CHK TRIG section bytes (multiple of 2400).
    // Returns count of triggers patched.
    internal static int ApplyRuntimeDump(byte[] trigData, byte[] dumpData)
    {
        int chkCount  = trigData.Length / 2400;
        int dumpCount = dumpData.Length / 2400;

        int validDumpBodies = CountValidRuntimeDumpBodies(dumpData, dumpCount);
        if (dumpCount > 1 && validDumpBodies * 4 < dumpCount * 3)
        {
            Console.WriteLine("  WARNING: runtime dump quality is low (" +
                              validDumpBodies + "/" + dumpCount +
                              " valid trigger bodies). Only encrypted CHK records will be patched.");
        }

        if (dumpCount < chkCount)
        {
            Console.WriteLine("  WARNING: dump has " + dumpCount + " triggers, CHK has " + chkCount +
                              ". Only patching available range.");
            chkCount = dumpCount;
        }

        int patched = 0;
        for (int t = 0; t < chkCount; t++)
        {
            int chkOff  = t * 2400;
            int dumpOff = t * 2400;

            uint chkFlag = BitConverter.ToUInt32(trigData, chkOff + 2368);
            if (chkFlag < 0x80000000u)
                continue;  // not encrypted in CHK, skip

            if (!ValidateTriggerBodyTypes(dumpData, dumpOff))
            {
                Console.WriteLine("  WARNING: dump trigger " + t + " body invalid — skipping " +
                                  "(game may not have decrypted it yet)");
                continue;
            }

            // Copy dump body into CHK
            Buffer.BlockCopy(dumpData, dumpOff, trigData, chkOff, 2400);

            // Restore exec_flags: strip bit 31 and random bits, keep lower 4 bits
            uint restoredFlag = (chkFlag - 0x80000000u) & 0x0Fu;
            trigData[chkOff + 2368] = (byte)restoredFlag;
            trigData[chkOff + 2369] = 0;
            trigData[chkOff + 2370] = 0;
            trigData[chkOff + 2371] = 0;

            Console.WriteLine("  Trigger " + t + ": patched (chkFlag=0x" + chkFlag.ToString("X8") +
                              " -> execFlags=0x" + restoredFlag.ToString("X2") + ")");
            patched++;
        }

        return patched;
    }

    private static int CountValidRuntimeDumpBodies(byte[] dumpData, int dumpCount)
    {
        int validBodies = 0;
        for (int t = 0; t < dumpCount; t++)
        {
            if (ValidateTriggerBodyTypes(dumpData, t * 2400))
                validBodies++;
        }
        return validBodies;
    }

    private static int ProcessFreezeProtection(Dictionary<string, List<byte[]>> grouped, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("TRIG", out list) || list.Count == 0)
            return 0;

        byte[] data = list[0];
        if (data.Length % FreezeTrigSize != 0)
            return 0;

        int totalTriggers = data.Length / FreezeTrigSize;
        Console.WriteLine("  Freeze05: Processing " + totalTriggers + " triggers...");

        int encryptedOffset;
        int encryptedCount = CountEncryptedFreezeTriggers(data, totalTriggers, out encryptedOffset);

        Console.WriteLine("  Freeze05: " + encryptedCount + " encrypted triggers found.");

        int decrypted = 0;

        if (encryptedCount > 0)
        {
            int staticDecrypted = TryDecryptFreezeTriggersStatically(data, totalTriggers);
            decrypted += staticDecrypted;
            stats.DecryptedFreezeTriggers = decrypted;

            int remainingEncrypted = CountEncryptedFreezeTriggers(data, totalTriggers, out encryptedOffset);
            if (remainingEncrypted == 0)
            {
                Console.WriteLine("  Static wlist decrypted all Freeze05 encrypted triggers.");
            }
            else
            {
                Console.WriteLine("  WARNING: Static wlist recovery decrypted " + staticDecrypted + "/" +
                                  encryptedCount + " trigger(s); " + remainingEncrypted +
                                  " trigger(s) remain encrypted.");

                int vmPatched = TryRecoverFreezeTriggersFromVm(data, totalTriggers);
                if (vmPatched > 0)
                {
                    decrypted += vmPatched;
                    stats.DecryptedFreezeTriggers = decrypted;
                    Console.WriteLine("  Freeze05 VM trace reconstructed " + vmPatched + " trigger(s).");
                }

                remainingEncrypted = CountEncryptedFreezeTriggers(data, totalTriggers, out encryptedOffset);
            }

            if (remainingEncrypted > 0 && !string.IsNullOrEmpty(stats.FreezeApplyDumpPath))
            {
                // --- Runtime dump path: apply CE memory dump only after static and VM recovery fail. ---
                Console.WriteLine("  Using runtime dump fallback: " + stats.FreezeApplyDumpPath);
                try
                {
                    byte[] dumpData = System.IO.File.ReadAllBytes(stats.FreezeApplyDumpPath);
                    int patched = ApplyRuntimeDump(data, dumpData);
                    if (patched > 0)
                    {
                        decrypted += patched;
                        stats.DecryptedFreezeTriggers = decrypted;
                        Console.WriteLine("  Applied runtime dump: " + patched + " trigger(s) decrypted.");
                    }
                    else
                    {
                        Console.WriteLine("  WARNING: Runtime dump applied but no triggers were patched.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  ERROR reading dump file: " + ex.Message);
                }

                remainingEncrypted = CountEncryptedFreezeTriggers(data, totalTriggers, out encryptedOffset);
            }

            if (remainingEncrypted > 0 && !string.IsNullOrEmpty(FreezeRecoverDumpPath))
            {
                // --- Key recovery path: recover key from encrypted/decrypted pair. ---
                Console.WriteLine("  Key recovery mode: comparing CHK with runtime dump...");
                try
                {
                    byte[] dumpData = System.IO.File.ReadAllBytes(FreezeRecoverDumpPath);
                    int dumpTrigCount = dumpData.Length / FreezeTrigSize;

                    // Find first encrypted trigger that has a valid dump counterpart
                    int refTrigIndex = -1;
                    for (int t = 0; t < totalTriggers && t < dumpTrigCount; t++)
                    {
                        uint fl = BitConverter.ToUInt32(data, t * 2400 + 2368);
                        if (fl >= 0x80000000u)
                        {
                            refTrigIndex = t;
                            break;
                        }
                    }

                    if (refTrigIndex >= 0)
                    {
                        Console.WriteLine("  Using trigger " + refTrigIndex + " for wlist recovery...");
                        int[] wlist = RecoverWlistFromDump(data, refTrigIndex * FreezeTrigSize, dumpData, refTrigIndex * FreezeTrigSize);
                        if (wlist != null)
                        {
                            uint fl = BitConverter.ToUInt32(data, refTrigIndex * FreezeTrigSize + 2368);
                            uint flagForCrypt = fl - 0x80000000u;
                            uint recoveredKey = RecoverFreezeKey(flagForCrypt, wlist);
                            if (recoveredKey != 0 || wlist[0] == 0)
                            {
                                // Verify by decrypting the reference trigger
                                byte[] testBuf = new byte[FreezeTrigSize];
                                TryDecryptFreezeTrigger(data, refTrigIndex * FreezeTrigSize, recoveredKey, testBuf);
                                if (ValidateDecryptedTrigger(testBuf))
                                {
                                    Console.WriteLine("  Key 0x" + recoveredKey.ToString("X8") +
                                                      " validated! Decrypting all triggers...");
                                    int keyDecrypted = DecryptAllFreezeTriggers(data, recoveredKey);
                                    decrypted += keyDecrypted;
                                    stats.DecryptedFreezeTriggers = decrypted;
                                }
                                else
                                {
                                    Console.WriteLine("  WARNING: Recovered key failed validation. " +
                                                      "Falling back to runtime dump apply...");
                                    int patched = ApplyRuntimeDump(data, dumpData);
                                    decrypted += patched;
                                    stats.DecryptedFreezeTriggers = decrypted;
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("  Wlist recovery failed. Falling back to runtime dump apply...");
                            int patched = ApplyRuntimeDump(data, dumpData);
                            decrypted += patched;
                            stats.DecryptedFreezeTriggers = decrypted;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  ERROR in key recovery: " + ex.Message);
                }
            }
        }

        DisableFreezeEudTriggers(data, stats);

        return decrypted;
    }
}
