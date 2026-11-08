using MegaplanUnisender.Core;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.Graylog;
using Serilog.Sinks.Graylog.Core.Transport;

namespace MegaplanUnisender.Api;

public static class ServiceLogging
{
    public static void Configure(LoggerConfiguration logger, Secrets secrets)
    {
        var level = Enum.TryParse<LogEventLevel>(secrets.GraylogMinimumLevel, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : LogEventLevel.Information;
        logger.MinimumLevel.Is(level).Enrich.FromLogContext()
            .Enrich.WithProperty("application_name", secrets.GraylogApplicationName)
            // Framework HTTP events may contain the webhook secret, API key or signed download URL.
            .Filter.ByExcluding(e => e.Properties.TryGetValue("SourceContext", out var source) && source is ScalarValue { Value: string category } &&
                (category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)))
            .WriteTo.Console(new JsonFormatter(renderMessage: true));
        if (string.IsNullOrWhiteSpace(secrets.GraylogAddress)) return;
        if (Uri.CheckHostName(secrets.GraylogAddress) == UriHostNameType.Unknown || secrets.GraylogPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(secrets.GraylogApplicationName))
            throw new InvalidOperationException("Заполните Secrets:GraylogAddress, GraylogPort и GraylogApplicationName для GELF UDP.");
        var options = new GraylogSinkOptions
        {
            HostnameOrAddress = secrets.GraylogAddress, Port = secrets.GraylogPort, TransportType = TransportType.Udp
        };
        if (!string.IsNullOrWhiteSpace(secrets.GraylogSource)) options.HostnameOverride = secrets.GraylogSource;
        logger.WriteTo.Graylog(options);
    }
}
