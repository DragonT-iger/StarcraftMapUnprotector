using System;
using System.Collections.Generic;
using System.Linq;
using TkMPQLib;

internal static partial class StarcraftMapUnprotector
{
    private struct FreezeVmCondition
    {
        public uint Location;
        public uint Player;
        public uint Amount;
        public ushort Unit;
        public byte Comparison;
        public byte Type;
        public byte Resource;
        public byte Flags;
        public ushort Eudx;
    }

    private struct FreezeVmAction
    {
        public uint Location;
        public uint String;
        public uint Wav;
        public uint Time;
        public uint Player1;
        public uint Player2;
        public ushort Unit;
        public byte Type;
        public byte Modifier;
        public byte Flags;
        public byte Internal;
        public ushort Eudx;
    }

    private sealed class FreezeSelfModWriteSummary
    {
        public uint Value;
        public int Count;
        public bool MultipleValues;
        public uint LastOwnerAddress;
    }

    private sealed class FreezeTriggerVm
    {
        private readonly FreezeVmMemory memory;
        private bool preserve;
        private uint currentTriggerAddress;
        private uint previousTriggerAddress;
        private bool hasPreviousTrigger;

        public readonly Dictionary<uint, FreezeSelfModWriteSummary> SelfModWrites =
            new Dictionary<uint, FreezeSelfModWriteSummary>();
        public readonly Dictionary<uint, uint> FirstNextByExecutedAddress = new Dictionary<uint, uint>();
        public readonly Dictionary<uint, uint> LastNextByExecutedAddress = new Dictionary<uint, uint>();
        public long ActionsExecuted;

        public FreezeTriggerVm(FreezeVmMemory memory)
        {
            this.memory = memory;
        }

