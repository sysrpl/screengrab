using screengrab.Helpers;

namespace screengrab.Models;

/// <summary>Where "Cloud" uploads go: the AWS keys, and the bucket and folder in it.</summary>
public sealed class S3Settings
{
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";

    /// <summary>Used to list buckets, and for uploads if the bucket's own region can't be looked up.</summary>
    public string Region { get; set; } = AwsRegions.Default;

    public string Bucket { get; set; } = "";

    /// <summary>The folder (key prefix) in the bucket, without slashes at either end; empty for the top.</summary>
    public string Folder { get; set; } = "";

    /// <summary>The last format uploaded: ".png", ".jpg" or ".gif".</summary>
    public string Format { get; set; } = ".png";

    public bool IsComplete =>
        AccessKeyId.Length > 0 && SecretAccessKey.Length > 0 && Bucket.Length > 0;
}
