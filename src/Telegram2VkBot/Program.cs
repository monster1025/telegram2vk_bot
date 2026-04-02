using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Telegram2VkBot;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("TELEGRAM"));
        builder.Services.Configure<VkOptions>(builder.Configuration.GetSection("VK"));
        builder.Services.Configure<DbOptions>(builder.Configuration.GetSection("DB"));

        builder.Services.AddHttpClient(VkApiClient.UploadHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        builder.Services.AddSingleton<VkApiClient>();
        builder.Services.AddSingleton<MappingRepository>();

        builder.Services.AddHostedService<ForwardWorker>();

        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });

        var host = builder.Build();
        await host.RunAsync();
    }
}

