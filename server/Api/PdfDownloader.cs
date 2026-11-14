using System.Net;
using System.Net.Sockets;

namespace Examinator.Api;

public interface IPdfDownloader
{
    Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken);
}

public static class ImportUrlPolicy
{
    public const int MaximumRedirects = 3;
    public const int MaximumUrlLength = 2048;

    public static Uri Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumUrlLength ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            throw new ImportProblem(400, "Geef een geldige https-link naar een PDF op.");
        return Validate(uri);
    }

    public static Uri Validate(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ImportProblem(400, "Alleen https-links zijn toegestaan.");
        if (uri.UserInfo.Length > 0) throw new ImportProblem(400, "Links met gebruikersnaam of wachtwoord zijn niet toegestaan.");
        if (!uri.IsDefaultPort) throw new ImportProblem(400, "Links met een afwijkende poort zijn niet toegestaan.");
        if (uri.HostNameType is UriHostNameType.Unknown || string.IsNullOrEmpty(uri.IdnHost))
            throw new ImportProblem(400, "Geef een geldige https-link naar een PDF op.");
        return uri;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] is 0 or 10 or 127 ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 198 && bytes[1] is 18 or 19) ||
                (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
                bytes[0] >= 224);
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
            (bytes[0] & 0xFE) == 0xFC ||
            (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8));
    }

    public static async Task<IPAddress[]> ResolvePublicAsync(string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try { addresses = await resolver(host, cancellationToken); }
        catch (SocketException) { throw new ImportProblem(400, "De link is niet bereikbaar."); }
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new ImportProblem(400, "Deze link verwijst naar een niet-openbaar adres en wordt geweigerd.");
        return addresses;
    }
}

public sealed class PdfDownloader(HttpClient client) : IPdfDownloader
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await ImportUrlPolicy.ResolvePublicAsync(context.DnsEndPoint.Host,
                (host, ct) => Dns.GetHostAddressesAsync(host, ct), cancellationToken);
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException exception)
                {
                    socket.Dispose();
                    last = exception;
                }
            }
            throw new HttpRequestException("The host is unreachable.", last);
        }
    };

    public async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        var uri = ImportUrlPolicy.Parse(url);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Accept", "application/pdf");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (redirects >= ImportUrlPolicy.MaximumRedirects || response.Headers.Location is not { } location)
                        throw new ImportProblem(400, "De link bevat te veel doorverwijzingen.");
                    uri = ImportUrlPolicy.Validate(location.IsAbsoluteUri ? location : new Uri(uri, location));
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new ImportProblem(400, $"De link kon niet worden opgehaald (HTTP {(int)response.StatusCode}).");
                return await ReadAsync(response, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ImportProblem(400, "Het ophalen van de link duurde te lang.");
        }
        catch (HttpRequestException)
        {
            throw new ImportProblem(400, "De link is niet bereikbaar.");
        }
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const string TooLarge = "Maximaal 20 MiB per PDF en 5 MiB per screenshot.";
        if (response.Content.Headers.ContentLength > PdfExtraction.MaximumFileBytes) throw new ImportProblem(413, TooLarge);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > PdfExtraction.MaximumFileBytes) throw new ImportProblem(413, TooLarge);
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var pdfType = string.Equals(response.Content.Headers.ContentType?.MediaType, "application/pdf",
            StringComparison.OrdinalIgnoreCase);
        if (!pdfType && !bytes.AsSpan().StartsWith("%PDF-"u8))
            throw new ImportProblem(400, "De link verwijst niet naar een PDF.");
        return bytes;
    }
}
