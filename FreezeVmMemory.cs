using System;
using System.Collections.Generic;

internal static partial class StarcraftMapUnprotector
{
    private static class FreezeSc
    {
        public const uint UnitInfoTable = 0x005193A0;
        public const uint PlayerTrigStructBase = 0x0051A280;
        public const uint DeathTable = 0x0058A364;
        public const uint LocationTable = 0x0058D740;
        public const uint SwitchTable = 0x0058DC40;
        public const uint MrgnTable = 0x0058DC60;
        public const uint MtxmPointer = 0x005993C4;
        public const uint StrTablePointer = 0x005993D4;
        public const uint UnitNodeTable = 0x0059CCA8;
        public const uint CurrentPlayer = 0x006509B0;
        public const uint NetworkBuffer = 0x00654880;
        public const uint TranWireGrpPtr = 0x0068C1F4;
        public const uint GrpWireGrpPtr = 0x0068C1FC;
        public const uint WireframeGrpPtr = 0x0068C204;
        public const uint GameTick = 0x0057F23C;
        public const uint LocalNationId = 0x00512684;
        public const uint NextUnitPtr = 0x00628438;
        public const uint DisplayTextBuffer = 0x006413E4;
        public const uint BwTechAvailable = 0x0058F050;
        public const uint BwTechResearched = 0x0058F140;
        public const uint BwUpgradesAvailable = 0x0058F278;
        public const uint BwUpgradesResearched = 0x0058F32C;
        public const uint ReplayHeader = 0x006D0F30;
        public const uint IsReplayFlag = 0x006D0F14;
        public const uint StormSStrLenImport = 0x004FE544;
        public const uint StormMapHandleSlot = 0x1505ADFC;
        public const uint StormBaseExpected = 0x15021A00;
        public const uint StrxSectionAlloc = 0x191943C8;
        public const uint RuntimeTrigAlloc = 0x21000000;
    }

    private sealed class FreezeVmMemory
    {
        internal sealed class Block
        {
            public uint Start;
            public byte[] Data;
            public string Label;

            public uint End
            {
                get { return unchecked(Start + (uint)Data.Length); }
            }
        }

        private readonly List<Block> blocks = new List<Block>();
        public bool Lenient;

        public IEnumerable<Block> Blocks
        {
            get { return blocks; }
        }

        public void Alloc(uint address, uint size, string label)
        {
            if (size == 0) return;
            ulong end = (ulong)address + size;
            if (end > UInt32.MaxValue + 1UL || size > Int32.MaxValue)
                throw new InvalidOperationException("Freeze VM allocation is too large.");

            foreach (Block block in blocks)
            {
                if ((ulong)block.End > address && (ulong)block.Start < end)
                    throw new InvalidOperationException("Freeze VM allocation overlap at 0x" + address.ToString("X8") + ".");
            }

            blocks.Add(new Block
            {
                Start = address,
                Data = new byte[(int)size],
                Label = label ?? ""
            });
            blocks.Sort(delegate(Block left, Block right) { return left.Start.CompareTo(right.Start); });
        }

        public void Alloc(uint address, byte[] data, string label)
        {
            Alloc(address, (uint)data.Length, label);
            WriteBytes(address, data, 0, data.Length);
        }

        public void TryAlloc(uint address, uint size, string label)
        {
            try { Alloc(address, size, label); }
            catch { }
        }

        public void DeallocRange(uint address, uint size)
        {
            ulong end = (ulong)address + size;
            var remove = new List<uint>();
            foreach (Block block in blocks)
            {
                if ((ulong)block.End > address && (ulong)block.Start < end)
                    remove.Add(block.Start);
            }
            foreach (uint key in remove) blocks.RemoveAll(delegate(Block block) { return block.Start == key; });
        }

