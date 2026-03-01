using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.FileSystem;

namespace Qodalis.Cli.Controllers;

[ApiController]
[Route("api/cli/fs")]
public class FileSystemController : ControllerBase
{
    private readonly FileSystemPathValidator _pathValidator;

    public FileSystemController(FileSystemPathValidator pathValidator)
    {
        _pathValidator = pathValidator;
    }

    [HttpGet("ls")]
    public IActionResult ListDirectory([FromQuery] string path)
    {
        var resolved = _pathValidator.Validate(path);
        if (resolved is null)
        {
            return StatusCode(403, new { Error = "Access denied: path is not within allowed paths." });
        }

        if (!Directory.Exists(resolved))
        {
            return NotFound(new { Error = $"Directory not found: {path}" });
        }

        try
        {
            var dirInfo = new DirectoryInfo(resolved);
            var entries = dirInfo.EnumerateFileSystemInfos()
                .Select(entry => new
                {
                    Name = entry.Name,
                    Type = entry is DirectoryInfo ? "directory" : "file",
                    Size = entry is FileInfo fi ? fi.Length : 0L,
                    Modified = entry.LastWriteTimeUtc,
                    Permissions = GetPermissions(entry),
                })
                .ToList();

            return Ok(new { Entries = entries });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { Error = "Access denied by the operating system." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("cat")]
    public IActionResult ReadFile([FromQuery] string path)
    {
        var resolved = _pathValidator.Validate(path);
        if (resolved is null)
        {
            return StatusCode(403, new { Error = "Access denied: path is not within allowed paths." });
        }

        if (!System.IO.File.Exists(resolved))
        {
            return NotFound(new { Error = $"File not found: {path}" });
        }

        try
        {
            var content = System.IO.File.ReadAllText(resolved);
            return Ok(new { Content = content });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { Error = "Access denied by the operating system." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("stat")]
    public IActionResult GetFileInfo([FromQuery] string path)
    {
        var resolved = _pathValidator.Validate(path);
        if (resolved is null)
        {
            return StatusCode(403, new { Error = "Access denied: path is not within allowed paths." });
        }

        try
        {
            if (System.IO.File.Exists(resolved))
            {
                var fi = new FileInfo(resolved);
                return Ok(new
                {
                    Name = fi.Name,
                    Type = "file",
                    Size = fi.Length,
                    Modified = fi.LastWriteTimeUtc,
                    Created = fi.CreationTimeUtc,
                    Permissions = GetPermissions(fi),
                });
            }

            if (Directory.Exists(resolved))
            {
                var di = new DirectoryInfo(resolved);
                return Ok(new
                {
                    Name = di.Name,
                    Type = "directory",
                    Size = 0L,
                    Modified = di.LastWriteTimeUtc,
                    Created = di.CreationTimeUtc,
                    Permissions = GetPermissions(di),
                });
            }

            return NotFound(new { Error = $"Path not found: {path}" });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { Error = "Access denied by the operating system." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("download")]
    public IActionResult DownloadFile([FromQuery] string path)
    {
        var resolved = _pathValidator.Validate(path);
        if (resolved is null)
        {
            return StatusCode(403, new { Error = "Access denied: path is not within allowed paths." });
        }

        if (!System.IO.File.Exists(resolved))
        {
            return NotFound(new { Error = $"File not found: {path}" });
        }

        try
        {
            var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileName = Path.GetFileName(resolved);
            return File(stream, "application/octet-stream", fileName);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { Error = "Access denied by the operating system." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile(IFormFile file, [FromForm] string path)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { Error = "No file provided." });
        }

        var targetDir = _pathValidator.Validate(path);
        if (targetDir is null)
        {
            return StatusCode(403, new { Error = "Access denied: path is not within allowed paths." });
        }

        if (!Directory.Exists(targetDir))
        {
            return NotFound(new { Error = $"Target directory not found: {path}" });
        }

        try
        {
            var filePath = Path.Combine(targetDir, file.FileName);

            // Re-validate the final file path to prevent escaping via filename
            var resolvedFilePath = _pathValidator.Validate(filePath);
            if (resolvedFilePath is null)
            {
                return StatusCode(403, new { Error = "Access denied: resulting file path is not within allowed paths." });
            }

            await using var stream = new FileStream(resolvedFilePath, FileMode.Create, FileAccess.Write);
            await file.CopyToAsync(stream);

            return Ok(new { Path = resolvedFilePath, Size = file.Length });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { Error = "Access denied by the operating system." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPost("mkdir")]
    public IActionResult CreateDirectory([FromBody] CreateDirectoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Path))
        {
            return BadRequest(new { Error = "Path is required." });
        }

        var resolved = _pathValidator.Validate(request.Path);
        if (resolved is null)
        {
            return StatusCode(403, new { Error = "Access denied: path is not within allowed paths." });
        }

        try
        {
            if (Directory.Exists(resolved))
            {
                return Ok(new { Path = resolved, Created = false, Message = "Directory already exists." });
            }

            Directory.CreateDirectory(resolved);
            return Ok(new { Path = resolved, Created = true });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { Error = "Access denied by the operating system." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpDelete("rm")]
    public IActionResult Delete([FromQuery] string path)
    {
        var resolved = _pathValidator.Validate(path);
        if (resolved is null)
        {
            return StatusCode(403, new { Error = "Access denied: path is not within allowed paths." });
        }

        try
        {
            if (System.IO.File.Exists(resolved))
            {
                System.IO.File.Delete(resolved);
                return Ok(new { Path = resolved, Deleted = true, Type = "file" });
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
                return Ok(new { Path = resolved, Deleted = true, Type = "directory" });
            }

            return NotFound(new { Error = $"Path not found: {path}" });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { Error = "Access denied by the operating system." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    private static string GetPermissions(FileSystemInfo entry)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                return entry.UnixFileMode.ToString();
            }
            catch
            {
                // Fall through to attribute-based permissions
            }
        }

        return entry.Attributes.ToString();
    }
}

public class CreateDirectoryRequest
{
    public string Path { get; set; } = string.Empty;
}
