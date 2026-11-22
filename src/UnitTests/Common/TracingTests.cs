using Common;
using Common.Observe;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Net.Http;

namespace UnitTests.Common
{
    public class TracingTests
	{
		[Test]
		public void ValidateConfig_InvalidPort_Throws([Values(-1, 0, 65536)] int port)
		{
			var config = new Observability();
			config.Jaeger.Enabled = true;
			config.Jaeger.AgentPort = port;
			config.Jaeger.AgentHost = "host";

			Action action = () => Tracing.ValidateConfig(config);
			action.Should().Throw<ArgumentException>();
		}

		[Test]
		public void ValidateConfig_ValidHostPort_DoesNotThrow()
		{
			var config = new Observability();
			config.Jaeger.Enabled = true;
			config.Jaeger.AgentPort = 4317;
			config.Jaeger.AgentHost = "host";

			Tracing.ValidateConfig(config);
		}

		[Test]
		public void ValidateConfig_LegacyJaegerUdpPort_Throws()
		{
			var config = new Observability();
			config.Jaeger.Enabled = true;
			config.Jaeger.AgentPort = 6831;
			config.Jaeger.AgentHost = "host";

			Action action = () => Tracing.ValidateConfig(config);
			action.Should().Throw<ArgumentException>()
				.WithMessage("*4317*");
		}

		[Test]
		public void ValidateConfig_InvalidHost_Throws([Values(null, "")] string agentHost)
		{
			var config = new Observability();
			config.Jaeger.Enabled = true;
			config.Jaeger.AgentPort = 8000;
			config.Jaeger.AgentHost = agentHost;

			Action action = () => Tracing.ValidateConfig(config);
			action.Should().Throw<ArgumentException>();
		}

		[Test]
		public void ValidateConfig_Disabled_DoesNotThrow()
		{
			var config = new Observability();
			config.Jaeger.Enabled = false;

			Tracing.ValidateConfig(config);
		}

		[Test]
		public void HttpRequestMessageEnricher_DoesNotCaptureQueryOrBody()
		{
			var activity = new Activity("test");
			var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/path?token=secret")
			{
				Content = new StringContent("password=secret")
			};

			Tracing.HttpRequestMessageEnricher(activity, request);

			activity.DisplayName.Should().Be("GET /path");
			activity.GetTagItem("http.request.path").Should().Be("/path");
			activity.GetTagItem("http.request.query").Should().BeNull();
			activity.GetTagItem("http.request.body").Should().BeNull();
		}

		[Test]
		public void AspNetCoreRequestEnricher_DoesNotCaptureQuery()
		{
			var activity = new Activity("test");
			var context = new DefaultHttpContext();
			context.Request.Path = "/poll";
			context.Request.QueryString = new QueryString("?token=secret");

			Tracing.AspNetCoreRequestEnricher(activity, context.Request);

			activity.GetTagItem("http.path").Should().Be("/poll");
			activity.GetTagItem("http.query").Should().BeNull();
		}
	}
}
