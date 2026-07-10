using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TkMPQLib;

internal static partial class StarcraftMapUnprotector
{
    private sealed class FreezeVmRunResult
    {
        public FreezeVmMemory Memory;
        public FreezeTriggerVm Vm;
        public long TriggersExecuted;
        public bool HitCap;
        public string StopReason;
    }

    private sealed class FreezeObjumpInfo
    {
        public int ArrayChkOffset;
        public bool FromRuntime;
        public int ExecutionProofs;
        public int CompensationProofs;
        public readonly List<uint> Slots = new List<uint>();
        public readonly List<uint> Targets = new List<uint>();
    }

    private sealed class FreezeKeyCandidate
    {
        public int ChainLength;
        public int NonTrivial;
        public uint Slot;
        public uint Value;
    }

    private sealed class FreezeUnfreezeResult
    {
        public byte[] Chk;
        public uint ActualKey;
        public string KeyMethod;
        public int EncryptedTriggers;
        public int DecryptedTriggers;
        public int ObjumpCount;
        public long VmTriggers;
        public long VmActions;
        public long VmMilliseconds;
    }

    private static FreezeUnfreezeResult BuildMelterFreezeChk(byte[] inputBytes, byte[] chk, Stats stats)
    {
        if (LooksLikeChk(inputBytes))
            throw new InvalidDataException("Lv2 requires an MPQ .scx/.scm input; raw CHK has no Freeze keycalc context.");

        byte[] trig;
        if (!TryGetFirstChkSection(chk, "TRIG", out trig) || trig.Length == 0 || trig.Length % FreezeTrigSize != 0)
            throw new InvalidDataException("Lv2: a valid TRIG section was not found.");

        List<int> encrypted = FindEncryptedFreezeTriggerIndexes(trig);
        if (!stats.IsFreezeProtected && encrypted.Count == 0)
            throw new InvalidDataException("Lv2: this is not a Freeze-protected map.");

        byte[] header;
        HashTable[] vmHashes;
        BlockTable[] vmBlocks;
        if (!TryRecoverFreezeVmTables(inputBytes, out header, out vmHashes, out vmBlocks))
            throw new InvalidDataException("Lv2: MPQ hash/block tables could not be recovered for the Freeze VM.");

        var watch = Stopwatch.StartNew();
        FreezeVmRunResult vmRun = RunFreezeVm(chk, trig, inputBytes, header, vmHashes, vmBlocks);
        watch.Stop();
        Console.WriteLine("  Lv2 VM trace       : " + vmRun.TriggersExecuted + " triggers, stop=" + vmRun.StopReason);
        if (vmRun.HitCap)
            Console.WriteLine("  WARNING: Freeze VM reached the trigger cap; attempting key validation from captured state.");

        uint actualKey = 0;
        string keyMethod = "defang-only";
        if (encrypted.Count > 0)
        {
            if (TryRecoverActualKeyFromSelfMod(trig, encrypted, vmRun.Memory, vmRun.Vm, out actualKey))
            {
                keyMethod = "self-mod";
            }
            else if (TryRecoverActualKeyFromMemory(trig, encrypted, vmRun.Memory, stats, out actualKey))
            {
                keyMethod = "memory-scan";
            }
            else
            {
                Console.WriteLine("  Lv2: VM key recovery failed; trying the legacy 2^32 search...");
                if (!TryRecoverFreezeKeyByFastBruteforce(trig, trig.Length / FreezeTrigSize, out actualKey))
                    throw new InvalidDataException("Lv2: actualKey recovery failed in all strategies.");
                keyMethod = "legacy-2^32";
            }

            if (!ValidateActualKeyAcrossTriggers(trig, encrypted, actualKey))
                throw new InvalidDataException("Lv2: recovered actualKey failed cross-trigger validation.");
        }

        FreezePayloadLayout layout = AnalyzeFreezePayload(chk);
        FreezeObjumpInfo objump;
        if (layout.Valid)
        {
            objump = DetectFreezeObjump(chk, layout, vmRun.Vm, vmRun.Memory);
            if (objump == null)
                throw new InvalidDataException("Lv2: obf-jump candidates exist but could not be proven; refusing to emit an edit-unsafe map.");
            if (vmRun.HitCap && objump.Slots.Count == 0)
                throw new InvalidDataException("Lv2: Freeze VM loop did not finish and no obf-jump defang was proven; refusing to emit a map that may hang.");
        }
        else
        {
            objump = new FreezeObjumpInfo();
            Console.WriteLine("  Lv2 obf-jump scan : no STRx payload; treating as encryption-only Freeze variant");
        }

        byte[] patchedChk = (byte[])chk.Clone();
        if (objump.Slots.Count > 0) ApplyFreezeObjumpPatch(patchedChk, layout, objump);
        byte[] decryptedTrig = (byte[])trig.Clone();
        int decrypted = 0;
        if (encrypted.Count > 0)
        {
            foreach (int index in encrypted)
            {
                int offset = index * FreezeTrigSize;
                byte[] one = new byte[FreezeTrigSize];
                if (!TryDecryptFreezeTrigger(decryptedTrig, offset, actualKey, one))
                    throw new InvalidDataException("Lv2: trigger " + index + " could not be decrypted.");
                Buffer.BlockCopy(one, 0, decryptedTrig, offset, FreezeTrigSize);
                uint oldFlag = BitConverter.ToUInt32(trig, offset + 2368);
                WriteUInt32At(decryptedTrig, offset + 2368, oldFlag & 0x0Fu);
                decrypted++;
            }
        }
        patchedChk = ReplaceTrigSection(patchedChk, decryptedTrig);
        if (patchedChk.Length != chk.Length)
            throw new InvalidDataException("Lv2: Freeze patch unexpectedly resized scenario.chk.");

        stats.DecryptedFreezeTriggers = decrypted;
        Console.WriteLine("  Lv2 VM             : " + vmRun.TriggersExecuted + " triggers, " +
                          vmRun.Vm.ActionsExecuted + " actions, " + watch.ElapsedMilliseconds + " ms");
        Console.WriteLine("  Lv2 key method     : " + keyMethod +
                          (encrypted.Count > 0 ? " actualKey=0x" + actualKey.ToString("X8") : ""));
        Console.WriteLine("  Lv2 decrypted      : " + decrypted + "/" + encrypted.Count);
        Console.WriteLine("  Lv2 obf-jumps      : " + objump.Slots.Count + " defanged");

        return new FreezeUnfreezeResult
        {
            Chk = patchedChk,
            ActualKey = actualKey,
            KeyMethod = keyMethod,
            EncryptedTriggers = encrypted.Count,
            DecryptedTriggers = decrypted,
            ObjumpCount = objump.Slots.Count,
            VmTriggers = vmRun.TriggersExecuted,
            VmActions = vmRun.Vm.ActionsExecuted,
            VmMilliseconds = watch.ElapsedMilliseconds
        };
    }

