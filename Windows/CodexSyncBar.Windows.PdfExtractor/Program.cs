using System.Text.Json;
using System.Text.Json.Serialization;
using UglyToad.PdfPig;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using WindowsPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace CodexSyncBar_Windows_PdfExtractor;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var detail = ParseDetail(args);
            var data = await ReadInputAsync(Console.OpenStandardInput());
            var result = await PdfExtractor.ExtractAsync(data, detail);
            var output = JsonSerializer.SerializeToUtf8Bytes(
                result,
                new JsonSerializerOptions { WriteIndented = false });

            if (output.Length > PdfExtractor.MaximumOutputBytes)
            {
                throw new PdfExtractionException("PDF extraction output is too large.");
            }

            await Console.OpenStandardOutput().WriteAsync(output);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static PdfRenderDetail ParseDetail(string[] args)
    {
        var value = "auto";
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--detail", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new PdfExtractionException("Missing PDF render detail.");
                }

                value = args[++index];
                continue;
            }

            throw new PdfExtractionException("Unknown PDF extractor argument.");
        }

        return value.ToLowerInvariant() switch
        {
            "low" => PdfRenderDetail.Low,
            "auto" => PdfRenderDetail.Auto,
            "high" => PdfRenderDetail.High,
            _ => throw new PdfExtractionException("Invalid PDF render detail."),
        };
    }

    private static async Task<byte[]> ReadInputAsync(Stream input)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;

            if (output.Length + read > PdfExtractor.MaximumInputBytes)
            {
                throw new PdfExtractionException("PDF input is too large.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}

internal enum PdfRenderDetail
{
    Low,
    Auto,
    High,
}

internal sealed class PdfExtractionException(string message) : Exception(message);

internal sealed class PdfExtractionResult
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("page_count")]
    public required int PageCount { get; init; }

    [JsonPropertyName("pages")]
    public required IReadOnlyList<PdfExtractedPage> Pages { get; init; }
}

internal sealed class PdfExtractedPage
{
    [JsonPropertyName("page")]
    public required int Page { get; init; }

    [JsonPropertyName("mime_type")]
    public string MimeType { get; init; } = "image/png";

    [JsonPropertyName("data")]
    public required string Data { get; init; }
}

internal static class PdfExtractor
{
    public const int MaximumInputBytes = 12 * 1024 * 1024;
    public const int MaximumPages = 16;
    public const int MaximumTextBytes = 2 * 1024 * 1024;
    public const int MaximumImageBytes = 8 * 1024 * 1024;
    public const int MaximumTotalImageBytes = 24 * 1024 * 1024;
    public const int MaximumOutputBytes = 36 * 1024 * 1024;

    public static async Task<PdfExtractionResult> ExtractAsync(
        byte[] data,
        PdfRenderDetail detail,
        CancellationToken cancellationToken = default)
    {
        if (data.Length > MaximumInputBytes)
        {
            throw new PdfExtractionException("PDF input is too large.");
        }

        var pageTexts = ReadText(data, out var pageCount);
        if (pageCount is <= 0 or > MaximumPages)
        {
            throw new PdfExtractionException("PDF page count is outside the supported range.");
        }

        var totalTextBytes = pageTexts.Sum(text => System.Text.Encoding.UTF8.GetByteCount(text))
            + Math.Max(0, pageTexts.Count - 1) * 2;
        if (totalTextBytes > MaximumTextBytes)
        {
            throw new PdfExtractionException("Extracted PDF text is too large.");
        }

        using var documentStream = await CreateRandomAccessStreamAsync(data, cancellationToken);
        WindowsPdfDocument document;
        try
        {
            document = await WindowsPdfDocument.LoadFromStreamAsync(documentStream);
        }
        catch (Exception error)
        {
            throw new PdfExtractionException($"PDF could not be opened: {error.Message}");
        }

        if (document.PageCount != pageCount || document.PageCount <= 0 || document.PageCount > MaximumPages)
            {
                throw new PdfExtractionException("PDF page count is invalid.");
            }

            var pages = new List<PdfExtractedPage>(checked((int)document.PageCount));
            var totalImageBytes = 0;
            for (uint index = 0; index < document.PageCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var page = document.GetPage(index);
                var image = await RenderPageAsync(page, detail, cancellationToken);
                if (image.Length > MaximumImageBytes)
                {
                    throw new PdfExtractionException("A rendered PDF page is too large.");
                }

                totalImageBytes += image.Length;
                if (totalImageBytes > MaximumTotalImageBytes)
                {
                    throw new PdfExtractionException("Rendered PDF pages are too large.");
                }

                pages.Add(new PdfExtractedPage
                {
                    Page = checked((int)index + 1),
                    Data = Convert.ToBase64String(image),
                });
            }

        return new PdfExtractionResult
        {
            Text = string.Join("\n\n", pageTexts),
            PageCount = pageCount,
            Pages = pages,
        };
    }

    private static List<string> ReadText(byte[] data, out int pageCount)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var document = UglyToad.PdfPig.PdfDocument.Open(stream);
            pageCount = document.NumberOfPages;
            return document.GetPages().Select(page => page.Text ?? string.Empty).ToList();
        }
        catch (Exception error)
        {
            throw new PdfExtractionException($"PDF text could not be read: {error.Message}");
        }
    }

    private static async Task<InMemoryRandomAccessStream> CreateRandomAccessStreamAsync(
        byte[] data,
        CancellationToken cancellationToken)
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using var writer = new DataWriter(stream.GetOutputStreamAt(0));
            writer.WriteBytes(data);
            await writer.StoreAsync().AsTask(cancellationToken);
            await writer.FlushAsync().AsTask(cancellationToken);
            writer.DetachStream();
            stream.Seek(0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> RenderPageAsync(
        PdfPage page,
        PdfRenderDetail detail,
        CancellationToken cancellationToken)
    {
        var size = page.Size;
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
        {
            throw new PdfExtractionException("PDF page has invalid dimensions.");
        }

        var maximumLongEdge = detail == PdfRenderDetail.Low ? 1_024 : 1_600;
        var scale = maximumLongEdge / Math.Max(size.Width, size.Height);
        var width = checked((uint)Math.Max(1, Math.Floor(size.Width * scale)));
        var height = checked((uint)Math.Max(1, Math.Floor(size.Height * scale)));

        using var stream = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height,
        };
        await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken);
        stream.Seek(0);

        if (stream.Size is <= 0 or > int.MaxValue)
        {
            throw new PdfExtractionException("PDF page rendering produced invalid output.");
        }

        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)bytes.Length).AsTask(cancellationToken);
        reader.ReadBytes(bytes);
        if (bytes.Length < 8 ||
            bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47 ||
            bytes[4] != 0x0D || bytes[5] != 0x0A || bytes[6] != 0x1A || bytes[7] != 0x0A)
        {
            throw new PdfExtractionException("PDF page rendering did not produce a PNG.");
        }

        return bytes;
    }
}
