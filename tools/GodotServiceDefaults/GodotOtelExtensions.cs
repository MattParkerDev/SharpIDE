using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

public static class GodotOtelExtensions
{
	private static TracerProvider _tracerProvider = null!;
	private static MeterProvider _meterProvider = null!;
	public static void AddServiceDefaults()
	{
		var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
		if (endpoint is null)
		{
			Console.WriteLine("OTEL_EXPORTER_OTLP_ENDPOINT is not set, skipping OpenTelemetry setup.");
			return;
		}
		var endpointUri = new Uri(endpoint);

		_tracerProvider = Sdk.CreateTracerProviderBuilder()
			.AddSource("SharpIde")
			.AddOtlpExporter(options =>
			{
				options.Endpoint = endpointUri;
				options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
			})
			.Build();

		_meterProvider = Sdk.CreateMeterProviderBuilder()
			.AddMeter("SharpIde")
			.AddRuntimeInstrumentation()
			.AddOtlpExporter(options =>
			{
				options.Endpoint = endpointUri;
				options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
			})
			.Build();
	}

	public static void AddGodotOpenTelemetry(this IServiceCollection services)
	{
		services.AddOpenTelemetry();
		services.AddOpenTelemetryExporters();
	}

	private static void AddOpenTelemetryExporters(this IServiceCollection services)
	{
		var useOtlpExporter = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
		if (useOtlpExporter)
		{
			services.AddOpenTelemetry().UseOtlpExporter();
		}
	}
}
