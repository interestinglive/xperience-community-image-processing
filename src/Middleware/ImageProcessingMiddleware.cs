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

                // Generate ETag (now includes resize mode)
                var eTag = GenerateETag(originalImageBytes, width ?? 0, height ?? 0, maxSideSize ?? 0, format, mode);

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
                resizedBitmap = ResizeBitmap(originalBitmap, newDims[0], newDims[1]);
            }

            if (resizedBitmap == null)
            {
                // _eventLogService.LogWarning(nameof(ImageProcessingMiddleware), nameof(ProcessImageAsync), "Failed to resize image.");
                return imageBytes;
            }

            // Optional gentle sharpen only when downscaling to improve perceived detail.
            bool isDownscaled = resizedBitmap.Width < originalBitmap.Width || resizedBitmap.Height < originalBitmap.Height;
            if (isDownscaled && _options.SharpenDownscaledImages && _options.SharpenAmount > 0f)
            {
                var sharpened = ApplySharpen(resizedBitmap, _options.SharpenAmount);
                if (!ReferenceEquals(sharpened, resizedBitmap))
                {
                    resizedBitmap.Dispose();
                    resizedBitmap = sharpened;
                }
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

    private static SKBitmap CropBitmap(SKBitmap source, SKRectI crop)
    {
        var dst = new SKBitmap(new SKImageInfo(crop.Width, crop.Height, source.ColorType, source.AlphaType, source.ColorSpace));
        using var canvas = new SKCanvas(dst);
        using var image = SKImage.FromBitmap(source);
        var srcRect = new SKRect(crop.Left, crop.Top, crop.Right, crop.Bottom);
        var destRect = new SKRect(0, 0, crop.Width, crop.Height);
        // 1:1 crop, no resampling needed, but keep dither on
        canvas.DrawImage(image, srcRect, destRect, HQSampling, HQPaint);
        canvas.Flush();
        return dst;
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
            return original;
        }

        float originalRatio = (float)original.Width / original.Height;
        float targetRatio = (float)targetWidth / targetHeight;

        int cropWidth, cropHeight;
        if (originalRatio > targetRatio)
        {
            cropHeight = original.Height;
            cropWidth = (int)(cropHeight * targetRatio);
        }
        else
        {
            cropWidth = original.Width;
            cropHeight = (int)(cropWidth / targetRatio);
        }

        int cropX = (original.Width - cropWidth) / 2;
        int cropY = (original.Height - cropHeight) / 2;

        // Avoid upscaling: if requested size exceeds original, return a centered crop only.
        if (targetWidth > original.Width || targetHeight > original.Height)
        {
            return CropBitmap(original, new SKRectI(cropX, cropY, cropX + cropWidth, cropY + cropHeight));
        }

        // One-pass crop+scale to target size
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
            return original;
        }

        // Compute centered source rect that matches target aspect ratio (cover logic),
        // then do a single high-quality resample to the exact target size.
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

        int cropX = (original.Width - cropWidth) / 2;
        int cropY = (original.Height - cropHeight) / 2;

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
            return original;
        }

        // Calculate scale to fit within target dimensions while preserving the aspect ratio.
        float scale = Math.Min((float)targetWidth / original.Width, (float)targetHeight / original.Height);

        // Do not upscale in contain mode.
        if (scale >= 1f)
        {
            return original;
        }

        int newWidth = (int)Math.Floor(original.Width * scale);
        int newHeight = (int)Math.Floor(original.Height * scale);

        var info = new SKImageInfo(newWidth, newHeight, original.ColorType, original.AlphaType, original.ColorSpace);
        var dst = new SKBitmap(info);
        using var canvas = new SKCanvas(dst);
        using var image = SKImage.FromBitmap(original);
        var srcRect = new SKRect(0, 0, original.Width, original.Height);
        var destRect = new SKRect(0, 0, newWidth, newHeight);
        canvas.DrawImage(image, srcRect, destRect, HQSampling, HQPaint);
        canvas.Flush();
        return dst;
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

    private static string GenerateETag(byte[] imageBytes, int width, int height, int maxSideSize, string format, string? mode)
    {
        var inputBytes = imageBytes
            .Concat(BitConverter.GetBytes(width))
            .Concat(BitConverter.GetBytes(height))
            .Concat(BitConverter.GetBytes(maxSideSize))
            .Concat(Encoding.UTF8.GetBytes(format))
            .Concat(Encoding.UTF8.GetBytes(mode ?? string.Empty))
            .ToArray();

        var hash = MD5.HashData(inputBytes);
        return Convert.ToBase64String(hash);
    }

    private static SKBitmap ApplySharpen(SKBitmap source, float amount)
    {
        // Simple unsharp-like 3x3 kernel:
        // [ 0, -a,  0 ]
        // [ -a, 1+4a, -a ]
        // [ 0, -a,  0 ]
        var a = Math.Clamp(amount, 0f, 1f);
        var center = 1f + 4f * a;
        var kernel = new float[]
        {
            0f, -a, 0f,
            -a, center, -a,
            0f, -a, 0f
        };

        var info = new SKImageInfo(source.Width, source.Height, source.ColorType, source.AlphaType, source.ColorSpace);
        var dst = new SKBitmap(info);
        using var canvas = new SKCanvas(dst);
        using var image = SKImage.FromBitmap(source);
        using var filter = SKImageFilter.CreateMatrixConvolution(new SKSizeI(3, 3), kernel, 1f, 0f, new SKPointI(1, 1), SKShaderTileMode.Clamp, true);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(image, 0, 0, paint);
        canvas.Flush();
        return dst;
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

    // Post-resize sharpening for downscaled images.
    public bool SharpenDownscaledImages { get; set; } = true;

    // 0..1 typical. 0.1–0.2 is subtle; higher may introduce halos.
    public float SharpenAmount { get; set; } = 0.15f;
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

