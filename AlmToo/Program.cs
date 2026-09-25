using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using AlmToo;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Accessor.BrowserGitAccessor.Service;
using AlmToo.Managers.Repositories;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<BrowserGitAccessor>();
builder.Services.AddScoped<IBrowserGitAccessor>(services => services.GetRequiredService<BrowserGitAccessor>());
builder.Services.AddScoped<IBrowserFileAccessor>(services => services.GetRequiredService<BrowserGitAccessor>());
builder.Services.AddScoped<RepositoryWorkspaceManager>();

await builder.Build().RunAsync();
