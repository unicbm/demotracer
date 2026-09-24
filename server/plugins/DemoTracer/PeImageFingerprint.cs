// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 unicbm. Shared with DemoTracer under the same license.
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace NativeCompatibility;

// pe-image-v1: PE signature/headers/section table + every section's raw bytes.
// Excludes DOS stub, certificate/overlay, timestamps, checksum and CodeView age.
// Retains code, data, relocations, unwind info, RVA/layout and PDB GUID.
internal static class PeImageFingerprint
{
    internal const string Algorithm = "pe-image-v1";

    internal static string Compute(byte[] file)
    {
        byte[] data = (byte[])file.Clone();
        using var reader = new PEReader(new MemoryStream(data, writable: false));
        var h = reader.PEHeaders;
        if (h.CoffHeader.Machine != Machine.Amd64 || h.PEHeader?.Magic != PEMagic.PE32Plus)
            throw new InvalidDataException("Fingerprint requires Windows x64 PE32+");
        int pe = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3c, 4));
        int optional = checked(pe + 24);
        int headerEnd = checked(optional + h.CoffHeader.SizeOfOptionalHeader + h.SectionHeaders.Length * 40);
        if (pe < 64 || headerEnd > data.Length || h.CoffHeader.SizeOfOptionalHeader < 160)
            throw new InvalidDataException("Invalid PE headers");
        int previousEnd = headerEnd;
        foreach (var section in h.SectionHeaders.OrderBy(s => s.PointerToRawData))
        {
            if (section.SizeOfRawData == 0) continue;
            if (section.PointerToRawData < previousEnd || section.SizeOfRawData < 0
                || section.PointerToRawData > data.Length - section.SizeOfRawData)
                throw new InvalidDataException("Invalid/overlapping PE sections");
            previousEnd = checked(section.PointerToRawData + section.SizeOfRawData);
        }
        void Clear(int offset, int size) => data.AsSpan(offset, size).Clear();
        void ClearDebug(int offset, int size)
        {
            if (!h.SectionHeaders.Any(s => offset >= s.PointerToRawData && size >= 0
                && (long)offset + size <= (long)s.PointerToRawData + s.SizeOfRawData
                && (s.SectionCharacteristics & (SectionCharacteristics.MemExecute | SectionCharacteristics.MemWrite)) == 0))
                throw new InvalidDataException("Debug metadata outside read-only data");
            Clear(offset, size);
        }
        Clear(pe + 8, 4); // COFF timestamp.
        Clear(optional + 64, 4); // Checksum.
        Clear(optional + 112 + 4 * 8, 8); // Certificate file offset and size.
        var directory = h.PEHeader.DebugTableDirectory;
        if (directory.Size != 0)
        {
            if (directory.Size % 28 != 0 || !h.TryGetDirectoryOffset(directory, out int start))
                throw new InvalidDataException("Invalid debug directory");
            var entries = reader.ReadDebugDirectory();
            for (int i = 0; i < entries.Length; i++)
            {
                ClearDebug(checked(start + i * 28 + 4), 4);
                var entry = entries[i];
                if (entry.Type != DebugDirectoryEntryType.CodeView) continue;
                _ = reader.ReadCodeViewDebugDirectoryData(entry); // Validate RSDS record.
                ClearDebug(checked(entry.DataPointer + 20), 4);
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(data.AsSpan(pe, headerEnd - pe));
        foreach (var section in h.SectionHeaders)
            if (section.SizeOfRawData > 0)
                hash.AppendData(data.AsSpan(section.PointerToRawData, section.SizeOfRawData));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
