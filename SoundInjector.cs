using System;
using System.Collections.Generic;
using System.IO;

// Minimal, additive "the map can still be edited" feature.
//
// Goal (per a Freeze author's remark): reading a protected map is not the same as
// unprotecting it; a real tool should at least be able to *change something* —
// "even a sound effect". This injects a single PlayWAV action WITHOUT touching the
// encrypted triggers or the live EUD VM, and WITHOUT changing the file size.
//
// Why size-invariant matters: the Freeze runtime's keycalc() re-derives seedKey from
// raw MPQ bytes including scenario.chk's sector offset table. Any size change shifts
// those bytes, breaks initOffsets/oJumper nextptr restoration, and kills the whole map
// (the documented Lv2 failure). So we:
//   1. write a PlayWAV into a free action slot of an existing plaintext, non-EUD,
//      executing trigger — keeping the TRIG section (and the whole CHK) the same length;
//   2. reuse a WAV string the map already references (no STR edit, no new MPQ file);
//   3. repack through the existing sector-preserving in-place patch (BuildLv2MpqPatch),
//      which keeps the sector offset table — and thus keycalc input — unchanged.
//
// Subtlety we learned the hard way: even a same-length CHK edit can fail if the edited
// 4KB MPQ sector recompresses larger than its original slot (zero-slack). Empty/zero
// triggers live in highly-compressible zero-padding and overflow immediately. So we try
// candidates in order of "most likely to have sector slack" and keep the first that the
// sector-preserving patch accepts; if none fit, we refuse rather than corrupt the map.
internal static partial class StarcraftMapUnprotector
{
    private const int InjectTrigSize = 2400;

    private sealed class InjectCandidate
    {
        public int TriggerIndex;
        public int ActionSlotOffset; // absolute offset of the 32-byte action slot in chk
        public int ActionCount;      // existing non-empty actions in this trigger
    }

    private static bool InjectSound(string input, string output)
    {
        try
        {
            byte[] inputBytes = File.ReadAllBytes(input);
            if (LooksLikeChk(inputBytes))
            {
                Console.Error.WriteLine("inject-sound: input is a raw .chk, not an MPQ map. Need an .scx/.scm.");
                return false;
            }

            var stats = new Stats();
            uint[] seedKey, destKey;
            if (DetectFreezeProtection(inputBytes, out seedKey, out destKey))
            {
                stats.IsFreezeProtected = true;
                stats.FreezeSeedKey = seedKey;
                stats.FreezeDestKey = destKey;
                Console.WriteLine("inject-sound: Freeze05 protection DETECTED (encrypted triggers/EUD VM left untouched).");
            }

            List<MpqFileEntry> extraFiles;
            byte[] chk = ExtractScenarioChk(input, stats, out extraFiles);
            if (chk == null || chk.Length == 0)
            {
                Console.Error.WriteLine("inject-sound: could not extract scenario.chk.");
                return false;
            }

            int trigPos, trigSize;
            if (!FindChkSectionSpan(chk, "TRIG", out trigPos, out trigSize) || trigSize < InjectTrigSize)
            {
                Console.Error.WriteLine("inject-sound: no usable TRIG section found.");
                return false;
            }
            if (trigSize % InjectTrigSize != 0)
            {
                Console.Error.WriteLine("inject-sound: TRIG section size is not a multiple of " + InjectTrigSize + ".");
                return false;
            }

            int wavIndex;
            string wavText;
            if (!FindExistingWavString(chk, stats, out wavIndex, out wavText))
            {
                Console.Error.WriteLine("inject-sound: the map references no .wav string to reuse.");
                Console.Error.WriteLine("              Adding a new WAV would change the file size → unsafe for live-EUD maps. Aborting.");
                return false;
            }

            int trigBase = trigPos + 8;
            int totalTriggers = trigSize / InjectTrigSize;
            byte refFlags = FindReferenceActionFlags(chk, trigBase, totalTriggers);

            List<InjectCandidate> candidates = CollectInjectCandidates(chk, trigBase, totalTriggers);
            if (candidates.Count == 0)
            {
                Console.Error.WriteLine("inject-sound: no plaintext, executing, non-EUD trigger with a free action slot to use.");
                return false;
            }

            // Try candidates in order; keep the first whose 4KB MPQ sector still fits its
            // original compressed slot (so the sector offset table — keycalc input — is unchanged).
            int attempts = 0;
            foreach (InjectCandidate cand in candidates)
            {
                attempts++;
                byte[] trial = (byte[])chk.Clone();
                int ao = cand.ActionSlotOffset;
                Array.Clear(trial, ao, 32);
                Buffer.BlockCopy(BitConverter.GetBytes((uint)wavIndex), 0, trial, ao + 8, 4); // wav string id
                Buffer.BlockCopy(BitConverter.GetBytes(0u), 0, trial, ao + 12, 4);            // duration 0 = full clip
                trial[ao + 26] = 8;        // PlayWAV
                trial[ao + 27] = 0;        // modifier (unused)
                trial[ao + 28] = refFlags; // flags (enabled)

                Lv2MpqPatchResult patch;
                try
                {
                    patch = BuildLv2MpqPatch(inputBytes, trial);
                }
                catch (InvalidDataException)
                {
                    continue; // this sector has no compression slack — try the next slot
                }

                File.WriteAllBytes(output, patch.File);
                Console.WriteLine("inject-sound: PlayWAV added to trigger #" + cand.TriggerIndex +
                                  " (action slot " + ((ao - (trigBase + cand.TriggerIndex * InjectTrigSize) - 320) / 32) +
                                  ", existing actions=" + cand.ActionCount + ", flags=0x" + refFlags.ToString("X2") + ").");
                Console.WriteLine("  WAV string [" + wavIndex + "]: " + wavText);
                Console.WriteLine("  CHK size unchanged (" + chk.Length + " bytes); fit after " + attempts + " candidate(s).");
                Console.WriteLine("  Sector offset table preserved → keycalc input unchanged → EUD VM intact.");
                Console.WriteLine("  Output: " + output);
                return true;
            }

            Console.Error.WriteLine("inject-sound: tried " + attempts + " trigger slots; none fit their MPQ sector's compressed slot.");
            Console.Error.WriteLine("              A size-invariant edit needs a sector with compression slack; this map has none free.");
            Console.Error.WriteLine("              (Refusing rather than shifting the sector table and breaking the map's EUD VM.)");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("inject-sound failed: " + ex.Message);
            return false;
        }
    }

