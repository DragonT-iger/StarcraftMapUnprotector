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
            Buffer.BlockCopy(trigger, offset, output, 0, 2400);

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

    private static bool ValidateDecryptedTrigger(byte[] decrypted)
    {
        for (int c = 0; c < 16; c++)
        {
            byte condType = decrypted[c * 20 + 15];
            if (condType > 23) return false;
        }
        for (int a = 0; a < 64; a++)
        {
            byte actType = decrypted[320 + a * 32 + 26];
            if (actType > 63) return false;
        }

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
            byte[] localBuf = new byte[2400];
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

    private static void DecryptAllFreezeTriggers(byte[] trigData, uint key)
    {
        int totalTriggers = trigData.Length / 2400;
        int decrypted = 0;

        for (int t = 0; t < totalTriggers; t++)
        {
            int offset = t * 2400;
            uint flag = BitConverter.ToUInt32(trigData, offset + 2368);
            if (flag < 0x80000000u)
                continue;

            byte[] buf = new byte[2400];
            TryDecryptFreezeTrigger(trigData, offset, key, buf);
            Buffer.BlockCopy(buf, 0, trigData, offset, 2400);

            uint restoredFlag = flag - 0x80000000u;
            restoredFlag &= 0x0F;
            trigData[offset + 2368] = (byte)restoredFlag;
            trigData[offset + 2369] = 0;
            trigData[offset + 2370] = 0;
            trigData[offset + 2371] = 0;

            decrypted++;
        }

        Console.WriteLine("  Decrypted " + decrypted + " encrypted triggers.");
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

            // Validate that the dump body looks sane (not garbage)
            // Condition types must be 0-23, action types 0-63
            bool valid = true;
            for (int c = 0; c < 16 && valid; c++)
            {
                byte ct = dumpData[dumpOff + c * 20 + 15];
                if (ct > 23) valid = false;
            }
            for (int a = 0; a < 64 && valid; a++)
            {
                byte at = dumpData[dumpOff + 320 + a * 32 + 26];
                if (at > 63) valid = false;
            }

            if (!valid)
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
            int dumpOff = t * 2400;
            bool valid = true;
            for (int c = 0; c < 16 && valid; c++)
            {
                if (dumpData[dumpOff + c * 20 + 15] > 23)
                {
                    valid = false;
                }
            }

            for (int a = 0; a < 64 && valid; a++)
            {
                if (dumpData[dumpOff + 320 + a * 32 + 26] > 63)
                {
                    valid = false;
                }
            }

            if (valid)
            {
                validBodies++;
            }
        }

        return validBodies;
    }

    private static int ProcessFreezeProtection(Dictionary<string, List<byte[]>> grouped, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("TRIG", out list) || list.Count == 0)
            return 0;

        byte[] data = list[0];
        if (data.Length % 2400 != 0)
            return 0;

        int totalTriggers = data.Length / 2400;
        Console.WriteLine("  Freeze05: Processing " + totalTriggers + " triggers...");

        int encryptedOffset = -1;
        int encryptedCount = 0;
        for (int t = 0; t < totalTriggers; t++)
        {
            uint flag = BitConverter.ToUInt32(data, t * 2400 + 2368);
            if (flag >= 0x80000000u)
            {
                if (encryptedOffset < 0) encryptedOffset = t * 2400;
                encryptedCount++;
            }
        }

        Console.WriteLine("  Freeze05: " + encryptedCount + " encrypted triggers found.");

        for (int t = 0; t < totalTriggers; t++)
        {
            uint fl = BitConverter.ToUInt32(data, t * 2400 + 2368);
            if (fl >= 0x80000000u)
            {
                Console.WriteLine("  Encrypted trigger " + t + ": flag=0x" + fl.ToString("X8"));
                int nzDwords = 0;
                for (int d = 0; d < 592; d++)
                {
                    if (BitConverter.ToUInt32(data, t * 2400 + d * 4) != 0) nzDwords++;
                }
                Console.WriteLine("  Non-zero dwords in body: " + nzDwords + " / 592");
                int nzConds = 0;
                for (int c = 0; c < 16; c++)
                {
                    if (data[t * 2400 + c * 20 + 15] != 0) nzConds++;
                }
                int nzActs = 0;
                for (int a = 0; a < 64; a++)
                {
                    if (data[t * 2400 + 320 + a * 32 + 26] != 0) nzActs++;
                }
                Console.WriteLine("  Non-zero condition types: " + nzConds + " / 16");
                Console.WriteLine("  Non-zero action types: " + nzActs + " / 64");
                Console.Write("  Condition types:");
                for (int c = 0; c < 16; c++)
                    Console.Write(" " + data[t * 2400 + c * 20 + 15]);
                Console.WriteLine();
                Console.Write("  Action types:");
                for (int a = 0; a < 64; a++)
                    Console.Write(" " + data[t * 2400 + 320 + a * 32 + 26]);
                Console.WriteLine();
            }
        }

        int decrypted = 0;

        if (encryptedCount > 0 && !string.IsNullOrEmpty(stats.FreezeApplyDumpPath))
        {
            // --- Runtime dump path: apply CE memory dump instead of brute-force ---
            Console.WriteLine("  Using runtime dump: " + stats.FreezeApplyDumpPath);
            try
            {
                byte[] dumpData = System.IO.File.ReadAllBytes(stats.FreezeApplyDumpPath);
                int patched = ApplyRuntimeDump(data, dumpData);
                if (patched > 0)
                {
                    decrypted = patched;
                    stats.DecryptedFreezeTriggers = patched;
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
        }
        else if (encryptedCount > 0 && !string.IsNullOrEmpty(FreezeRecoverDumpPath))
        {
            // --- Key recovery path: recover key from encrypted/decrypted pair ---
            Console.WriteLine("  Key recovery mode: comparing CHK with runtime dump...");
            try
            {
                byte[] dumpData = System.IO.File.ReadAllBytes(FreezeRecoverDumpPath);
                int dumpTrigCount = dumpData.Length / 2400;

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
                    int[] wlist = RecoverWlistFromDump(data, refTrigIndex * 2400, dumpData, refTrigIndex * 2400);
                    if (wlist != null)
                    {
                        uint fl = BitConverter.ToUInt32(data, refTrigIndex * 2400 + 2368);
                        uint flagForCrypt = fl - 0x80000000u;
                        uint recoveredKey = RecoverFreezeKey(flagForCrypt, wlist);
                        if (recoveredKey != 0 || wlist[0] == 0)
                        {
                            // Verify by decrypting the reference trigger
                            byte[] testBuf = new byte[2400];
                            TryDecryptFreezeTrigger(data, refTrigIndex * 2400, recoveredKey, testBuf);
                            if (ValidateDecryptedTrigger(testBuf))
                            {
                                Console.WriteLine("  Key 0x" + recoveredKey.ToString("X8") +
                                                  " validated! Decrypting all triggers...");
                                DecryptAllFreezeTriggers(data, recoveredKey);
                                decrypted = encryptedCount;
                                stats.DecryptedFreezeTriggers = decrypted;
                            }
                            else
                            {
                                Console.WriteLine("  WARNING: Recovered key failed validation. " +
                                                  "Falling back to runtime dump apply...");
                                int patched = ApplyRuntimeDump(data, dumpData);
                                decrypted = patched;
                                stats.DecryptedFreezeTriggers = patched;
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("  Wlist recovery failed. Falling back to runtime dump apply...");
                        int patched = ApplyRuntimeDump(data, dumpData);
                        decrypted = patched;
                        stats.DecryptedFreezeTriggers = patched;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  ERROR in key recovery: " + ex.Message);
            }
        }
        else if (encryptedCount > 0 && stats.FreezeSeedKey != null)
        {
            // --- Static brute-force path (known to fail for armoha builds) ---
            uint cryptKeyVal = ComputeCryptKeyVal(stats.FreezeSeedKey);
            Console.WriteLine("  cryptKeyVal: 0x" + cryptKeyVal.ToString("X8"));

            string encDumpPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                "freeze_encrypted_trigger.bin");
            try
            {
                byte[] trigDump = new byte[2400];
                Buffer.BlockCopy(data, encryptedOffset, trigDump, 0, 2400);
                System.IO.File.WriteAllBytes(encDumpPath, trigDump);
                Console.WriteLine("  Dumped encrypted trigger to: " + encDumpPath);
            }
            catch { }

            uint trigCryptKey = BruteForceDecryptionKey(data, encryptedOffset);
            byte[] testBuf = new byte[2400];
            TryDecryptFreezeTrigger(data, encryptedOffset, trigCryptKey, testBuf);
            if (ValidateDecryptedTrigger(testBuf))
            {
                DecryptAllFreezeTriggers(data, trigCryptKey);
                decrypted = encryptedCount;
                stats.DecryptedFreezeTriggers = decrypted;
            }
        }

        DisableFreezeEudTriggers(data, stats);

        return decrypted;
    }
}
