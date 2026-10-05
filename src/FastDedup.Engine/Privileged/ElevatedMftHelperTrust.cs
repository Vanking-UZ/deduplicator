using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace FastDedup.Engine;

internal readonly record struct MftHelperTrustResult(bool IsTrusted, string Reason, string? HelperPath = null);

/// <summary>
/// Fail-closed проверка отдельного helper-файла. Release разрешает UAC только для подписанной пары
/// Deduplicator.exe/Deduplicator.MftHelper.exe в защищённом Program Files. Portable/unsigned сборка
/// остаётся на Win32Find. В Debug есть единственный явный test hook, скомпилированный из Release.
/// </summary>
internal static class ElevatedMftHelperTrust
{
    internal const string HelperFileName = "Deduplicator.MftHelper.exe";
    internal const string MainFileName = "Deduplicator.exe";
    internal const string DebugBypassEnvironmentVariable = "DEDUP_MFT_HELPER_TEST_TRUST_BYPASS";

    private const int ErrorAccessDenied = 5;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly uint[] DirectoryMutationRights =
    {
        0x00000002, // FILE_ADD_FILE
        0x00000004, // FILE_ADD_SUBDIRECTORY
        0x00000040, // FILE_DELETE_CHILD
        0x00010000, // DELETE (нельзя переименовать/заменить весь каталог приложения)
        0x00040000, // WRITE_DAC
        0x00080000  // WRITE_OWNER
    };

    private static readonly uint[] FileMutationRights =
    {
        0x00000002, // FILE_WRITE_DATA
        0x00000004, // FILE_APPEND_DATA
        0x00010000, // DELETE
        0x00040000, // WRITE_DAC
        0x00080000  // WRITE_OWNER
    };

