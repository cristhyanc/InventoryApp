using System.Text.Json;
using System.Xml;
using Inventory.Application.Imports;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// Reads pending Nayax reimbursement XML files from the local filesystem and parses them into
/// <see cref="PendingReimbursementXmlFile"/> facts (issue #299), behind the Application's
/// <see cref="IPendingReimbursementXmlSource"/> port.
///
/// Discovery, SHA-256 hashing, XML parsing and removal all moved here unchanged from
/// <c>InventoryApi.Services.ImportService.ImportPendingXmlFilesAsync</c>/<c>ParseDocument</c>/
/// <c>ParseReimbursement</c>. Since issue #1149 the parsing itself is
/// <see cref="ReimbursementXmlParser"/>, shared with the uploaded-archive reader so there is exactly
/// one set of decisions about what a settlement figure means; every one of them is unchanged, and
/// this adapter still owns discovery, the folder confinement and removal.
///
/// Filesystem and XML failures are translated here and never cross the port: an unreadable or
/// malformed file is logged and answered as <c>null</c>, a file that cannot be deleted is logged
/// and answered as <c>false</c>. Caller cancellation is excluded from that translation and stays
/// cancellation.
/// </summary>
public sealed class FileSystemPendingReimbursementXmlSource : IPendingReimbursementXmlSource
{
    /// <summary>The web-root-relative folder pending reimbursement files are collected in.</summary>
    public const string PendingFolderName = "ImportedFiles";

    private readonly string _folder;
    private readonly ILogger<FileSystemPendingReimbursementXmlSource> _logger;

    public FileSystemPendingReimbursementXmlSource(
        PendingReimbursementXmlOptions options,
        ILogger<FileSystemPendingReimbursementXmlSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContentRootPath);

        var webRoot = options.WebRootPath ?? Path.Combine(options.ContentRootPath, "wwwroot");
        _folder = Path.Combine(webRoot, PendingFolderName);
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListPendingFiles()
    {
        // Created rather than probed, exactly as before: the folder is this import's inbox, and
        // an operator who has not dropped a file in yet is not an error.
        Directory.CreateDirectory(_folder);

        return Directory.EnumerateFiles(_folder, "*.xml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<PendingReimbursementXmlFile?> ReadAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = ConfineToFolder(fileName);
        if (path is null)
        {
            _logger.LogError("Could not import XML file {FileName}: it does not name a file in the pending folder.", fileName);
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);

            return ReimbursementXmlParser.Parse(Path.GetFileName(path), bytes);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or FormatException
            or JsonException
            or XmlException)
        {
            // The file name, never the server path: a log line is not a place to publish where
            // business documents live on disk.
            _logger.LogError(ex, "Could not import XML file {FileName}", fileName);
            return null;
        }
    }

    /// <inheritdoc />
    public bool TryDiscard(string fileName)
    {
        var path = ConfineToFolder(fileName);
        if (path is null)
        {
            _logger.LogError("Could not remove XML file {FileName}: it does not name a file in the pending folder.", fileName);
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not remove imported XML file {FileName}", fileName);
            return false;
        }
    }

    /// <summary>
    /// Reduces a name to its final path segment and proves the result is inside the pending
    /// folder, or answers <c>null</c>. The names this adapter hands out come from its own
    /// enumeration, but they make the round trip through the Application layer, so they are
    /// re-checked here: no caller may choose an arbitrary filesystem path.
    /// </summary>
    private string? ConfineToFolder(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var leaf = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(leaf) || !string.Equals(leaf, fileName, StringComparison.Ordinal)) return null;

        var folderFullPath = Path.GetFullPath(_folder);
        var candidate = Path.GetFullPath(Path.Combine(folderFullPath, leaf));
        var prefix = folderFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? folderFullPath
            : folderFullPath + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }
}
