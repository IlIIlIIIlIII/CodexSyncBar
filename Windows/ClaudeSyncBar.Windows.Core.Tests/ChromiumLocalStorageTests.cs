using System.Buffers.Binary;
using System.Text;
using ClaudeSyncBar.Windows.Core;

namespace ClaudeSyncBar.Windows.Core.Tests;

/// <summary>LevelDB databases built the way LevelDB itself writes them, and values as Chromium stores them.</summary>
internal static class LevelDbFixture
{
    public const string Origin = "https://claude.ai";

    /// <summary>A database as LevelDB creates it: CURRENT naming a manifest that records the comparator.</summary>
    public static void CreateEmpty(string directory) => Create(directory, logNumber: 0, nextFile: 2, lastSequence: 0, tables: []);

    public static void Create(string directory, ulong logNumber, ulong nextFile, ulong lastSequence, IEnumerable<ulong> tables,
        string comparator = "leveldb.BytewiseComparator")
    {
        Directory.CreateDirectory(directory);
        var edit = new List<byte>();
        ChromiumLocalStorage.AppendVarint(edit, 1);
        ChromiumLocalStorage.AppendSlice(edit, Encoding.ASCII.GetBytes(comparator));
        foreach (var (tag, value) in new[] { (2UL, logNumber), (9UL, 0UL), (3UL, nextFile), (4UL, lastSequence) })
        {
            ChromiumLocalStorage.AppendVarint(edit, tag);
            ChromiumLocalStorage.AppendVarint(edit, value);
        }

        foreach (var table in tables)
        {
            ChromiumLocalStorage.AppendVarint(edit, 7);
            ChromiumLocalStorage.AppendVarint(edit, 0);
            ChromiumLocalStorage.AppendVarint(edit, table);
            ChromiumLocalStorage.AppendVarint(edit, 1);
            ChromiumLocalStorage.AppendSlice(edit, "a\0\0\0\0\0\0\0\0"u8);
            ChromiumLocalStorage.AppendSlice(edit, "z\0\0\0\0\0\0\0\0"u8);
        }

        File.WriteAllBytes(Path.Combine(directory, "MANIFEST-000001"), ChromiumLocalStorage.EncodeLog([.. edit]));
        File.WriteAllText(Path.Combine(directory, "CURRENT"), "MANIFEST-000001\n");
        File.WriteAllBytes(Path.Combine(directory, "LOCK"), []);
    }

