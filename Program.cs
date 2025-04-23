using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System;

var builder = WebApplication.CreateBuilder(args);

// ✅ Add CORS services
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("https://www.bing.com") // Update this with the allowed origin
              .AllowCredentials()
              .WithMethods("GET")
              .WithHeaders("Content-Type");
    });
});

// Get the dynamic port from the environment variable for Render
var port = Environment.GetEnvironmentVariable("PORT") ?? "5001"; // Default to 5001 if no environment variable is found

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // Render automatically configures HTTPS, so no need for UseHttps()
    serverOptions.ListenAnyIP(int.Parse(port)); // Listen on dynamic port
});

var app = builder.Build();

const string apiUrl = "https://ps-ig63.ifbus.de/api/search/1.1/rpc/search/search"; // Your backend API URL
const string loginUrl = "https://ps-ig63.ifbus.de/auth/login/basic/";
const string username = "igadmin";
const string password = "igadmin";
const string baseUrl = "https://ducksearch.onrender.com"; // Replace with the actual public URL from Render

// Map GET /suggest endpoint
app.MapGet("/suggest", async (HttpContext context) =>
{
    var query = context.Request.Query["qry"]
        .FirstOrDefault(q => !string.IsNullOrWhiteSpace(q?.Trim()));

    if (string.IsNullOrWhiteSpace(query))
    {
        return Results.Json(
            new { Suggestions = new object[] { } },
            new JsonSerializerOptions { PropertyNamingPolicy = null }
        );
    }

    Console.WriteLine("Received query: " + query);

    var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
    var handler = new HttpClientHandler { UseCookies = false };
    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

    var loginResponse = await client.PostAsync(loginUrl, null);
    if (!loginResponse.Headers.TryGetValues("Set-Cookie", out var cookies))
    {
        return Results.Json(
            new { Suggestions = new object[] { }, Error = "Auth failed" },
            new JsonSerializerOptions { PropertyNamingPolicy = null }
        );
    }

    var sessionCookie = cookies.FirstOrDefault()?.Split(';')[0];
    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Add("Cookie", sessionCookie);

    var body = new
    {
        parameters = new
        {
            querySyntax = "js",
            query = JsonSerializer.Serialize(new { query = new { nlq = new { text = query } } }),
            limit = 10,
            attributes = new object[]
            {
                new { name = "common.title" },
                new { name = "displayurl" }
            }
        },
        indexKey = "multiplex"
    };

    var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    var response = await client.PostAsync(apiUrl, content);
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"API call failed: {response.StatusCode}");
        return Results.Json(
            new { Suggestions = new object[] { } },
            new JsonSerializerOptions { PropertyNamingPolicy = null }
        );
    }

    var resultJson = await response.Content.ReadAsStringAsync();

    var suggestions = new List<object>
    {
        new
        {
            Text = "🔧 Test Suggestion",
            Attributes = new
            {
                url = "https://en.wikipedia.org/wiki/Test",
                query = "Test Suggestion",
                previewPaneUrl = "https://en.wikipedia.org/wiki/Test"
            }
        }
    };

    using var doc = JsonDocument.Parse(resultJson);
    if (doc.RootElement.TryGetProperty("values", out var values))
    {
        foreach (var item in values.EnumerateArray())
        {
            try
            {
                string title = null;
                string displayUrl = null;

                if (item.TryGetProperty("common.title", out var titleElement))
                {
                    if (titleElement.ValueKind == JsonValueKind.Array && titleElement.GetArrayLength() > 0)
                    {
                        var first = titleElement[0];
                        if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("raw", out var rawTitle) && rawTitle.ValueKind == JsonValueKind.String)
                        {
                            title = rawTitle.GetString();
                        }
                        else if (first.ValueKind == JsonValueKind.String)
                        {
                            title = first.GetString();
                        }
                    }
                    else if (titleElement.ValueKind == JsonValueKind.String)
                    {
                        title = titleElement.GetString();
                    }
                }

                if (item.TryGetProperty("displayurl", out var urlElement))
                {
                    if (urlElement.ValueKind == JsonValueKind.Array && urlElement.GetArrayLength() > 0)
                    {
                        var first = urlElement[0];
                        if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("raw", out var rawUrl) && rawUrl.ValueKind == JsonValueKind.String)
                        {
                            displayUrl = rawUrl.GetString();
                        }
                        else if (first.ValueKind == JsonValueKind.String)
                        {
                            displayUrl = first.GetString();
                        }
                    }
                    else if (urlElement.ValueKind == JsonValueKind.String)
                    {
                        displayUrl = urlElement.GetString();
                    }
                }

                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(displayUrl))
                {
                    var fullUrl = $"{baseUrl}{displayUrl}";

                    suggestions.Add(new
                    {
                        Text = title,
                        Attributes = new
                        {
                            url = fullUrl,
                            query = title,
                            previewPaneUrl = fullUrl
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Skipped item due to parsing error: {ex.Message}");
            }
        }
    }

    context.Response.Headers.Append("Access-Control-Allow-Origin", "https://www.bing.com");
    context.Response.Headers.Append("Access-Control-Allow-Credentials", "true");
    context.Response.Headers.Append("Access-Control-Allow-Methods", "GET");
    context.Response.ContentType = "application/json; charset=utf-8";

    return Results.Json(
        new { Suggestions = suggestions },
        new JsonSerializerOptions { PropertyNamingPolicy = null }
    );
});

app.Run();
