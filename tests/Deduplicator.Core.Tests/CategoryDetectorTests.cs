using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Tests;

public class CategoryDetectorTests
{
    [Fact]
    public void Jpeg_signature_is_image_even_with_wrong_extension()
    {
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        // Расширение .txt должно игнорироваться — решает содержимое (FR-5).
        Assert.Equal(FileCategory.Image, CategoryDetector.Detect(jpeg, ".txt"));
    }

    [Fact]
    public void Png_signature_is_image()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.Equal(FileCategory.Image, CategoryDetector.Detect(png, null));
    }

    [Fact]
    public void Pdf_signature_is_document()
    {
        byte[] pdf = { 0x25, 0x50, 0x44, 0x46, 0x2D };
        Assert.Equal(FileCategory.Document, CategoryDetector.Detect(pdf, ".bin"));
    }

    [Fact]
    public void Mp3_id3_is_audio()
    {
        byte[] mp3 = { 0x49, 0x44, 0x33, 0x03, 0x00 };
        Assert.Equal(FileCategory.Audio, CategoryDetector.Detect(mp3, null));
    }

    [Fact]
    public void Riff_wave_is_audio_avi_is_video()
    {
        byte[] wave = { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E' };
        byte[] avi = { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, (byte)'A', (byte)'V', (byte)'I', (byte)' ' };
        Assert.Equal(FileCategory.Audio, CategoryDetector.Detect(wave, null));
        Assert.Equal(FileCategory.Video, CategoryDetector.Detect(avi, null));
    }

    [Fact]
    public void Unknown_falls_back_to_extension()
    {
        byte[] junk = { 0x00, 0x01, 0x02, 0x03 };
        Assert.Equal(FileCategory.Document, CategoryDetector.Detect(junk, ".docx"));
        Assert.Equal(FileCategory.Other, CategoryDetector.Detect(junk, ".xyz"));
    }
}
