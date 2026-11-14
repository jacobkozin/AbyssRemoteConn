using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http.Features;

namespace AbyssRemote.Host.Features;

/// <summary>A file in the transfer folder, as a paired device sees it.</summary>
public sealed record TransferFile(string Name, long Size, DateTimeOffset ModifiedUtc);

/// <summary>An upload grew past <see cref="TransferFolder.MaxUploadBytes"/>.</summary>
public sealed class UploadTooLargeException() : Exception("The upload is larger than this PC accepts.");

/// <summary>The one folder on the PC that paired devices can send files to and fetch files from.
/// A device never supplies a path. Uploads get a checked name inside this folder, and a
/// download or delete must name a file that a listing of this folder really contains: a plain
/// file directly inside it, never a sub-folder, a link, a hidden or system file, or an upload
/// that is still arriving.</summary>
public sealed class TransferFolder
{
    /// <summary>The largest upload, 2 GiB: several minutes of 4K phone video, which is the
    /// biggest thing a phone normally sends. Uploads cannot be resumed, so anything larger
    /// would rarely finish over Wi-Fi anyway, and the limit caps what one request can write.</summary>
    public const long DefaultMaxUploadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>An upload arrives under a hidden name with this prefix and gets its real name
    /// only when it is complete. Devices cannot list, fetch, delete or upload such names.</summary>
    internal const string PartialPrefix = ".abyss-upload-";
    private const string PartialSuffix = ".part";

    /// <summary>"photo (2).jpg" up to "photo (999).jpg", then the upload is refused.</summary>
    private const int MaxCopies = 999;

    private const FileAttributes NotAPlainFile =
        FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Device;

    public TransferFolder(string directory)
    {
        Directory = Path.GetFullPath(directory);
        FreeBytes = () => FreeBytesNear(Directory);
    }

    /// <summary>The folder's full path. It is created when the first file arrives.</summary>
    public string Directory { get; }

    /// <summary>The largest file a device may send. Tests lower it.</summary>
    public long MaxUploadBytes { get; set; } = DefaultMaxUploadBytes;

    /// <summary>Free space on the folder's drive. Tests replace it to pretend the drive is full.</summary>
    internal Func<long> FreeBytes { get; set; }

    /// <summary>The production folder: "Abyss Remote" inside the user's Downloads folder.</summary>
    public static TransferFolder InDownloads() => new(Path.Combine(DownloadsFolder(), AppInfo.Name));

