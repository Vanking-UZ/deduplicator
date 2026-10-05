using System.Buffers.Binary;
using System.Text;

namespace FastDedup.Engine;

/// <summary>
/// Разобранная запись USN_RECORD_V2. Формат общий для FSCTL_ENUM_USN_DATA (перечисление MFT)
/// и FSCTL_READ_USN_JOURNAL (дельта журнала); Reason/Usn осмысленны только для дельты
/// (при перечислении MFT там лежат нули/служебные значения — вызывающий их не использует).
/// </summary>
internal readonly record struct ParsedUsnRecord(
    int Length, ushort Major, ulong Frn, ulong ParentFrn, long Usn, uint Reason, uint Attributes, string Name);

/// <summary>
/// Общий парсер записей USN. Раньше разбор жил внутри <see cref="UsnEnumerator"/>; вынесен, чтобы
/// дельта-чтение журнала (<see cref="UsnDeltaReader"/>) разбирало записи ТЕМ ЖЕ проверенным кодом
/// (смещения V2 сверены с winioctl.h). Версии, отличные от V2, не разбираются (Major≠2 → поля
/// нулевые, Length валиден для перешагивания) — решение об аномалии принимает вызывающий.
/// </summary>
internal static class UsnRecordParser
{
    /// <summary>Минимальный размер заголовка USN_RECORD_V2 (до поля имени).</summary>
    public const int MinRecordBytes = 60;

    /// <summary>
    /// Разбор записи по смещению. false — записи кончились или битая длина (стоп всего буфера).
    /// true при Major≠2: содержимое не извлекается, запись перешагивается по Length.
    /// Имя пустое, если поле имени отсутствует или выходит за границы записи.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buf, int offset, out ParsedUsnRecord rec)
    {
        rec = default;
        if (offset < 0 || offset + MinRecordBytes > buf.Length) return false;

        var span = buf.Slice(offset);
        uint recLen = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (recLen == 0 || offset + (long)recLen > buf.Length) return false;

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
        if (major != 2)
        {
            // V3 (128-битные FRN) и прочие версии: заголовок другой, поля не читаем.
            rec = new ParsedUsnRecord((int)recLen, major, 0, 0, 0, 0, 0, string.Empty);
            return true;
        }

        if (recLen < MinRecordBytes) return false;

        ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8));
        ulong pfrn = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16));
        long usn = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(24));
        uint reason = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(40));
        uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(52));
        ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(56));
        ushort nameOff = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(58));

        if (nameLen > 0 && ((nameLen & 1) != 0 || nameOff < MinRecordBytes ||
                            (uint)nameOff + nameLen > recLen))
            return false;

        string name = nameLen > 0
            ? Encoding.Unicode.GetString(span.Slice(nameOff, nameLen))
            : string.Empty;

        rec = new ParsedUsnRecord((int)recLen, major, frn, pfrn, usn, reason, attrs, name);
        return true;
    }
}