    /// <summary>One write batch in its own log file, as LevelDB appends them.</summary>
    public static void WriteLog(string path, ulong sequence, params (byte[] Key, byte[]? Value)[] writes)
    {
        var batch = new List<byte>(new byte[12]);
        BinaryPrimitives.WriteUInt64LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch)[8..], (uint)writes.Length);
        foreach (var (key, value) in writes)
        {
            batch.Add(value is null ? (byte)0 : (byte)1);
            ChromiumLocalStorage.AppendSlice(batch, key);
            if (value is not null) ChromiumLocalStorage.AppendSlice(batch, value);
        }

        File.WriteAllBytes(path, ChromiumLocalStorage.EncodeLog([.. batch]));
    }

    /// <summary>A table with one data block of the entries (a null value: a deletion), compressed with Snappy or not.</summary>
    public static void WriteTable(string path, IEnumerable<(byte[] Key, ulong Sequence, byte[]? Value)> entries, bool compress)
    {
        var sorted = entries.OrderBy(entry => entry.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ThenByDescending(entry => entry.Sequence).ToList();
        var table = new List<byte>();
        var data = new List<byte>();
        byte[] last = [];
        foreach (var (key, sequence, value) in sorted)
        {
            var tag = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tag, sequence << 8 | (value is null ? 0UL : 1UL));
            last = [.. key, .. tag];
            ChromiumLocalStorage.AppendVarint(data, 0);
            ChromiumLocalStorage.AppendVarint(data, (ulong)last.Length);
            ChromiumLocalStorage.AppendVarint(data, (ulong)(value?.Length ?? 0));
            data.AddRange(last);
            data.AddRange(value ?? []);
        }

        var dataHandle = AppendBlock(table, Finish(data), compress);
        var index = new List<byte>();
        var handle = new List<byte>();
        ChromiumLocalStorage.AppendVarint(handle, dataHandle.Offset);
        ChromiumLocalStorage.AppendVarint(handle, dataHandle.Size);
        ChromiumLocalStorage.AppendVarint(index, 0);
        ChromiumLocalStorage.AppendVarint(index, (ulong)last.Length);
        ChromiumLocalStorage.AppendVarint(index, (ulong)handle.Count);
        index.AddRange(last);
        index.AddRange(handle);
        var metaHandle = AppendBlock(table, Finish([]), compress: false);
        var indexHandle = AppendBlock(table, Finish(index), compress: false);
        var footer = new List<byte>();
        foreach (var value in new[] { metaHandle.Offset, metaHandle.Size, indexHandle.Offset, indexHandle.Size })
            ChromiumLocalStorage.AppendVarint(footer, value);
        footer.AddRange(new byte[40 - footer.Count]);
        var magic = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(magic, 0xdb4775248b80fb57);
        footer.AddRange(magic);
        table.AddRange(footer);
        File.WriteAllBytes(path, [.. table]);
    }

    // Entries followed by the restart points: one, at the start.
    private static byte[] Finish(List<byte> entries) => [.. entries, 0, 0, 0, 0, 1, 0, 0, 0];

    private static (ulong Offset, ulong Size) AppendBlock(List<byte> table, byte[] block, bool compress)
    {
        var contents = compress ? Snappy(block) : block;
        var offset = (ulong)table.Count;
        table.AddRange(contents);
        byte type = compress ? (byte)1 : (byte)0;
        table.Add(type);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, ChromiumLocalStorage.Mask(ChromiumLocalStorage.Crc32C(contents, [type])));
        table.AddRange(crc);
        return (offset, (ulong)contents.Length);
    }

    /// <summary>Snappy's block format with literals only, which every Snappy reader accepts.</summary>
    public static byte[] Snappy(byte[] data)
    {
        var output = new List<byte>();
        ChromiumLocalStorage.AppendVarint(output, (ulong)data.Length);
        for (var start = 0; start < data.Length; start += 256)
        {
            var length = Math.Min(256, data.Length - start);
            output.Add(60 << 2);
            output.Add((byte)(length - 1));
            output.AddRange(data.AsSpan(start, length));
        }

        return [.. output];
    }

    /// <summary>A value as Chromium stores it: Latin-1 behind a 1 when it can be, UTF-16 behind a 0 otherwise.</summary>
    public static byte[] Value(string text) => text.All(character => character <= 'ÿ')
        ? [1, .. Encoding.Latin1.GetBytes(text)]
        : [0, .. Encoding.Unicode.GetBytes(text)];

    public static string Text(byte[] value) => value[0] == 0
        ? Encoding.Unicode.GetString(value, 1, value.Length - 1)
        : Encoding.Latin1.GetString(value, 1, value.Length - 1);

    public static string? Read(string directory, string name) =>
        new ChromiumLocalStorage(directory).Read(Origin, [name]).TryGetValue(name, out var value) ? Text(value) : null;

    public static void Put(string directory, params (string Name, string? Text)[] entries) =>
        new ChromiumLocalStorage(directory).Update(Origin, [], _ => entries.Select(entry => (entry.Name, entry.Text is null ? null : Value(entry.Text))));
}