        public void InitializeStaticAreas()
        {
            memory.Alloc(FreezeSc.UnitInfoTable, 12 * 228, "UnitInfo");
            memory.Alloc(FreezeSc.PlayerTrigStructBase, 12 * 8, "PTS");
            memory.Alloc(FreezeSc.DeathTable, 48 * 228, "DeathTable");
            memory.Alloc(FreezeSc.LocationTable, 20 * 64, "LocationTable");
            memory.Alloc(FreezeSc.SwitchTable, 32, "SwitchTable");
            memory.Alloc(FreezeSc.MrgnTable, 20 * 255, "MRGN");
            memory.Alloc(FreezeSc.MtxmPointer, 4, "MTXM pointer");
            memory.Alloc(FreezeSc.StrTablePointer, 4, "STR pointer");
            memory.Write32(FreezeSc.StrTablePointer, FreezeSc.StrxSectionAlloc);
            memory.Alloc(FreezeSc.UnitNodeTable, 571200, "UnitNode");
            memory.Alloc(FreezeSc.CurrentPlayer, 4, "CurrentPlayer");
            memory.Alloc(FreezeSc.NetworkBuffer, 496, "NetworkBuffer");
            memory.Alloc(FreezeSc.TranWireGrpPtr, 4, "TranWireGrpPtr");
            memory.Alloc(FreezeSc.GrpWireGrpPtr, 4, "GrpWireGrpPtr");
            memory.Alloc(FreezeSc.WireframeGrpPtr, 4, "WireframeGrpPtr");
            memory.Alloc(FreezeSc.ReplayHeader, 633, "ReplayHeader");
            memory.Alloc(FreezeSc.IsReplayFlag, 28, "ReplayState");
            memory.Write32(FreezeSc.IsReplayFlag, 0);
            memory.Alloc(0, 0x1000, "NULL page");

            TryAlloc(0x00400000, 0x4FE000 - 0x400000, "SC gap 1");
            TryAlloc(0x004FE600, 0x512000 - 0x4FE600, "SC gap 2");
            TryAlloc(0x00512700, 0x519000 - 0x512700, "SC gap 3");
            TryAlloc(0x0051B000, 0x57F000 - 0x51B000, "SC gap 4");
            TryAlloc(0x0057F300, 0x58A000 - 0x57F300, "SC gap 5");
            TryAlloc(0x0058D000, 0x58D700 - 0x58D000, "SC gap 6");
            TryAlloc(0x0058F400, 0x599000 - 0x58F400, "SC gap 7");
            TryAlloc(0x00599400, 0x59CC00 - 0x599400, "SC gap 8");
            TryAlloc(0x00629000, 0x641000 - 0x629000, "SC gap 9");
            TryAlloc(0x00642000, 0x650000 - 0x642000, "SC gap 10");
            TryAlloc(0x00651000, 0x654000 - 0x651000, "SC gap 11");
            TryAlloc(0x00655000, 0x68C000 - 0x655000, "SC gap 12");
            TryAlloc(0x0068D000, 0x6D0000 - 0x68D000, "SC gap 13");
            TryAlloc(0x006D2000, 0x900000 - 0x6D2000, "SC gap 14");
            TryAlloc(0x005C0000, 0x2000, "SC dat block");
            TryAlloc(0x00512800, 240, "Trigger action functions");
            TryAlloc(0x00515A98, 96, "Trigger condition functions");
            TryAlloc(0x00598250, 64, "Sprite table");
            TryAlloc(0xFC000000, 0x01000000, "EPD high");
            TryAlloc(0x4BC00000, 0x00200000, "EPD middle");

            memory.Alloc(FreezeSc.GameTick, 4, "GameTick");
            memory.Alloc(FreezeSc.LocalNationId, 4, "LocalNationId");
            memory.Alloc(FreezeSc.NextUnitPtr, 4, "NextUnitPtr");
            memory.Write32(FreezeSc.NextUnitPtr, FreezeSc.UnitNodeTable);
            memory.Alloc(FreezeSc.DisplayTextBuffer, 218 * 12, "DisplayTextBuffer");
            memory.Alloc(FreezeSc.BwTechAvailable, 20 * 12, "BwTechAvailable");
            memory.Alloc(FreezeSc.BwTechResearched, 20 * 12, "BwTechResearched");
            memory.Alloc(FreezeSc.BwUpgradesResearched, 15 * 12, "BwUpgradesResearched");
            memory.Alloc(FreezeSc.BwUpgradesAvailable, 15 * 12, "BwUpgradesAvailable");
            memory.Alloc(FreezeSc.StormSStrLenImport, 4, "Storm SStrLen");
            memory.Write32(FreezeSc.StormSStrLenImport, FreezeSc.StormBaseExpected);
            memory.Alloc(0x004FE304, 4, "GetMapHandle probe");
            memory.Alloc(FreezeSc.StormBaseExpected, 0x400000, "Storm DLL");
            memory.Write32(FreezeSc.StormMapHandleSlot, FreezeSc.StormBaseExpected);
        }

        private void TryAlloc(uint address, uint size, string label)
        {
            memory.TryAlloc(address, size, label);
        }

        private static uint EpdOf(uint address)
        {
            return unchecked((address - FreezeSc.DeathTable) / 4u);
        }

        public void MockStormMpq(byte[] header32, HashTable[] hashes, BlockTable[] blocks, byte[] rawMpq)
        {
            const uint mapHandle = 0x16000000;
            const uint mpqHeader = 0x60000000;
            uint blockBytes = checked((uint)blocks.Length * 16u);
            uint hashBytes = checked((uint)hashes.Length * 16u);
            uint blockAddress = mpqHeader + 0x20;
            uint hashAddress = blockAddress + blockBytes;
            uint rawAddress = hashAddress + hashBytes;

            memory.Write32(FreezeSc.StormSStrLenImport, FreezeSc.StormBaseExpected);
            memory.Write32(0x004FE304, EpdOf(FreezeSc.StormBaseExpected));
            memory.Write32(FreezeSc.StormMapHandleSlot, mapHandle);
            memory.Alloc(mapHandle, 0x200, "MapHandle");
            memory.Write32(mapHandle + 0x130, mpqHeader);
            memory.Write32(mapHandle + 0x134, blockAddress);
            memory.Write32(mapHandle + 0x138, hashAddress);

            memory.Alloc(mpqHeader, 0x20, "MPQ header");
            memory.WriteBytes(mpqHeader, header32, 0, Math.Min(32, header32.Length));
            byte[] blockData = SerializeBlocks(blocks);
            byte[] hashData = SerializeHashes(hashes);
            memory.Alloc(blockAddress, blockData, "MPQ block table");
            memory.Alloc(hashAddress, hashData, "MPQ hash table");
            if (rawMpq != null && rawMpq.Length > 0)
                memory.Alloc(rawAddress, rawMpq, "MPQ raw file");
        }

