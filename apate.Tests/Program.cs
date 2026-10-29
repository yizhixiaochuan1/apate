using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using apate;

var tests = new (string Name, Action Run)[]
{
    ("Round trips preserve every byte, name and both source files", RoundTrips),
    ("Ordinary files and old files are rejected by normal restoration", RejectOrdinaryFiles),
    ("Corrupt/truncated metadata and payload never publish output", RejectCorruption),
    ("Unsafe stored names cannot escape the source directory", RejectUnsafeName),
    ("Output collisions never overwrite existing files", Collisions),
    ("Partial writes, cancellation and commit races clean up temporary files", TransactionFailures),
    ("Legacy restoration works on shorter/equal/longer and empty originals", LegacyRoundTrips),
    ("Empty, maximum-size and missing masks are rejected", InvalidMasks),
    ("Batch input is deduplicated and a failed item does not stop others", BatchIsolation),
    ("Batch cancellation stops without modifying inputs", BatchCancellation),
    ("Directory links are skipped rather than recursively followed", DirectoryLinks),
    ("Locked source produces a recoverable batch failure on Windows", LockedSource)
};
int failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + ex); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} test groups passed");
return failed == 0 ? 0 : 1;

static void RoundTrips()
{
    using var folder = new TestFolder();
    foreach (int length in new[] { 0, 1, 31, 32, 33, 2000000 })
    {
        byte[] original = Bytes(length);
        string source = folder.File($"中文-{length}.data", original);
        byte[] mask = Bytes(32);
        string disguised = FileTransformer.Disguise(source, mask, ".mp4");
        Equal(original, File.ReadAllBytes(source));
        Check(File.ReadAllBytes(disguised).Take(32).SequenceEqual(mask), "mask prefix missing");
        byte[] disguisedBytes = File.ReadAllBytes(disguised);
        string renamed = disguised + ".renamed";
        File.Move(disguised, renamed);
        string restored = FileTransformer.Reveal(renamed);
        Check(Path.GetFileName(restored) == $"中文-{length}.restored.data", "stored name was not restored");
        Equal(original, File.ReadAllBytes(restored));
        Equal(original, File.ReadAllBytes(source));
        Equal(disguisedBytes, File.ReadAllBytes(renamed));
        Reject(() => FileTransformer.Disguise(renamed, mask, ".mp4"));
    }
    folder.NoTemps();
}

static void RejectOrdinaryFiles()
{
    using var folder = new TestFolder();
    var inputs = new[]
    {
        Array.Empty<byte>(), new byte[4], Bytes(95), new byte[200],
        Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP").Concat(BitConverter.GetBytes(4)).ToArray(),
        LegacyBytes(Bytes(200), Bytes(32))
    };
    for (int i = 0; i < inputs.Length; i++)
    {
        string source = folder.File($"ordinary-{i}.mp4", inputs[i]);
        int count = Directory.GetFiles(folder.Path).Length;
        Reject(() => FileTransformer.Reveal(source));
        Equal(inputs[i], File.ReadAllBytes(source));
        Check(Directory.GetFiles(folder.Path).Length == count, "rejected file produced output");
    }
    folder.NoTemps();
}

static void RejectCorruption()
{
    using var folder = new TestFolder();
    string source = folder.File("original.bin", Bytes(300));
    string disguised = FileTransformer.Disguise(source, Bytes(32), ".mp4");
    byte[] pristine = File.ReadAllBytes(disguised);
    var mutations = new List<byte[]>();
    foreach (int offset in new[] { 40, 300, pristine.Length - 96, pristine.Length - 88, pristine.Length - 84,
        pristine.Length - 76, pristine.Length - 72, pristine.Length - 68, pristine.Length - 64, pristine.Length - 1 })
    {
        byte[] damaged = (byte[])pristine.Clone();
        damaged[offset] ^= 0x7f;
        mutations.Add(damaged);
    }
    mutations.Add(pristine[..^1]);
    mutations.Add(pristine[..^100]);
    byte[] overflow = (byte[])pristine.Clone();
    BinaryPrimitives.WriteInt64LittleEndian(overflow.AsSpan(overflow.Length - 84), long.MaxValue);
    mutations.Add(overflow);
    for (int i = 0; i < mutations.Count; i++)
    {
        string damaged = folder.File($"damaged-{i}.mp4", mutations[i]);
        int count = Directory.GetFiles(folder.Path).Length;
        Reject(() => FileTransformer.Reveal(damaged));
        Equal(mutations[i], File.ReadAllBytes(damaged));
        Check(Directory.GetFiles(folder.Path).Length == count, "damaged input produced output");
    }
    folder.NoTemps();
}

