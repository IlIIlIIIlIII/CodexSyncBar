// Shared by the Ubuntu and Windows builds: Claude Desktop is a Chromium app on
// both, and Chromium writes the same LevelDB files everywhere.
using System.Buffers.Binary;
using System.Text;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>Chromium's Local Storage was not as expected or was in use; nothing in it was changed.</summary>
internal sealed class ChromiumLocalStorageException(string message) : Exception(message);

/// <summary>
/// Entries of Chromium's Local Storage: the LevelDB database a profile keeps in
/// "Local Storage/leveldb", one entry per page origin and name. Only for use
/// while the browser is closed. A read takes the newest version of each entry
/// from the tables and logs the database lists. A write adds one batch in a new
/// log file, which LevelDB takes up the next time it opens the database: it
/// replays every log newer than the ones it has on record. The files already
/// there are never changed, and a database with anything in it this class does
/// not understand is left alone.
/// </summary>
internal sealed class ChromiumLocalStorage(string directory)
{
    private const int BlockSize = 32 * 1024;
    private const int HeaderSize = 7;
    private const ulong TableMagic = 0xdb4775248b80fb57;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const string Comparator = "leveldb.BytewiseComparator";
    private const byte Deletion = 0;
    private const byte Value = 1;

    private sealed record Manifest(ulong LogNumber, ulong PrevLogNumber, ulong NextFileNumber, ulong LastSequence, IReadOnlySet<ulong> Tables);

    // The newest version of every entry, and what a new batch has to come after.
    private sealed record Snapshot(Dictionary<string, (ulong Sequence, byte[]? Value)> Entries, ulong LastSequence, ulong NextFileNumber);

    public string Location => directory;

    public bool Exists => File.Exists(Path.Combine(directory, "CURRENT"));

    /// <summary>The database key of a page's entry, as Chromium writes a name that is all Latin-1.</summary>
    public static byte[] Key(string origin, string name)
    {
        if (!IsLatin1(origin) || !IsLatin1(name)) throw new ArgumentException("Local Storage 이름은 Latin-1 문자여야 합니다.", nameof(name));
        return Encoding.Latin1.GetBytes("_" + origin + "\0\u0001" + name);
    }

    /// <summary>The current values of the named entries; a name without a value is left out.</summary>
    public IReadOnlyDictionary<string, byte[]> Read(string origin, IEnumerable<string> names) => Update(origin, names, _ => []);

    /// <summary>Every entry of the origin whose name Chromium wrote as Latin-1.</summary>
    public IReadOnlyDictionary<string, byte[]> ReadAll(string origin)
    {
        using var held = Hold();
        var prefix = Encoding.Latin1.GetString(Key(origin, ""));
        return Read().Entries
            .Where(entry => entry.Value.Value is not null && entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key[prefix.Length..], entry => entry.Value.Value!, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads the named entries and writes what change returns for them, a null
    /// value removing the entry, as one batch. Returns what was read.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> Update(string origin, IEnumerable<string> names,
        Func<IReadOnlyDictionary<string, byte[]>, IEnumerable<(string Name, byte[]? Value)>> change)
    {
        using var held = Hold();
        var snapshot = Read();
        var current = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in names.Distinct(StringComparer.Ordinal))
            if (snapshot.Entries.TryGetValue(Encoding.Latin1.GetString(Key(origin, name)), out var entry) && entry.Value is not null)
                current[name] = entry.Value;
        var writes = change(current).Select(write => (Key(origin, write.Name), write.Value)).ToList();
        if (writes.Count > 0) WriteBatch(writes, snapshot.LastSequence + 1, snapshot.NextFileNumber);
        return current;
    }

