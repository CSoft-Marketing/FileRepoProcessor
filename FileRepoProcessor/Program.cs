using Consac.License;
using FileRepoProcessor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Runtime.InteropServices;

// Create log directory
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);
var SpireLicKey = "X7lUr8gBAByicCxwjBEIXev+8FrH8cmPlfmmxBQcmsJnAKnHYmoqnbGd8XBEByazdDiVZL0TtsJi2hp4jgR4eSX8qYoBGTFOa4dSGvCShItfYq+40IsaHYNpMgk3lalCib6GYj407Q1AJ5GjGvxvTSlL6RXKBFIRnpamXbffUt5tsSQxVnUipvbpTCuErAhS5IDkfytEa3LvfB4Ms9E7BsHw30Hbq8yKv1KewVzKOnPcfe9+qfDEWgQOhmZ2G0MvSUGIupzsnRYIjowagnU3T+hGQxezXKs3uAqLE/TNQn+C+EeD28xJONLQNXmr73qJieBMBURzwp40QlVKIg9VhYQaqfVKWesA8lx5lDIoAQilZC3ZxoDb6E62PeuTjf3aN5oXxB/T5qaqtDJI9tFXst45ZRcgJ8AlJjU4lovqCmDycqtXEG0AsVjmgKnz65vBekqQwN7UV7cBBa1Fwnor9ZLxWXu0nhOvMMgPYXL5MRfb9/Dn+Xy56kKzUjUTIy6LxlWWh7ZA3cs07pH9FSD5OfycDT1ibfagl7ofoAMQBsv1eTGL7/l3sgF/z2/fSuJLxgzZUQGQ49uc5oEg+gxNrVLgJNzSZJh8TwcUbNgdBC5AkxK2N3czhrypLUeADMDQGOtJlLmQcviZriOzjUDZl+cG9i+JbjHqMzSZngsHCXkojbkFlLS39aJXbBzHrPYsURs0hZpaRi2HEvoaEOBDtcGz1G+WvAZMuXZkvCHffTg9W10Vy6E/KEFFdpLLcI9aulWA7s1+NZ7SkMD895OHbfPdCj4y7YbH8KPRGzrL/tnsJdz8JxBUkCkFDt1gFhVpdNvbjO/Jdv4kEvmkAzmPeZQdWrX16S5JhI0aPk+7PRwekRlZ+EBBmGzy1yDiThIf9mlIBcQQJ7BFzD32dY9DGJCnIchH3sI6dfQfwm3vv8lhz89GsV+9ItiixhpHHsmEbJ2wvpsb5NgLQMkWifaFLBEOqGelXKnrM8Gv4xdOfC13LLrWQAVYuHmJ0vsHHmv/m9JDCAKSwgYUL6leUU+0R6akxDFGUTn2zeSoPxsZLSsBvZRBrj4gZ4WJQ9i1HfYPDmHOAqv2gPkUnF3BbIJZnESD/3mTe9l/7YfjvzM9Wl+inenRlGL0sPdjwTF/LaJYs3cjqryCDLGEDUFczDlKXV2i3119JW4hdWDZprZ5B2rmXVTloWN+UWPvDjeWNRAQXv97ujyDcIq0Ru0fOlEpYPtylxXjSezzaMvN2GooQ3+rsxmZydM+Y7DcROE1x4HC9q2gZ5MxhtZuzm2a762iZmxLc0MAkBx6019exDodk0THfz9sap2e+BsNXi/zhqyIq5bbGiT6oHNcU+rmTa5Mh+zACgXFwL4onjJWL2rOZyaIkahZgo5kRfDt+JhkCubdtR+Ti+n6zcc+i/6pIT8XFx3EdxqDUoHAtXjsAE36qRuVIyaUIlnlmUQb9bIm/cCZ1fpZ6A8DZ/UqSrJRATinQwgfALrW7ri7cPtaE1IjfFn+ZBP+XYEWig8Tm8nmwQ3zgbfpxuYctp5PIZ0CYXLHJVT0KhgHg7LQ+yPzeBhhk91sgjoyi7PGgHDa3aZ04omWqoxFKRv9LYU=";

Spire.Doc.License.LicenseProvider.SetLicenseKey(SpireLicKey);

Spire.Pdf.License.LicenseProvider.SetLicenseKey(SpireLicKey);

Spire.Xls.License.LicenseProvider.SetLicenseKey(SpireLicKey);

Spire.Presentation.License.LicenseProvider.SetLicenseKey(SpireLicKey);

IHostBuilder builder = Host.CreateDefaultBuilder(args);

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    builder = builder.UseWindowsService();
}
else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
{
    builder = builder.UseSystemd();
}

IHost host = builder
    
    .UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext())
    .ConfigureServices((context, services) =>
    {
        // License validation
        string licPath = context.Configuration["Paths:LicFilePath"];
        string validationKey = context.Configuration["Paths:ValidationKey"];
        ChkLic checkLicense = new ChkLic(licPath, validationKey);
        if (!checkLicense.GetStatus())
        {
            Log.Fatal("License validation failed.");
            throw new InvalidOperationException(
                "License validation failed."
            );
        }
        var tempPath = context.Configuration["Paths:TempPath"];

        if (string.IsNullOrWhiteSpace(tempPath))
        {
            tempPath = Path.GetTempPath();
        }

        try
        {
            Directory.CreateDirectory(tempPath);
        }
        catch
        {
            tempPath = Path.GetTempPath();
            Directory.CreateDirectory(tempPath);
        }

        Environment.SetEnvironmentVariable("MAGICK_TEMPORARY_PATH", tempPath);
        Environment.SetEnvironmentVariable("MAGICK_TMPDIR", tempPath);
        services.Configure<PathOptions>(
            context.Configuration.GetSection("Paths"));

        services.AddSingleton<IFileQueue, FileQueue>();
        services.AddSingleton<IPathMapper, PathMapper>();
        services.AddSingleton<IRepoScanner, RepoScanner>();
        services.AddSingleton<IFileWatcher, RepoFileWatcher>();
        services.AddSingleton<IFileProcessor, FileProcessor>();

        services.AddHostedService<Worker>();
    })
    .Build();

host.Run();