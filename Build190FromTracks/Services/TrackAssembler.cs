using System.Security.Cryptography;
using System.Text;

namespace Build190FromTracks.Services;

public sealed class TrackAssembler
{
    public const int SectorSize = 512;
    public const int SectorsPerTrack = 3010;
    public const int TrackFileBytes = SectorsPerTrack * SectorSize;
    public const int DefaultFirstTrack = 346;
    public const int DefaultMinTrack = 6;

    public sealed class BuildOptions
    {
        public required string TracksDirectory { get; init; }
        public required string OutputPath { get; init; }
        public long OutputByteLength { get; init; }
        public int FirstTrack { get; init; } = DefaultFirstTrack;
        public int MinTrack { get; init; } = DefaultMinTrack;
        public long RegionStartSectorOffset { get; init; }
        public int PrimaryHead { get; init; } = 2;
        public bool VerifySecondaryHead { get; init; } = true;
        public int SecondaryHead { get; init; } = 3;
        public IProgress<string>? Log { get; init; }
        public IProgress<(int Current, int Total)>? Progress { get; init; }
    }

    public sealed class BuildResult
    {
        public required long BytesWritten { get; init; }
        public required string Sha256 { get; init; }
        public required int TracksUsed { get; init; }
        public required int FirstTrackUsed { get; init; }
        public required int LastTrackUsed { get; init; }
        public required int LastTrackBytesUsed { get; init; }
        public bool HeadsIdentical { get; init; }
        public string? HeaderSummary { get; init; }
    }