    // Returns absolute offset (start of the section name) and the section's data size.
    private static bool FindChkSectionSpan(byte[] chk, string sectionName, out int dataPos, out int dataSize)
    {
        dataPos = 0;
        dataSize = 0;
        int pos = 0;
        while (pos + 8 <= chk.Length)
        {
            string name = System.Text.Encoding.ASCII.GetString(chk, pos, 4);
            uint size32 = BitConverter.ToUInt32(chk, pos + 4);
            if (size32 > (uint)(chk.Length - pos - 8))
            {
                break;
            }

            int size = (int)size32;
            if (name == sectionName)
            {
                dataPos = pos;        // points at the 8-byte header; data begins at pos+8
                dataSize = size;
                return true;
            }

            pos += 8 + size;
        }

        return false;
    }

    // Ordered list of safe injection points. We prefer triggers that already carry real
    // actions (their 4KB sector holds varied data and is more likely to recompress with
    // slack) over empty ones (which live in zero-padding with no slack at all).
    private static List<InjectCandidate> CollectInjectCandidates(byte[] chk, int trigBase, int totalTriggers)
    {
        var list = new List<InjectCandidate>();
        for (int t = 0; t < totalTriggers; t++)
        {
            int off = trigBase + t * InjectTrigSize;

            if (BitConverter.ToUInt32(chk, off + 2368) >= 0x80000000u)
            {
                continue; // still-encrypted Freeze trigger — never touch
            }
            if (IsFreezeEudTrigger(chk, off))
            {
                continue; // eudplib VM trigger — never touch
            }

            bool runs = false;
            for (int p = 0; p < 28; p++)
            {
                if (chk[off + 2372 + p] != 0) { runs = true; break; }
            }
            if (!runs)
            {
                continue; // never executes → silent injection
            }

            int freeSlot = -1;
            int actionCount = 0;
            for (int a = 0; a < 64; a++)
            {
                int ao = off + 320 + a * 32;
                if (chk[ao + 26] == 0)
                {
                    if (freeSlot < 0) freeSlot = ao;
                }
                else
                {
                    actionCount++;
                }
            }
            if (freeSlot < 0)
            {
                continue; // all 64 action slots used
            }

            list.Add(new InjectCandidate
            {
                TriggerIndex = t,
                ActionSlotOffset = freeSlot,
                ActionCount = actionCount
            });
        }

        // Most existing actions first (best chance the sector has compression slack).
        list.Sort((x, y) => y.ActionCount.CompareTo(x.ActionCount));
        return list;
    }

    private static byte FindReferenceActionFlags(byte[] chk, int trigBase, int totalTriggers)
    {
        for (int t = 0; t < totalTriggers; t++)
        {
            int off = trigBase + t * InjectTrigSize;
            if (BitConverter.ToUInt32(chk, off + 2368) >= 0x80000000u)
            {
                continue;
            }

            for (int a = 0; a < 64; a++)
            {
                int ao = off + 320 + a * 32;
                if (chk[ao + 26] != 0)
                {
                    return chk[ao + 28];
                }
            }
        }

        return 4; // StarEdit/SCMDraft default for an enabled action
    }

    private static bool FindExistingWavString(byte[] chk, Stats stats, out int wavIndex, out string wavText)
    {
        wavIndex = -1;
        wavText = null;

        var sections = ParseChk(chk);
        if (sections == null || sections.Count == 0)
        {
            return false;
        }

        var grouped = GroupSectionsForTriggerDump(sections, stats);
        string[] strings = ReadBestStringTable(grouped);
        if (strings == null)
        {
            return false;
        }

        int fallback = -1;
        string fallbackText = null;
        for (int i = 1; i < strings.Length; i++)
        {
            string s = strings[i];
            if (string.IsNullOrEmpty(s))
            {
                continue;
            }

            string low = s.ToLowerInvariant();
            if (!low.EndsWith(".wav"))
            {
                continue;
            }

            // Prefer a stock "sound\..." path — guaranteed present in StarCraft itself.
            if (low.StartsWith("sound\\") || low.StartsWith("sound/"))
            {
                wavIndex = i;
                wavText = s;
                return true;
            }

            if (fallback < 0)
            {
                fallback = i;
                fallbackText = s;
            }
        }

        if (fallback > 0)
        {
            wavIndex = fallback;
            wavText = fallbackText;
            return true;
        }

        return false;
    }
}
