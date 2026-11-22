using Common.Stateful;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;

namespace Common.Observe
{
	public static class TagKey
	{
		public static string Category = "category";
		public static string App = "app";
		public static string WorkoutId = "workout_id";
		public static string Table = "table";
		public static string Format = "file_type";
	}

	public static class TagValue
	{
		public static string Default = "app";
		public static string Db = "db";
		public static string Fit = "fit";
		public static string Tcx = "tcx";
		public static string P2G = "p2g";
	}

	public static class Tracing
	{
		public static ActivitySource Source;

		public static TracerProvider EnableConsoleTracing(Jaeger config)
		{
			if (!ValidateExporterConfig(new Observability { Jaeger = config }))
				return null;

			TracerProvider tracing = null;
			if (!config.Enabled)
				return tracing;

			var builder = Sdk.CreateTracerProviderBuilder()
				.ConfigureDefaultBuilder(config)
				.Build();

			Log.Information("Tracing started and exporting via OTLP to: http://{@Host}:{@Port}", config.AgentHost, config.AgentPort);

			return tracing;
		}

		public static void EnableApiTracing(IServiceCollection services, Jaeger config)
		{
			if (!ValidateExporterConfig(new Observability { Jaeger = config }))
				return;

			if (!config.Enabled)
				return;

			services
				.AddOpenTelemetry()
				.WithTracing((builder) =>
				{
					builder
					.ConfigureDefaultBuilder(config)
					.AddAspNetCoreInstrumentation(c =>
					{
						c.RecordException = true;
						c.EnrichWithHttpRequest = AspNetCoreRequestEnricher;
					});
				});

			Log.Information("Tracing started and exporting via OTLP to: http://{@Host}:{@Port}", config.AgentHost, config.AgentPort);

		}

		public static void EnableWebUITracing(IServiceCollection services, Jaeger config)
		{
			if (!ValidateExporterConfig(new Observability { Jaeger = config }))
				return;

			if (!config.Enabled)
				return;

			services
				.AddOpenTelemetry()
				.WithTracing(
				(builder) =>
				{
					builder.ConfigureDefaultBuilder(config);
				});

			Log.Information("Tracing started and exporting via OTLP to: http://{@Host}:{@Port}", config.AgentHost, config.AgentPort);
		}

		private static TracerProviderBuilder ConfigureDefaultBuilder(this TracerProviderBuilder builder, Jaeger config)
		{
			return builder
					.AddSource(Statics.TracingService)
					.SetResourceBuilder(
						ResourceBuilder
						.CreateDefault()
						.AddService(serviceName: Statics.TracingService, serviceVersion: Constants.AppVersion)
						.AddAttributes(new List<KeyValuePair<string, object>>()
						{
							new KeyValuePair<string, object>("host.machineName", Environment.MachineName),
							new KeyValuePair<string, object>("host.os", Environment.OSVersion.VersionString),
							new KeyValuePair<string, object>("dotnet.version", Environment.Version.ToString()),
						})
					)
					.SetSampler(new AlwaysOnSampler())
					.SetErrorStatusOnException()
					.AddHttpClientInstrumentation(h =>
					{
						h.RecordException = true;
						h.EnrichWithHttpRequestMessage = HttpRequestMessageEnricher;
						h.EnrichWithException = HttpExceptionEnricher;
					})
					.AddOtlpExporter(o =>
					{
						o.Endpoint = new Uri($"http://{config.AgentHost}:{config.AgentPort.GetValueOrDefault()}");
						o.Protocol = OtlpExportProtocol.Grpc;
					});
		}

		public static Activity Trace(string name, string category = "app", ActivityKind kind = ActivityKind.Server)
		{
			var activity = Source?.StartActivity(name, kind);

			activity?
				.SetTag(TagKey.Category, category)
				.SetTag("SpanId", activity.SpanId)
				.SetTag("TraceId", activity.TraceId);

			return activity;
		}

		public static Activity ClientTrace(string name, string category = "app", ActivityKind kind = ActivityKind.Client)
		{
			if (Activity.Current is null || Activity.Current.Kind != ActivityKind.Client)
			{
				Activity.Current?.Dispose();
				Activity.Current = null;
				Activity.Current = Source?.CreateActivity(name, kind);
			}

			var activity = Source?.StartActivity(name, kind);

			activity?
				.SetTag(TagKey.Category, category)
				.SetTag("SpanId", activity.SpanId)
				.SetTag("TraceId", activity.TraceId);

			return activity;
		}

		public static Activity WithWorkoutId(this Activity activity, string workoutId)
		{
			return activity?.SetTag(TagKey.WorkoutId, workoutId);
		}

		public static Activity WithTable(this Activity activity, string table)
		{
			return activity?.SetTag(TagKey.Table, table);
		}

		public static Activity WithTag(this Activity activity, string key, string value)
		{
			return activity?.SetTag(key, value);
		}

		public static void ValidateConfig(Observability config)
		{
			ValidateExporterConfig(config, throwOnInvalid: true);
		}

		public static bool ValidateExporterConfig(Observability config, bool throwOnInvalid = false)
		{
			if (!config.Jaeger.Enabled)
				return true;

			if (string.IsNullOrEmpty(config.Jaeger.AgentHost))
			{
				FailValidation("Agent Host must be set.", nameof(config.Jaeger.AgentHost), throwOnInvalid);
				return false;
			}

			if (config.Jaeger.AgentPort is null || config.Jaeger.AgentPort <= 0 || config.Jaeger.AgentPort > 65535)
			{
				FailValidation("Agent Port must be a valid port.", nameof(config.Jaeger.AgentPort), throwOnInvalid);
				return false;
			}

			if (config.Jaeger.AgentPort == 6831)
			{
				FailValidation("Jaeger UDP port 6831 is not supported. Configure AgentPort 4317 for OTLP gRPC.", nameof(config.Jaeger.AgentPort), throwOnInvalid);
				return false;
			}

			return true;
		}

		private static void FailValidation(string message, string parameterName, bool throwOnInvalid)
		{
			Log.Error("{Message}", message);
			if (throwOnInvalid)
				throw new ArgumentException(message, parameterName);
		}

		public static void HttpRequestMessageEnricher(Activity activity, HttpRequestMessage request)
		{
			activity.DisplayName = $"{request.Method} {request.RequestUri.AbsolutePath}";
			activity.SetTag("http.request.path", request.RequestUri.AbsolutePath);
		}

		public static void HttpExceptionEnricher(Activity activity, Exception exception)
		{
			activity.SetTag("stackTrace", exception.StackTrace);
		}

		public static void AspNetCoreRequestEnricher(Activity activity, HttpRequest httpRequest)
		{
			if (httpRequest.Headers.TryGetValue("TraceId", out var incomingTraceParent))
				activity.SetParentId(incomingTraceParent);

			if (httpRequest.Headers.TryGetValue("uber-trace-id", out incomingTraceParent))
				activity.SetParentId(incomingTraceParent);

			activity.SetTag("http.path", httpRequest.Path.ToString());
		}
	}
}
