/* Copyright (c) 2026 unicbm. Licensed under AGPL-3.0-only; see LICENSE. */
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace DemoTracer;

internal sealed record ProjectilePhysicsProfile(
    string ImageSha256, string BodySha256, int EntryRva, int BodyLength,
    int[] VtableSlotRvas, string Signature)
{
    internal static ProjectilePhysicsProfile Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != 1
            || root.GetProperty("algorithm").GetString() != NativeCompatibility.PeImageFingerprint.Algorithm)
            throw new InvalidDataException("unsupported_native_profile");
        static string Hash(JsonElement e, string key)
        {
            string value = e.GetProperty(key).GetString()!;
            if (Convert.FromHexString(value).Length != 32) throw new InvalidDataException("invalid_native_profile_hash");
            return value.ToUpperInvariant();
        }
        var p = root.GetProperty("projectilePhysics");
        var result = new ProjectilePhysicsProfile(Hash(root, "imageSha256"), Hash(p, "bodySha256"),
            p.GetProperty("entryRva").GetInt32(), p.GetProperty("bodyLength").GetInt32(),
            p.GetProperty("vtableSlotRvas").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            p.GetProperty("signature").GetString()!);
        if (result.EntryRva <= 0 || result.BodyLength < 5 || result.VtableSlotRvas.Length == 0
            || result.VtableSlotRvas.Any(x => x <= 0 || x % 8 != 0))
            throw new InvalidDataException("invalid_native_profile_layout");
        return result;
    }

    internal bool OffsetsFitImage(int size) => size > 0 && EntryRva <= size - (long)BodyLength
        && VtableSlotRvas.All(rva => rva <= size - (long)sizeof(long));

    internal int ResolveEntry(byte[] data)
    {
        byte?[] pattern = Signature.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s is "?" or "??" ? (byte?)null : Convert.ToByte(s, 16)).ToArray();
        if (pattern.Length < 5 || !pattern.Any(x => x.HasValue)) throw new InvalidDataException("invalid_native_signature");
        using var pe = new PEReader(new MemoryStream(data, writable: false));
        int found = -1;
        foreach (var section in pe.PEHeaders.SectionHeaders)
        {
            if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0) continue;
            for (int i = 0; i <= section.SizeOfRawData - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                    if (pattern[j] is byte b && data[section.PointerToRawData + i + j] != b) { match = false; break; }
                if (!match) continue;
                if (found >= 0) throw new InvalidDataException("ambiguous_projectile_signature");
                found = section.VirtualAddress + i;
            }
        }
        if (found != EntryRva) throw new InvalidDataException("projectile_signature_profile_mismatch");
        return found;
    }
}