        public Block Find(uint address)
        {
            int low = 0;
            int high = blocks.Count - 1;
            Block found = null;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                Block block = blocks[middle];
                if (block.Start <= address)
                {
                    found = block;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return found != null && address < found.End ? found : null;
        }

        private bool TryRange(uint address, int length, out Block block, out int offset)
        {
            block = Find(address);
            offset = 0;
            if (block == null) return false;
            ulong end = (ulong)address + (uint)length;
            if (end > block.End) return false;
            offset = (int)(address - block.Start);
            return true;
        }

        public byte Read8(uint address)
        {
            Block b; int o;
            if (!TryRange(address, 1, out b, out o))
            {
                if (Lenient) return 0;
                throw new InvalidOperationException("Freeze VM read outside mapped memory at 0x" + address.ToString("X8") + ".");
            }
            return b.Data[o];
        }

        public ushort Read16(uint address)
        {
            Block b; int o;
            if (!TryRange(address, 2, out b, out o))
            {
                if (Lenient) return 0;
                throw new InvalidOperationException("Freeze VM read outside mapped memory at 0x" + address.ToString("X8") + ".");
            }
            return (ushort)(b.Data[o] | (b.Data[o + 1] << 8));
        }

        public uint Read32(uint address)
        {
            Block b; int o;
            if (!TryRange(address, 4, out b, out o))
            {
                if (Lenient) return 0;
                throw new InvalidOperationException("Freeze VM read outside mapped memory at 0x" + address.ToString("X8") + ".");
            }
            return (uint)(b.Data[o] |
                          (b.Data[o + 1] << 8) |
                          (b.Data[o + 2] << 16) |
                          (b.Data[o + 3] << 24));
        }

        public byte[] ReadBytes(uint address, int length)
        {
            var result = new byte[length];
            Block b; int o;
            if (TryRange(address, length, out b, out o))
            {
                Buffer.BlockCopy(b.Data, o, result, 0, length);
                return result;
            }
            if (!Lenient)
                throw new InvalidOperationException("Freeze VM read outside mapped memory at 0x" + address.ToString("X8") + ".");
            for (int i = 0; i < length; i++) result[i] = Read8(unchecked(address + (uint)i));
            return result;
        }

        public void Write8(uint address, byte value)
        {
            Block b; int o;
            if (!TryRange(address, 1, out b, out o))
            {
                if (Lenient) return;
                throw new InvalidOperationException("Freeze VM write outside mapped memory at 0x" + address.ToString("X8") + ".");
            }
            b.Data[o] = value;
        }

        public void Write16(uint address, ushort value)
        {
            Block b; int o;
            if (!TryRange(address, 2, out b, out o))
            {
                if (Lenient) return;
                throw new InvalidOperationException("Freeze VM write outside mapped memory at 0x" + address.ToString("X8") + ".");
            }
            b.Data[o] = (byte)value;
            b.Data[o + 1] = (byte)(value >> 8);
        }

        public void Write32(uint address, uint value)
        {
            Block b; int o;
            if (!TryRange(address, 4, out b, out o))
            {
                if (Lenient) return;
                throw new InvalidOperationException("Freeze VM write outside mapped memory at 0x" + address.ToString("X8") + ".");
            }
            b.Data[o] = (byte)value;
            b.Data[o + 1] = (byte)(value >> 8);
            b.Data[o + 2] = (byte)(value >> 16);
            b.Data[o + 3] = (byte)(value >> 24);
        }

        public void WriteBytes(uint address, byte[] data)
        {
            WriteBytes(address, data, 0, data.Length);
        }

        public void WriteBytes(uint address, byte[] data, int sourceOffset, int length)
        {
            Block b; int o;
            if (TryRange(address, length, out b, out o))
            {
                Buffer.BlockCopy(data, sourceOffset, b.Data, o, length);
                return;
            }
            if (!Lenient)
                throw new InvalidOperationException("Freeze VM write outside mapped memory at 0x" + address.ToString("X8") + ".");
            for (int i = 0; i < length; i++) Write8(unchecked(address + (uint)i), data[sourceOffset + i]);
        }

        public void EnsurePage(uint address, string label)
        {
            if (Find(address) != null) return;
            uint page = address & 0xFFFFF000u;
            TryAlloc(page, 4096, label);
        }
    }

    private sealed class FreezePayloadLayout
    {
        public bool Valid;
        public int StrxChkOffset;
        public int StrxSize;
        public int PayloadStrxOffset;
        public int PayloadSize;
        public uint PayloadMemoryAddress;
    }

    private static FreezePayloadLayout AnalyzeFreezePayload(byte[] chk)
    {
        var result = new FreezePayloadLayout();
        int pos = 0;
        while (pos + 8 <= chk.Length)
        {
            uint size32 = BitConverter.ToUInt32(chk, pos + 4);
            if (size32 > Int32.MaxValue || pos + 8L + size32 > chk.Length) return result;
            int size = (int)size32;
            if (System.Text.Encoding.ASCII.GetString(chk, pos, 4) == "STRx")
            {
                if (size < 4) return result;
                int start = pos + 8;
                uint count32 = BitConverter.ToUInt32(chk, start);
                if (count32 == 0 || count32 > 0x100000) return result;
                long tableEnd = 4L + count32 * 4L;
                if (tableEnd > size) return result;
                int maxEnd = (int)tableEnd;
                for (int i = 1; i <= (int)count32; i++)
                {
                    uint off32 = BitConverter.ToUInt32(chk, start + i * 4);
                    if (off32 == 0 || off32 >= size) continue;
                    int end = (int)off32;
                    while (end < size && chk[start + end] != 0) end++;
                    if (end < size) end++;
                    if (end > maxEnd) maxEnd = end;
                }
                int padding = (-maxEnd) & 3;
                int payloadOffset = maxEnd + padding;
                if (payloadOffset >= size) return result;
                result.Valid = true;
                result.StrxChkOffset = start;
                result.StrxSize = size;
                result.PayloadStrxOffset = payloadOffset;
                result.PayloadSize = size - payloadOffset;
                result.PayloadMemoryAddress = unchecked(FreezeSc.StrxSectionAlloc + (uint)payloadOffset);
                return result;
            }
            pos += 8 + size;
        }
        return result;
    }
}
