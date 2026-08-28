using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using KyoshinEewViewer;
using KyoshinEewViewer.Core;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using KyoshinEewViewer.Map.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using KyoshinEewViewer.Series;

namespace SlackBot
{
	internal class Program
	{
		public static AutoResetEvent Are { get; } = new(false);

		// Initialization code. Don't use any Avalonia, third-party APIs or any
		// SynchronizationContext-reliant code before AppMain is called: things aren't initialized
		// yet and stuff might break.
		[STAThread]
		public static void Main(string[] args)
		{
			CultureInfo.CurrentCulture = new CultureInfo("ja-JP");
			LoggingAdapter.EnableConsoleLogger = true;
			PolygonFeature.AsyncVerticeMode = false;
			PolylineFeature.AsyncMode = false;

			var builder = BuildAvaloniaApp();
			builder.SetupWithoutStarting();

			var window = new MainWindow();
			window.Show();

			var logger = AppLog.Create<Program>();

			var webBuilder = WebApplication.CreateSlimBuilder(args);
			webBuilder.WebHost.ConfigureKestrel((context, serverOptions) =>
			{
				serverOptions.Listen(IPAddress.Any, 5000);
			});
			var webApp = webBuilder.Build();
			async Task SwitchAndCaptureAndResponseAsync(HttpContext context, SeriesBase series)
			{
				if (!window.Mres.IsSet)
					await Task.Run(window.Mres.Wait);

				window.Mres.Reset();
				try
				{
					await Dispatcher.UIThread.InvokeAsync(() => window.SelectedSeries = series);
					context.Response.ContentType = "image/webp";
					await window.CaptureImageAsync(context.Response.BodyWriter.AsStream());
				}
				finally
				{
					window.Mres.Set();
				}
			}
			async Task CaptureAndResponseAsync(HttpContext context)
			{
				context.Response.ContentType = "image/webp";
				await window.CaptureImageAsync(context.Response.BodyWriter.AsStream());
			}
			webApp.MapGet("/", CaptureAndResponseAsync);
			webApp.MapGet("/tsunami", context => SwitchAndCaptureAndResponseAsync(context, window.TsunamiSeries));
			webApp.MapGet("/earthquake", context => SwitchAndCaptureAndResponseAsync(context, window.EarthquakeSeries));
			webApp.MapGet("/kyoshin-monitor", context => SwitchAndCaptureAndResponseAsync(context, window.KyoshinMonitorSeries));

			// スクリーンショット取得モード(fork のスクショ用ブランチ限定・upstream には出さない)
			if (Environment.GetEnvironmentVariable("KEVI_SHOT_HEX") is { } shotHexes)
			{
				_ = Task.Run(async () =>
				{
					try
					{
						await Task.Delay(TimeSpan.FromSeconds(20));
						await Dispatcher.UIThread.InvokeAsync(() => window.SelectedSeries = window.QzssSeries);
						await Task.Delay(TimeSpan.FromSeconds(3));
						var index = 0;
						foreach (var hex in shotHexes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
						{
							await Dispatcher.UIThread.InvokeAsync(() =>
								KyoshinEewViewer.Series.Qzss.Events.ProcessManualDCReportRequested.Request(
									KyoshinEewViewer.DCReportParser.DCReport.Parse(Convert.FromHexString(hex))));
							await Task.Delay(TimeSpan.FromSeconds(4));
							var path = $"shot_{index:00}.webp";
							using (var fs = File.Create(path))
								await window.CaptureImageAsync(fs);
							logger.LogInformation("captured {Path} ({Hex})", path, hex);
							index++;
						}
					}
					catch (Exception ex)
					{
						logger.LogError(ex, "スクリーンショット取得に失敗しました");
					}
					Environment.Exit(0);
				});
			}

			Console.CancelKeyPress += (s, e) =>
			{
				e.Cancel = true;
				logger.LogInformation("キャンセルキーを検知しました。");
				webApp.StopAsync().Wait();
				Dispatcher.UIThread.Invoke(() => window.Close());
				Dispatcher.UIThread.InvokeShutdown();
			};
			Dispatcher.UIThread.ShutdownStarted += (s, e) => logger.LogInformation("シャットダウンを開始しました。");
			Dispatcher.UIThread.ShutdownFinished += (s, e) => logger.LogInformation("シャットダウンが完了しました。");

			webApp.RunAsync();
			Dispatcher.UIThread.MainLoop(CancellationToken.None);
		}

		// Avalonia configuration, don't remove; also used by visual designer.
		public static AppBuilder BuildAvaloniaApp()
			=> AppBuilder.Configure<App>()
				// .UsePlatformDetect()
				.UseSkia()
				.UseHarfBuzz()
				.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
				.LogToTrace();
	}
}
