using Gatherum.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddScoped<IAppData, HttpAppData>();
builder.Services.AddScoped<TreeState>();
builder.Services.AddScoped<OutlineState>();
builder.Services.AddScoped<ThemeState>();

await builder.Build().RunAsync();