    // While the browser has the database open it holds a lock on this file,
    // and holding it here keeps the browser out until the work is done.
    private FileStream Hold()
    {
        if (!Exists) throw new ChromiumLocalStorageException("Local Storage 데이터베이스가 없습니다.");
        FileStream stream;
        try
        {
            stream = new FileStream(Path.Combine(directory, "LOCK"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException)
        {
            throw new ChromiumLocalStorageException("Local Storage를 다른 프로그램이 쓰고 있습니다.");
        }

        try
        {
            // Where a lock cannot be taken there is no telling whether the browser is running.
            if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            else stream.Lock(0, 1);
            return stream;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            stream.Dispose();
            throw new ChromiumLocalStorageException("Local Storage를 다른 프로그램이 쓰고 있습니다.");
        }
    }

    private Snapshot Read()
    {
        var manifest = ReadManifest();
        var entries = new Dictionary<string, (ulong Sequence, byte[]? Value)>(StringComparer.Ordinal);
        var last = manifest.LastSequence;
        void Note(byte[] key, ulong sequence, byte type, byte[]? value)
        {
            last = Math.Max(last, sequence);
            var name = Encoding.Latin1.GetString(key);
            if (!entries.TryGetValue(name, out var known) || known.Sequence < sequence)
                entries[name] = (sequence, type == Value ? value : null);
        }

        foreach (var table in manifest.Tables)
        {
            var path = new[] { $"{table:D6}.ldb", $"{table:D6}.sst" }.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists)
                ?? throw new ChromiumLocalStorageException("Local Storage 표 파일이 없습니다.");
            foreach (var (key, sequence, type, value) in TableEntries(ReadFile(path))) Note(key, sequence, type, value);
        }

        var numbers = Numbered().ToList();
        foreach (var log in numbers.Where(file => file.Extension == ".log" && (file.Number >= manifest.LogNumber || file.Number == manifest.PrevLogNumber))
                     .Select(file => file.Number).Order())
            foreach (var record in LogRecords(ReadFile(Path.Combine(directory, $"{log:D6}.log"))))
            {
                var (sequence, operations) = ParseBatch(record);
                foreach (var (offset, (type, key, value)) in operations.Index()) Note(key, sequence + (ulong)offset, type, value);
                // An empty batch still counts: a new one must come after it. One past it is as good.
                last = Math.Max(last, sequence);
            }

        var next = Math.Max(manifest.NextFileNumber, numbers.Count == 0 ? 0 : numbers.Max(file => file.Number) + 1);
        return new(entries, last, next);
    }

    // Files LevelDB names by number: tables, logs and manifests.
    private IEnumerable<(ulong Number, string Extension)> Numbered()
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("MANIFEST-", StringComparison.Ordinal) && ulong.TryParse(name["MANIFEST-".Length..], out var manifest))
                yield return (manifest, "");
            else if (Path.GetExtension(name) is ".log" or ".ldb" or ".sst" or ".dbtmp"
                && ulong.TryParse(Path.GetFileNameWithoutExtension(name), out var number))
                yield return (number, Path.GetExtension(name));
        }
    }

    private Manifest ReadManifest()
    {
        var current = Encoding.ASCII.GetString(ReadFile(Path.Combine(directory, "CURRENT"))).TrimEnd('\n');
        if (!current.StartsWith("MANIFEST-", StringComparison.Ordinal) || !ulong.TryParse(current["MANIFEST-".Length..], out _))
            throw new ChromiumLocalStorageException("Local Storage의 CURRENT 파일을 읽지 못했습니다.");
        ulong log = 0, previousLog = 0, next = 0, last = 0;
        var tables = new HashSet<ulong>();
        foreach (var record in LogRecords(ReadFile(Path.Combine(directory, current))))
        {
            var reader = new Reader(record);
            while (!reader.Done)
                switch (reader.Varint32())
                {
                    case 1:
                        if (Encoding.ASCII.GetString(reader.Slice()) != Comparator)
                            throw new ChromiumLocalStorageException("Local Storage의 키 정렬 방식이 예상과 다릅니다.");
                        break;
                    case 2: log = reader.Varint64(); break;
                    case 3: next = reader.Varint64(); break;
                    case 4: last = reader.Varint64(); break;
                    case 5: reader.Varint32(); reader.Slice(); break;
                    case 6: reader.Varint32(); tables.Remove(reader.Varint64()); break;
                    case 7:
                        reader.Varint32();
                        tables.Add(reader.Varint64());
                        reader.Varint64();
                        reader.Slice();
                        reader.Slice();
                        break;
                    case 9: previousLog = reader.Varint64(); break;
                    default: throw new ChromiumLocalStorageException("Local Storage의 MANIFEST에 모르는 기록이 있습니다.");
                }
        }

        return new(log, previousLog, next, last, tables);
    }

    private byte[] ReadFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || file.Length > MaximumFileBytes)
            throw new ChromiumLocalStorageException("Local Storage 파일을 읽을 수 없습니다.");
        return File.ReadAllBytes(path);
    }

    private void WriteBatch(IReadOnlyList<(byte[] Key, byte[]? Value)> writes, ulong sequence, ulong number)
    {
        var batch = new List<byte>();
        Span<byte> fixed64 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(fixed64, sequence);
        batch.AddRange(fixed64);
        BinaryPrimitives.WriteUInt32LittleEndian(fixed64, (uint)writes.Count);
        batch.AddRange(fixed64[..4]);
        foreach (var (key, value) in writes)
        {
            batch.Add(value is null ? Deletion : Value);
            AppendSlice(batch, key);
            if (value is not null) AppendSlice(batch, value);
        }

        var destination = Path.Combine(directory, $"{number:D6}.log");
        if (File.Exists(destination)) throw new ChromiumLocalStorageException("Local Storage에 같은 번호의 로그가 이미 있습니다.");
        // LevelDB ignores a file it cannot name, so the batch shows up whole or not at all.
        var temporary = Path.Combine(directory, ".claude-syncbar-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(EncodeLog([.. batch]));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // One record in LevelDB's log format, split over 32 KiB blocks as needed, from the start of a new file.
    internal static byte[] EncodeLog(ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        var blockOffset = 0;
        var position = 0;
        var begin = true;
        Span<byte> header = stackalloc byte[HeaderSize];
        do
        {
            var leftover = BlockSize - blockOffset;
            if (leftover < HeaderSize)
            {
                output.Write(new byte[leftover]);
                blockOffset = 0;
            }

            var fragment = Math.Min(payload.Length - position, BlockSize - blockOffset - HeaderSize);
            var end = position + fragment == payload.Length;
            byte type = begin && end ? (byte)1 : begin ? (byte)2 : end ? (byte)4 : (byte)3;
            var data = payload.Slice(position, fragment);
            BinaryPrimitives.WriteUInt32LittleEndian(header, Mask(Crc32C([type], data)));
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)fragment);
            header[6] = type;
            output.Write(header);
            output.Write(data);
            blockOffset += HeaderSize + fragment;
            position += fragment;
            begin = false;
        }
        while (position < payload.Length);

        return output.ToArray();
    }

    // The records of a log, as LevelDB reads them. A record cut short at the end of
    // the file is one the writer never finished and is dropped, as LevelDB drops it;
    // anything else out of place means the database is not one to write to.
    internal static IEnumerable<byte[]> LogRecords(byte[] data)
    {
        var offset = 0;
        List<byte>? pending = null;
        while (offset < data.Length)
        {
            var leftover = BlockSize - offset % BlockSize;
            if (leftover < HeaderSize)
            {
                offset += leftover;
                continue;
            }

            if (data.Length - offset < HeaderSize) yield break;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
            var type = data[offset + 6];
            if (type == 0 && length == 0)
            {
                // Space a writer set aside ahead of time; the rest of the block is unused.
                offset += leftover;
                continue;
            }

            if (offset + HeaderSize + length > data.Length) yield break;
            if (HeaderSize + length > leftover || type is < 1 or > 4
                || Mask(Crc32C([type], data.AsSpan(offset + HeaderSize, length))) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)))
                throw new ChromiumLocalStorageException("Local Storage 로그가 손상되었습니다.");
            var fragment = data.AsSpan(offset + HeaderSize, length).ToArray();
            offset += HeaderSize + length;
            switch (type)
            {
                case 1:
                    if (pending is not null) throw new ChromiumLocalStorageException("Local Storage 로그가 손상되었습니다.");
                    yield return fragment;
                    break;
                case 2:
                    if (pending is not null) throw new ChromiumLocalStorageException("Local Storage 로그가 손상되었습니다.");
                    pending = [.. fragment];
                    break;
                default:
                    if (pending is null) throw new ChromiumLocalStorageException("Local Storage 로그가 손상되었습니다.");
                    pending.AddRange(fragment);
                    if (type == 4)
                    {
                        yield return [.. pending];
                        pending = null;
                    }

                    break;
            }
        }
    }

    private static (ulong Sequence, List<(byte Type, byte[] Key, byte[]? Value)> Operations) ParseBatch(byte[] record)
    {
        if (record.Length < 12) throw new ChromiumLocalStorageException("Local Storage 로그가 손상되었습니다.");
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(record);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8));
        var reader = new Reader(record.AsSpan(12).ToArray());
        var operations = new List<(byte, byte[], byte[]?)>();
        while (!reader.Done)
        {
            var type = reader.Byte();
            if (type is not (Deletion or Value)) throw new ChromiumLocalStorageException("Local Storage 로그가 손상되었습니다.");
            var key = reader.Slice();
            operations.Add((type, key, type == Value ? reader.Slice() : null));
        }

        if (operations.Count != count) throw new ChromiumLocalStorageException("Local Storage 로그가 손상되었습니다.");
        return (sequence, operations);
    }

    private static IEnumerable<(byte[] Key, ulong Sequence, byte Type, byte[] Value)> TableEntries(byte[] table)
    {
        if (table.Length < 48 || BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(table.Length - 8)) != TableMagic)
            throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
        var footer = new Reader(table.AsSpan(table.Length - 48, 40).ToArray());
        footer.Varint64();
        footer.Varint64();
        var index = Block(table, footer.Varint64(), footer.Varint64());
        foreach (var (_, handle) in BlockEntries(index))
        {
            var position = new Reader(handle);
            foreach (var (key, value) in BlockEntries(Block(table, position.Varint64(), position.Varint64())))
            {
                if (key.Length < 8) throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
                var tag = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(key.Length - 8));
                yield return (key[..^8], tag >> 8, (byte)tag, value);
            }
        }
    }

    private static byte[] Block(byte[] table, ulong offset, ulong size)
    {
        if (offset + size + 5 > (ulong)table.Length || size > int.MaxValue)
            throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
        var contents = table.AsSpan((int)offset, (int)size);
        var type = table[(int)(offset + size)];
        if (Mask(Crc32C(contents, [type])) != BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan((int)(offset + size + 1))))
            throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
        return type switch
        {
            0 => contents.ToArray(),
            1 => Unsnappy(contents),
            _ => throw new ChromiumLocalStorageException("Local Storage 표 파일의 압축 방식을 모릅니다."),
        };
    }

    private static IEnumerable<(byte[] Key, byte[] Value)> BlockEntries(byte[] block)
    {
        if (block.Length < 4) throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
        var restarts = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - 4));
        var end = block.Length - 4 - 4L * restarts;
        if (end < 0) throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
        var reader = new Reader(block[..(int)end]);
        var key = Array.Empty<byte>();
        while (!reader.Done)
        {
            var shared = reader.Varint32();
            var unshared = reader.Varint32();
            var length = reader.Varint32();
            if (shared > key.Length) throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
            key = [.. key.AsSpan(0, (int)shared), .. reader.Bytes(unshared)];
            yield return (key, reader.Bytes(length));
        }
    }

    // Snappy's block format, which LevelDB uses to compress table blocks.
    internal static byte[] Unsnappy(ReadOnlySpan<byte> input)
    {
        var reader = new Reader(input.ToArray());
        var length = reader.Varint32();
        if (length > MaximumFileBytes) throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
        var output = new byte[length];
        var written = 0;
        while (!reader.Done)
        {
            var tag = reader.Byte();
            int size, offset;
            switch (tag & 3)
            {
                case 0:
                    size = tag >> 2;
                    if (size >= 60)
                    {
                        var extra = reader.Bytes((uint)(size - 59));
                        size = 0;
                        for (var i = extra.Length - 1; i >= 0; i--) size = (size << 8) | extra[i];
                    }

                    var literal = reader.Bytes((uint)size + 1);
                    if (written + literal.Length > output.Length) throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
                    literal.CopyTo(output, written);
                    written += literal.Length;
                    continue;
                case 1:
                    size = ((tag >> 2) & 7) + 4;
                    offset = ((tag >> 5) << 8) | reader.Byte();
                    break;
                case 2:
                    size = (tag >> 2) + 1;
                    offset = BinaryPrimitives.ReadUInt16LittleEndian(reader.Bytes(2));
                    break;
                default:
                    size = (tag >> 2) + 1;
                    offset = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(reader.Bytes(4)), int.MaxValue);
                    break;
            }

            if (offset == 0 || offset > written || written + size > output.Length)
                throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
            // The copy may overlap what it is writing, so it goes byte by byte.
            for (var i = 0; i < size; i++, written++) output[written] = output[written - offset];
        }

        if (written != output.Length) throw new ChromiumLocalStorageException("Local Storage 표 파일이 손상되었습니다.");
        return output;
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
            table[i] = crc;
        }

        return table;
    }

    /// <summary>CRC-32C of the two spans one after the other.</summary>
    internal static uint Crc32C(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in first) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (var value in second) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    // How LevelDB stores a CRC next to data that may contain CRCs itself.
    internal static uint Mask(uint crc) => ((crc >> 15) | (crc << 17)) + 0xa282ead8u;

    internal static void AppendVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    internal static void AppendSlice(List<byte> output, ReadOnlySpan<byte> value)
    {
        AppendVarint(output, (ulong)value.Length);
        output.AddRange(value);
    }

    private static bool IsLatin1(string value) => value.All(character => character <= 'ÿ');

    private sealed class Reader(byte[] data)
    {
        private int _position;

        public bool Done => _position >= data.Length;

        public byte Byte() => Done ? throw Damaged() : data[_position++];

        public byte[] Bytes(uint count)
        {
            if (count > data.Length - _position) throw Damaged();
            var bytes = data.AsSpan(_position, (int)count).ToArray();
            _position += (int)count;
            return bytes;
        }

        public uint Varint32()
        {
            var value = Varint64();
            return value <= uint.MaxValue ? (uint)value : throw Damaged();
        }

        public ulong Varint64()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var part = Byte();
                value |= (ulong)(part & 0x7F) << shift;
                if (part < 0x80) return value;
            }

            throw Damaged();
        }

        public byte[] Slice() => Bytes(Varint32());

        private static ChromiumLocalStorageException Damaged() => new("Local Storage 데이터가 손상되었습니다.");
    }
}
