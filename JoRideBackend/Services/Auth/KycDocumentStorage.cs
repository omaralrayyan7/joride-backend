namespace JoRideBackend.Services.Auth
{
    /// <summary>
    /// Stores KYC document files on disk OUTSIDE wwwroot — app.UseStaticFiles() only serves
    /// wwwroot, so nothing in this directory has a public URL at all, ever. The only way to
    /// read a file back is KycController's signed, time-limited download link
    /// (KycSigningService), never a direct path.
    /// </summary>
    public class KycDocumentStorage
    {
        private readonly string _rootPath;

        public KycDocumentStorage(IWebHostEnvironment env, IConfiguration configuration)
        {
            var configured = configuration["Kyc:StorageRoot"];
            _rootPath = !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Path.Combine(env.ContentRootPath, "kyc-storage");

            Directory.CreateDirectory(_rootPath);
        }

        /// <summary>Saves the stream under a per-user subfolder with a random file name (never the
        /// user-supplied one — avoids path traversal and name collisions). Returns the relative
        /// storage path recorded on the KycDocument row.</summary>
        public async Task<string> SaveAsync(int userId, Stream content, string originalFileName, CancellationToken ct = default)
        {
            var extension = Path.GetExtension(originalFileName);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10)
            {
                extension = ".bin";
            }

            var relativePath = Path.Combine(userId.ToString(), $"{Guid.NewGuid():N}{extension}");
            var fullPath = Path.Combine(_rootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            await using var fileStream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write);
            await content.CopyToAsync(fileStream, ct);

            return relativePath.Replace('\\', '/');
        }

        /// <summary>Opens a document for reading by its stored relative path. Throws if the
        /// resolved path would escape the storage root (defense in depth against a corrupted
        /// or tampered StoragePath value).</summary>
        public Stream OpenRead(string relativePath)
        {
            var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
            var rootFull = Path.GetFullPath(_rootPath);
            if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Resolved KYC document path escapes the storage root.");
            }

            return new FileStream(fullPath, FileMode.Open, FileAccess.Read);
        }
    }
}