    public BuildResult Build(BuildOptions options, CancellationToken cancellationToken = default)
    {
        if (options.OutputByteLength <= 0 || options.OutputByteLength % SectorSize != 0)
            throw new ArgumentException("Output length must be a positive multiple of 512 bytes.");

        if (!Directory.Exists(options.TracksDirectory))
            throw new DirectoryNotFoundException(options.TracksDirectory);

        long totalSectors = options.OutputByteLength / SectorSize;
        var trackCache = new Dictionary<(int Track, int Head), byte[]>();

        void Log(string msg) => options.Log?.Report(msg);

        byte[] GetTrack(int track, int head)
        {
            var key = (track, head);
            if (trackCache.TryGetValue(key, out var cached))
                return cached;

            string path = Path.Combine(options.TracksDirectory, $"{track:D4}_{head:D2}.trk");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Missing track file for head {head:D2}.", path);

            var info = new FileInfo(path);
            if (info.Length != TrackFileBytes)
                throw new InvalidDataException(
                    $"Track {track:D4}_{head:D2}.trk size is {info.Length}, expected {TrackFileBytes}.");

            var data = File.ReadAllBytes(path);
            trackCache[key] = data;
            return data;
        }

        int firstTrackHit = -1;
        int lastTrackHit = -1;
        int lastTrackBytes = 0;
        var tracksSeen = new HashSet<int>();

        using var output = new FileStream(
            options.OutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024);

        using var sha = SHA256.Create();
        var sectorBuffer = new byte[SectorSize];

        for (long outSector = 0; outSector < totalSectors; outSector++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long regionSector = options.RegionStartSectorOffset + outSector;
            int track = options.FirstTrack - (int)(regionSector / SectorsPerTrack);
            int sectorInTrack = (int)(regionSector % SectorsPerTrack);

            if (track < options.MinTrack)
                throw new InvalidOperationException(
                    $"Track {track} below minimum {options.MinTrack}; increase first track or check length/offset.");

            byte[] primary = GetTrack(track, options.PrimaryHead);
            ReadSector(primary, sectorInTrack, sectorBuffer);

            if (options.VerifySecondaryHead)
            {
                byte[] secondary = GetTrack(track, options.SecondaryHead);
                if (!SectorEquals(primary, sectorInTrack, secondary, sectorInTrack))
                    throw new InvalidDataException(
                        $"Head {options.PrimaryHead:D2} and {options.SecondaryHead:D2} differ at track {track:D4}, sector {sectorInTrack}.");
            }

            output.Write(sectorBuffer, 0, SectorSize);
            sha.TransformBlock(sectorBuffer, 0, SectorSize, null, 0);

            if (firstTrackHit < 0)
                firstTrackHit = track;
            if (track != lastTrackHit)
            {
                lastTrackHit = track;
                lastTrackBytes = SectorSize;
                tracksSeen.Add(track);
            }
            else
            {
                lastTrackBytes += SectorSize;
            }

            if (outSector % 5000 == 0)
                options.Progress?.Report(((int)outSector, (int)totalSectors));
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        output.Flush(true);

        var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        Log($"SHA-256: {hash}");

        byte[] header = new byte[Math.Min(32, options.OutputByteLength)];
        using (var readBack = File.OpenRead(options.OutputPath))
        {
            readBack.ReadExactly(header, 0, header.Length);
        }

        string headerSummary = ValidateHeader(header);
        Log(headerSummary);

        return new BuildResult
        {
            BytesWritten = options.OutputByteLength,
            Sha256 = hash,
            TracksUsed = tracksSeen.Count,
            FirstTrackUsed = firstTrackHit,
            LastTrackUsed = lastTrackHit,
            LastTrackBytesUsed = lastTrackBytes,
            HeadsIdentical = options.VerifySecondaryHead,
            HeaderSummary = headerSummary
        };
    }

    /// <summary>
    /// Fast path used when region offset is 0: concatenate whole tracks from firstTrack downward.
    /// </summary>
    public BuildResult BuildSequentialTracks(BuildOptions options, CancellationToken cancellationToken = default)
    {
        if (options.RegionStartSectorOffset != 0)
            return Build(options, cancellationToken);

        if (options.OutputByteLength <= 0)
            throw new ArgumentException("Output length must be positive.");

        void Log(string msg) => options.Log?.Report(msg);

        using var output = new FileStream(
            options.OutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024);
        using var sha = SHA256.Create();

        long remaining = options.OutputByteLength;
        int track = options.FirstTrack;
        int firstTrackUsed = track;
        int lastTrackUsed = track;
        int lastTrackBytes = 0;
        int tracksUsed = 0;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (track < options.MinTrack)
                throw new InvalidOperationException($"Insufficient tracks at {track:D4}.");

            string primaryPath = Path.Combine(options.TracksDirectory, $"{track:D4}_{options.PrimaryHead:D2}.trk");
            if (!File.Exists(primaryPath))
                throw new FileNotFoundException("Missing track file.", primaryPath);

            var primaryInfo = new FileInfo(primaryPath);
            if (primaryInfo.Length != TrackFileBytes)
                throw new InvalidDataException($"Unexpected size for {primaryInfo.Name}.");

            int toCopy = (int)Math.Min(remaining, TrackFileBytes);
            using (var input = File.OpenRead(primaryPath))
            {
                if (options.VerifySecondaryHead)
                {
                    string secondaryPath = Path.Combine(options.TracksDirectory, $"{track:D4}_{options.SecondaryHead:D2}.trk");
                    ComparePrefix(primaryPath, secondaryPath, toCopy);
                }

                var buffer = new byte[81920];
                int copied = 0;
                while (copied < toCopy)
                {
                    int want = Math.Min(buffer.Length, toCopy - copied);
                    int n = input.Read(buffer, 0, want);
                    if (n == 0)
                        throw new EndOfStreamException(primaryPath);
                    output.Write(buffer, 0, n);
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    copied += n;
                }
            }

            remaining -= toCopy;
            lastTrackUsed = track;
            lastTrackBytes = toCopy;
            tracksUsed++;
            Log($"Track {track:D4}_{options.PrimaryHead:D2}: wrote {toCopy:N0} bytes ({remaining:N0} remaining)");
            track--;
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        output.Flush(true);

        var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        byte[] header = new byte[Math.Min(32, options.OutputByteLength)];
        using (var readBack = File.OpenRead(options.OutputPath))
        {
            readBack.ReadExactly(header, 0, header.Length);
        }

        string headerSummary = ValidateHeader(header);

        return new BuildResult
        {
            BytesWritten = options.OutputByteLength,
            Sha256 = hash,
            TracksUsed = tracksUsed,
            FirstTrackUsed = firstTrackUsed,
            LastTrackUsed = lastTrackUsed,
            LastTrackBytesUsed = lastTrackBytes,
            HeadsIdentical = options.VerifySecondaryHead,
            HeaderSummary = headerSummary
        };
    }

    public static string ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12)
            return "Header too short for ROYL check.";