    private static bool TryRecoverFreezeVmTables(
        byte[] inputBytes,
        out byte[] headerBytes,
        out HashTable[] hashes,
        out BlockTable[] blocks)
    {
        headerBytes = null;
        hashes = null;
        blocks = null;

        MpqTableLocation located = LocateScenarioTablesForPatch(inputBytes);
        if (located != null)
        {
            headerBytes = new byte[32];
            Buffer.BlockCopy(inputBytes, located.Header.BaseOffset, headerBytes, 0, 32);
            hashes = located.Hashes;
            blocks = ReadFreezeVmBlockTable(inputBytes, located);
            return true;
        }

        try
        {
            using (var stream = new MemoryStream(inputBytes, false))
            using (var mpq = new TkMPQ(stream))
            {
                var hashField = typeof(TkMPQ).GetField("HashTables", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var blockField = typeof(TkMPQ).GetField("BlockTables", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                HashTable[] normalHashes = hashField == null ? null : hashField.GetValue(mpq) as HashTable[];
                BlockTable[] normalBlocks = blockField == null ? null : blockField.GetValue(mpq) as BlockTable[];
                if (normalHashes != null && normalBlocks != null && normalHashes.Length > 0 && normalBlocks.Length > 0)
                {
                    headerBytes = new byte[32];
                    Buffer.BlockCopy(inputBytes, 0, headerBytes, 0, 32);
                    hashes = normalHashes;
                    blocks = normalBlocks;
                    return true;
                }
            }
        }
        catch { }

        foreach (MpqHeaderCandidate header in FindMpqHeaderCandidates(inputBytes))
        {
            var hashOffsets = new List<int>();
            AddFixedHashCandidates(hashOffsets, inputBytes.Length, header.BaseOffset,
                header.HashTableOffset, header.HashCount);
            hashOffsets.AddRange(FindHashTableByPattern(inputBytes, header.BaseOffset + 32, header.HashCount));
            foreach (int hashOffset in hashOffsets.Distinct())
            {
                HashTable[] candidateHashes;
                try { candidateHashes = ReadHashTable(inputBytes, hashOffset, header.HashCount); }
                catch { continue; }
                if (!candidateHashes.Any(IsScenarioHash)) continue;

                BlockTable[] candidateBlocks = null;
                try
                {
                    if (header.BlockCount * 16 <= inputBytes.Length)
                        candidateBlocks = ReadBlockTable(inputBytes, 0, header.BlockCount);
                }
                catch { }
                if (candidateBlocks == null)
                {
                    foreach (int blockOffset in BuildAdjacentBlockCandidates(inputBytes, hashOffset,
                        header.HashCount, header.BlockTableOffset, header.BlockCount, header.BaseOffset))
                    {
                        try
                        {
                            candidateBlocks = ReadBlockTable(inputBytes, blockOffset, header.BlockCount);
                            break;
                        }
                        catch { }
                    }
                }
                if (candidateBlocks == null) continue;

                headerBytes = new byte[32];
                Buffer.BlockCopy(inputBytes, header.BaseOffset, headerBytes, 0, 32);
                hashes = candidateHashes;
                blocks = candidateBlocks;
                return true;
            }
        }
        return false;
    }
    private static BlockTable[] ReadFreezeVmBlockTable(byte[] inputBytes, MpqTableLocation tables)
    {
        try
        {
            int count = tables.Header.BlockCount;
            if (count > 0 && count <= 65536 && count * 16 <= inputBytes.Length)
                return ReadBlockTable(inputBytes, 0, count);
        }
        catch { }
        return tables.Blocks;
    }

    private static FreezeVmRunResult RunFreezeVm(
        byte[] chk,
        byte[] trig,
        byte[] rawMpq,
        byte[] header,
        HashTable[] hashes,
        BlockTable[] blocks)
    {
        var memory = new FreezeVmMemory();
        var vm = new FreezeTriggerVm(memory);
        vm.InitializeStaticAreas();
        memory.Lenient = true;
        vm.LoadChkSections(chk);
        vm.MockStormMpq(header, hashes, blocks, rawMpq);

        const uint runtimeBase = FreezeSc.RuntimeTrigAlloc;
        int triggerCount = trig.Length / FreezeTrigSize;
        uint runtimeSize = checked((uint)triggerCount * 2408u);
        memory.Alloc(runtimeBase, runtimeSize, "Runtime TRIG");
        for (int i = 0; i < triggerCount; i++)
        {
            uint baseAddress = runtimeBase + (uint)i * 2408u;
            memory.WriteBytes(baseAddress + 8, trig, i * FreezeTrigSize, FreezeTrigSize);
        }

        for (int player = 0; player < 8; player++)
        {
            var indexes = new List<int>();
            for (int i = 0; i < triggerCount; i++)
            {
                if (FreezePlayerExecutes(trig, i * FreezeTrigSize + 2372, player)) indexes.Add(i);
            }
            uint pts = FreezeSc.PlayerTrigStructBase + (uint)player * 12u;
            if (indexes.Count == 0)
            {
                memory.Write32(pts + 4, pts);
                memory.Write32(pts + 8, pts);
                continue;
            }
            for (int k = 0; k < indexes.Count; k++)
            {
                uint current = runtimeBase + (uint)indexes[k] * 2408u;
                uint prev = k == 0 ? pts : runtimeBase + (uint)indexes[k - 1] * 2408u;
                uint next = k + 1 == indexes.Count ? pts : runtimeBase + (uint)indexes[k + 1] * 2408u;
                memory.Write32(current, prev);
                memory.Write32(current + 4, next);
            }
            memory.Write32(pts + 4, runtimeBase + (uint)indexes[indexes.Count - 1] * 2408u);
            memory.Write32(pts + 8, runtimeBase + (uint)indexes[0] * 2408u);
        }

        byte[] pupxSnapshot = memory.ReadBytes(FreezeSc.BwUpgradesAvailable, 360);
        byte[] ptexSnapshot = memory.ReadBytes(FreezeSc.BwTechAvailable, 480);
        memory.WriteBytes(FreezeSc.BwUpgradesAvailable, pupxSnapshot);
        memory.WriteBytes(FreezeSc.BwTechAvailable, ptexSnapshot);
        memory.Write32(FreezeSc.CurrentPlayer, 0);

        uint pts0 = FreezeSc.PlayerTrigStructBase;
        uint triggerAddress = memory.Read32(pts0 + 8);
        long executed = 0;
        string stop = "unknown";
        while (triggerAddress != pts0 && executed < 20000000L)
        {

            bool runtime = triggerAddress >= runtimeBase &&
                           triggerAddress < unchecked(runtimeBase + runtimeSize) &&
                           (triggerAddress - runtimeBase) % 2408u == 0;
            if (!runtime && memory.Find(triggerAddress) == null)
            {
                stop = "unmapped payload trigger 0x" + triggerAddress.ToString("X8");
                break;
            }
            uint executeAddress = triggerAddress + 8;
            vm.ExecuteAt(executeAddress);
            executed++;
            uint next = memory.Read32(triggerAddress + 4);
            if (next == 0)
            {
                stop = "zero terminator";
                break;
            }
            if ((next & 1u) != 0)
            {
                stop = "odd sentinel";
                break;
            }
            if (next >= 0x80000000u)
            {
                stop = "high sentinel";
                break;
            }
            triggerAddress = next;
        }
        if (triggerAddress == pts0) stop = "PTS sentinel";
        bool cap = executed >= 20000000L;
        if (cap) stop = "safety cap";

        return new FreezeVmRunResult
        {
            Memory = memory,
            Vm = vm,
            TriggersExecuted = executed,
            HitCap = cap,
            StopReason = stop
        };
    }

    private static bool FreezePlayerExecutes(byte[] trig, int offset, int player)
    {
        if (trig[offset + player] != 0 || trig[offset + 17] != 0) return true;
        for (int force = 0; force < 4; force++) if (trig[offset + 18 + force] != 0) return true;
        return false;
    }

    private static List<int> FindEncryptedFreezeTriggerIndexes(byte[] trig)
    {
        var result = new List<int>();
        for (int i = 0; i < trig.Length / FreezeTrigSize; i++)
        {
            if ((BitConverter.ToUInt32(trig, i * FreezeTrigSize + 2368) & 0x80000000u) != 0) result.Add(i);
        }
        return result;
    }

    private static bool ValidateActualKeyAcrossTriggers(byte[] trig, List<int> encrypted, uint key)
    {
        foreach (int index in encrypted)
        {
            if (!ValidateFreezeActualKey(trig, index * FreezeTrigSize, key)) return false;
        }
        return true;
    }

    private static bool ValidateFreezeActualKey(byte[] trig, int offset, uint key)
    {
        byte[] decrypted = new byte[FreezeTrigSize];
        if (!TryDecryptFreezeTrigger(trig, offset, key, decrypted)) return false;
        for (int action = 0; action < 64; action++)
        {
            if (decrypted[320 + action * 32 + 26] > 57) return false;
        }
        return true;
    }

    private static bool TryRecoverActualKeyFromSelfMod(
        byte[] trig,
        List<int> encrypted,
        FreezeVmMemory memory,
        FreezeTriggerVm vm,
        out uint key)
    {
        key = 0;
        var lastOwner = new Dictionary<uint, uint>();
        var ranked = new List<FreezeKeyCandidate>();
        foreach (KeyValuePair<uint, FreezeSelfModWriteSummary> pair in vm.SelfModWrites)
            lastOwner[pair.Key] = pair.Value.LastOwnerAddress;

        foreach (KeyValuePair<uint, FreezeSelfModWriteSummary> pair in vm.SelfModWrites)
        {
            FreezeSelfModWriteSummary summary = pair.Value;
            if (summary.MultipleValues || summary.Count < 2) continue;
            ranked.Add(new FreezeKeyCandidate
            {
                ChainLength = FreezeSelfModChainLength(pair.Key, memory, lastOwner),
                NonTrivial = summary.Value >= 0x10000u && !IsFreezePayloadAddress(summary.Value) ? 1 : 0,
                Slot = pair.Key,
                Value = summary.Value
            });
        }

        foreach (FreezeKeyCandidate candidate in ranked
            .OrderByDescending(c => c.ChainLength)
            .ThenByDescending(c => c.NonTrivial)
            .ThenByDescending(c => c.Slot)
            .ThenByDescending(c => c.Value))
        {
            if (ValidateActualKeyAcrossTriggers(trig, encrypted, candidate.Value))
            {
                key = candidate.Value;
                Console.WriteLine("  Lv2 self-mod       : candidates=" + ranked.Count +
                                  " chain=" + candidate.ChainLength + " slot=0x" + candidate.Slot.ToString("X8"));
                return true;
            }
        }
        Console.WriteLine("  Lv2 self-mod       : " + ranked.Count + " candidates, no validated key");
        return false;
    }

    private static int FreezeSelfModChainLength(uint slot, FreezeVmMemory memory, Dictionary<uint, uint> lastOwner)
    {
        var seen = new HashSet<uint>();
        uint current = slot;
        uint value = memory.Read32(slot);
        int length = 0;
        while (seen.Add(current) && length < 200)
        {
            uint owner;
            if (!lastOwner.TryGetValue(current, out owner)) break;
            uint valueField = unchecked(owner + 0x154u);
            if (!IsFreezePayloadAddress(valueField) || memory.Read32(valueField) != value) break;
            current = valueField;
            length++;
        }
        return length;
    }

    private static bool TryRecoverActualKeyFromMemory(
        byte[] trig,
        List<int> encrypted,
        FreezeVmMemory memory,
        Stats stats,
        out uint key)
    {
        key = 0;
        uint cryptKey = stats.FreezeSeedKey != null && stats.FreezeSeedKey.Length >= 4
            ? ComputeCryptKeyVal(stats.FreezeSeedKey)
            : 0;
        var candidates = new HashSet<uint>();
        foreach (FreezeVmMemory.Block block in memory.Blocks)
        {
            for (int i = 0; i + 4 <= block.Data.Length; i += 4)
                candidates.Add(BitConverter.ToUInt32(block.Data, i));
        }
        foreach (uint candidate in candidates)
        {
            if (ValidateActualKeyAcrossTriggers(trig, encrypted, candidate))
            {
                key = candidate;
                Console.WriteLine("  Lv2 memory scan    : " + candidates.Count + " dwords, direct key");
                return true;
            }
            uint mixed = FreezeMix2(candidate, cryptKey);
            if (ValidateActualKeyAcrossTriggers(trig, encrypted, mixed))
            {
                key = mixed;
                Console.WriteLine("  Lv2 memory scan    : " + candidates.Count + " dwords, mixed key");
                return true;
            }
        }
        Console.WriteLine("  Lv2 memory scan    : " + candidates.Count + " dwords, no key");
        return false;
    }

    private static bool IsFreezePayloadAddress(uint address)
    {
        return address >= 0x19000000u && address < 0x1C000000u;
    }

    private static FreezeObjumpInfo DetectFreezeObjump(
        byte[] chk,
        FreezePayloadLayout layout,
        FreezeTriggerVm vm,
        FreezeVmMemory memory)
    {
        uint begin = layout.PayloadMemoryAddress;
        uint end = unchecked(begin + (uint)layout.PayloadSize);
        int chkBegin = layout.StrxChkOffset + layout.PayloadStrxOffset;
        int chkEnd = chkBegin + layout.PayloadSize;
        var candidates = new List<FreezeObjumpInfo>();

        int i = chkBegin;
        while (i + 4 <= chkEnd)
        {
            uint decoded = DecodeFreezeEpd(BitConverter.ToUInt32(chk, i));
            if (decoded >= begin && decoded < end)
            {
                var candidate = new FreezeObjumpInfo { ArrayChkOffset = i };
                int j = i;
                while (j + 4 <= chkEnd)
                {
                    uint slot = DecodeFreezeEpd(BitConverter.ToUInt32(chk, j));
                    if (slot < begin || slot >= end) break;
                    candidate.Slots.Add(slot);
                    j += 4;
                }
                bool terminated = j + 4 <= chkEnd && BitConverter.ToUInt32(chk, j) == 0;
                if (candidate.Slots.Count >= 2 && terminated) candidates.Add(candidate);
                i = Math.Max(j, i + 1);
            }
            else
            {
                i++;
            }
        }

        Console.WriteLine("  Lv2 obf-jump scan : " + candidates.Count + " static candidate(s)");
        if (candidates.Count == 0)
        {
            int relative = 0;
            while (relative + 4 <= layout.PayloadSize)
            {
                uint decoded = DecodeFreezeEpd(memory.Read32(unchecked(begin + (uint)relative)));
                if (decoded >= begin && decoded < end)
                {
                    var candidate = new FreezeObjumpInfo
                    {
                        ArrayChkOffset = chkBegin + relative,
                        FromRuntime = true
                    };
                    int next = relative;
                    while (next + 4 <= layout.PayloadSize)
                    {
                        uint slot = DecodeFreezeEpd(memory.Read32(unchecked(begin + (uint)next)));
                        if (slot < begin || slot >= end) break;
                        candidate.Slots.Add(slot);
                        next += 4;
                    }
                    bool terminated = next + 4 <= layout.PayloadSize &&
                                      memory.Read32(unchecked(begin + (uint)next)) == 0;
                    if (candidate.Slots.Count >= 2 && terminated) candidates.Add(candidate);
                    relative = Math.Max(next, relative + 1);
                }
                else
                {
                    relative++;
                }
            }
            Console.WriteLine("  Lv2 obf-jump scan : " + candidates.Count + " runtime candidate(s)");
        }
        FreezeObjumpInfo best = null;
        foreach (FreezeObjumpInfo candidate in candidates)
        {
            int verified = 0;
            int inferredCount = 0;
            foreach (uint slot in candidate.Slots)
            {
                uint nextExecuted;
                Dictionary<uint, uint> edges = candidate.FromRuntime
                    ? vm.LastNextByExecutedAddress
                    : vm.FirstNextByExecutedAddress;
                if (!edges.TryGetValue(unchecked(slot + 4u), out nextExecuted))
                {
                    uint inferredTarget;
                    if (candidate.FromRuntime && TryInferFreezeObjumpTarget(slot, begin, end, memory, out inferredTarget))
                    {
                        candidate.Targets.Add(inferredTarget);
                        verified++;
                        inferredCount++;
                        continue;
                    }
                    candidate.Targets.Add(0);
                    continue;
                }
                uint target = unchecked(nextExecuted - 8u);
                if (target < begin || target >= end)
                {
                    candidate.Targets.Add(0);
                    continue;
                }
                candidate.Targets.Add(target);
                verified++;
            }
            candidate.ExecutionProofs = verified - inferredCount;
            candidate.CompensationProofs = inferredCount;
            if (verified == candidate.Slots.Count && candidate.Targets.Count == candidate.Slots.Count &&
                (best == null || candidate.Slots.Count > best.Slots.Count)) best = candidate;
        }
        if (best == null && candidates.Count == 0) return new FreezeObjumpInfo();
        if (best != null)
            Console.WriteLine("  Lv2 obf-jump proof: " + (best.FromRuntime ? "runtime" : "static") +
                              " slots=" + best.Slots.Count +
                              " execution=" + best.ExecutionProofs +
                              " compensation=" + best.CompensationProofs);
        return best;
    }

    private static bool TryInferFreezeObjumpTarget(
        uint slot,
        uint begin,
        uint end,
        FreezeVmMemory memory,
        out uint target)
    {
        target = 0;
        if (memory.Read8(unchecked(slot + 350u)) != 45 || memory.Read8(unchecked(slot + 351u)) != 8)
            return false;
        uint addValue = memory.Read32(unchecked(slot + 344u));
        uint negativeValue = unchecked(0u - addValue);
        int matches = 0;
        for (uint candidate = begin; candidate + 356u < end; candidate += 4u)
        {
            if (memory.Read8(unchecked(candidate + 354u)) != 45) continue;
            byte modifier = memory.Read8(unchecked(candidate + 355u));
            uint value = memory.Read32(unchecked(candidate + 348u));
            if (!((modifier == 8 && value == negativeValue) || (modifier == 9 && value == addValue)))
                continue;
            target = candidate;
            matches++;
            if (matches > 1) return false;
        }
        return matches == 1;
    }

    private static uint DecodeFreezeEpd(uint value)
    {
        return unchecked(value * 4u + FreezeSc.DeathTable);
    }

    private static void ApplyFreezeObjumpPatch(byte[] chk, FreezePayloadLayout layout, FreezeObjumpInfo objump)
    {
        var writes = new List<KeyValuePair<int, uint>>();
        for (int i = 0; i <= objump.Slots.Count; i++)
            writes.Add(new KeyValuePair<int, uint>(objump.ArrayChkOffset + i * 4, 0));

        for (int i = 0; i < objump.Slots.Count; i++)
        {
            int slotOffset = FreezeMemoryToChk(layout, objump.Slots[i]);
            int plusOffset = FreezeMemoryToChk(layout, unchecked(objump.Slots[i] + 344u));
            int minusOffset = FreezeMemoryToChk(layout, unchecked(objump.Targets[i] + 348u));
            writes.Add(new KeyValuePair<int, uint>(slotOffset, objump.Targets[i]));
            writes.Add(new KeyValuePair<int, uint>(plusOffset, 0));
            writes.Add(new KeyValuePair<int, uint>(minusOffset, 0));
        }

        foreach (KeyValuePair<int, uint> write in writes)
        {
            if (write.Key < 0 || write.Key + 4 > chk.Length)
                throw new InvalidDataException("Lv2: obf-jump patch offset is outside scenario.chk.");
        }
        foreach (KeyValuePair<int, uint> write in writes) WriteUInt32At(chk, write.Key, write.Value);
    }

    private static int FreezeMemoryToChk(FreezePayloadLayout layout, uint address)
    {
        if (address < layout.PayloadMemoryAddress ||
            address + 4u > unchecked(layout.PayloadMemoryAddress + (uint)layout.PayloadSize))
            throw new InvalidDataException("Lv2: obf-jump address 0x" + address.ToString("X8") + " is outside STRx payload.");
        return checked(layout.StrxChkOffset + layout.PayloadStrxOffset + (int)(address - layout.PayloadMemoryAddress));
    }

    private static void WriteUInt32At(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
