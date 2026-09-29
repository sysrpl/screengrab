namespace screengrab.Models;

/// <summary>A file uploaded to S3: where it is, so it can be deleted, and its link.</summary>
public sealed record S3Upload(string Bucket, string Key, string Url);