    /// <summary>Visible files, newest first.</summary>
    public IReadOnlyList<TransferFile> List() =>
        VisibleFiles()
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new TransferFile(file.Name, file.Length, file.LastWriteTimeUtc))
            .ToList();

    /// <summary>The visible file with exactly this name, or null. The name is only compared
    /// with real directory entries and never turned into a path, so "..\x", "C:\x",
    /// "x::$DATA", "x." or an 8.3 alias reach nothing: no entry has such a name.</summary>
    public FileInfo? Find(string name) => VisibleFiles().FirstOrDefault(file => file.Name == name);

    /// <summary>Stores an upload. The bytes go to a hidden partial file first; only a complete
    /// file is marked as downloaded and renamed, and a taken name becomes "name (2).ext"
    /// instead of replacing a file. Whatever goes wrong, the partial file is removed.</summary>
    /// <returns>The name the file ended up with, and its size.</returns>
    public async Task<(string Name, long Size)> SaveAsync(string name, Stream content, CancellationToken cancel)
    {
        if (!TransferName.IsSafe(name)) throw new ArgumentException("Not a safe file name.", nameof(name));
        System.IO.Directory.CreateDirectory(Directory);
        RemoveAbandonedPartials();

        string partial = Path.Combine(Directory, PartialPrefix + Guid.NewGuid().ToString("N") + PartialSuffix);
        try
        {
            long size;
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous, BufferSize = 0, // the copy loop already writes large blocks
            };
            await using (var file = new FileStream(partial, options))
            {
                File.SetAttributes(partial, FileAttributes.Hidden);
                size = await CopyAsync(content, file, cancel);
            }
            File.SetAttributes(partial, FileAttributes.Archive); // complete: an ordinary new file again
            MarkAsDownloaded(partial);
            return (MoveToFreeName(partial, name), size);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private List<FileInfo> VisibleFiles()
    {
        try { return new DirectoryInfo(Directory).EnumerateFiles().Where(file => IsVisible(file.Attributes, file.Name)).ToList(); }
        catch (IOException) { return []; } // no folder yet: nothing was ever sent to this PC
    }

    /// <summary>A link (reparse point) is never followed: it is simply not a visible file.</summary>
    internal static bool IsVisible(FileAttributes attributes, string name) =>
        (attributes & NotAPlainFile) == 0 && !name.StartsWith(PartialPrefix, StringComparison.OrdinalIgnoreCase);

    private async Task<long> CopyAsync(Stream from, FileStream to, CancellationToken cancel)
    {
        var buffer = new byte[80 * 1024];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, cancel)) > 0)
        {
            // Counted here as well as by the web server, so the limit holds even for a body
            // that did not say how long it is.
            total += read;
            if (total > MaxUploadBytes) throw new UploadTooLargeException();
            await to.WriteAsync(buffer.AsMemory(0, read), cancel);
        }
        return total;
    }

    /// <summary>Removes partial files that nobody is writing any more, left behind when the
    /// host was closed or lost power in the middle of an upload. An upload that is still
    /// running keeps its file open without sharing, so Windows refuses to delete that one.</summary>
    private void RemoveAbandonedPartials()
    {
        foreach (string partial in System.IO.Directory.EnumerateFiles(Directory, PartialPrefix + "*" + PartialSuffix))
            TryDelete(partial);
    }

    /// <summary>The "Mark of the Web" that browsers put on downloads: an alternate data stream
    /// named Zone.Identifier saying the file came from the Internet zone (3). Windows then warns
    /// before running it and SmartScreen checks it, like any other download.</summary>
    private static void MarkAsDownloaded(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Only NTFS has alternate streams. On a FAT or exFAT drive the file is kept
            // without the mark, which is also what browsers do there.
        }
    }

    private string MoveToFreeName(string partial, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        if (stem.Length == 0) (stem, extension) = (name, ""); // ".gitignore" is all name
        for (int copy = 1; copy <= MaxCopies; copy++)
        {
            string candidate = copy == 1 ? name : $"{stem} ({copy}){extension}";
            string target = Path.Combine(Directory, candidate);
            try
            {
                File.Move(partial, target); // fails when the name is taken; it never replaces a file
                return candidate;
            }
            catch (IOException) when (File.Exists(target) || System.IO.Directory.Exists(target))
            {
                // Taken (possibly by an upload that finished a moment ago): try the next number.
            }
        }
        throw new IOException($"There are already {MaxCopies} files named \"{name}\".");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere. RemoveAbandonedPartials tries again on the next upload.
        }
    }

    /// <summary>Free space where the folder is, or will be once it exists.</summary>
    private static long FreeBytesNear(string directory)
    {
        string? existing = directory;
        while (existing is not null && !System.IO.Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        // If Windows will not say, do not block the upload: writing fails cleanly on a full drive.
        return existing is not null && GetDiskFreeSpaceEx(existing, out long free, out _, out _) ? free : long.MaxValue;
    }

    /// <summary>Asks Windows where Downloads is, because it can be moved to another drive.</summary>
    private static string DownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads (KnownFolders.h)
        int result = SHGetKnownFolderPath(in downloads, 0, IntPtr.Zero, out IntPtr path);
        try
        {
            if (result == 0 && Marshal.PtrToStringUni(path) is { Length: > 0 } found) return found;
        }
        finally { Marshal.FreeCoTaskMem(path); } // the caller frees it, whether the call worked or not
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out long freeForThisUser, out long total, out long free);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, IntPtr token, out IntPtr path);
}

