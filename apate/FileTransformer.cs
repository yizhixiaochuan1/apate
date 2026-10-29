using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace apate;

// All operations read the source and publish a separate output only after validation.
internal static class FileTransformer
{
    internal const int MaximumMaskLength = int.MaxValue / 7;
    // Layout: masked body, reversed saved head, UTF-8 original name, 96-byte footer.
    // Footer (little endian): magic[8], version[4], original length[8], mask/head/name
    // lengths[4 each], original SHA-256[32], SHA-256(head + name + first 64 footer bytes)[32].
    private const int FooterSize = 96;
    private const int FooterDataSize = 64;
    private const int MaxNameBytes = 1024;
    private const int BufferSize = 128 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("APATE2\r\n");
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] LoadMask(string path, CancellationToken cancellationToken = default)
    {
        using var input = OpenSource(path);
        ValidateMaskLength(input.Length);
        var mask = new byte[(int)input.Length];
        ReadExactly(input, mask, cancellationToken);
        if (input.Length != mask.Length)
            throw new IOException("读取过程中面具文件发生了变化。");
        return mask;
    }

    internal static string Disguise(string path, byte[] mask, string extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ValidateMaskLength(mask.Length);
        if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.' || extension.Length == 1)
            throw new ArgumentException("面具文件必须具有扩展名。");
        ValidateFileName("file" + extension);
        path = Path.GetFullPath(path);
        string destination = path + extension;
        EnsureDestinationAvailable(path, destination);
        using var input = OpenSource(path);
        if (HasMarker(input))
            throw new InvalidDataException("此文件已有新版伪装标识，请先还原，避免重复伪装。");

        long originalLength = input.Length;
        byte[] name = Utf8.GetBytes(Path.GetFileName(path));
        if (name.Length == 0 || name.Length > MaxNameBytes)
            throw new ArgumentException("原文件名过长。");
        ValidateFileName(Path.GetFileName(path));
        var originalHead = new byte[(int)Math.Min(originalLength, mask.Length)];
        input.Position = 0;
        ReadExactly(input, originalHead, cancellationToken);

        WriteNewFile(destination, output =>
        {
            using var originalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            originalHash.AppendData(originalHead);
            output.Write(mask);
            CopyExactly(input, output, originalLength - originalHead.Length, originalHash, cancellationToken);
            if (input.Length != originalLength)
                throw new IOException("处理过程中原文件发生了变化。");
            byte[] footer = new byte[FooterSize];
            Magic.CopyTo(footer, 0);
            BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(8), 1);
            BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(12), originalLength);
            BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(20), mask.Length);
            BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(24), originalHead.Length);
            BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(28), name.Length);
            originalHash.GetHashAndReset().CopyTo(footer, 32);
            Array.Reverse(originalHead);
            MetadataHash(originalHead, name, footer).CopyTo(footer, FooterDataSize);
            output.Write(originalHead);
            output.Write(name);
            output.Write(footer);
        }, cancellationToken);
        return destination;
    }

    internal static string Reveal(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        using var input = OpenSource(path);
        Metadata metadata = ReadMetadata(input, cancellationToken);
        string destination = RestorationPath(path, metadata.Name);
        WriteNewFile(destination, output =>
        {
            Array.Reverse(metadata.Head);
            using var originalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            output.Write(metadata.Head);
            originalHash.AppendData(metadata.Head);
            input.Position = metadata.MaskLength;
            CopyExactly(input, output, metadata.OriginalLength - metadata.Head.Length,
                originalHash, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(originalHash.GetHashAndReset(), metadata.OriginalHash))
                throw new InvalidDataException("文件完整性校验失败，未生成还原文件；原件已保留。");
        }, cancellationToken);
        return destination;
    }

    // Old files have no trustworthy signature. Only the explicit legacy UI invokes this.
    internal static string RevealLegacy(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        using var input = OpenSource(path);
        if (HasMarker(input))
            throw new InvalidDataException("这是新版格式，请使用右侧的正常还原区。");
        if (input.Length < 4)
            throw new InvalidDataException("文件过短，不符合旧版格式。");
        var lengthBytes = new byte[4];
        input.Position = input.Length - 4;
        ReadExactly(input, lengthBytes, cancellationToken);
        int maskLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        ValidateMaskLength(maskLength);
        long originalLength = input.Length - maskLength - 4;
        if (originalLength < 0)
            throw new InvalidDataException("旧版长度信息无效。");
        int savedLength = (int)Math.Min(originalLength, maskLength);
        long bodyLength = Math.Max(originalLength, maskLength);
        var head = new byte[savedLength];
        input.Position = bodyLength;
        ReadExactly(input, head, cancellationToken);
        Array.Reverse(head);
        string name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name))
            name = Path.GetFileName(path) + ".restored";
        string destination = RestorationPath(path, name);
        WriteNewFile(destination, output =>
        {
            output.Write(head);
            input.Position = maskLength;
            CopyExactly(input, output, originalLength - savedLength, null, cancellationToken);
        }, cancellationToken);
        return destination;
    }

    private static Metadata ReadMetadata(Stream input, CancellationToken cancellationToken)
    {
        if (!HasMarker(input))
            throw new InvalidDataException("未识别到新版伪装标识。普通文件不会被修改；旧版文件请使用“还原旧版文件”。");
        var footer = new byte[FooterSize];
        input.Position = input.Length - FooterSize;
        ReadExactly(input, footer, cancellationToken);
        int version = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(8));
        long originalLength = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(12));
        int maskLength = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(20));
        int savedLength = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(24));
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(28));
        if (version != 1 || originalLength < 0 || maskLength <= 0 || maskLength >= MaximumMaskLength
            || savedLength != Math.Min(originalLength, maskLength) || nameLength <= 0 || nameLength > MaxNameBytes)
            throw new InvalidDataException("伪装元数据无效或版本不受支持。");
        long bodyLength = Math.Max(originalLength, maskLength);
        if (bodyLength > long.MaxValue - savedLength - nameLength - FooterSize
            || bodyLength + savedLength + nameLength + FooterSize != input.Length)
            throw new InvalidDataException("文件长度与伪装记录不一致，文件可能已被截断。");
        var head = new byte[savedLength];
        var nameBytes = new byte[nameLength];
        input.Position = bodyLength;
        ReadExactly(input, head, cancellationToken);
        ReadExactly(input, nameBytes, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(MetadataHash(head, nameBytes, footer), footer.AsSpan(FooterDataSize)))
            throw new InvalidDataException("伪装元数据或保存的文件头已损坏。");
        string name = Utf8.GetString(nameBytes);
        ValidateFileName(name);
        return new Metadata(originalLength, maskLength, head, name, footer.AsSpan(32, 32).ToArray());
    }

    private static bool HasMarker(Stream input)
    {
        if (input.Length < FooterSize)
            return false;
        input.Position = input.Length - FooterSize;
        var marker = new byte[Magic.Length];
        ReadExactly(input, marker, CancellationToken.None);
        return marker.AsSpan().SequenceEqual(Magic);
    }

    private static byte[] MetadataHash(byte[] head, byte[] name, byte[] footer)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(head);
        hash.AppendData(name);
        hash.AppendData(footer, 0, FooterDataSize);
        return hash.GetHashAndReset();
    }

    private static string RestorationPath(string source, string name)
    {
        ValidateFileName(name);
        string directory = Path.GetDirectoryName(source)!;
        string destination = Path.Combine(directory, name);
        if (SamePath(source, destination) || File.Exists(destination) || Directory.Exists(destination))
        {
            string extension = Path.GetExtension(name);
            string stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length == 0)
                stem = "restored";
            destination = Path.Combine(directory, stem + ".restored" + extension);
        }
        EnsureDestinationAvailable(source, destination);
        return destination;
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void EnsureDestinationAvailable(string source, string destination)
    {
        if (SamePath(source, destination) || File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("输出路径已存在，未覆盖任何文件：" + destination);
    }

    private static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || name.Any(char.IsControl))
            throw new InvalidDataException("文件名无效，拒绝写入输出路径。");
    }

    private static void ValidateMaskLength(long length)
    {
        if (length <= 0 || length >= MaximumMaskLength)
            throw new InvalidDataException($"面具大小必须大于 0 且小于 {MaximumMaskLength} 字节。");
    }

    private static FileStream OpenSource(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);

    // Keeping publication here makes copy/write/rename failures leave the source untouched.
    internal static void WriteNewFile(string destination, Action<Stream> write,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("输出路径已存在：" + destination);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".apate-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                write(output);
                cancellationToken.ThrowIfCancellationRequested();
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ReadExactly(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = input.Read(buffer, offset, Math.Min(buffer.Length - offset, BufferSize));
            if (count == 0)
                throw new EndOfStreamException("文件意外结束。");
            offset += count;
        }
    }

    private static void CopyExactly(Stream input, Stream output, long remaining,
        IncrementalHash? hash, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0)
                throw new EndOfStreamException("文件意外结束。");
            hash?.AppendData(buffer, 0, count);
            output.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    private sealed record Metadata(long OriginalLength, int MaskLength, byte[] Head, string Name, byte[] OriginalHash);
}