public sealed class ChromiumLocalStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-leveldb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string[] Logs() => Directory.GetFiles(_root, "*.log").Select(path => Path.GetFileName(path)).Order().ToArray();

    [Fact]
    public void ChecksumsAreLevelDbsCrc32C()
    {
        Assert.Equal(0xE3069283u, ChromiumLocalStorage.Crc32C("123456789"u8, []));
        // LevelDB's own test values.
        Assert.Equal(0x8a9136aau, ChromiumLocalStorage.Crc32C(new byte[32], []));
        Assert.Equal(0x62a8ab43u, ChromiumLocalStorage.Crc32C(Enumerable.Repeat((byte)0xff, 32).ToArray(), []));
        Assert.Equal(ChromiumLocalStorage.Crc32C("hello world"u8, []), ChromiumLocalStorage.Crc32C("hello "u8, "world"u8));
    }

    [Fact]
    public void KeysAreTheLatin1FormChromiumWrites()
    {
        Assert.Equal("_https://claude.ai\0\u0001persisted.x"u8.ToArray(), ChromiumLocalStorage.Key(LevelDbFixture.Origin, "persisted.x"));
        Assert.Throws<ArgumentException>(() => ChromiumLocalStorage.Key(LevelDbFixture.Origin, "프리셋"));
    }

    [Fact]
    public void SnappyBlocksAreDecodedIncludingOverlappingCopies()
    {
        // A literal, then a copy with a one-byte offset that overlaps what it writes.
        Assert.Equal("abcabcabcabc"u8.ToArray(), ChromiumLocalStorage.Unsnappy([12, 0x08, (byte)'a', (byte)'b', (byte)'c', 0x15, 0x03]));
        // A copy with a two-byte offset, and one with a four-byte offset.
        Assert.Equal("hellohellohello"u8.ToArray(), ChromiumLocalStorage.Unsnappy([15, 0x10, .. "hello"u8, 0x26, 5, 0]));
        Assert.Equal("hellohellohello"u8.ToArray(), ChromiumLocalStorage.Unsnappy([15, 0x10, .. "hello"u8, 0x27, 5, 0, 0, 0]));
        // A literal whose length takes a byte of its own.
        var long_ = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        Assert.Equal(long_, ChromiumLocalStorage.Unsnappy([100, 0xF0, 99, .. long_]));
        Assert.Equal(long_, ChromiumLocalStorage.Unsnappy(LevelDbFixture.Snappy(long_)));
        // A copy from before the start, or more than was announced, is damage.
        Assert.Throws<ChromiumLocalStorageException>(() => ChromiumLocalStorage.Unsnappy([12, 0x08, (byte)'a', (byte)'b', (byte)'c', 0x15, 0x09]));
        Assert.Throws<ChromiumLocalStorageException>(() => ChromiumLocalStorage.Unsnappy([2, 0x08, (byte)'a', (byte)'b', (byte)'c']));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32 * 1024 - 7)]
    [InlineData(32 * 1024 - 8)]
    [InlineData(32 * 1024 - 6)]
    [InlineData(32 * 1024)]
    [InlineData(2 * (32 * 1024 - 7))]
    [InlineData(100_000)]
    public void ALogRecordOfAnySizeReadsBackWhole(int size)
    {
        var payload = Enumerable.Range(0, size).Select(value => (byte)(value * 7)).ToArray();
        Assert.Equal(payload, Assert.Single(ChromiumLocalStorage.LogRecords(ChromiumLocalStorage.EncodeLog(payload))));
    }

    [Fact]
    public void LogRecordsFillBlocksAndContinueInTheNextAsLevelDbLaysThemOut()
    {
        const int block = 32 * 1024;
        // A record that fills its block exactly, one a byte short, and one a byte over.
        Assert.Equal(block, ChromiumLocalStorage.EncodeLog(new byte[block - 7]).Length);
        Assert.Equal(block - 1, ChromiumLocalStorage.EncodeLog(new byte[block - 8]).Length);
        Assert.Equal(block + 7 + 1, ChromiumLocalStorage.EncodeLog(new byte[block - 6]).Length);

        // A writer that ends a record with less than a header's room left pads the block with zeros.
        var first = Enumerable.Repeat((byte)1, block - 7 - 3).ToArray();
        var second = Enumerable.Repeat((byte)2, 10).ToArray();
        byte[] log = [.. ChromiumLocalStorage.EncodeLog(first), 0, 0, 0, .. ChromiumLocalStorage.EncodeLog(second)];
        Assert.Equal(new[] { first, second }, ChromiumLocalStorage.LogRecords(log));
        // Space set aside ahead of time reads as the end of the block.
        byte[] reserved = [.. ChromiumLocalStorage.EncodeLog(second), .. new byte[block - 17], .. ChromiumLocalStorage.EncodeLog(first)];
        Assert.Equal(new[] { second, first }, ChromiumLocalStorage.LogRecords(reserved));
    }

    [Fact]
    public void WritesAreNewLogsThatReadBackNewestFirst()
    {
        LevelDbFixture.CreateEmpty(_root);
        LevelDbFixture.Put(_root, ("persisted.a", "first"), ("persisted.b", "프리셋 {\"name\":\"컴퓨터 관리\"}"));
        Assert.Equal("first", LevelDbFixture.Read(_root, "persisted.a"));
        Assert.Equal("프리셋 {\"name\":\"컴퓨터 관리\"}", LevelDbFixture.Read(_root, "persisted.b"));

        LevelDbFixture.Put(_root, ("persisted.a", null), ("persisted.b", "second"));
        Assert.Null(LevelDbFixture.Read(_root, "persisted.a"));
        Assert.Equal("second", LevelDbFixture.Read(_root, "persisted.b"));
        // Each write is a log of its own after the ones there, with numbers after theirs.
        Assert.Equal(new[] { "000002.log", "000003.log" }, Logs());
        var sequences = Logs().Select(name => BinaryPrimitives.ReadUInt64LittleEndian(
            Assert.Single(ChromiumLocalStorage.LogRecords(File.ReadAllBytes(Path.Combine(_root, name)))))).ToArray();
        Assert.Equal(new ulong[] { 1, 3 }, sequences);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        // Nothing to write writes nothing.
        new ChromiumLocalStorage(_root).Update(LevelDbFixture.Origin, ["persisted.b"], _ => []);
        Assert.Equal(2, Logs().Length);
    }

    [Fact]
    public void AValueLargerThanALogBlockIsWrittenWhole()
    {
        LevelDbFixture.CreateEmpty(_root);
        var large = string.Concat(Enumerable.Repeat("{\"name\":\"프리셋\",\"cwd\":\"/home/user/project\"},", 2000));
        LevelDbFixture.Put(_root, ("persisted.large", large));
        Assert.Equal(large, LevelDbFixture.Read(_root, "persisted.large"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TablesAndLogsTogetherGiveTheNewestVersion(bool compress)
    {
        var key = (string name) => ChromiumLocalStorage.Key(LevelDbFixture.Origin, name);
        LevelDbFixture.Create(_root, logNumber: 9, nextFile: 10, lastSequence: 7, tables: [5]);
        LevelDbFixture.WriteTable(Path.Combine(_root, "000005.ldb"),
        [
            (key("persisted.a"), 5, LevelDbFixture.Value("old")),
            (key("persisted.a"), 2, LevelDbFixture.Value("older")),
            (key("persisted.b"), 6, LevelDbFixture.Value("from the table")),
            (key("persisted.c"), 7, null),
            (key("persisted.c"), 3, LevelDbFixture.Value("deleted since")),
        ], compress);
        // A table the manifest does not list is a leftover and no part of the database.
        LevelDbFixture.WriteTable(Path.Combine(_root, "000004.ldb"), [(key("persisted.d"), 4, LevelDbFixture.Value("gone"))], compress);
        // A log older than the one on record was already written into the tables.
        LevelDbFixture.WriteLog(Path.Combine(_root, "000003.log"), 100, (key("persisted.b"), LevelDbFixture.Value("stale")));
        LevelDbFixture.WriteLog(Path.Combine(_root, "000009.log"), 8, (key("persisted.a"), LevelDbFixture.Value("newer")));

        var storage = new ChromiumLocalStorage(_root);
        var all = storage.ReadAll(LevelDbFixture.Origin);
        Assert.Equal(new[] { "persisted.a", "persisted.b" }, all.Keys.Order());
        Assert.Equal("newer", LevelDbFixture.Text(all["persisted.a"]));
        Assert.Equal("from the table", LevelDbFixture.Text(all["persisted.b"]));

        LevelDbFixture.Put(_root, ("persisted.b", "written"));
        Assert.Equal("written", LevelDbFixture.Read(_root, "persisted.b"));
        // The new log is numbered after every file there and its batch after every sequence number.
        var record = Assert.Single(ChromiumLocalStorage.LogRecords(File.ReadAllBytes(Path.Combine(_root, "000010.log"))));
        Assert.Equal(9UL, BinaryPrimitives.ReadUInt64LittleEndian(record));
    }

    [Fact]
    public void ADamagedLogLeavesTheDatabaseAlone()
    {
        LevelDbFixture.CreateEmpty(_root);
        LevelDbFixture.Put(_root, ("persisted.a", "kept"));
        var log = Path.Combine(_root, "000002.log");
        var bytes = File.ReadAllBytes(log);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(log, bytes);

        Assert.Throws<ChromiumLocalStorageException>(() => LevelDbFixture.Put(_root, ("persisted.a", "replaced")));
        Assert.Equal(new[] { "000002.log" }, Logs());
    }

    [Fact]
    public void ALogTheWriterNeverFinishedIsDroppedAsLevelDbDropsIt()
    {
        LevelDbFixture.CreateEmpty(_root);
        LevelDbFixture.Put(_root, ("persisted.a", "first"), ("persisted.b", "kept"));
        var unfinished = Path.Combine(_root, "000003.log");
        LevelDbFixture.WriteLog(unfinished, 3, (ChromiumLocalStorage.Key(LevelDbFixture.Origin, "persisted.a"), LevelDbFixture.Value("cut short")));
        File.WriteAllBytes(unfinished, File.ReadAllBytes(unfinished)[..^4]);

        Assert.Equal("first", LevelDbFixture.Read(_root, "persisted.a"));
        LevelDbFixture.Put(_root, ("persisted.a", "after"));
        Assert.Equal("after", LevelDbFixture.Read(_root, "persisted.a"));
        Assert.Equal("kept", LevelDbFixture.Read(_root, "persisted.b"));
    }

    [Fact]
    public void AnotherKeyOrderIsADatabaseLeftAlone()
    {
        LevelDbFixture.Create(_root, 0, 2, 0, [], comparator: "idb_cmp1");
        Assert.Throws<ChromiumLocalStorageException>(() => LevelDbFixture.Put(_root, ("persisted.a", "x")));
        Assert.Empty(Logs());
    }

    [WindowsFact]
    public void ADatabaseTheBrowserHasOpenIsLeftAlone()
    {
        LevelDbFixture.CreateEmpty(_root);
        // Chromium locks the whole LOCK file for as long as it has the database open.
        using (var browser = new FileStream(Path.Combine(_root, "LOCK"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            browser.Lock(0, long.MaxValue);
            Assert.Throws<ChromiumLocalStorageException>(() => LevelDbFixture.Put(_root, ("persisted.a", "x")));
            Assert.Empty(Logs());
        }

        LevelDbFixture.Put(_root, ("persisted.a", "x"));
        Assert.Equal("x", LevelDbFixture.Read(_root, "persisted.a"));
    }

    [LinuxProcessLockFact]
    public void ADatabaseAnotherProcessHasOpenOnLinuxIsLeftAlone()
    {
        LevelDbFixture.CreateEmpty(_root);
        // Chromium on Linux takes an fcntl lock on the whole LOCK file; Python's lockf takes the same kind.
        using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python3")
        {
            ArgumentList = { "-c", "import fcntl,sys; f=open(sys.argv[1],'r+'); fcntl.lockf(f, fcntl.LOCK_EX); print('held', flush=True); sys.stdin.read()", Path.Combine(_root, "LOCK") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        try
        {
            Assert.Equal("held", browser.StandardOutput.ReadLine());
            Assert.Throws<ChromiumLocalStorageException>(() => LevelDbFixture.Put(_root, ("persisted.a", "x")));
            Assert.Empty(Logs());
        }
        finally
        {
            browser.StandardInput.Close();
            browser.WaitForExit(10000);
        }

        LevelDbFixture.Put(_root, ("persisted.a", "x"));
        Assert.Equal("x", LevelDbFixture.Read(_root, "persisted.a"));
    }
}

public sealed class LinuxProcessLockFactAttribute : FactAttribute
{
    public LinuxProcessLockFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux에서 다른 프로세스의 fcntl 잠금을 확인합니다.";
        else if (!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(directory => File.Exists(Path.Combine(directory, "python3"))))
            Skip = "python3가 필요합니다.";
    }
}
