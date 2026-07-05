using System.Net.Http.Headers;

namespace Sarab.Api.Services;

public sealed record AvatarImageInfo(int Width, int Height, bool HasAlpha);

public interface IAvatarAssetStorage
{
    Task<string> SaveAsync(IFormFile file, CancellationToken cancellationToken = default);
}

public sealed class AvatarAssetStorage(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IHttpClientFactory httpClientFactory) : IAvatarAssetStorage
{
    private const long MaxBytes = 5 * 1024 * 1024;

    public async Task<string> SaveAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file.Length is <= 0 or > MaxBytes)
        {
            throw new InvalidOperationException("Avatar PNG must be between 1 byte and 5 MB.");
        }

        if (!file.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && file.ContentType != "image/png")
        {
            throw new InvalidOperationException("Avatar part must be a PNG.");
        }

        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        var imageInfo = AvatarPngInspector.Inspect(bytes);
        if (imageInfo.Width != 1024 || imageInfo.Height != 1024)
        {
            throw new InvalidOperationException("Avatar part PNG must be exactly 1024x1024.");
        }

        if (!imageInfo.HasAlpha)
        {
            throw new InvalidOperationException("Avatar part PNG must include transparency.");
        }

        var supabaseUrl = configuration["Supabase:Url"] ?? configuration["VITE_SUPABASE_URL"];
        var serviceRoleKey = configuration["Supabase:ServiceRoleKey"];
        var bucket = configuration["Supabase:AvatarBucket"] ?? "avatar-parts";
        var safeName = $"{Guid.NewGuid():N}.png";

        if (!string.IsNullOrWhiteSpace(supabaseUrl) && !string.IsNullOrWhiteSpace(serviceRoleKey))
        {
            var baseUrl = supabaseUrl.TrimEnd('/');
            var uploadUrl = $"{baseUrl}/storage/v1/object/{bucket}/{safeName}";
            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceRoleKey);
            request.Headers.TryAddWithoutValidation("apikey", serviceRoleKey);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Supabase Storage upload failed: {detail}");
            }

            return $"{baseUrl}/storage/v1/object/public/{bucket}/{safeName}";
        }

        var webRoot = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
        }

        var folder = Path.Combine(webRoot, "uploads", "avatar-parts");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, safeName);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return $"/uploads/avatar-parts/{safeName}";
    }
}

public static class AvatarPngInspector
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static AvatarImageInfo Inspect(byte[] bytes)
    {
        if (bytes.Length < 33 || !Signature.SequenceEqual(bytes.Take(Signature.Length)))
        {
            throw new InvalidOperationException("Avatar part must be a valid PNG.");
        }

        var width = ReadInt32(bytes, 16);
        var height = ReadInt32(bytes, 20);
        var colorType = bytes[25];
        var hasAlpha = colorType is 4 or 6 || HasTransparencyChunk(bytes);
        return new AvatarImageInfo(width, height, hasAlpha);
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24)
            | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }

    private static bool HasTransparencyChunk(byte[] bytes)
    {
        var offset = 8;
        while (offset + 12 <= bytes.Length)
        {
            var length = ReadInt32(bytes, offset);
            if (length < 0 || offset + 12 + length > bytes.Length)
            {
                return false;
            }

            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            if (type == "tRNS")
            {
                return true;
            }

            offset += 12 + length;
        }

        return false;
    }
}
