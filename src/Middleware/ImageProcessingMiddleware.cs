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
            var format = contentType;

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

            if (context.Request.Query.ContainsKey("format"))
            {
                string? formatParsed = context.Request.Query["format"];

                if (!string.IsNullOrEmpty(formatParsed))
                {
                    if (!formatParsed.StartsWith("image/")) formatParsed = $"image/{formatParsed}";
                    if (formatParsed == "image/jpg") formatParsed = "image/jpeg";
                    if (IsSupportedContentType(formatParsed)) format = formatParsed;
                }
            }

            if (width.HasValue || height.HasValue || maxSideSize.HasValue)
            {
                responseBodyStream.Seek(0, SeekOrigin.Begin);
                var originalImageBytes = responseBodyStream.ToArray();

                // Generate ETag
                var eTag = GenerateETag(originalImageBytes, width ?? 0, height ?? 0, maxSideSize ?? 0, format);

                // Check if the ETag matches the client's If-None-Match header
                if (context.Request.Headers.IfNoneMatch == eTag)
                {
                    context.Response.StatusCode = StatusCodes.Status304NotModified;
                    context.Response.Headers.ETag = eTag;
                    context.Response.Body = originalResponseBodyStream;
                    return;
                }

                var processedImageBytes = await ProcessImageAsync(originalImageBytes, width ?? 0, height ?? 0, maxSideSize ?? 0, format, contentType, context.Request.Path, mode);

                var filename = $"{Path.GetFileNameWithoutExtension(context.Request.Path)}.{GetFileExtensionByContentType(format)}";

                context.Response.Body = originalResponseBodyStream;
                context.Response.ContentType = format;
                context.Response.Headers.ETag = eTag;
                context.Response.Headers.CacheControl = "public, max-age=31536000";
                context.Response.Headers.ContentLength = processedImageBytes.Length;
                context.Response.Headers.ContentDisposition = $"inline; filename={filename}";

                if (context.Response.StatusCode != StatusCodes.Status304NotModified)
                {
                    await context.Response.Body.WriteAsync(processedImageBytes);
                }
                return;
            }
        }

        await CopyStreamAndRestore(responseBodyStream, originalResponseBodyStream, context);
    }

    private async Task<byte[]> ProcessImageAsync(byte[] imageBytes, int width, int height, int maxSideSize, string format, string contentType, string path, string? resizeMode)
    {
        if (imageBytes.Length == 0 || (!IsSupportedContentType(contentType) && !IsPathToBeProcessed(path, _options)))
        {
            return imageBytes;
        }

        if (width <= 0 && height <= 0 && maxSideSize <= 0 && format == contentType)
        {
            return imageBytes;
        }

        try
        {
            using var inputStream = new MemoryStream(imageBytes);
            using var originalBitmap = SKBitmap.Decode(inputStream);
            if (originalBitmap == null)
            {
                //_eventLogService.LogWarning(nameof(ImageProcessingMiddleware), nameof(ProcessImageAsync), "Failed to decode image.");
                return imageBytes;
            }

            SKBitmap? resizedBitmap = null;
            var newDims = ImageHelper.EnsureImageDimensions(width, height, maxSideSize, originalBitmap.Width, originalBitmap.Height);

            if (resizeMode == "cover")
            {
                resizedBitmap = ResizeWithCover(originalBitmap, width, height);
            }
            else if (resizeMode == "crop")
            {
                resizedBitmap = ResizeWithCrop(originalBitmap, width, height);
            }
            else if (resizeMode == "contain")
            {
                resizedBitmap = ResizeWithContains(originalBitmap, width, height);
            }
            else
            {
                resizedBitmap = originalBitmap.Resize(new SKImageInfo(newDims[0], newDims[1]), SKFilterQuality.High);
            }

            if (resizedBitmap == null)
            {
                // _eventLogService.LogWarning(nameof(ImageProcessingMiddleware), nameof(ProcessImageAsync), "Failed to resize image.");
                return imageBytes;
            }

            using var outputStream = new MemoryStream();
            var imageFormat = GetImageFormat(format);

            // Choose a higher encoder quality for lossy formats to reduce pixelation.
            var quality = imageFormat switch
            {
                SKEncodedImageFormat.Jpeg => _options.EncoderQuality,
                SKEncodedImageFormat.Webp => _options.EncoderQuality,
                SKEncodedImageFormat.Png => 100, // quality parameter is not used the same way for PNG
                _ => _options.EncoderQuality
            };

            using (var encoded = resizedBitmap.Encode(imageFormat, quality))
            {
                encoded.SaveTo(outputStream);
            }

            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            _eventLogService.LogException(nameof(ImageProcessingMiddleware), nameof(ProcessImageAsync), ex);
            return imageBytes;
        }
    }
    private SKBitmap ResizeWithCover(SKBitmap original, int targetWidth, int targetHeight)
    {
        // Calculate missing dimension if needed.
        if (targetWidth <= 0 && targetHeight > 0)
        {
            targetWidth = (int)Math.Round((double)original.Width * targetHeight / original.Height);
        }
        else if (targetHeight <= 0 && targetWidth > 0)
        {
            targetHeight = (int)Math.Round((double)original.Height * targetWidth / original.Width);
        }
        else if (targetWidth <= 0 && targetHeight <= 0)
        {
            // No dimensions provided; return original image.
            return original;
        }

        float originalRatio = (float)original.Width / original.Height;
        float targetRatio = (float)targetWidth / targetHeight;
        int cropWidth, cropHeight;

        // When the original is smaller than requested, avoid upscaling.
        if (original.Width < targetWidth || original.Height < targetHeight)
        {
            if (originalRatio > targetRatio)
            {
                // Wider relative to target: use full height.
                cropHeight = original.Height;
                cropWidth = (int)(cropHeight * targetRatio);
            }
            else
            {
                // Taller relative to target: use full width.
                cropWidth = original.Width;
                cropHeight = (int)(cropWidth / targetRatio);
            }
            cropWidth = Math.Min(cropWidth, original.Width);
            cropHeight = Math.Min(cropHeight, original.Height);
        }
        else
        {
            // For larger images, perform a cover crop.
            if (originalRatio > targetRatio)
            {
                cropWidth = (int)(original.Height * targetRatio);
                cropHeight = original.Height;
            }
            else
            {
                cropHeight = (int)(original.Width / targetRatio);
                cropWidth = original.Width;
            }
        }

        // Center cropping.
        int cropX = (original.Width - cropWidth) / 2;
        int cropY = (original.Height - cropHeight) / 2;
        SKBitmap croppedBitmap = new SKBitmap(cropWidth, cropHeight);
        using (var canvas = new SKCanvas(croppedBitmap))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.DrawBitmap(
                original,
                new SKRect(cropX, cropY, cropX + cropWidth, cropY + cropHeight),
                new SKRect(0, 0, cropWidth, cropHeight),
                paint);
        }

        // If original is smaller than target, return cropped image without upscaling.
        if (targetWidth > original.Width || targetHeight > original.Height)
        {
            return croppedBitmap;
        }

        // Use SKSamplingOptions.High instead of SKFilterQuality.High
        SKBitmap resizedBitmap = croppedBitmap.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        if (resizedBitmap != null)
        {
            croppedBitmap.Dispose();
            return resizedBitmap;
        }
        else
        {
            return croppedBitmap;
        }
    }

    private SKBitmap ResizeWithCrop(SKBitmap original, int targetWidth, int targetHeight)
    {
        // Calculate missing dimension if needed.
        if (targetWidth <= 0 && targetHeight > 0)
        {
            targetWidth = (int)Math.Round((double)original.Width * targetHeight / original.Height);
        }
        else if (targetHeight <= 0 && targetWidth > 0)
        {
            targetHeight = (int)Math.Round((double)original.Height * targetWidth / original.Width);
        }
        else if (targetWidth <= 0 && targetHeight <= 0)
        {
            // No dimensions provided; return original image.
            return original;
        }

        // Scale so that the image covers the target area.
        float scale = Math.Max((float)targetWidth / original.Width, (float)targetHeight / original.Height);
        int newWidth = (int)(original.Width * scale);
        int newHeight = (int)(original.Height * scale);

        // Use SKSamplingOptions.High instead of SKFilterQuality.High
        using var resized = original.Resize(new SKImageInfo(newWidth, newHeight), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        if (resized == null)
        {
            return original; // Fallback if resizing fails.
        }

        int cropX = (newWidth - targetWidth) / 2;
        int cropY = (newHeight - targetHeight) / 2;

        var croppedImage = new SKBitmap(targetWidth, targetHeight);
        using (var canvas = new SKCanvas(croppedImage))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            var sourceRect = new SKRect(cropX, cropY, cropX + targetWidth, cropY + targetHeight);
            var destRect = new SKRect(0, 0, targetWidth, targetHeight);
            canvas.DrawBitmap(resized, sourceRect, destRect, paint);
        }

        return croppedImage;
    }

    private SKBitmap ResizeWithContains(SKBitmap original, int targetWidth, int targetHeight)
    {
        // Calculate missing dimension if needed.
        if (targetWidth <= 0 && targetHeight > 0)
        {
            targetWidth = (int)Math.Round((double)original.Width * targetHeight / original.Height);
        }
        else if (targetHeight <= 0 && targetWidth > 0)
        {
            targetHeight = (int)Math.Round((double)original.Height * targetWidth / original.Width);
        }
        else if (targetWidth <= 0 && targetHeight <= 0)
        {
            // No dimensions provided; return original image.
            return original;
        }

        // Calculate scale to fit within target dimensions while preserving the aspect ratio.
        float scale = Math.Min((float)targetWidth / original.Width, (float)targetHeight / original.Height);

        // Do not upscale in contain mode.
        if (scale >= 1f)
        {
            return original;
        }

        int newWidth = (int)(original.Width * scale);
        int newHeight = (int)(original.Height * scale);

        // Use SKSamplingOptions.High instead of SKFilterQuality.High
        SKBitmap resizedBitmap = original.Resize(new SKImageInfo(newWidth, newHeight), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        return resizedBitmap ?? original;
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

    private static string GenerateETag(byte[] imageBytes, int width, int height, int maxSideSize, string format)
    {
        var inputBytes = imageBytes
            .Concat(BitConverter.GetBytes(width))
            .Concat(BitConverter.GetBytes(height))
            .Concat(BitConverter.GetBytes(maxSideSize))
            .Concat(Encoding.UTF8.GetBytes(format))
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

    // Encoder quality for lossy formats (JPEG/WEBP). Higher values reduce artifacts.
    public int EncoderQuality { get; set; } = 90;
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