
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>();
var app = builder.Build();

var dataDir = Path.Combine(
    app.Environment.ContentRootPath, "Data");

Directory.CreateDirectory(dataDir);

var issues = new (string Category, string Description)[]
{
    ("התחברות", "המשתמש לא מצליח להיכנס למערכת"),
    ("התחברות", "הסיסמה נדחית למרות שהיא תקינה"),
    ("ביצועים", "טעינת הדף אורכת זמן רב"),
    ("ביצועים", "השרת מגיב באיטיות"),
    ("נתונים", "חלק מהנתונים לא //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////// נשמרו"),
    ("נתונים", "נמצאו רשומות כפולות"),
    ("ממשק", "כפתור השמירה אינו מגיב"),
    ("ממשק", "הטבלה אינה מוצגת כראוי")
};

var systems = new[] { "CRM", "ERP", "Portal" };
var severities = new[] { "Low", "Medium", "High" };

var random = new Random(42);

// Generate 10 files, 1,000 records each
for (int fileIndex = 1; fileIndex <= 10; fileIndex++)
{
    var fileName = $"report_{fileIndex:00}.jsonl";
    var path = Path.Combine(dataDir, fileName);

    if (File.Exists(path))
        continue;

    var records = Enumerable.Range(1, 1000).Select(i =>
    {
        var issue = issues[random.Next(issues.Length)];
        var system = systems[random.Next(systems.Length)];

        return new Report(
            Id: (fileIndex - 1) * 1000 + i,
            Date: new DateOnly(2026, 1, 1)
                .AddDays(random.Next(300)),
            Category: issue.Category,
            Severity: severities[random.Next(severities.Length)],
            Description: $"{issue.Description} במערכת {system}",
            SourceFile: fileName
        );
    });

    File.WriteAllLines(path,
        records.Select(r => JsonSerializer.Serialize(r)));
}

// Load all records
var reports = Directory
    .GetFiles(dataDir, "*.jsonl")
    .SelectMany(file => File.ReadLines(file)
        .Select(line => JsonSerializer.Deserialize<Report>(line)!))
    .ToList();

// Basic statistics
app.MapGet("/summary", () => new
{
    FileCount = Directory.GetFiles(dataDir, "*.jsonl").Length,
    TotalRecords = reports.Count,
    UniqueRecords = reports.Select(r => r.Id).Distinct().Count(),
    Categories = reports
        .GroupBy(r => r.Category)
        .Select(g => new { Category = g.Key, Count = g.Count() })
});

// View sample records
app.MapGet("/sample", () => reports.Take(5));


var ragService = new RagSearchService(dataDir);
ragService.LoadFullIndex(reports);

// Create vector index
app.MapPost("/index-demo", async () =>
{
    var count = await ragService.BuildIndexAsync(reports);

    return Results.Ok(new { IndexedRecords = count });
});

// Search for similar reports
app.MapGet("/search", async (string question) =>
{
    if (!ragService.HasIndex)
        return Results.BadRequest("Run /index-demo first");

    var results = await ragService.SearchAsync(question);

    return Results.Ok(results);
});


app.MapPost("/ask", async (AskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest("Question is required");

    if (!ragService.HasIndex)
        return Results.BadRequest("Create index first");

    var result = await ragService.AskAsync(request.Text);

    return Results.Ok(result);
});

app.MapPost("/index-all", async () =>
{
    var result = await ragService.BuildFullIndexAsync(reports);
    return Results.Ok(result);
});


app.MapGet("/analytics", () =>
{
    // Total number of reports
    var totalRecords = reports.Count;

    // Reports by category
    var byCategory = reports
        .GroupBy(r => r.Category)
        .Select(g => new
        {
            Category = g.Key,
            Count = g.Count(),
            Percentage = Math.Round(
                g.Count() * 100.0 / totalRecords, 2)
        })
        .OrderByDescending(x => x.Count)
        .ToArray();

    // Reports by severity
    var bySeverity = reports
        .GroupBy(r => r.Severity)
        .Select(g => new
        {
            Severity = g.Key,
            Count = g.Count()
        })
        .OrderByDescending(x => x.Count)
        .ToArray();

    // Reports by month
    var byMonth = reports
        .GroupBy(r => r.Date.ToString("yyyy-MM"))
        .Select(g => new
        {
            Month = g.Key,
            Count = g.Count()
        })
        .OrderBy(x => x.Month)
        .ToArray();

    // Top recurring issues across all files
    var topIssues = reports
        .GroupBy(r => new
        {
            r.Category,
            r.Description
        })
        .Select(g => new
        {
            g.Key.Category,
            g.Key.Description,
            Count = g.Count(),
            SampleIds = g.Take(3)
                .Select(r => r.Id).ToArray(),
            Files = g.Select(r => r.SourceFile)
                .Distinct()
                .OrderBy(f => f)
                .ToArray()
        })
        .OrderByDescending(x => x.Count)
        .Take(10)
        .ToArray();

    return Results.Ok(new
    {
        TotalRecords = totalRecords,
        ByCategory = byCategory,
        BySeverity = bySeverity,
        ByMonth = byMonth,
        TopIssues = topIssues
    });
});


app.MapPost("/ask-analytics", async (AskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest("Question is required");

    // Calculate facts from ALL 10,000 records
    var statistics = new
    {
        TotalRecords = reports.Count,

        DateRange = new
        {
            From = reports.Min(r => r.Date),
            To = reports.Max(r => r.Date)
        },

        ByCategory = reports
            .GroupBy(r => r.Category)
            .Select(g => new
            {
                Category = g.Key,
                Count = g.Count()
            })
            .ToArray(),

        BySeverity = reports
            .GroupBy(r => r.Severity)
            .Select(g => new
            {
                Severity = g.Key,
                Count = g.Count()
            })
            .ToArray(),

        // Cross-analysis: category + severity
        ByCategoryAndSeverity = reports
            .GroupBy(r => new
            {
                r.Category,
                r.Severity
            })
            .Select(g => new
            {
                g.Key.Category,
                g.Key.Severity,
                Count = g.Count()
            })
            .ToArray(),

        ByMonth = reports
            .GroupBy(r => r.Date.ToString("yyyy-MM"))
            .Select(g => new
            {
                Month = g.Key,
                Count = g.Count()
            })
            .OrderBy(x => x.Month)
            .ToArray(),

        TopIssues = reports
            .GroupBy(r => new
            {
                r.Category,
                r.Description
            })
            .Select(g => new
            {
                g.Key.Category,
                g.Key.Description,
                Count = g.Count(),
                FileCount = g.Select(r => r.SourceFile)
                    .Distinct().Count()
            })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToArray()
    };

    var answer = await ragService.ExplainAnalyticsAsync(
        request.Text, statistics);

    return Results.Ok(new
    {
        Answer = answer,
        BasedOnRecords = reports.Count,
        Statistics = statistics
    });
});


app.Run();

public record Report(
    int Id,
    DateOnly Date,
    string Category,
    string Severity,
    string Description,
    string SourceFile
);
public record AskRequest(string Text);