        private static byte[] SerializeHashes(HashTable[] hashes)
        {
            var data = new byte[hashes.Length * 16];
            for (int i = 0; i < hashes.Length; i++)
            {
                int p = i * 16;
                WriteUInt32(data, p, hashes[i].NameA);
                WriteUInt32(data, p + 4, hashes[i].NameB);
                WriteUInt16(data, p + 8, (ushort)hashes[i].Locale);
                WriteUInt16(data, p + 10, hashes[i].Platform);
                WriteUInt32(data, p + 12, unchecked((uint)hashes[i].BlockTable));
            }
            return data;
        }

        private static byte[] SerializeBlocks(BlockTable[] blocks)
        {
            var data = new byte[blocks.Length * 16];
            for (int i = 0; i < blocks.Length; i++)
            {
                int p = i * 16;
                WriteUInt32(data, p, unchecked((uint)blocks[i].FileOffset));
                WriteUInt32(data, p + 4, blocks[i].CompSize);
                WriteUInt32(data, p + 8, blocks[i].FileSize);
                WriteUInt32(data, p + 12, (uint)blocks[i].Flags);
            }
            return data;
        }

        public void LoadChkSections(byte[] chk)
        {
            byte[] section;
            byte[] stringSection = null;
            if (TryGetFirstChkSection(chk, "STRx", out section) && section.Length > 0)
            {
                stringSection = section;
            }
            else if (TryGetFirstChkSection(chk, "STR ", out section) && section.Length > 0)
            {
                // Older Freeze variants store their runtime trigger payload in
                // the legacy string table instead of STRx. StarCraft exposes
                // either table through the same runtime string allocation.
                stringSection = section;
            }
            if (stringSection != null)
            {
                memory.DeallocRange(FreezeSc.StrxSectionAlloc, (uint)stringSection.Length);
                memory.Alloc(FreezeSc.StrxSectionAlloc, stringSection, "String table");
            }
            if (TryGetFirstChkSection(chk, "MRGN", out section))
                WriteLimited(FreezeSc.MrgnTable, section, 5100);

            if (TryGetFirstChkSection(chk, "PTEx", out section))
            {
                for (int player = 0; player < 12; player++)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        int available = i + 24 + 44 * player;
                        int researched = available + 44 * 12;
                        if (available < section.Length) memory.Write8(FreezeSc.BwTechAvailable + (uint)(i + 20 * player), section[available]);
                        if (researched < section.Length) memory.Write8(FreezeSc.BwTechResearched + (uint)(i + 20 * player), section[researched]);
                    }
                }
            }
            if (TryGetFirstChkSection(chk, "PUPx", out section))
            {
                for (int player = 0; player < 12; player++)
                {
                    for (int i = 0; i < 15; i++)
                    {
                        int max = i + 46 + 61 * player;
                        int start = max + 61 * 12;
                        if (max < section.Length) memory.Write8(FreezeSc.BwUpgradesAvailable + (uint)(i + 15 * player), section[max]);
                        if (start < section.Length) memory.Write8(FreezeSc.BwUpgradesResearched + (uint)(i + 15 * player), section[start]);
                    }
                }
            }

            LoadSimpleSection(chk, "DIM ", 0x0057F1D4, 4);
            LoadSimpleSection(chk, "OWNR", 0x0057F1B4, 12);
            LoadSimpleSection(chk, "SIDE", 0x0057F1C0, 12);
            LoadSimpleSection(chk, "COLR", 0x0057F21C, 8);
            LoadSimpleSection(chk, "ERA ", 0x0057F1DC, 2);
            LoadSimpleSection(chk, "FORC", 0x0058D5B0, 20);
            LoadSimpleSection(chk, "SPRP", 0x0057F244, 4);
            LoadSimpleSection(chk, "PUNI", 0x0057F27C, 228);

