using Deduplicator.Core.Models;

namespace Deduplicator.Core.Util;

/// <summary>
/// Определение категории файла по «магическим байтам» содержимого (FR-5).
/// Расширение используется лишь как запасной вариант для неоднозначных контейнеров.
/// Переименованный <c>.jpg → .txt</c> опознаётся как изображение.
/// </summary>
public static class CategoryDetector
{
    /// <summary>Сколько байт головы достаточно для распознавания всех известных сигнатур.</summary>
    public const int ProbeBytes = 32;

    /// <summary>Определить категорию по уже прочитанной голове файла.</summary>
    public static FileCategory Detect(ReadOnlySpan<byte> head, string? ext = null)
    {
        if (head.Length >= 3)
        {
            // --- Изображения ---
            if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return FileCategory.Image;          // JPEG
            if (StartsWith(head, 0x89, 0x50, 0x4E, 0x47)) return FileCategory.Image;                        // PNG
            if (StartsWith(head, 0x47, 0x49, 0x46, 0x38)) return FileCategory.Image;                        // GIF8
            if (StartsWith(head, 0x42, 0x4D) && IsBmp(head)) return FileCategory.Image;                     // BMP
            if (StartsWith(head, 0x49, 0x49, 0x2A, 0x00) || StartsWith(head, 0x4D, 0x4D, 0x00, 0x2A))
                return FileCategory.Image;                                                                  // TIFF

            // --- Документы ---
            if (StartsWith(head, 0x25, 0x50, 0x44, 0x46)) return FileCategory.Document;                     // PDF "%PDF"
            if (StartsWith(head, 0xD0, 0xCF, 0x11, 0xE0)) return FileCategory.Document;                     // OLE2 (doc/xls/ppt)
            if (StartsWith(head, 0x7B, 0x5C, 0x72, 0x74, 0x66)) return FileCategory.Document;               // RTF "{\rtf"

            // --- Аудио ---
            if (StartsWith(head, 0x66, 0x4C, 0x61, 0x43)) return FileCategory.Audio;                        // FLAC "fLaC"
            if (StartsWith(head, 0x4F, 0x67, 0x67, 0x53)) return FileCategory.Audio;                        // OGG "OggS"
            if (StartsWith(head, 0x49, 0x44, 0x33)) return FileCategory.Audio;                              // MP3 with ID3
            if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return FileCategory.Audio;                     // MP3/AAC frame sync

            // --- Видео ---
            if (StartsWith(head, 0x1A, 0x45, 0xDF, 0xA3)) return FileCategory.Video;                        // Matroska/WebM (EBML)
            if (StartsWith(head, 0x46, 0x4C, 0x56, 0x01)) return FileCategory.Video;                        // FLV
        }

        // RIFF контейнеры: WAV (audio), AVI/WEBP — смотрим тип на смещении 8.
        if (head.Length >= 12 && StartsWith(head, 0x52, 0x49, 0x46, 0x46)) // "RIFF"
        {
            var fourcc = System.Text.Encoding.ASCII.GetString(head.Slice(8, 4));
            return fourcc switch
            {
                "WAVE" => FileCategory.Audio,
                "AVI " => FileCategory.Video,
                "WEBP" => FileCategory.Image,
                _ => FromExt(ext)
            };
        }

        // ISO-BMFF (mp4/mov/m4a/heic): на смещении 4 — "ftyp", далее бренд.
        if (head.Length >= 12 && head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70) // "ftyp"
        {
            var brand = System.Text.Encoding.ASCII.GetString(head.Slice(8, 4)).ToLowerInvariant();
            if (brand.StartsWith("m4a") || brand.StartsWith("m4b")) return FileCategory.Audio;
            if (brand.StartsWith("heic") || brand.StartsWith("heix") ||
                brand.StartsWith("mif1") || brand.StartsWith("avif")) return FileCategory.Image;
            if (brand.StartsWith("qt") || brand.StartsWith("isom") || brand.StartsWith("mp4") ||
                brand.StartsWith("m4v") || brand.StartsWith("avc") || brand.StartsWith("dash") ||
                brand.StartsWith("iso")) return FileCategory.Video;
            return FileCategory.Video;
        }

        // ASF: WMV (video) и WMA (audio) делят контейнер — различаем по расширению.
        if (head.Length >= 4 && StartsWith(head, 0x30, 0x26, 0xB2, 0x75))
            return FromExt(ext) is FileCategory.Audio ? FileCategory.Audio : FileCategory.Video;

        // ZIP-контейнер: docx/xlsx/pptx/odt и т.п. — по расширению.
        if (head.Length >= 4 && StartsWith(head, 0x50, 0x4B, 0x03, 0x04))
        {
            var c = FromExt(ext);
            return c == FileCategory.Document ? FileCategory.Document : FileCategory.Other;
        }

        return FromExt(ext);
    }

    private static bool IsBmp(ReadOnlySpan<byte> head) => true; // "BM" сигнатуры достаточно для BMP

    private static bool StartsWith(ReadOnlySpan<byte> data, params byte[] sig)
    {
        if (data.Length < sig.Length) return false;
        for (int i = 0; i < sig.Length; i++)
            if (data[i] != sig[i]) return false;
        return true;
    }

    /// <summary>Запасной вариант распознавания по расширению (для неоднозначных контейнеров).</summary>
    public static FileCategory FromExt(string? ext)
    {
        if (string.IsNullOrEmpty(ext)) return FileCategory.Other;
        var e = ext.TrimStart('.').ToLowerInvariant();
        return e switch
        {
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "tif" or "tiff" or "webp"
                or "heic" or "heif" or "avif" or "raw" or "cr2" or "nef" or "arw" or "dng" => FileCategory.Image,
            "mp3" or "flac" or "wav" or "ogg" or "oga" or "m4a" or "aac" or "wma" or "opus"
                or "aiff" or "ape" or "alac" => FileCategory.Audio,
            "mp4" or "mkv" or "webm" or "avi" or "mov" or "wmv" or "flv" or "m4v" or "mpg"
                or "mpeg" or "3gp" or "ts" or "m2ts" => FileCategory.Video,
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "odt" or "ods"
                or "odp" or "rtf" or "txt" or "md" or "epub" => FileCategory.Document,
            _ => FileCategory.Other
        };
    }
}
