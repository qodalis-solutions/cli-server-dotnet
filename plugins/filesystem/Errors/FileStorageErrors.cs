namespace Qodalis.Cli.FileSystem;

public class FileStorageNotFoundError : Exception
{
    public string Path { get; }
    public FileStorageNotFoundError(string path) : base($"Path not found: {path}") { Path = path; }
}

public class FileStoragePermissionError : Exception
{
    public string Path { get; }
    public FileStoragePermissionError(string path) : base($"Access denied: {path}") { Path = path; }
}

public class FileStorageExistsError : Exception
{
    public string Path { get; }
    public FileStorageExistsError(string path) : base($"Path already exists: {path}") { Path = path; }
}

public class FileStorageNotADirectoryError : Exception
{
    public string Path { get; }
    public FileStorageNotADirectoryError(string path) : base($"Not a directory: {path}") { Path = path; }
}

public class FileStorageIsADirectoryError : Exception
{
    public string Path { get; }
    public FileStorageIsADirectoryError(string path) : base($"Is a directory: {path}") { Path = path; }
}
