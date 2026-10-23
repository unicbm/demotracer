/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoTracer;

/// <summary>v13 container index. The independently encoded replay resources retain v12.</summary>
internal sealed class DtrArchive : IDisposable
{
    public const uint Version = 13;
    private const long MaxResourceBytes = 64L * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal sealed record Entry(
        [property: JsonPropertyName("path"), JsonRequired] string Path,
        [property: JsonPropertyName("offset"), JsonRequired] ulong Offset,
        [property: JsonPropertyName("length"), JsonRequired] ulong Length,
        [property: JsonPropertyName("sha256"), JsonRequired] string Sha256);

    public DtrArchive(string path)
    {
        _stream = File.OpenRead(path);
        try
        {
            if (_stream.Length < 64 || _stream.Length > 128L * 1024 * 1024 * 1024)
                throw new InvalidDataException("invalid DTR container size");
            Span<byte> header = stackalloc byte[64];
            _stream.ReadExactly(header);
            if (!header[..8].SequenceEqual("CSDTRPAK"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != Version
                || BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) != 64)
                throw new InvalidDataException("unsupported DTR container header; expected v13");
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
            var length = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
            if (offset < 64 || length is 0 or > 8 * 1024 * 1024 || offset > (ulong)_stream.Length
                || length != (ulong)_stream.Length - offset)
                throw new InvalidDataException("invalid DTR index bounds");
            _stream.Position = (long)offset;
            var bytes = new byte[(int)length];
            _stream.ReadExactly(bytes);
            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(header[32..]))
                throw new InvalidDataException("DTR index checksum mismatch");
            var rows = JsonSerializer.Deserialize<Entry[]>(bytes, new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("missing DTR index");
            if (rows.Length is 0 or > 32768) throw new InvalidDataException("invalid DTR entry count");
            foreach (var row in rows)
            {
                if (row is null) throw new InvalidDataException("null DTR resource entry");
                ValidateName(row.Path);
                if (row.Offset < 64 || row.Offset > offset || row.Length > (ulong)MaxResourceBytes || row.Length > offset - row.Offset
                    || row.Sha256 is not { Length: 64 } || !row.Sha256.All(Uri.IsHexDigit))
                    throw new InvalidDataException("invalid DTR resource bounds or digest");
                if (!_entries.TryAdd(row.Path, row)) throw new InvalidDataException("duplicate DTR resource");
            }
            ulong end = 64;
            foreach (var row in rows.OrderBy(row => row.Offset).ThenBy(row => row.Length))
            {
                if (row.Offset < end) throw new InvalidDataException("overlapping DTR resources");
                end = row.Offset + row.Length;
            }
        }
        catch { _stream.Dispose(); throw; }
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || System.Text.Encoding.UTF8.GetByteCount(name) > 1024
            || name.Any(c => char.IsControl(c) || c is '\\' or ':')
            || name.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("invalid DTR resource name");
    }

    public Entry GetEntry(string name) => _entries.TryGetValue(name, out var entry)
        ? entry : throw new FileNotFoundException($"DTR resource missing: {name}");
    public bool Contains(string name) => _entries.ContainsKey(name);
    public byte[] Read(string name)
    {
        var entry = GetEntry(name);
        _stream.Position = (long)entry.Offset;
        var bytes = new byte[(int)entry.Length];
        _stream.ReadExactly(bytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"DTR resource checksum mismatch: {name}");
        return bytes;
    }
    public void Dispose() => _stream.Dispose();
}

/// <summary>Internal resource addresses preserve entry identity in the existing prefetch caches.
/// Only this I/O boundary interprets the archive.dtr!/resource address; it is never extracted.</summary>
internal static class DtrResources
{
    public static bool IsContainer(string path) => path.EndsWith(".dtr", StringComparison.OrdinalIgnoreCase);
    public static string Root(string manifestPath) => IsContainer(manifestPath)
        ? Path.GetFullPath(manifestPath) : Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
    public static string Address(string container, string name)
    {
        DtrArchive.ValidateName(name);
        return Path.GetFullPath(container) + "!/" + name;
    }
    private static (string File, string? Entry) Split(string path)
    {
        var normalized = path.Replace('\\', '/');
        var split = normalized.LastIndexOf(".dtr!/", StringComparison.OrdinalIgnoreCase);
        if (split < 0) return (path, null);
        var name = normalized[(split + 6)..];
        DtrArchive.ValidateName(name);
        return (normalized[..(split + 4)], name);
    }
    public static string PhysicalPath(string path) => Split(path).File;
    public static Stream OpenRead(string path)
    {
        var (file, entry) = Split(path);
        if (entry == null) return File.OpenRead(file);
        using var archive = new DtrArchive(file);
        return new MemoryStream(archive.Read(entry), writable: false);
    }
    public static byte[] ReadAllBytes(string path)
    {
        var (file, entry) = Split(path);
        if (entry == null) return File.ReadAllBytes(file);
        using var archive = new DtrArchive(file);
        return archive.Read(entry);
    }
    public static long Length(string path)
    {
        var (file, entry) = Split(path);
        if (entry == null) return new FileInfo(file).Length;
        using var archive = new DtrArchive(file);
        return checked((long)archive.GetEntry(entry).Length);
    }
    public static bool Exists(string path)
    {
        var (file, entry) = Split(path);
        if (!File.Exists(file)) return false;
        if (entry == null) return true;
        using var archive = new DtrArchive(file);
        return archive.Contains(entry);
    }
}
