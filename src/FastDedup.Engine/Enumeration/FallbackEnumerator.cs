using System.Collections.Concurrent;

namespace FastDedup.Engine;

/// <summary>
/// Универсальный обход дерева каталогов. Работает везде (любая ФС, без админа),
/// но медленнее MFT-режима. Обход ручным стеком позволяет рано отсекать исключённые
/// поддеревья: node_modules, .git, временные каталоги и т.п.
/// </summary>
public sealed class FallbackEnumerator : IFileEnumerator
{
    public IEnumerable<string> EnumerateFiles(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct)
    {
        // AttributesToSkip применяется и к файлам, и к каталогам: при SkipHiddenSystem мы НЕ заходим
        // внутрь скрытых/системных папок (иначе нескрытые файлы внутри них попали бы в индекс — P1.5).
        var skipAttrs = options.IncludeReparsePoints ? (FileAttributes)0 : FileAttributes.ReparsePoint;
        if (options.SkipHiddenSystem) skipAttrs |= FileAttributes.Hidden | FileAttributes.System;
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = skipAttrs,
            ReturnSpecialDirectories = false
        };

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            foreach (var file in SafeEnumerate(
                         () => Directory.EnumerateFiles(dir, "*", enumOptions), dir, "files", errors))
            {
                ct.ThrowIfCancellationRequested();
                if (ScanFilters.IsExcludedByPath(file, options)) continue;
                yield return file;
            }

            foreach (var subdir in SafeEnumerate(
                         () => Directory.EnumerateDirectories(dir, "*", enumOptions), dir, "dirs", errors))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(subdir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (ScanFilters.ShouldSkipDirectoryName(name, options)) continue;
                stack.Push(subdir);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(
        Func<IEnumerable<string>> factory, string dir, string kind, ConcurrentBag<ScanError> errors)
    {
        IEnumerator<string>? e = null;
        try { e = factory().GetEnumerator(); }
        catch (Exception ex) { errors.Add(new ScanError(dir, $"enumerate {kind}: " + ex.Message)); }
        if (e == null) yield break;

        using (e)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!e.MoveNext()) break;
                    current = e.Current;
                }
                catch (Exception ex)
                {
                    errors.Add(new ScanError(dir, $"iterate {kind}: " + ex.Message));
                    yield break;
                }
                yield return current;
            }
        }
    }
}
