var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "GeoJSON to KML API",
        Version = "v1",
        Description = "GeoJSON to KML API",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "pap",
            Email = "info@pap.ir"
        }
    });
});
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "GeoJSON to KML API v1");
    c.RoutePrefix = string.Empty; 
    c.ConfigObject.AdditionalItems["theme"] = "dark";
    c.DefaultModelsExpandDepth(-1);
    c.DisplayOperationId();
    c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
});
app.MapControllers();

app.Run();
