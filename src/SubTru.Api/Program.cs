using SubTru.Api.Bootstrap;

var builder = WebApplication.CreateBuilder(args);

builder.AddSubTruServices();

var app = builder.Build();

app.ValidateSubTruStartup();
app.UseSubTruPipeline();
app.MapSubTruEndpoints();

app.Run();

public partial class Program;