            if (TryGetFirstChkSection(chk, "UNIS", out section)) WriteLimited(0x00514000, section, 4332);
            if (TryGetFirstChkSection(chk, "MTXM", out section) && section.Length > 0)
            {
                const uint address = 0x04000000;
                memory.TryAlloc(address, (uint)section.Length, "MTXM data");
                if (memory.Find(address) != null)
                {
                    memory.WriteBytes(address, section);
                    memory.Write32(FreezeSc.MtxmPointer, address);
                }
            }
        }

        private void LoadSimpleSection(byte[] chk, string name, uint address, int max)
        {
            byte[] section;
            if (TryGetFirstChkSection(chk, name, out section)) WriteLimited(address, section, max);
        }

        private void WriteLimited(uint address, byte[] section, int max)
        {
            int count = Math.Min(section.Length, max);
            if (count > 0) memory.WriteBytes(address, section, 0, count);
        }

        public void ExecuteAt(uint address)
        {
            if (hasPreviousTrigger && !FirstNextByExecutedAddress.ContainsKey(previousTriggerAddress))
            {
                FirstNextByExecutedAddress.Add(previousTriggerAddress, address);
            }
            if (hasPreviousTrigger) LastNextByExecutedAddress[previousTriggerAddress] = address;
            previousTriggerAddress = address;
            hasPreviousTrigger = true;
            currentTriggerAddress = address;

            byte[] bytes = memory.ReadBytes(address, FreezeTrigSize);
            uint flag = BitConverter.ToUInt32(bytes, 2368);
            preserve = false;
            int conditionsMet = 0;
            for (int i = 0; i < 16; i++)
            {
                FreezeVmCondition c = ReadCondition(address + (uint)(i * 20));
                if ((c.Flags & 2) != 0) continue;
                if (c.Type == 0) break;
                if (c.Type >= 24 || !Evaluate(c))
                {
                    return;
                }
                conditionsMet++;
            }

            int actions = 0;
            for (int i = 0; i < 64; i++)
            {
                FreezeVmAction a = ReadAction(address + 320u + (uint)(i * 32));
                if ((a.Flags & 2) != 0) continue;
                if (a.Type == 0) break;
                if (++actions > 1024) break;
                ActionsExecuted++;
                ExecuteAction(a);
            }
            // Melter disables the SC already-executed bit for EUD payload loops.
            if (!preserve && false) memory.Write32(address + 2368, flag | 8u);
        }

        private FreezeVmCondition ReadCondition(uint address)
        {
            return new FreezeVmCondition
            {
                Location = memory.Read32(address),
                Player = memory.Read32(address + 4),
                Amount = memory.Read32(address + 8),
                Unit = memory.Read16(address + 12),
                Comparison = memory.Read8(address + 14),
                Type = memory.Read8(address + 15),
                Resource = memory.Read8(address + 16),
                Flags = memory.Read8(address + 17),
                Eudx = memory.Read16(address + 18)
            };
        }

        private FreezeVmAction ReadAction(uint address)
        {
            return new FreezeVmAction
            {
                Location = memory.Read32(address),
                String = memory.Read32(address + 4),
                Wav = memory.Read32(address + 8),
                Time = memory.Read32(address + 12),
                Player1 = memory.Read32(address + 16),
                Player2 = memory.Read32(address + 20),
                Unit = memory.Read16(address + 24),
                Type = memory.Read8(address + 26),
                Modifier = memory.Read8(address + 27),
                Flags = memory.Read8(address + 28),
                Internal = memory.Read8(address + 29),
                Eudx = memory.Read16(address + 30)
            };
        }

        private bool Evaluate(FreezeVmCondition c)
        {
            if (c.Type == 22) return true;
            if (c.Type == 23) return false;
            if (c.Type != 15) return false;

            uint player = c.Player;
            if (player == 13) player = memory.Read32(FreezeSc.CurrentPlayer);
            uint address = unchecked(FreezeSc.DeathTable + (uint)c.Unit * 48u + player * 4u);
            if (c.Eudx != 0x4353 && player > 11 && memory.Find(address) == null) return false;
            uint value = memory.Find(address) == null ? 0 : memory.Read32(address);
            uint mask = c.Eudx == 0x4353 ? c.Location : UInt32.MaxValue;
            return Compare(value & mask, c.Comparison, c.Amount);
        }

        private static bool Compare(uint left, byte comparison, uint right)
        {
            if (comparison == 0) return left >= right;
            if (comparison == 1) return left <= right;
            if (comparison == 10) return left == right;
            return false;
        }

        private static uint ApplyModifier(uint current, byte modifier, uint value)
        {
            if (modifier == 7) return value;
            if (modifier == 8) return unchecked(current + value);
            if (modifier == 9) return current > value ? current - value : 0;
            return current;
        }

        private void ExecuteAction(FreezeVmAction a)
        {
            switch (a.Type)
            {
                case 3:
                    preserve = true;
                    return;
                case 13:
                    SetSwitch(a);
                    return;
                case 14:
                    WriteGeneric(a, 0x0058D6F4, a.Time, false);
                    return;
                case 26:
                    SetResources(a);
                    return;
                case 27:
                    SetScore(a);
                    return;
                case 45:
                    SetDeaths(a);
                    return;
                default:
                    return;
            }
        }

        private void SetDeaths(FreezeVmAction a)
        {
            uint player = a.Player1 == 13 ? memory.Read32(FreezeSc.CurrentPlayer) : a.Player1;
            uint mask = a.Eudx == 0x4353 ? a.Location : UInt32.MaxValue;
            uint address = unchecked(FreezeSc.DeathTable + (uint)a.Unit * 48u + player * 4u);
            if (a.Modifier == 7 && IsFreezePayloadAddress(address))
                RecordSelfModWrite(address, a.Player2);
            memory.EnsurePage(address, "EUD user page");
            if (memory.Find(address) == null) return;
            uint current = memory.Read32(address);
            uint result;
            if (a.Eudx == 0x4353)
            {
                uint modified = ApplyModifier(current & mask, a.Modifier, a.Player2 & mask);
                result = (current & ~mask) | (modified & mask);
            }
            else
            {
                result = ApplyModifier(current, a.Modifier, a.Player2);
            }
            memory.Write32(address, result);
        }

        private void RecordSelfModWrite(uint address, uint value)
        {
            FreezeSelfModWriteSummary summary;
            if (!SelfModWrites.TryGetValue(address, out summary))
            {
                SelfModWrites.Add(address, new FreezeSelfModWriteSummary
                {
                    Value = value,
                    Count = 1,
                    LastOwnerAddress = currentTriggerAddress
                });
                return;
            }

            if (summary.Value == value)
            {
                if (summary.Count < 2) summary.Count++;
            }
            else
            {
                summary.MultipleValues = true;
            }
            summary.LastOwnerAddress = currentTriggerAddress;
        }

        private void SetSwitch(FreezeVmAction a)
        {
            uint address = unchecked(FreezeSc.SwitchTable + (a.Player2 & 0xFF));
            memory.EnsurePage(address, "EUD switch page");
            if (memory.Find(address) == null) return;
            byte current = memory.Read8(address);
            if (a.Modifier == 4) memory.Write8(address, 1);
            else if (a.Modifier == 5) memory.Write8(address, 0);
            else if (a.Modifier == 6) memory.Write8(address, (byte)(current ^ 1));
        }

        private void SetResources(FreezeVmAction a)
        {
            uint[] bases = { 0x0057F0F0, 0x0057F120, 0x0057F150, 0x0057F180 };
            int type = a.Unit & 0xFF;
            if (type > 3) return;
            WriteGeneric(a, bases[type], a.Player2, true);
        }

        private void SetScore(FreezeVmAction a)
        {
            uint[] bases = { 0x00581E44, 0x00581E44, 0x00582024, 0x00581E44, 0x00581F04, 0x00582054, 0x00581F04, 0x005822F4 };
            WriteGeneric(a, bases[a.Unit & 7], a.Player2, true);
        }

        private void WriteGeneric(FreezeVmAction a, uint baseAddress, uint value, bool indexed)
        {
            uint player = a.Player1 == 13 ? memory.Read32(FreezeSc.CurrentPlayer) : a.Player1;
            uint address = unchecked(baseAddress + player * 4u);
            memory.EnsurePage(address, "EUD generic page");
            if (memory.Find(address) == null) return;
            memory.Write32(address, ApplyModifier(memory.Read32(address), a.Modifier, value));
        }

        private static void WriteUInt16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }
    }
}