    internal static MftHelperTrustResult EvaluateForMainProcess()
    {
        if (!OperatingSystem.IsWindows()) return new(false, "MFT-helper поддерживается только в Windows.");
        string? mainPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(mainPath)) return new(false, "Не удалось определить путь основного процесса.");
        string helperPath = Path.Combine(Path.GetDirectoryName(mainPath) ?? "", HelperFileName);
        return EvaluatePair(mainPath, helperPath, verifyCurrentUserCannotModify: true);
    }

    /// <summary>Проверка из elevated helper: PID обязан принадлежать той же подписанной паре.</summary>
    internal static MftHelperTrustResult EvaluateParentForHelper(int parentPid)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "MFT-helper поддерживается только в Windows.");
        string? helperPath = Environment.ProcessPath;
        string? parentPath = TryGetProcessImagePath(parentPid);
        if (string.IsNullOrWhiteSpace(helperPath) || string.IsNullOrWhiteSpace(parentPath))
            return new(false, "Не удалось подтвердить путь родительского процесса.");
        // Elevated токен по определению может изменять Program Files, поэтому ACL-проверку выполняет
        // только обычный родитель до UAC. Здесь повторно проверяем расположение и подписи пары.
        return EvaluatePair(parentPath, helperPath, verifyCurrentUserCannotModify: false);
    }

    internal static MftHelperTrustResult EvaluatePair(
        string mainPath, string helperPath, bool verifyCurrentUserCannotModify)
    {
        try
        {
            string main = Path.GetFullPath(mainPath);
            string helper = Path.GetFullPath(helperPath);
            if (!string.Equals(Path.GetFileName(main), MainFileName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(helper), HelperFileName, StringComparison.OrdinalIgnoreCase))
                return new(false, "Неверные имена файлов доверенной пары.", helper);
            if (!string.Equals(Path.GetDirectoryName(main), Path.GetDirectoryName(helper),
                    StringComparison.OrdinalIgnoreCase))
                return new(false, "MFT-helper должен находиться рядом с основным приложением.", helper);
            if (!File.Exists(main) || !File.Exists(helper))
                return new(false, "Файл MFT-helper не установлен.", helper);

#if DEBUG
            // Явный hook существует только в Debug/Test бинарниках. Пользовательская настройка его
            // не включает, а Release вообще не содержит этой ветки.
            if (string.Equals(Environment.GetEnvironmentVariable(DebugBypassEnvironmentVariable), "1",
                    StringComparison.Ordinal))
            {
                if (!PathHasNoReparsePoints(main, Path.GetDirectoryName(main)!) ||
                    !PathHasNoReparsePoints(helper, Path.GetDirectoryName(helper)!))
                    return new(false, "Debug helper не должен проходить через reparse-point.", helper);
                return new(true, "Явный Debug/Test trust hook.", helper);
            }
#endif

            if (!TryGetProtectedRoot(main, out string protectedRoot) ||
                !IsPathInsideRoot(helper, protectedRoot))
                return new(false, "MFT-helper отключён вне защищённого каталога Program Files.", helper);
            string directory = Path.GetDirectoryName(main)!;
            if (!PathHasNoReparsePoints(main, protectedRoot) ||
                !PathHasNoReparsePoints(helper, protectedRoot))
                return new(false, "Путь MFT-helper содержит reparse-point.", helper);

            if (verifyCurrentUserCannotModify)
            {
                if (!MutationRightsAreDenied(directory, isDirectory: true) ||
                    !RuntimeFilesAreProtected(directory))
                    return new(false, "Каталог или файлы среды MFT-helper доступны обычному пользователю на запись.", helper);
            }

            if (!HasTrustedAuthenticodeSignature(main) || !HasTrustedAuthenticodeSignature(helper))
                return new(false, "Подпись MFT-helper или основного приложения не подтверждена.", helper);
            string? mainSigner = TryGetSignerFingerprint(main);
            string? helperSigner = TryGetSignerFingerprint(helper);
            if (mainSigner is null || helperSigner is null ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(mainSigner), Convert.FromHexString(helperSigner)))
                return new(false, "MFT-helper подписан другим издателем.", helper);

            return new(true, "Доверенная подписанная пара в защищённом каталоге.", helper);
        }
        catch (Exception ex)
        {
            return new(false, "Проверка доверия MFT-helper не пройдена: " + ex.Message);
        }
    }

    internal static bool IsPathInsideRoot(string path, string root)
    {
        try
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static string? TryGetProcessImagePath(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0) return null;
        using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return null;
        int capacity = 32_768;
        var buffer = new char[capacity];
        if (!QueryFullProcessImageName(process, 0, buffer, ref capacity) || capacity <= 0) return null;
        return new string(buffer, 0, capacity);
    }

    private static bool TryGetProtectedRoot(string path, out string root)
    {
        root = "";
        foreach (string candidate in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsPathInsideRoot(path, candidate)) continue;
            root = Path.GetFullPath(candidate);
            return true;
        }
        return false;
    }

    private static bool PathHasNoReparsePoints(string path, string stopRoot)
    {
        string current = Path.GetFullPath(path);
        string stop = Path.GetFullPath(stopRoot).TrimEnd(Path.DirectorySeparatorChar);
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            string trimmed = current.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(trimmed, stop, StringComparison.OrdinalIgnoreCase)) return true;
            string? parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrWhiteSpace(parent) || !IsPathInsideRoot(current, stop)) return false;
            current = parent;
        }
    }

    private static bool MutationRightsAreDenied(string path, bool isDirectory)
    {
        uint flags = isDirectory ? FileFlagBackupSemantics : 0;
        foreach (uint desiredAccess in isDirectory ? DirectoryMutationRights : FileMutationRights)
        {
            using SafeFileHandle handle = CreateFile(path, desiredAccess,
                FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
            if (!handle.IsInvalid) return false; // хотя бы одно право записи реально доступно
            if (Marshal.GetLastWin32Error() != ErrorAccessDenied) return false; // fail closed
        }
        return true;
    }

    private static bool RuntimeFilesAreProtected(string directory)
    {
        // Подписанный apphost загружает соседние managed/native DLL и json-конфигурацию. Проверять
        // только EXE недостаточно: user-writable FastDedup.Engine.dll дал бы тот же elevated code exec.
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            string extension = Path.GetExtension(file);
            if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                !MutationRightsAreDenied(file, isDirectory: false)) return false;
        }
        return true;
    }

    private static bool HasTrustedAuthenticodeSignature(string path)
    {
        IntPtr pathPtr = IntPtr.Zero;
        IntPtr fileInfoPtr = IntPtr.Zero;
        try
        {
            pathPtr = Marshal.StringToCoTaskMemUni(path);
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPtr,
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero
            };
            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,              // WTD_UI_NONE
                RevocationChecks = 0,      // WTD_REVOKE_NONE; без сетевого запроса при каждом скане
                UnionChoice = 1,           // WTD_CHOICE_FILE
                FileInfo = fileInfoPtr,
                StateAction = 0,           // WTD_STATEACTION_IGNORE
                ProviderFlags = 0x00001000 // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            if (fileInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPtr);
            if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    private static string? TryGetSignerFingerprint(string path)
    {
        try
        {
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
            return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, [Out] char[] fileName, ref int size);

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);
}