        string magic = Encoding.ASCII.GetString(header.Slice(0, 4));
        ushort id = BitConverter.ToUInt16(header.Slice(8, 2));
        ushort lenLo = BitConverter.ToUInt16(header.Slice(10, 2));

        if (magic != "ROYL")
            return $"Warning: magic is '{magic}', expected ROYL.";
        if (id != Module190Directory.ModuleId)
            return $"Warning: module ID is 0x{id:X4}, expected 0x{Module190Directory.ModuleId:X4}.";

        return $"Header OK: ROYL, ID=0x{id:X4}, length low 16 bits=0x{lenLo:X4}.";
    }

    public static string PreviewPlan(long outputBytes, int firstTrack, long regionStartSectorOffset, int minTrack)
    {
        long totalSectors = outputBytes / SectorSize;
        long endRegionSector = regionStartSectorOffset + totalSectors - 1;
        int lastTrack = firstTrack - (int)(endRegionSector / SectorsPerTrack);
        int lastSectorInTrack = (int)(endRegionSector % SectorsPerTrack);
        int lastTrackBytes = (lastSectorInTrack + 1) * SectorSize;
        int trackCount = firstTrack - lastTrack + 1;

        var sb = new StringBuilder();
        sb.AppendLine($"Output: {outputBytes:N0} bytes ({totalSectors:N0} sectors)");
        sb.AppendLine($"Region start sector offset: {regionStartSectorOffset}");
        sb.AppendLine($"Track range: {firstTrack:D4} down to {lastTrack:D4} ({trackCount} track files)");
        sb.AppendLine($"Last track bytes used: {lastTrackBytes:N0} / {TrackFileBytes:N0}");
        if (lastTrack < minTrack)
            sb.AppendLine($"Warning: last track {lastTrack} is below minimum {minTrack}.");
        return sb.ToString();
    }

    private static void ReadSector(byte[] track, int sectorInTrack, byte[] dest)
    {
        int offset = sectorInTrack * SectorSize;
        Array.Copy(track, offset, dest, 0, SectorSize);
    }

    private static bool SectorEquals(byte[] a, int sectorA, byte[] b, int sectorB)
    {
        int offA = sectorA * SectorSize;
        int offB = sectorB * SectorSize;
        for (int i = 0; i < SectorSize; i++)
        {
            if (a[offA + i] != b[offB + i])
                return false;
        }

        return true;
    }

    private static void ComparePrefix(string pathA, string pathB, int bytes)
    {
        using var a = File.OpenRead(pathA);
        using var b = File.OpenRead(pathB);
        var bufA = new byte[81920];
        var bufB = new byte[81920];
        int remaining = bytes;
        while (remaining > 0)
        {
            int chunk = Math.Min(bufA.Length, remaining);
            a.ReadExactly(bufA, 0, chunk);
            b.ReadExactly(bufB, 0, chunk);
            for (int i = 0; i < chunk; i++)
            {
                if (bufA[i] != bufB[i])
                    throw new InvalidDataException($"Track mismatch between {Path.GetFileName(pathA)} and {Path.GetFileName(pathB)}.");
            }

            remaining -= chunk;
        }
    }
}
