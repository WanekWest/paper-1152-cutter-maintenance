using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Infrastructure;

public sealed class Sha256XoshiroRandomStreamFactory : IRandomStreamFactory
{
    private static readonly byte[] DomainTag = Encoding.UTF8.GetBytes("rng-v1");

    public IRandomStream Create(RandomStreamKey key) => Xoshiro256StarStar.FromDigest(DeriveDigest(key));

    public byte[] DeriveDigest(RandomStreamKey key) => SHA256.HashData(EncodeKey(key));

    public byte[] EncodeKey(RandomStreamKey key)
    {
        using var stream = new MemoryStream(256);
        WriteUtf8(stream, DomainTag);
        WriteUInt64(stream, key.Context.MasterSeed);
        WriteUtf8(stream, key.Context.ExperimentId.Value);
        WriteUtf8(stream, key.Context.ScenarioId.Value);
        WriteUtf8(stream, key.Context.TrajectoryId.Value);
        WriteUtf8(stream, key.Context.ComponentEpisodeId.Value);
        WriteUtf8(stream, key.Context.DecisionId.Value);
        WriteUtf8(stream, key.Context.CouplingGroupId);
        Span<byte> purpose = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(purpose, (int)key.Purpose);
        stream.Write(purpose);
        WriteUInt64(stream, key.SubstreamIndex);
        return stream.ToArray();
    }

    private static void WriteUtf8(Stream stream, string value) => WriteUtf8(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteUtf8(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)value.Length));
        stream.Write(length);
        stream.Write(value);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}

public sealed class Xoshiro256StarStar : IRandomStream
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    private Xoshiro256StarStar(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        if ((s0 | s1 | s2 | s3) == 0)
        {
            var split = 0x9E3779B97F4A7C15UL;
            s0 = SplitMix64(ref split);
            s1 = SplitMix64(ref split);
            s2 = SplitMix64(ref split);
            s3 = SplitMix64(ref split);
        }
        _s0 = s0; _s1 = s1; _s2 = s2; _s3 = s3;
    }

    public static Xoshiro256StarStar FromDigest(ReadOnlySpan<byte> digest)
    {
        var state = DecodeInitialState(digest);
        return new(state.S0, state.S1, state.S2, state.S3);
    }

    public static XoshiroInitialState DecodeInitialState(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != 32) throw new ArgumentException("A SHA-256 digest is required.", nameof(digest));
        return new(
            BinaryPrimitives.ReadUInt64LittleEndian(digest[0..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..16]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[16..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[24..32]));
    }

    public ulong NextUInt64()
    {
        var result = RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);
        return result;
    }

    public double NextUnitDouble() => (NextUInt64() >> 11) * (1d / 9007199254740992d);

    private static ulong RotateLeft(ulong value, int count) => (value << count) | (value >> (64 - count));

    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

public readonly record struct XoshiroInitialState(ulong S0, ulong S1, ulong S2, ulong S3);
