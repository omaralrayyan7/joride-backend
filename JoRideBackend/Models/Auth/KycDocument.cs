namespace JoRideBackend.Models.Auth
{
    public enum KycDocumentType
    {
        DrivingLicense,
        NationalId,
    }

    /// <summary>
    /// Metadata for one uploaded KYC document. The file itself is never stored under
    /// wwwroot (which app.UseStaticFiles() serves publicly) — StoragePath points into a
    /// private directory outside the web root; see KycDocumentStorage. There is no public
    /// URL to this file at all — the only way to read it is
    /// KycController's signed, time-limited download link.
    /// </summary>
    public class KycDocument
    {
        public Guid Id { get; set; }
        public int UserId { get; set; }
        public KycDocumentType DocumentType { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public string StoragePath { get; set; } = string.Empty;
        public DateTime UploadedAt { get; set; }
    }
}