static void RejectUnsafeName()
{
    using var folder = new TestFolder();
    string source = folder.File("good.bin", Bytes(64));
    string disguised = FileTransformer.Disguise(source, Bytes(32), ".mp4");
    byte[] data = File.ReadAllBytes(disguised);
    // Recompute a structurally valid metadata digest to test path validation itself.
    int footer = data.Length - 96;
    byte[] unsafeName = Encoding.UTF8.GetBytes("../x.bin");
    unsafeName.CopyTo(data, footer - unsafeName.Length);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    hash.AppendData(data, 64, footer - 64);
    hash.AppendData(data, footer, 64);
    hash.GetHashAndReset().CopyTo(data, footer + 64);
    File.WriteAllBytes(disguised, data);
    Reject(() => FileTransformer.Reveal(disguised));
    folder.NoTemps();
}

static void Collisions()
{
    using var folder = new TestFolder();
    byte[] original = Bytes(100);
    string source = folder.File("file.bin", original);
    string collision = folder.File("file.bin.mp4", new byte[] { 9, 8, 7 });
    Reject(() => FileTransformer.Disguise(source, Bytes(32), ".mp4"));
    Equal(original, File.ReadAllBytes(source));
    Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(collision));
    File.Delete(collision);
    string disguised = FileTransformer.Disguise(source, Bytes(32), ".mp4");
    byte[] before = File.ReadAllBytes(disguised);
    folder.File("file.restored.bin", new byte[] { 6 });
    Reject(() => FileTransformer.Reveal(disguised));
    Equal(before, File.ReadAllBytes(disguised));
    Equal(new byte[] { 6 }, File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "file.restored.bin")));
    // Directory collisions are rejected before any writes too.
    string another = folder.File("another.bin", original);
    Directory.CreateDirectory(another + ".mp4");
    Reject(() => FileTransformer.Disguise(another, Bytes(32), ".mp4"));
    Equal(original, File.ReadAllBytes(another));
    folder.NoTemps();
}

static void TransactionFailures()
{
    using var folder = new TestFolder();
    string output = System.IO.Path.Combine(folder.Path, "output.bin");
    Reject(() => FileTransformer.WriteNewFile(output, stream =>
    {
        stream.Write(Bytes(100));
        throw new IOException("simulated disk write failure");
    }));
    Check(!File.Exists(output), "partial output was published");
    folder.NoTemps();
    using var cancellation = new CancellationTokenSource();
    Reject(() => FileTransformer.WriteNewFile(output, stream =>
    {
        stream.Write(Bytes(100));
        cancellation.Cancel();
    }, cancellation.Token));
    Check(!File.Exists(output), "cancelled output was published");
    folder.NoTemps();
    Reject(() => FileTransformer.WriteNewFile(output, stream =>
    {
        stream.Write(Bytes(100));
        File.WriteAllBytes(output, new byte[] { 77 });
    }));
    Equal(new byte[] { 77 }, File.ReadAllBytes(output));
    folder.NoTemps();
}

static void LegacyRoundTrips()
{
    using var folder = new TestFolder();
    foreach (int length in new[] { 0, 1, 31, 32, 33, 2000000 })
    {
        byte[] original = Bytes(length);
        byte[] disguisedBytes = LegacyBytes(original, Bytes(32));
        string disguised = folder.File($"old-{length}.bin.mp4", disguisedBytes);
        string restored = FileTransformer.RevealLegacy(disguised);
        Equal(original, File.ReadAllBytes(restored));
        Equal(disguisedBytes, File.ReadAllBytes(disguised));
    }
    string zeroFooter = folder.File("ordinary.mp4", new byte[128]);
    Reject(() => FileTransformer.RevealLegacy(zeroFooter));
    string modernSource = folder.File("modern.bin", Bytes(40));
    string modern = FileTransformer.Disguise(modernSource, Bytes(32), ".mp4");
    Reject(() => FileTransformer.RevealLegacy(modern));
    folder.NoTemps();
}

static void InvalidMasks()
{
    using var folder = new TestFolder();
    string empty = folder.File("empty.mp4", Array.Empty<byte>());
    Reject(() => FileTransformer.LoadMask(empty));
    string large = folder.File("large.mp4", Array.Empty<byte>());
    using (var stream = new FileStream(large, FileMode.Open, FileAccess.Write))
        stream.SetLength(FileTransformer.MaximumMaskLength);
    Reject(() => FileTransformer.LoadMask(large));
    Reject(() => FileTransformer.LoadMask(System.IO.Path.Combine(folder.Path, "missing.mp4")));
    byte[] original = Bytes(50);
    string source = folder.File("source.bin", original);
    Reject(() => FileTransformer.Disguise(source, Array.Empty<byte>(), ".mp4"));
    Reject(() => FileTransformer.Disguise(source, Bytes(4), ""));
    Reject(() => FileTransformer.Disguise(source, Bytes(4), ".bad/extension"));
    Equal(original, File.ReadAllBytes(source));
    folder.NoTemps();
}

