using System.Globalization;

namespace FixSfi;

/// <summary>
/// CLI tool that repairs Smallworld VMDS Super File Index (.sfi) files by rebuilding
/// local .ds paths and validating referenced files.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Entry point.
    /// </summary>
    /// <param name="args">
    /// Optional directory path plus mode flags.
    /// Default mode is dry-run ("what-if"); use <c>--apply</c> to write changes.
    /// </param>
    /// <returns>
    /// Exit code 0 for success, 1 when validation errors are found, 2 for invalid arguments.
    /// </returns>
    public static int Main(string[] args)
    {
        var parseResult = TryParseArguments(args);
        if (!parseResult.Success)
        {
            Console.Error.WriteLine(parseResult.ErrorMessage);
            PrintUsage();
            return 2;
        }

        if (parseResult.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        var targetPath = Path.GetFullPath(parseResult.TargetPath ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(targetPath))
        {
            Console.Error.WriteLine($"Target path does not exist: {targetPath}");
            return 2;
        }

        var modeText = parseResult.Apply ? "APPLY" : "WHAT-IF";
        Console.WriteLine($"Mode: {modeText}");
        Console.WriteLine($"Target path: {targetPath}");

        var sfiFiles = Directory
            .EnumerateFiles(targetPath, "*.sfi", SearchOption.AllDirectories)
            .Where(path => !IsBackupSfi(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Found {sfiFiles.Count} .sfi file(s).");

        if (sfiFiles.Count == 0)
        {
            return 0;
        }

        var updatedCount = 0;
        var unchangedCount = 0;
        var errorCount = 0;

        foreach (var sfiFile in sfiFiles)
        {
            var result = ProcessSfiFile(sfiFile, parseResult.Apply);

            if (!string.IsNullOrWhiteSpace(result.ParseError))
            {
                Console.WriteLine($"[ERROR] {sfiFile}");
                Console.WriteLine($"        {result.ParseError}");
                errorCount++;
                continue;
            }

            if (result.WouldChange)
            {
                var actionText = parseResult.Apply ? "UPDATED" : "FIX";
                Console.WriteLine($"[{actionText}] {sfiFile}");
                Console.WriteLine($"          .ds entries: {result.CurrentEntryCount} -> {result.DesiredEntryCount}");
                Console.WriteLine($"          backup file: {result.BackupPath}");
                updatedCount++;
            }
            else
            {
                Console.WriteLine($"[OK] {sfiFile}");
                unchangedCount++;
            }

            foreach (var fileError in result.FileErrors)
            {
                Console.WriteLine($"[ERROR] {sfiFile}");
                Console.WriteLine($"        {fileError}");
                errorCount++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: changed={updatedCount}, unchanged={unchangedCount}, errors={errorCount}");
        return errorCount > 0 ? 1 : 0;
    }

    /// <summary>
    /// Processes one .sfi file.
    /// </summary>
    /// <param name="sfiPath">Absolute or relative path to the .sfi file.</param>
    /// <param name="apply">
    /// True to perform changes on disk; false for dry-run output only.
    /// </param>
    /// <returns>Structured result including change detection and validation errors.</returns>
    private static ProcessResult ProcessSfiFile(string sfiPath, bool apply)
    {
        var text = File.ReadAllText(sfiPath);
        var newLine = DetectLineEnding(text);

        var rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (rawLines.Length > 0 && rawLines[^1].Length == 0)
        {
            rawLines = rawLines[..^1];
        }

        if (rawLines.Length < 2)
        {
            return new ProcessResult
            {
                ParseError = "Invalid .sfi file: expected at least 2 lines."
            };
        }

        var identifierLine = rawLines[0];
        var databaseLine = rawLines[1];
        var currentDsEntries = rawLines
            .Skip(2)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var directory = Path.GetDirectoryName(sfiPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(sfiPath);
        var desiredDsEntries = Directory
            .EnumerateFiles(directory, $"{baseName}*.ds", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToList();
        desiredDsEntries.Sort((left, right) => CompareDsPaths(left, right, baseName));

        var wouldChange = !currentDsEntries.SequenceEqual(desiredDsEntries, StringComparer.Ordinal);

        var result = new ProcessResult
        {
            WouldChange = wouldChange,
            CurrentEntryCount = currentDsEntries.Count,
            DesiredEntryCount = desiredDsEntries.Count,
            BackupPath = BuildBackupPath(sfiPath)
        };

        if (wouldChange && apply)
        {
            var updatedLines = new List<string>(2 + desiredDsEntries.Count)
            {
                identifierLine,
                databaseLine
            };
            updatedLines.AddRange(desiredDsEntries);
            var updatedText = string.Join(newLine, updatedLines) + newLine;

            File.Move(sfiPath, result.BackupPath);
            File.WriteAllText(sfiPath, updatedText);
        }

        var entriesToValidate = wouldChange ? desiredDsEntries : currentDsEntries;
        foreach (var entry in entriesToValidate)
        {
            if (!File.Exists(entry))
            {
                result.FileErrors.Add($"Missing .ds file: {entry}");
                continue;
            }

            var fileInfo = new FileInfo(entry);
            if (fileInfo.Length <= 0)
            {
                result.FileErrors.Add($"Empty .ds file (0 bytes): {entry}");
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a unique timestamped backup path in the format:
    /// &lt;name&gt;.&lt;yyyyMMddHHmmss&gt;.sfi
    /// </summary>
    /// <param name="sfiPath">Original .sfi path.</param>
    /// <returns>Backup file path that does not currently exist.</returns>
    private static string BuildBackupPath(string sfiPath)
    {
        var directory = Path.GetDirectoryName(sfiPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(sfiPath);
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"{baseName}.{timestamp}.sfi");
        var suffix = 1;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName}.{timestamp}_{suffix}.sfi");
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// Preserves the original line ending style when rewriting a file.
    /// </summary>
    /// <param name="content">Full file text.</param>
    /// <returns><c>\r\n</c> when CRLF is detected; otherwise <c>\n</c>.</returns>
    private static string DetectLineEnding(string content)
    {
        return content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    /// <summary>
    /// Sorts matched .ds paths in domain order:
    /// 1) &lt;base&gt;.ds
    /// 2) numbered suffix variants (for example -1, -2, _1, _2)
    /// 3) lexical fallback for any other matches.
    /// </summary>
    /// <param name="leftPath">Left path to compare.</param>
    /// <param name="rightPath">Right path to compare.</param>
    /// <param name="baseName">Base name from the .sfi file.</param>
    /// <returns>Comparison result compatible with <see cref="List{T}.Sort(Comparison{T})"/>.</returns>
    private static int CompareDsPaths(string leftPath, string rightPath, string baseName)
    {
        var leftName = Path.GetFileNameWithoutExtension(leftPath);
        var rightName = Path.GetFileNameWithoutExtension(rightPath);

        var leftIsBase = string.Equals(leftName, baseName, StringComparison.OrdinalIgnoreCase);
        var rightIsBase = string.Equals(rightName, baseName, StringComparison.OrdinalIgnoreCase);
        if (leftIsBase && !rightIsBase)
        {
            return -1;
        }

        if (!leftIsBase && rightIsBase)
        {
            return 1;
        }

        var leftNumber = TryParseDsSuffixNumber(leftName, baseName);
        var rightNumber = TryParseDsSuffixNumber(rightName, baseName);

        if (leftNumber.HasValue && rightNumber.HasValue)
        {
            var numberComparison = leftNumber.Value.CompareTo(rightNumber.Value);
            if (numberComparison != 0)
            {
                return numberComparison;
            }
        }
        else if (leftNumber.HasValue && !rightNumber.HasValue)
        {
            return -1;
        }
        else if (!leftNumber.HasValue && rightNumber.HasValue)
        {
            return 1;
        }

        return string.Compare(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses numeric .ds suffix from file names like "&lt;base&gt;-7" or "&lt;base&gt;_7".
    /// </summary>
    /// <param name="fileNameWithoutExtension">Name without extension.</param>
    /// <param name="baseName">Expected base prefix.</param>
    /// <returns>Parsed integer suffix when present; otherwise null.</returns>
    private static int? TryParseDsSuffixNumber(string fileNameWithoutExtension, string baseName)
    {
        if (!fileNameWithoutExtension.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = fileNameWithoutExtension[baseName.Length..];
        if (suffix.Length < 2)
        {
            return null;
        }

        var first = suffix[0];
        if (first != '-' && first != '_')
        {
            return null;
        }

        return int.TryParse(suffix[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Parses command line arguments.
    /// </summary>
    /// <param name="args">Raw arguments passed to the process.</param>
    /// <returns>Argument parse result with validation status.</returns>
    private static ParseResult TryParseArguments(string[] args)
    {
        var result = new ParseResult { Success = true };
        var explicitWhatIf = false;

        foreach (var arg in args)
        {
            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase))
            {
                result.ShowHelp = true;
                return result;
            }

            if (string.Equals(arg, "--apply", StringComparison.OrdinalIgnoreCase))
            {
                result.Apply = true;
                continue;
            }

            if (string.Equals(arg, "--what-if", StringComparison.OrdinalIgnoreCase))
            {
                explicitWhatIf = true;
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                result.Success = false;
                result.ErrorMessage = $"Unknown option: {arg}";
                return result;
            }

            if (!string.IsNullOrWhiteSpace(result.TargetPath))
            {
                result.Success = false;
                result.ErrorMessage = "Only one path argument is allowed.";
                return result;
            }

            result.TargetPath = arg;
        }

        if (result.Apply && explicitWhatIf)
        {
            result.Success = false;
            result.ErrorMessage = "Use either --apply or --what-if, not both.";
            return result;
        }

        return result;
    }

    /// <summary>
    /// Determines whether a .sfi file name matches the tool's backup pattern:
    /// "&lt;name&gt;.&lt;digits&gt;.sfi" or "&lt;name&gt;.&lt;digits&gt;_&lt;digits&gt;.sfi".
    /// </summary>
    /// <param name="path">Path to test.</param>
    /// <returns>True when the file should be treated as a backup and skipped.</returns>
    private static bool IsBackupSfi(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".sfi", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        var lastDotIndex = nameWithoutExtension.LastIndexOf('.');
        if (lastDotIndex <= 0 || lastDotIndex >= nameWithoutExtension.Length - 1)
        {
            return false;
        }

        var suffix = nameWithoutExtension[(lastDotIndex + 1)..];
        if (AllDigits(suffix))
        {
            return true;
        }

        // Also treat "<name>.<digits>_<counter>.sfi" as backup when timestamp collides.
        var underscoreIndex = suffix.IndexOf('_');
        if (underscoreIndex <= 0 || underscoreIndex >= suffix.Length - 1)
        {
            return false;
        }

        var left = suffix[..underscoreIndex];
        var right = suffix[(underscoreIndex + 1)..];
        return AllDigits(left) && AllDigits(right);
    }

    /// <summary>
    /// Returns true when all characters in a string are ASCII digits.
    /// </summary>
    /// <param name="value">Input text.</param>
    /// <returns>True only for non-empty digit-only strings.</returns>
    private static bool AllDigits(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Prints CLI usage help text.
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  fixsfi [path] [--what-if]");
        Console.WriteLine("  fixsfi [path] --apply");
        Console.WriteLine();
        Console.WriteLine("Defaults:");
        Console.WriteLine("  path      Current directory");
        Console.WriteLine("  mode      --what-if (dry-run)");
    }

    /// <summary>
    /// Parsed command line arguments.
    /// </summary>
    private sealed class ParseResult
    {
        /// <summary>
        /// True when parsing succeeded.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Error message for invalid arguments.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// True when help output was requested.
        /// </summary>
        public bool ShowHelp { get; set; }

        /// <summary>
        /// Optional target directory to scan.
        /// </summary>
        public string? TargetPath { get; set; }

        /// <summary>
        /// True when running in write mode.
        /// </summary>
        public bool Apply { get; set; }
    }

    /// <summary>
    /// Per-file processing outcome.
    /// </summary>
    private sealed class ProcessResult
    {
        /// <summary>
        /// True when local .ds list differs from current .sfi content.
        /// </summary>
        public bool WouldChange { get; set; }

        /// <summary>
        /// Number of .ds entries currently stored in the file.
        /// </summary>
        public int CurrentEntryCount { get; set; }

        /// <summary>
        /// Number of .ds entries discovered locally from &lt;base&gt;*.ds.
        /// </summary>
        public int DesiredEntryCount { get; set; }

        /// <summary>
        /// Backup path reserved for this operation.
        /// </summary>
        public string BackupPath { get; set; } = string.Empty;

        /// <summary>
        /// Parse error when the .sfi does not have the required structure.
        /// </summary>
        public string? ParseError { get; set; }

        /// <summary>
        /// Validation errors for missing/empty .ds files.
        /// </summary>
        public List<string> FileErrors { get; } = new();
    }
}
