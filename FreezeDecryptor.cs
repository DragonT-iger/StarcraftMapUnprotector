using System;
using System.Collections.Generic;

internal static partial class StarcraftMapUnprotector
{
    private const uint FreezeT2Const = 0x8ADA4053;
    private const uint FreezeMixConst = 0x10F874F3;
    private const int FreezeTrigBodySize = 2368;
    private const int FreezeTabCount = 16;
    private const int FreezeStride = FreezeTrigBodySize / 32; // 74
    private const int FreezeTrigSize = 2400;

    private static uint FreezeT2(uint x)
    {
        uint xsq = x * x;
        uint x4 = xsq * xsq;
        return x * (xsq * (x4 + 1) + 1) + FreezeT2Const;
    }

    internal static uint FreezeMix2(uint x, uint y)
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

    private static uint GetFreezeCryptFlag(uint encryptedFlag)
    {
        return unchecked(encryptedFlag - 0x80000000u) & 0x7FFFF000u;
    }

    private static bool TryDecryptFreezeTrigger(byte[] trigger, int offset, uint key, byte[] output)
    {
        uint flag = BitConverter.ToUInt32(trigger, offset + 2368);
        if (flag < 0x80000000u)
            return false;

        if (output != null)
            Buffer.BlockCopy(trigger, offset, output, 0, FreezeTrigSize);

        flag = GetFreezeCryptFlag(flag);
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

    private static int DecryptAllFreezeTriggers(byte[] trigData, uint key)
    {
        return DecryptAllFreezeTriggers(trigData, key, false);
    }

    private static int DecryptAllFreezeTriggers(byte[] trigData, uint key, bool preserveLv2FlagPayload)
    {
        return DecryptAllFreezeTriggers(trigData, key, preserveLv2FlagPayload, false);
    }

    private static int DecryptAllFreezeTriggers(byte[] trigData, uint key, bool preserveLv2FlagPayload, bool clearExecFlags)
    {
        return DecryptAllFreezeTriggers(trigData, key, preserveLv2FlagPayload, clearExecFlags, false);
    }

    private static int DecryptAllFreezeTriggers(byte[] trigData, uint key, bool preserveLv2FlagPayload, bool clearExecFlags, bool forcePlayerSlots)
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

            uint restoredFlag = clearExecFlags
                ? 0u
                : preserveLv2FlagPayload
                ? flag - 0x80000000u
                : (flag - 0x80000000u) & 0x0F;
            trigData[offset + 2368] = (byte)restoredFlag;
            trigData[offset + 2369] = (byte)(restoredFlag >> 8);
            trigData[offset + 2370] = (byte)(restoredFlag >> 16);
            trigData[offset + 2371] = (byte)(restoredFlag >> 24);
            if (forcePlayerSlots)
            {
                for (int p = 0; p < 8; p++)
                {
                    trigData[offset + 2372 + p] = 1;
                }
            }

            decrypted++;
        }

        Console.WriteLine("  Decrypted " + decrypted + " encrypted triggers.");
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
        if (LooksLikeEudVmTriggerSet(totalTriggers, freezeEudCandidates))
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

        return freezeEudCandidates * 4 >= totalTriggers;
    }

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
                continue;

            if (!ValidateTriggerBodyTypes(dumpData, dumpOff))
            {
                Console.WriteLine("  WARNING: dump trigger " + t + " body invalid — skipping " +
                                  "(game may not have decrypted it yet)");
                continue;
            }

            Buffer.BlockCopy(dumpData, dumpOff, trigData, chkOff, 2400);

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
            if (stats.FreezeBruteforceKey)
            {
                uint recoveredKey;
                if (TryRecoverFreezeKeyByFastBruteforce(data, totalTriggers, out recoveredKey))
                {
                    Console.WriteLine("  Key 0x" + recoveredKey.ToString("X8") +
                                      " validated! Decrypting all triggers...");
                    int keyDecrypted = DecryptAllFreezeTriggers(data, recoveredKey, stats.Lv2Mode);
                    decrypted += keyDecrypted;
                    stats.DecryptedFreezeTriggers = decrypted;
                }
            }

            int remainingEncrypted = CountEncryptedFreezeTriggers(data, totalTriggers, out encryptedOffset);

            if (remainingEncrypted > 0 && !string.IsNullOrEmpty(stats.FreezeApplyDumpPath))
            {
                Console.WriteLine("  Using runtime dump: " + stats.FreezeApplyDumpPath);
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
            }

            remainingEncrypted = CountEncryptedFreezeTriggers(data, totalTriggers, out encryptedOffset);

            if (remainingEncrypted > 0 && !string.IsNullOrEmpty(FreezeRecoverDumpPath))
            {
                Console.WriteLine("  Key recovery mode: comparing CHK with runtime dump...");
                try
                {
                    byte[] dumpData = System.IO.File.ReadAllBytes(FreezeRecoverDumpPath);
                    int dumpTrigCount = dumpData.Length / FreezeTrigSize;

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
                            uint flagForCrypt = GetFreezeCryptFlag(fl);
                            uint recoveredKey = RecoverFreezeKey(flagForCrypt, wlist);
                            if (recoveredKey != 0 || wlist[0] == 0)
                            {
                                byte[] testBuf = new byte[FreezeTrigSize];
                                TryDecryptFreezeTrigger(data, refTrigIndex * FreezeTrigSize, recoveredKey, testBuf);
                                if (ValidateDecryptedTrigger(testBuf))
                                {
                                    Console.WriteLine("  Key 0x" + recoveredKey.ToString("X8") +
                                                      " validated! Decrypting all triggers...");
                                    int keyDecrypted = DecryptAllFreezeTriggers(data, recoveredKey, stats.Lv2Mode);
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

        if (stats.Lv2Mode)
        {
            Console.WriteLine("  Lv2 mode: EUD VM triggers preserved (DisableFreezeEudTriggers skipped).");
        }
        else
        {
            DisableFreezeEudTriggers(data, stats);
        }

        return decrypted;
    }
}
