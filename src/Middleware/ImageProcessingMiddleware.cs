using CMS.Core;
using CMS.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using SkiaSharp;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Path = System.IO.Path;

namespace XperienceCommunity.ImageProcessing;

public class ImageProcessingMiddleware(RequestDelegate next, IEventLogService eventLogService, IOptions<ImageProcessingOptions>? options)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly IEventLogService _eventLogService = eventLogService ?? throw new ArgumentNullException(nameof(eventLogService));
    private readonly ImageProcessingOptions _options = options?.Value ?? new ImageProcessingOptions();
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

    private readonly string[] _supportedContentTypes =
    [
        "image/jpeg",
        "image/png",
        "image/webp"
    ];

    // High-quality sampling (Mitchell-Netravali cubic). Great for downscaling without ringing.
    private static readonly SKSamplingOptions HQSampling = new(new SKCubicResampler(1f / 3f, 1f / 3f));
    private static readonly SKPaint HQPaint = new()
    {
        IsAntialias = true,
        IsDither = true
    };

    // Simplified policy constants
    private const int RetinaThreshold = 500; // px
    private const double RetinaScale = 2.0; // 2x for retina
    private const int FixedQuality = 96; // Fixed encoder quality for lossy formats

    public async Task InvokeAsync(HttpContext context)
    {
        var originalResponseBodyStream = context.Response.Body;
        var contentType = GetContentTypeFromPath(context.Request.Path);

        using var responseBodyStream = new MemoryStream();
        context.Response.Body = responseBodyStream;

        await _next(context);

        if (!IsSupportedContentType(contentType) && !IsPathToBeProcessed(context.Request.Path, _options))
        {
            await CopyStreamAndRestore(responseBodyStream, originalResponseBodyStream, context);
            return;
        }

        if (IsPathToBeProcessed(context.Request.Path, _options))
        {
            int? width = null;
            int? height = null;
            int? maxSideSize = null;
            string? mode = null;
            string? explicitFormat = null;

            if (context.Request.Query.ContainsKey("width") && int.TryParse(context.Request.Query["width"], out int parsedWidth))
            {
                width = parsedWidth;
            }

            if (context.Request.Query.ContainsKey("mode"))
            {
                mode = context.Request.Query["mode"];
            }
            if (context.Request.Query.ContainsKey("height") && int.TryParse(context.Request.Query["height"], out int parsedHeight))
            {
                height = parsedHeight;
            }

            if (context.Request.Query.ContainsKey("maxSideSize") && int.TryParse(context.Request.Query["maxSideSize"], out int parsedMaxSideSize))
            {
                maxSideSize = parsedMaxSideSize;
            }

            bool explicitFormatRequested = false;
            if (context.Request.Query.ContainsKey("format"))
            {
                explicitFormatRequested = true;
                explicitFormat = context.Request.Query["format"];
            }

            if (width.HasValue || height.HasValue || maxSideSize.HasValue)
            {
                responseBodyStream.Seek(0, SeekOrigin.Begin);
                var originalImageBytes = responseBodyStream.ToArray();

                var result = await ProcessImageAsync(
                    originalImageBytes,
                    width ?? 0,
                    height ?? 0,
                    maxSideSize ?? 0,
                    explicitFormat,
                    contentType,
                    context.Request.Path,
                    mode,
                    explicitFormatRequested
                );

                // Generate ETag: include final target dims, format, mode, quality, and retina flag
                var eTag = GenerateETag(
                    originalImageBytes,
                    result.TargetWidth,
                    result.TargetHeight,
                    result.Format,
                    mode,
                    FixedQuality,
                    result.RetinaApplied
                );

                if (context.Request.Headers.IfNoneMatch == eTag)
                {
                    context.Response.StatusCode = StatusCodes.Status304NotModified;
                    context.Response.Headers.ETag = eTag;
                    context.Response.Body = originalResponseBodyStream;
                    return;
                }

                var filename = $"{Path.GetFileNameWithoutExtension(context.Request.Path)}.{GetFileExtensionByContentType(result.Format)}";

                context.Response.Body = originalResponseBodyStream;
                context.Response.ContentType = result.Format;
                context.Response.Headers.ETag = eTag;
                context.Response.Headers.CacheControl = "public, max-age=31536000";
                context.Response.Headers.ContentLength = result.Bytes.Length;
                context.Response.Headers.ContentDisposition = $"inline; filename={filename}";

                if (context.Response.StatusCode != StatusCodes.Status304NotModified)
                {
                    await context.Response.Body.WriteAsync(result.Bytes);
                }
                return;
            }
        }

        await CopyStreamAndRestore(responseBodyStream, originalResponseBodyStream, context);
    }

    private readonly record struct ProcessedImageResult(byte[] Bytes, string Format, int TargetWidth, int TargetHeight, bool RetinaApplied);

    private async Task<ProcessedImageResult> ProcessImageAsync(
        byte[] imageBytes,
        int width,
        int height,
        int maxSideSize,
        string? requestedFormat,
        string originalContentType,
        string path,
        string? resizeMode,
        bool explicitFormatRequested)
    {
        if (imageBytes.Length == 0 || (!IsSupportedContentType(originalContentType) && !IsPathToBeProcessed(path, _options)))
        {
            return new ProcessedImageResult(imageBytes, requestedFormat ?? "image/webp", 0, 0, false);
        }

        if (width <= 0 && height <= 0 && maxSideSize <= 0 && !explicitFormatRequested)
        {
            // No resize and no format change requested -> default to WebP output without change
            return new ProcessedImageResult(imageBytes, "image/webp", 0, 0, false);
        }

        try
        {
            using var inputStream = new MemoryStream(imageBytes);
            using var originalBitmap = SKBitmap.Decode(inputStream);
            if (originalBitmap == null)
            {
                return new ProcessedImageResult(imageBytes, requestedFormat ?? "image/webp", 0, 0, false);
            }

            // Compute target dims
            var dims = ImageHelper.EnsureImageDimensions(width, height, maxSideSize, originalBitmap.Width, originalBitmap.Height);
            int targetW = dims[0];
            int targetH = dims[1];

            // Apply Retina upscaling if target is small (cap to original to avoid excessive blur)
            bool retinaApplied = false;
            int maxTargetSide = Math.Max(targetW, targetH);
            if (maxTargetSide > 0 && maxTargetSide < RetinaThreshold)
            {
                targetW = (int)Math.Round(targetW * RetinaScale);
                targetH = (int)Math.Round(targetH * RetinaScale);
                targetW = Math.Clamp(targetW, 1, originalBitmap.Width);
                targetH = Math.Clamp(targetH, 1, originalBitmap.Height);
                retinaApplied = true;
            }

            // Fallback to original if no valid size computed
            if (targetW <= 0 || targetH <= 0)
            {
                targetW = originalBitmap.Width;
                targetH = originalBitmap.Height;
            }

            // Choose format: explicit request wins, otherwise always WebP
            string finalFormat = ResolveFormat(requestedFormat, explicitFormatRequested);

            // Perform resize based on mode
            SKBitmap? resizedBitmap = resizeMode switch
            {
                "cover" => ResizeWithCover(originalBitmap, targetW, targetH),
                "crop" => ResizeWithCrop(originalBitmap, targetW, targetH),
                "contain" => ResizeWithContains(originalBitmap, targetW, targetH),
                _ => ResizeBitmap(originalBitmap, targetW, targetH)
            };

            if (resizedBitmap == null)
            {
                return new ProcessedImageResult(imageBytes, finalFormat, targetW, targetH, retinaApplied);
            }

            using var outputStream = new MemoryStream();
            var imageFormat = GetImageFormat(finalFormat);

            // Encode with fixed quality; PNG quality parameter is ignored by encoder but harmless to pass 100
            int quality = imageFormat == SKEncodedImageFormat.Png ? 100 : FixedQuality;

            using (var image = SKImage.FromBitmap(resizedBitmap))
            {
                // Prefer the generic encode path for simplicity
                using var data = image.Encode(imageFormat, quality);
                data.SaveTo(outputStream);
            }

            return new ProcessedImageResult(outputStream.ToArray(), finalFormat, targetW, targetH, retinaApplied);
        }
        catch (Exception ex)
        {
            _eventLogService.LogException(nameof(ImageProcessingMiddleware), nameof(ProcessImageAsync), ex);
            return new ProcessedImageResult(imageBytes, requestedFormat ?? "image/webp", 0, 0, false);
        }
    }

    // High-quality single-pass resize using cubic resampling + dithering.
    private static SKBitmap ResizeBitmap(SKBitmap source, int targetWidth, int targetHeight)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            return source;
        }

        var info = new SKImageInfo(targetWidth, targetHeight, source.ColorType, source.AlphaType, source.ColorSpace);
        var dst = new SKBitmap(info);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(source);
        var srcRect = new SKRect(0, 0, source.Width, source.Height);
        var destRect = new SKRect(0, 0, targetWidth, targetHeight);
        canvas.DrawImage(image, srcRect, destRect, HQSampling, HQPaint);
        canvas.Flush();
        return dst;
    }

    private static SKBitmap ResizeWithCover(SKBitmap original, int targetWidth, int targetHeight)
    {
        // Compute centered crop to match aspect ratio, then scale to target (allow upscaling)
        float targetRatio = (float)targetWidth / targetHeight;
        float srcRatio = (float)original.Width / original.Height;

        int cropWidth, cropHeight;
        if (srcRatio > targetRatio)
        {
            cropHeight = original.Height;
            cropWidth = (int)(cropHeight * targetRatio);
        }
        else
        {
            cropWidth = original.Width;
            cropHeight = (int)(cropWidth / targetRatio);
        }

        int cropX = Math.Max(0, (original.Width - cropWidth) / 2);
        int cropY = Math.Max(0, (original.Height - cropHeight) / 2);

        var info = new SKImageInfo(targetWidth, targetHeight, original.ColorType, original.AlphaType, original.ColorSpace);
        var dst = new SKBitmap(info);
        using var canvas = new SKCanvas(dst);
        using var image = SKImage.FromBitmap(original);
        var srcRect = new SKRect(cropX, cropY, cropX + cropWidth, cropY + cropHeight);
        var destRect = new SKRect(0, 0, targetWidth, targetHeight);
        canvas.DrawImage(image, srcRect, destRect, HQSampling, HQPaint);
        canvas.Flush();
        return dst;
    }

    private static SKBitmap ResizeWithCrop(SKBitmap original, int targetWidth, int targetHeight)
    {
        // Same as cover logic to fill target exactly
        return ResizeWithCover(original, targetWidth, targetHeight);
    }

    private static SKBitmap ResizeWithContains(SKBitmap original, int targetWidth, int targetHeight)
    {
        // Fit within target box while preserving aspect ratio (allow upscaling for retina targets)
        float scale = Math.Min((float)targetWidth / original.Width, (float)targetHeight / original.Height);
        int newWidth = Math.Max(1, (int)Math.Round(original.Width * scale));
        int newHeight = Math.Max(1, (int)Math.Round(original.Height * scale));
        return ResizeBitmap(original, newWidth, newHeight);
    }

    private static string ResolveFormat(string? requestedFormat, bool explicitRequested)
    {
        if (explicitRequested && !string.IsNullOrEmpty(requestedFormat))
        {
            var fmt = requestedFormat;
            if (!fmt.StartsWith("image/")) fmt = $"image/{fmt}";
            if (fmt == "image/jpg") fmt = "image/jpeg";
            return fmt;
        }
        return "image/webp"; // default
    }

    private string GetContentTypeFromPath(PathString path)
    {
        var extension = CMS.IO.Path.GetExtension(path.Value).ToLowerInvariant();

        if (_contentTypeProvider.TryGetContentType(extension, out var contentType))
        {
            return contentType;
        }

        return "application/octet-stream";
    }

    private bool IsSupportedContentType(string contentType)
    {
        return Array.Exists(_supportedContentTypes, ct => ct.Equals(contentType, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPathMediaLibrary(PathString path) => path.StartsWithSegments("/getmedia");
    private static bool IsPathContentItemAsset(PathString path) => path.StartsWithSegments("/getContentAsset");

    private static bool IsPathToBeProcessed(PathString path, ImageProcessingOptions options)
    {
        // Set default values
        var processMediaLibrary = options.ProcessMediaLibrary ??= true;
        var processContentItemAssets = options.ProcessContentItemAssets ??= true;

        if (processMediaLibrary && IsPathMediaLibrary(path))
        {
            return true;
        }

        return processContentItemAssets && IsPathContentItemAsset(path);
    }

    private static SKEncodedImageFormat GetImageFormat(string contentType) => contentType switch
    {
        "image/jpeg" => SKEncodedImageFormat.Jpeg,
        "image/png" => SKEncodedImageFormat.Png,
        "image/webp" => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Webp,
    };

    private static string GetFileExtensionByContentType(string contentType) => contentType switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/webp" => "webp",
        _ => "webp",
    };

    private static string GenerateETag(byte[] imageBytes, int targetWidth, int targetHeight, string format, string? mode, int quality, bool retinaApplied)
    {
        var inputBytes = imageBytes
            .Concat(BitConverter.GetBytes(targetWidth))
            .Concat(BitConverter.GetBytes(targetHeight))
            .Concat(Encoding.UTF8.GetBytes(format))
            .Concat(Encoding.UTF8.GetBytes(mode ?? string.Empty))
            .Concat(BitConverter.GetBytes(quality))
            .Concat(BitConverter.GetBytes(retinaApplied))
            .ToArray();

        var hash = MD5.HashData(inputBytes);
        return Convert.ToBase64String(hash);
    }

    private static async Task CopyStreamAndRestore(MemoryStream responseBodyStream, Stream originalResponseBodyStream, HttpContext context)
    {
        responseBodyStream.Seek(0, SeekOrigin.Begin);
        await responseBodyStream.CopyToAsync(originalResponseBodyStream);
        context.Response.Body = originalResponseBodyStream;
    }
}

public class ImageProcessingOptions
{
    public bool? ProcessMediaLibrary { get; set; } = true;
    public bool? ProcessContentItemAssets { get; set; } = true;
}

public static class ImageProcessingMiddlewareExtensions
{
    /// <summary>
    ///     Add the Image Processing middleware.
    /// </summary>
    /// <param name="builder">The Microsoft.AspNetCore.Builder.IApplicationBuilder to add the middleware to.</param>
    /// <returns></returns>
    public static IApplicationBuilder UseXperienceCommunityImageProcessing(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ImageProcessingMiddleware>();
    }
}