static void BatchIsolation()
{
    using var folder = new TestFolder();
    string source = folder.File("good.bin", Bytes(80));
    var result = BatchProcessor.Run(new[] { folder.Path, source, System.IO.Path.Combine(folder.Path, "missing.bin") },
        FileOperation.Disguise, Bytes(32), ".mp4", null, CancellationToken.None);
    Check(result.Succeeded == 1 && result.Errors.Count == 1, "deduplication or failure isolation failed");
    byte[] valid = File.ReadAllBytes(source + ".mp4");
    string ordinary = folder.File("ordinary.mp4", new byte[128]);
    var restore = BatchProcessor.Run(new[] { ordinary, source + ".mp4" }, FileOperation.Reveal,
        Array.Empty<byte>(), "", null, CancellationToken.None);
    Check(restore.Succeeded == 1 && restore.Errors.Count == 1, "batch stopped after invalid file");
    Equal(valid, File.ReadAllBytes(source + ".mp4"));
    Equal(Bytes(80), File.ReadAllBytes(source));
    Equal(Bytes(80), File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "good.restored.bin")));
    folder.NoTemps();
}

static void BatchCancellation()
{
    using var folder = new TestFolder();
    string one = folder.File("one.bin", Bytes(100));
    string two = folder.File("two.bin", Bytes(100));
    using var cancellation = new CancellationTokenSource();
    var progress = new InlineProgress(p => { if (p.Completed == 1) cancellation.Cancel(); });
    var result = BatchProcessor.Run(new[] { one, two }, FileOperation.Disguise, Bytes(32), ".mp4",
        progress, cancellation.Token);
    Check(result.Cancelled && result.Succeeded == 1, "cancellation did not stop after the current file");
    Check(!File.Exists(two + ".mp4"), "cancelled batch processed another input");
    Equal(Bytes(100), File.ReadAllBytes(one));
    Equal(Bytes(100), File.ReadAllBytes(two));
    folder.NoTemps();
}

static void DirectoryLinks()
{
    using var folder = new TestFolder();
    string source = folder.File("source.bin", Bytes(10));
    string link = System.IO.Path.Combine(folder.Path, "loop");
    try { Directory.CreateSymbolicLink(link, folder.Path); }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    {
        Console.WriteLine("  SKIP symlink creation unavailable: " + ex.Message);
        return;
    }
    try
    {
        var result = BatchProcessor.Run(new[] { folder.Path }, FileOperation.Disguise, Bytes(4), ".mp4", null, CancellationToken.None);
        Check(result.Succeeded == 1 && result.Errors.Count == 1, "directory link was followed");
        Equal(Bytes(10), File.ReadAllBytes(source));
    }
    finally { Directory.Delete(link); }
    folder.NoTemps();
}

static void LockedSource()
{
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("  SKIP Windows-only sharing semantics");
        return;
    }
    using var folder = new TestFolder();
    string source = folder.File("locked.bin", Bytes(10));
    using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var result = BatchProcessor.Run(new[] { source }, FileOperation.Disguise, Bytes(4), ".mp4", null, CancellationToken.None);
        Check(result.Succeeded == 0 && result.Errors.Count == 1, "locked source escaped per-file handling");
    }
    Equal(Bytes(10), File.ReadAllBytes(source));
    folder.NoTemps();
}

static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 37 + 11)).ToArray();
static void Equal(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual), "file content changed");
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OperationCanceledException or UnauthorizedAccessException) { return; }
    throw new Exception("operation unexpectedly succeeded");
}

// Fixture for the documented v1.4.2 format, independent of the new writer.
static byte[] LegacyBytes(byte[] original, byte[] mask)
{
    using var stream = new MemoryStream();
    stream.Write(mask);
    if (original.Length > mask.Length)
        stream.Write(original.AsSpan(mask.Length));
    stream.Write(original.Take(mask.Length).Reverse().ToArray());
    stream.Write(BitConverter.GetBytes(mask.Length));
    return stream.ToArray();
}

sealed class InlineProgress : IProgress<BatchProgress>
{
    private readonly Action<BatchProgress> report;
    internal InlineProgress(Action<BatchProgress> report) => this.report = report;
    public void Report(BatchProgress value) => report(value);
}

sealed class TestFolder : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apate-tests-" + Guid.NewGuid().ToString("N"));
    internal TestFolder() => Directory.CreateDirectory(Path);
    internal string File(string name, byte[] bytes)
    {
        string path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }
    internal void NoTemps()
    {
        if (Directory.GetFiles(Path, ".apate-*.tmp", SearchOption.AllDirectories).Length != 0)
            throw new Exception("temporary file leaked");
    }
    public void Dispose() => Directory.Delete(Path, true);
}
