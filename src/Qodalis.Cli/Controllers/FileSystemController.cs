using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.FileSystem;

namespace Qodalis.Cli.Controllers;

[ApiController]
[Route("api/qcli/fs")]
public class FileSystemController : ControllerBase
{
    private readonly IFileStorageProvider _provider;

    public FileSystemController(IFileStorageProvider provider)
    {
        _provider = provider;
    }

    [HttpGet("ls")]
    public async Task<IActionResult> ListDirectory([FromQuery] string path, CancellationToken ct)
    {
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
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (FileStorageNotADirectoryError ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("cat")]
    public async Task<IActionResult> ReadFile([FromQuery] string path, CancellationToken ct)
    {
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
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (FileStorageIsADirectoryError ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("stat")]
    public async Task<IActionResult> GetFileInfo([FromQuery] string path, CancellationToken ct)
    {
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
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("download")]
    public async Task<IActionResult> DownloadFile([FromQuery] string path, CancellationToken ct)
    {
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
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (FileStorageIsADirectoryError ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile(IFormFile file, [FromForm] string path, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { Error = "No file provided." });
        }

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
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPost("mkdir")]
    public async Task<IActionResult> CreateDirectory([FromBody] CreateDirectoryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Path))
        {
            return BadRequest(new { Error = "Path is required." });
        }

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
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpDelete("rm")]
    public async Task<IActionResult> Delete([FromQuery] string path, CancellationToken ct)
    {
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
            return StatusCode(403, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}

public class CreateDirectoryRequest
{
    public string Path { get; set; } = string.Empty;
}
