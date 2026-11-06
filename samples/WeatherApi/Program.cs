using FunctionsWebAppHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;

var builder = FunctionsApplication.CreateBuilder(args);

builder.HostWebApp(
    webBuilder =>
    {
        webBuilder.Services.AddOpenApi();
        webBuilder.Services.AddProblemDetails();
    },
    app =>
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (app.Environment.IsDevelopment() ||
            app.Configuration.GetValue<bool>("EnableApiDocumentation"))
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        string[] summaries =
        [
            "Freezing", "Bracing", "Chilly", "Cool", "Mild",
            "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        ];

        app.MapGet("/weatherforecast", (CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var forecast = Enumerable.Range(1, 5)
                .Select(day => new WeatherForecast(
                    today.AddDays(day),
                    Random.Shared.Next(-20, 55),
                    summaries[Random.Shared.Next(summaries.Length)]))
                .ToArray();
            return TypedResults.Ok(forecast);
        })
        .WithName("GetWeatherForecast")
        .WithSummary("Get a five-day weather forecast")
        .WithDescription("Returns randomly generated sample data, not a real weather forecast.");
    });

builder.Build().Run();

/// <summary>A sample daily forecast with temperatures in Celsius and Fahrenheit.</summary>
public sealed record WeatherForecast(DateOnly Date, int TemperatureC, string Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
