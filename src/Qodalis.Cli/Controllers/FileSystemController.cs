using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Plugin.FileSystem;

namespace Qodalis.Cli.Controllers;

/// <summary>
/// Controller for filesystem operations (list, read, stat, download, upload, mkdir, delete).
/// Access is restricted to paths whitelisted via <see cref="FileSystem.FileSystemPathValidator"/>.
/// </summary>
[ApiController]
[Route("api/qcli/fs")]
public class FileSystemController : ControllerBase
{
    private readonly IFileStorageProvider _provider;
    private readonly ILogger<FileSystemController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FileSystemController"/>.
    /// </summary>
    /// <param name="provider">The file storage provider.</param>
    /// <param name="logger">The logger instance.</param>
    public FileSystemController(IFileStorageProvider provider, ILogger<FileSystemController> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    /// <summary>
    /// Lists entries in the specified directory.
    /// </summary>
    /// <param name="path">The directory path to list.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    [HttpGet("ls")]
    public async Task<IActionResult> ListDirectory([FromQuery] string path, CancellationToken ct)
    {
        _logger.LogDebug("Listing directory: {Path}", path);
        try
        {
            var entries = await _provider.ListAsync(path, ct);
            return Ok(new { Entries = entries });
        }
        catch (FileStorageNotFoundError ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (FileStoragePermissionError ex)
        {
            _logger.LogWarning("Permission denied: {Path}", path);
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (FileStorageNotADirectoryError ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list directory: {Path}", path);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Reads and returns the contents of a file.
    /// </summary>
    /// <param name="path">The file path to read.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    [HttpGet("cat")]
    public async Task<IActionResult> ReadFile([FromQuery] string path, CancellationToken ct)
    {
        _logger.LogDebug("Reading file: {Path}", path);
        try
        {
            var content = await _provider.ReadFileAsync(path, ct);
            return Ok(new { Content = content });
        }
        catch (FileStorageNotFoundError ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (FileStoragePermissionError ex)
        {
            _logger.LogWarning("Permission denied: {Path}", path);
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (FileStorageIsADirectoryError ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read file: {Path}", path);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Returns metadata (size, type, timestamps) for a file or directory.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    [HttpGet("stat")]
    public async Task<IActionResult> GetFileInfo([FromQuery] string path, CancellationToken ct)
    {
        _logger.LogDebug("Getting file info: {Path}", path);
        try
        {
            var stat = await _provider.StatAsync(path, ct);
            return Ok(stat);
        }
        catch (FileStorageNotFoundError ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (FileStoragePermissionError ex)
        {
            _logger.LogWarning("Permission denied: {Path}", path);
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get file info: {Path}", path);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Downloads a file as a binary stream.
    /// </summary>
    /// <param name="path">The file path to download.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    [HttpGet("download")]
    public async Task<IActionResult> DownloadFile([FromQuery] string path, CancellationToken ct)
    {
        _logger.LogDebug("Downloading file: {Path}", path);
        try
        {
            var stream = await _provider.GetDownloadStreamAsync(path, ct);
            var fileName = Path.GetFileName(path);
            return File(stream, "application/octet-stream", fileName);
        }
        catch (FileStorageNotFoundError ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (FileStoragePermissionError ex)
        {
            _logger.LogWarning("Permission denied: {Path}", path);
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (FileStorageIsADirectoryError ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file: {Path}", path);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Uploads a file via multipart form data.
    /// </summary>
    /// <param name="file">The file to upload.</param>
    /// <param name="path">The destination path on the server.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile(IFormFile file, [FromForm] string path, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { Error = "No file provided." });
        }

        _logger.LogDebug("Uploading file: {Path}, size={Size}", path, file.Length);

        try
        {
            var filePath = string.IsNullOrEmpty(path) ? file.FileName : path;

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var content = ms.ToArray();

            await _provider.UploadFileAsync(filePath, content, ct);

            return Ok(new { Path = filePath, Size = file.Length });
        }
        catch (FileStorageNotFoundError ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (FileStoragePermissionError ex)
        {
            _logger.LogWarning("Permission denied: {Path}", path);
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file: {Path}", path);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Creates a directory at the specified path.
    /// </summary>
    /// <param name="request">The request containing the directory path.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    [HttpPost("mkdir")]
    public async Task<IActionResult> CreateDirectory([FromBody] CreateDirectoryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Path))
        {
            return BadRequest(new { Error = "Path is required." });
        }

        _logger.LogDebug("Creating directory: {Path}", request.Path);

        try
        {
            var exists = await _provider.ExistsAsync(request.Path, ct);
            if (exists)
            {
                return Ok(new { Path = request.Path, Created = false, Message = "Directory already exists." });
            }

            await _provider.MkdirAsync(request.Path, recursive: true, ct);
            return Ok(new { Path = request.Path, Created = true });
        }
        catch (FileStorageExistsError)
        {
            return Ok(new { Path = request.Path, Created = false, Message = "Directory already exists." });
        }
        catch (FileStoragePermissionError ex)
        {
            _logger.LogWarning("Permission denied: {Path}", request.Path);
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create directory: {Path}", request.Path);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a file or directory recursively.
    /// </summary>
    /// <param name="path">The path to delete.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    [HttpDelete("rm")]
    public async Task<IActionResult> Delete([FromQuery] string path, CancellationToken ct)
    {
        _logger.LogDebug("Deleting: {Path}", path);
        try
        {
            var stat = await _provider.StatAsync(path, ct);
            await _provider.RemoveAsync(path, recursive: true, ct);
            return Ok(new { Path = path, Deleted = true, Type = stat.Type });
        }
        catch (FileStorageNotFoundError ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (FileStoragePermissionError ex)
        {
            _logger.LogWarning("Permission denied: {Path}", path);
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete: {Path}", path);
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}

/// <summary>
/// Request body for the create-directory endpoint.
/// </summary>
public class CreateDirectoryRequest
{
    /// <summary>Gets or sets the directory path to create.</summary>
    public string Path { get; set; } = string.Empty;
}