public static class FileTransferEndpoints
{
    public static void Map(WebApplication app, ServerContext c)
    {
        var folder = c.Transfer;

        app.MapGet("/api/v1/files", (HttpContext ctx) => c.Device(ctx) is null
            ? Results.Unauthorized()
            : Results.Ok(new { folder = folder.Directory, maxUploadBytes = folder.MaxUploadBytes, files = folder.List() }));

        // The body is the file itself; the name travels in the query string.
        app.MapPost("/api/v1/files", async Task<IResult> (HttpContext ctx) =>
        {
            var device = c.Device(ctx);
            if (!c.Authorized(ctx, device)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var names = ctx.Request.Query["name"];
            if (names.Count != 1 || !TransferName.IsSafe(names[0]))
                return Results.BadRequest(new { error = "That file name cannot be used on this PC. Rename the file and try again." });

            // Refuse early when the request already says it is too big, before reading any of it.
            long limit = folder.MaxUploadBytes;
            long? declared = ctx.Request.ContentLength;
            if (declared > limit) return TooLarge(limit);
            if (declared > folder.FreeBytes()) return NoRoom();

            // Every other request body is limited to 64 KB (AbyssServer). Only this one may be
            // larger, and only now that the device, the origin, the CSRF token and the name
            // have all been checked. A body that states its length gets the upload limit. A
            // chunked body has no stated length, and the web server would count its chunk
            // framing as well and refuse a file of exactly the limit, so for that one its
            // limit is lifted and SaveAsync's own count of the file's bytes is the limit.
            var bodySize = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = declared is null ? null : limit;

            try
            {
                var (savedAs, size) = await folder.SaveAsync(names[0]!, ctx.Request.Body, ctx.RequestAborted);
                c.Notice($"Received \"{savedAs}\" ({Size(size)}) from \"{device!.Name}\".");
                return Results.Created($"/api/v1/files/{Uri.EscapeDataString(savedAs)}", new { name = savedAs, size });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or UploadTooLargeException)
            {
                return UploadFailed(ctx, ex, limit);
            }
        });

        app.MapGet("/api/v1/files/{name}", IResult (HttpContext ctx, string name) =>
        {
            var device = c.Device(ctx);
            if (device is null) return Results.Unauthorized();
            var file = folder.Find(name);
            if (file is null) return Results.NotFound();

            FileStream content;
            try
            {
                // Others may read or delete the file meanwhile, but a file that some program
                // is still writing is not sent half-finished.
                content = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException) { return Results.NotFound(); } // deleted a moment ago
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Json(new { error = "That file is in use on the PC. Try again in a moment." }, statusCode: StatusCodes.Status409Conflict); }

            if (StartsAtTheBeginning(ctx)) c.Notice($"Sending \"{file.Name}\" ({Size(content.Length)}) to \"{device.Name}\".");
            // Always "a file to save", never "a page to show": nothing a device uploaded is
            // ever displayed by a browser as part of this site.
            return Results.File(content, "application/octet-stream", file.Name, lastModified: file.LastWriteTimeUtc, enableRangeProcessing: true);
        });

        app.MapDelete("/api/v1/files/{name}", IResult (HttpContext ctx, string name) =>
        {
            var device = c.Device(ctx);
            if (!c.Authorized(ctx, device)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var file = folder.Find(name);
            if (file is null) return Results.NotFound();
            try { file.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Json(new { error = "Windows could not delete that file. It may be open in a program on the PC." }, statusCode: StatusCodes.Status409Conflict); }
            c.Notice($"\"{device!.Name}\" deleted \"{file.Name}\".");
            return Results.Ok(new { deleted = true });
        });
    }

    private static IResult TooLarge(long limit) =>
        Results.Json(new { error = $"That file is too large. The limit is {Size(limit)}." }, statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult NoRoom() =>
        Results.Json(new { error = "The PC does not have enough free space for that file." }, statusCode: StatusCodes.Status507InsufficientStorage);

    private static IResult UploadFailed(HttpContext ctx, Exception problem, long limit) => problem switch
    {
        UploadTooLargeException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => TooLarge(limit),
        // The device went away (cancelled, lost Wi-Fi): nobody is waiting for an answer.
        _ when ctx.RequestAborted.IsCancellationRequested => Results.StatusCode(StatusCodes.Status499ClientClosedRequest),
        BadHttpRequestException bad => Results.Json(new { error = "The upload stopped before the whole file arrived." }, statusCode: bad.StatusCode),
        IOException full when (full.HResult & 0xFFFF) is 112 or 39 => NoRoom(), // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        _ => Results.Json(new { error = "The PC could not save that file." }, statusCode: StatusCodes.Status500InternalServerError),
    };

    /// <summary>False when a request continues an earlier download ("Range: bytes=5000-"), so
    /// a file fetched in pieces is announced once, not once per piece.</summary>
    private static bool StartsAtTheBeginning(HttpContext ctx)
    {
        string range = ctx.Request.Headers.Range.ToString();
        return range.Length == 0 || range.StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase);
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
