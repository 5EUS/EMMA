WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
WebApplication app = builder.Build();

app.MapGet("/", () => Results.Text("EMMA", "text/plain"));

await app.RunAsync().ConfigureAwait(false);
