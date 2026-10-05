using System.Buffers.Binary;
using FastDedup.Engine;

namespace Deduplicator.Core.Tests;

public sealed class ElevatedMftProtocolTests
{
    [Fact]
    public async Task Frame_roundtrip_is_length_bounded_and_strict()
    {
        byte[] payload = ElevatedMftProtocol.CreateEnumerateRequest(7, @"c:\", "1a2b-3c4d");
        await using var stream = new MemoryStream();
        await ElevatedMftProtocol.WriteFrameAsync(
            stream, MftBrokerMessage.EnumerateMft, payload, CancellationToken.None);

        stream.Position = 0;
        MftBrokerFrame frame = await ElevatedMftProtocol.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Equal(MftBrokerMessage.EnumerateMft, frame.Type);
        MftEnumerateRequest parsed = ElevatedMftProtocol.ParseEnumerateRequest(frame.Payload);
        Assert.Equal(7, parsed.RequestId);
        Assert.Equal(@"C:\", parsed.Root);
        Assert.Equal("1A2B-3C4D", parsed.ExpectedSerial);
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_before_payload_allocation()
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, ElevatedMftProtocol.MaxFrameBytes + 1);
        await using var stream = new MemoryStream(bytes, writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ElevatedMftProtocol.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_frame_is_never_treated_as_complete()
    {
        var bytes = new byte[7];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 20);
        bytes[4] = (byte)MftBrokerMessage.RawRecords;
        await using var stream = new MemoryStream(bytes, writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ElevatedMftProtocol.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void Raw_record_batch_roundtrips_without_full_paths()
    {
        var source = new[]
        {
            new UsnEnumerator.RawRec(42, 5, 0x20, "photo.jpg"),
            new UsnEnumerator.RawRec(43, 42, 0x10, "Архив")
        };

        byte[] payload = ElevatedMftProtocol.CreateRawRecords(9, source);
        var parsed = ElevatedMftProtocol.ParseRawRecords(payload);

        Assert.Equal(9, parsed.RequestId);
        Assert.Equal(source, parsed.Records);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\file.txt", true)]
    [InlineData(@"C:\", @"c:\folder\file.txt", true)]
    [InlineData(@"C:\", @"D:\file.txt", false)]
    [InlineData(@"C:\", @"\\server\share\file.txt", false)]
    [InlineData(@"C:\", @"\\?\C:\file.txt", false)]
    [InlineData(@"C:\", @"C:\file.txt:stream", false)]
    [InlineData(@"C:\", @"relative.txt", false)]
    public void Returned_paths_must_stay_on_requested_volume(string root, string path, bool expected)
        => Assert.Equal(expected, ElevatedMftProtocol.IsPathWithinRoot(root, path));

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"Z:\")]
    public void Whole_volume_root_is_canonicalized(string root)
        => Assert.Equal(root, ElevatedMftProtocol.NormalizeWholeVolumeRoot(root));

    [Theory]
    [InlineData(@"C:\folder")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\.\C:")]
    [InlineData("C:")]
    [InlineData("")]
    public void Folder_unc_and_device_roots_are_rejected(string root)
        => Assert.Throws<InvalidDataException>(() => ElevatedMftProtocol.NormalizeWholeVolumeRoot(root));

    [Theory]
    [InlineData("normal.txt", true)]
    [InlineData("$MFT", true)]
    [InlineData("..", false)]
    [InlineData("bad/name", false)]
    [InlineData("file:stream", false)]
    [InlineData("bad\0name", false)]
    public void Mft_names_are_validated_before_path_construction(string name, bool expected)
        => Assert.Equal(expected, ElevatedMftProtocol.IsSafeMftName(name));

    [Fact]
    public void Random_token_roundtrips_and_fixed_time_check_detects_tampering()
    {
        byte[] token = ElevatedMftProtocol.CreateToken();
        byte[] decoded = ElevatedMftProtocol.DecodeToken(ElevatedMftProtocol.EncodeToken(token));
        Assert.True(ElevatedMftProtocol.FixedTimeTokenEquals(token, decoded));
        decoded[0] ^= 0x80;
        Assert.False(ElevatedMftProtocol.FixedTimeTokenEquals(token, decoded));
    }

    [Fact]
    public void Protected_root_check_is_boundary_aware()
    {
        Assert.True(ElevatedMftHelperTrust.IsPathInsideRoot(
            @"C:\Program Files\Deduplicator\Deduplicator.MftHelper.exe", @"C:\Program Files"));
        Assert.False(ElevatedMftHelperTrust.IsPathInsideRoot(
            @"C:\Program Files Fake\Deduplicator.MftHelper.exe", @"C:\Program Files"));
    }

    [Fact]
    public void Unsigned_portable_pair_is_fail_closed_even_when_user_enabled_the_feature()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dedup_mft_trust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string main = Path.Combine(directory, ElevatedMftHelperTrust.MainFileName);
        string helper = Path.Combine(directory, ElevatedMftHelperTrust.HelperFileName);
        File.WriteAllBytes(main, new byte[] { 1 });
        File.WriteAllBytes(helper, new byte[] { 2 });
        try
        {
            MftHelperTrustResult result = ElevatedMftHelperTrust.EvaluatePair(
                main, helper, verifyCurrentUserCannotModify: true);
            Assert.False(result.IsTrusted);
            Assert.Contains("Program Files", result.Reason);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
